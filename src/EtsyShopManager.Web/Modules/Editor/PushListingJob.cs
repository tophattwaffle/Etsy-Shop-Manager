using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>
/// Writes a listing's local edits to Etsy:
/// 1. re-read the listing from Etsy; if it changed there meanwhile (drift), stop without writing;
/// 2. snapshot what Etsy holds (for rollback);
/// 3. updateListing with the changed listing fields, then updateListingInventory if prices/processing changed
///    (built from a fresh inventory read, so quantities and everything else stay exactly as Etsy has them);
/// 4. re-read, and keep in the draft anything Etsy didn't take.
/// Not retried automatically: a failed write is reported and the person decides.
/// </summary>
public sealed class PushListingJob(
    IDbContextFactory<AppDbContext> dbFactory,
    ListingImporter importer,
    ImageStore images,
    EtsyApiClient etsy,
    IDomainEventPublisher events,
    IAuditLog audit,
    TimeProvider time,
    ILogger<PushListingJob> logger) : IJobHandler
{
    public const string Type = "etsy.listing.push";
    public string JobType => Type;
    public string DisplayName => "Push listing to Etsy";

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        var pushId = context.GetPayload<PushPayload>()?.PushId ?? throw new JobPermanentFailureException("PushId is required.");
        int listingId;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var push = await db.ListingPushes.FirstOrDefaultAsync(p => p.Id == pushId, ct)
                       ?? throw new JobPermanentFailureException($"Push {pushId} not found.");
            listingId = push.ListingId;
            push.Status = ListingPushStatus.Running;
            await db.SaveChangesAsync(ct);
        }

        try
        {
            context.Result = await PushAsync(pushId, listingId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FinishAsync(pushId, ListingPushStatus.Failed, ex.Message, onlyIfRunning: true);
            throw ex is RetryLaterException ? new JobPermanentFailureException(ex.Message, ex) : ex;
        }
    }

    private async Task<string> PushAsync(int pushId, int listingId, CancellationToken ct)
    {
        // 1. Fresh read: this updates the Etsy state, base and drift flags exactly as an import would.
        await importer.RefreshListingAsync(listingId, ct);

        Listing listing;
        ListingEditModel model;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            listing = await db.Listings.AsNoTracking().Include(l => l.Shop).ThenInclude(s => s.Token).Include(l => l.EtsyState)
                .FirstAsync(l => l.Id == listingId, ct);
            model = ListingEditModel.Parse(listing.EtsyState!.ManagedJson, listing.EtsyState.BaseJson, listing.DraftJson);
        }

        var preview = ListingEditor.Preview(listing, model, pushInProgress: false, images);
        if (preview.Blockers.Count > 0)
        {
            var reason = string.Join(" ", preview.Blockers);
            await FinishAsync(pushId, ListingPushStatus.Failed, reason);
            throw new JobPermanentFailureException($"Not pushed: {reason}");
        }
        if (preview.Changes.Count == 0)
        {
            await FinishAsync(pushId, ListingPushStatus.Succeeded, null);
            return "nothing to change (Etsy already matches)";
        }

        var auth = EtsyAuth.ForShop(listing.ShopId);
        var local = model.LocalState();
        var listingFields = preview.Changes
            .Where(c => c.Key is not (ListingFields.Inventory or ListingFields.Images))
            .Select(c => c.Key).ToList();
        var imagesChanged = preview.Changes.Any(c => c.Key == ListingFields.Images);
        var inventoryChanged = preview.Changes.Any(c => c.Key == ListingFields.Inventory);

        // 2. Snapshot what Etsy holds right now.
        var inventory = await etsy.GetListingInventoryAsync(listing.EtsyListingId, auth, ct);
        var snapshotId = await SnapshotAsync(listing, inventory, "Before push", ct);

        // 3. Write. Once something is written, a later failure must still reconcile; otherwise the next import would
        // report the fields this push changed as drift (changed on Etsy) and block further pushes.
        var wrote = false;
        var uploadedFiles = new Dictionary<long, string>();
        try
        {
            if (listingFields.Count > 0)
            {
                var form = ListingFormMapper.ToForm(local, listingFields);
                logger.LogInformation("Pushing {Fields} for listing {ListingId}", string.Join(", ", listingFields), listing.EtsyListingId);
                await etsy.UpdateListingAsync(listing.Shop.EtsyShopId, listing.EtsyListingId, form, auth, ct);
                wrote = true;
            }
            if (imagesChanged)
            {
                await PushImagesAsync(listing, model.Etsy[ListingFields.Images]!.AsArray(),
                    local[ListingFields.Images]!.AsArray(), auth, uploadedFiles, () => wrote = true, ct);
            }
            if (inventoryChanged)
            {
                var body = InventoryWriter.WithLocalPrices(inventory, local[ListingFields.Inventory]!);
                logger.LogInformation("Pushing prices/processing for listing {ListingId}", listing.EtsyListingId);
                await etsy.UpdateListingInventoryAsync(listing.EtsyListingId, body, auth, ct);
            }
        }
        catch (Exception ex) when (wrote && ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Push {PushId} failed after a partial write; reconciling listing {ListingId}", pushId, listing.EtsyListingId);
            var rest = await ReconcileAsync(pushId, listingId, snapshotId, uploadedFiles, ct);
            var applied = preview.Changes.Select(c => c.Key).Except(rest).Select(ListingEditor.Label);
            var message = $"{ex.Message} Applied before the error: {string.Join(", ", applied)}; the rest is still in your edits.";
            await FinishAsync(pushId, ListingPushStatus.PartlyApplied, message);
            await audit.WriteAsync("listing.pushed", nameof(Listing), listing.EtsyListingId.ToString(), message, "system", ct);
            throw new JobPermanentFailureException(message, ex);
        }

        // 4. Re-read and reconcile: Etsy's new state is agreed; what Etsy didn't take stays in the draft.
        var notApplied = await ReconcileAsync(pushId, listingId, snapshotId, uploadedFiles, ct);

        var pushed = preview.Changes.Select(c => c.Key).Except(notApplied).ToList();
        var labels = string.Join(", ", pushed.Select(ListingEditor.Label));
        if (notApplied.Count > 0)
        {
            var message = $"Etsy didn't take: {string.Join(", ", notApplied.Select(ListingEditor.Label))} (still in your edits)";
            await FinishAsync(pushId, ListingPushStatus.PartlyApplied, message);
            await audit.WriteAsync("listing.pushed", nameof(Listing), listing.EtsyListingId.ToString(), $"{labels}; {message}", "system", ct);
            await events.PublishAsync(new ListingPushed(listing.ShopId, listingId, listing.EtsyListingId, pushId, pushed), ct);
            return $"{labels} pushed; {message}";
        }

        await FinishAsync(pushId, ListingPushStatus.Succeeded, null);
        await audit.WriteAsync("listing.pushed", nameof(Listing), listing.EtsyListingId.ToString(), labels, "system", ct);
        await events.PublishAsync(new ListingPushed(listing.ShopId, listingId, listing.EtsyListingId, pushId, pushed), ct);
        return $"pushed {labels}";
    }

    private async Task PushImagesAsync(Listing listing, JsonArray etsyImages, JsonArray localImages, EtsyAuth auth,
        Dictionary<long, string> uploadedFiles, Action markWrote, CancellationToken ct)
    {
        var shopId = listing.Shop.EtsyShopId;
        var etsyListingId = listing.EtsyListingId;
        var currentByRank = etsyImages.Select((img, idx) => (
                Rank: (int?)img?["rank"] ?? (idx + 1),
                Id: (long?)img?["etsy_image_id"] ?? 0,
                Alt: (string?)img?["alt_text"] ?? ""))
            .GroupBy(x => x.Rank).ToDictionary(g => g.Key, g => g.First());

        var keptIds = localImages.Select(i => (long?)i?["etsy_image_id"] ?? 0).Where(id => id > 0).ToHashSet();
        var toDelete = etsyImages.Select(i => (long?)i?["etsy_image_id"] ?? 0)
            .Where(id => id > 0 && !keptIds.Contains(id)).ToList();

        // If every existing photo is being replaced, keep one until the first new photo is uploaded so the listing is
        // never left with zero photos on Etsy.
        long? deleteAfterFirstUpload = keptIds.Count == 0 && toDelete.Count > 0 ? toDelete[^1] : null;
        foreach (var id in deleteAfterFirstUpload is null ? toDelete : toDelete.SkipLast(1))
        {
            logger.LogInformation("Deleting image {ImageId} from listing {ListingId}", id, etsyListingId);
            await etsy.DeleteListingImageAsync(shopId, etsyListingId, id, auth, ct);
            markWrote();
        }

        var ranksShifted = toDelete.Count > 0;
        for (var i = 0; i < localImages.Count; i++)
        {
            var img = localImages[i]!.AsObject();
            var rank = i + 1;
            img["rank"] = rank;
            var id = (long?)img["etsy_image_id"] ?? 0;
            var alt = (string?)img["alt_text"] ?? "";

            if (id <= 0)
            {
                var fileName = (string?)img["file_name"]
                    ?? throw new JobPermanentFailureException($"Photo #{rank} has no uploaded file.");
                var bytes = await File.ReadAllBytesAsync(images.PathFor(fileName), ct);
                logger.LogInformation("Uploading new image ({FileName}) at rank {Rank} for listing {ListingId}", fileName, rank, etsyListingId);
                var created = await etsy.UploadListingImageAsync(shopId, etsyListingId, bytes, fileName, rank, alt, auth, ct);
                markWrote();
                ranksShifted = true;
                uploadedFiles[created.ListingImageId] = fileName;
                img["etsy_image_id"] = created.ListingImageId;
                img.Remove("file_name");
                await SaveDraftImagesAsync(listing.Id, localImages, ct);

                if (deleteAfterFirstUpload is { } deferredId)
                {
                    deleteAfterFirstUpload = null;
                    logger.LogInformation("Deleting replaced image {ImageId} from listing {ListingId}", deferredId, etsyListingId);
                    await etsy.DeleteListingImageAsync(shopId, etsyListingId, deferredId, auth, ct);
                }
                continue;
            }

            if (!ranksShifted && currentByRank.TryGetValue(rank, out var cur) && cur.Id == id && cur.Alt == alt)
                continue;

            if (!currentByRank.TryGetValue(rank, out var curAtRank) || curAtRank.Id != id)
                ranksShifted = true;

            logger.LogInformation("Updating image {ImageId} to rank {Rank} for listing {ListingId}", id, rank, etsyListingId);
            await etsy.UploadListingImageAsync(shopId, etsyListingId, id, rank, alt, auth, ct);
            markWrote();
        }
    }

    private async Task SaveDraftImagesAsync(int listingId, JsonArray localImages, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Listings.FirstAsync(l => l.Id == listingId, ct);
        if (string.IsNullOrWhiteSpace(row.DraftJson)) return;
        var draft = JsonNode.Parse(row.DraftJson)!.AsObject();
        draft[ListingFields.Images] = localImages.DeepClone();
        row.DraftJson = draft.ToJsonString();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Re-reads the listing: Etsy's new state becomes agreed. Returns the draft fields Etsy doesn't hold.</summary>
    private async Task<IReadOnlyList<string>> ReconcileAsync(int pushId, int listingId, int snapshotId,
        IReadOnlyDictionary<long, string> uploadedFiles, CancellationToken ct)
    {
        await importer.RefreshListingAsync(listingId, ct, raiseDriftEvents: false);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Listings.Include(l => l.EtsyState).Include(l => l.Images).FirstAsync(l => l.Id == listingId, ct);
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var (etsyImageId, fileName) in uploadedFiles)
        {
            if (row.Images.FirstOrDefault(i => i.EtsyImageId == etsyImageId) is { FileName: null } imgRow && images.Exists(fileName))
            {
                imgRow.FileName = fileName;
                imgRow.ByteSize = new FileInfo(images.PathFor(fileName)).Length;
                imgRow.DownloadedAtUtc = now;
            }
        }
        var after = ListingEditModel.Parse(row.EtsyState!.ManagedJson, row.EtsyState.BaseJson, row.DraftJson);
        var notApplied = after.AcceptPushed(JsonNode.Parse(row.EtsyState.ManagedJson)!.AsObject());
        ListingMapper.Apply(row, after);
        var push = await db.ListingPushes.FirstAsync(p => p.Id == pushId, ct);
        push.SnapshotId = snapshotId;
        await db.SaveChangesAsync(ct);
        return notApplied;
    }

    private async Task<int> SnapshotAsync(Listing listing, Etsy.Models.EtsyInventory inventory, string reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var snapshot = new ListingSnapshot
        {
            ListingId = listing.Id,
            TakenAtUtc = time.GetUtcNow().UtcDateTime,
            Reason = reason,
            ManagedJson = listing.EtsyState!.ManagedJson,
            RawListingJson = listing.EtsyState.RawJson,
            RawInventoryJson = JsonSerializer.Serialize(inventory, Etsy.Models.EtsyJson.Options),
        };
        db.ListingSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
        return snapshot.Id;
    }

    /// <param name="onlyIfRunning">Leave a push that already has its outcome recorded alone.</param>
    private async Task FinishAsync(int pushId, ListingPushStatus status, string? error, bool onlyIfRunning = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        var now = time.GetUtcNow().UtcDateTime;
        await db.ListingPushes.Where(p => p.Id == pushId && (!onlyIfRunning || p.Status == ListingPushStatus.Running)).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.Status, status)
            .SetProperty(p => p.CompletedAtUtc, now)
            .SetProperty(p => p.Error, error), CancellationToken.None);
    }
}

/// <summary>Turns managed-state fields into updateListing form fields.</summary>
public static class ListingFormMapper
{
    public static IReadOnlyList<KeyValuePair<string, string>> ToForm(JsonObject local, IEnumerable<string> keys)
    {
        var form = new List<KeyValuePair<string, string>>();
        foreach (var key in keys)
        {
            var value = local[key];
            switch (key)
            {
                case ListingFields.Inventory or ListingFields.Images:
                    continue; // written through their own endpoints
                case "tags" or "materials" or "production_partner_ids":
                    // Array fields are sent comma-separated (Etsy doesn't allow commas inside tags or materials).
                    form.Add(new(key, string.Join(",", (value?.AsArray() ?? []).Select(v => v?.ToString()))));
                    break;
                case "shop_section_id":
                    form.Add(new(key, value is null ? "0" : Scalar(value))); // 0 = no section
                    break;
                default:
                    if (value is null) continue; // Etsy has no "clear" for the remaining fields
                    form.Add(new(key, Scalar(value)));
                    break;
            }
        }
        return form;
    }

    private static string Scalar(JsonNode node) => node.GetValueKind() switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => node.GetValue<decimal>().ToString(CultureInfo.InvariantCulture),
        _ => node.GetValue<string>(),
    };
}
