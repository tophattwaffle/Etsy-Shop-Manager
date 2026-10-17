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
/// 3. updateListing with the changed listing fields, then the category attributes (one call each), personalization
///    (one call replacing all questions), photos, and updateListingInventory if variations/prices/processing changed
///    (built from a fresh inventory read, so quantities and everything else stay exactly as Etsy has them);
/// 4. re-read, and keep in the draft anything Etsy didn't take.
/// Not retried automatically: a failed write is reported and the person decides.
/// </summary>
public sealed class PushListingJob(
    IDbContextFactory<AppDbContext> dbFactory,
    ListingImporter importer,
    ListingImportQueue importQueue,
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

    /// <summary>Fields written by their own calls rather than updateListing.</summary>
    private static readonly HashSet<string> SeparateCalls =
        [ListingFields.Inventory, ListingFields.Images, ListingFields.Attributes, ListingFields.Personalization];

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        var pushId = context.GetPayload<PushPayload>()?.PushId ?? throw new JobPermanentFailureException("PushId is required.");
        int listingId;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var push = await db.ListingPushes.FirstOrDefaultAsync(p => p.Id == pushId, ct)
                       ?? throw new JobPermanentFailureException($"Push {pushId} not found.");
            // Only a waiting push runs (or one a restart interrupted). A finished one re-run from the Jobs page would
            // send whatever the draft holds now, without the review the push was requested with.
            if (push.Status is not (ListingPushStatus.Pending or ListingPushStatus.Running))
                throw new JobPermanentFailureException(
                    $"Push {pushId} already finished ({push.Status}). Review the changes and push again from the editor.");
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
            listing = await db.Listings.AsNoTracking().Include(l => l.Shop).ThenInclude(s => s.Token).Include(l => l.EtsyState).Include(l => l.Products).ThenInclude(p => p.Offerings).AsSplitQuery()
                .FirstAsync(l => l.Id == listingId, ct);
            model = ListingEditModel.Parse(listing.EtsyState!.ManagedJson, listing.EtsyState.BaseJson, listing.DraftJson);
        }

        IReadOnlyList<AttributeProperty>? properties;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            properties = await AttributeEditor.LoadAsync(db, (long?)model.Local("taxonomy_id"), ct);
        var preview = ListingEditor.Preview(listing, model, pushInProgress: false, images.Exists, properties);
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
        var listingFields = preview.Changes.Where(c => !SeparateCalls.Contains(c.Key)).Select(c => c.Key).ToList();
        var attributesChanged = preview.Changes.Any(c => c.Key == ListingFields.Attributes);
        var personalizationChanged = preview.Changes.Any(c => c.Key == ListingFields.Personalization);
        var photosChanged = preview.Changes.Any(c => c.Key == ListingFields.Images);
        var inventoryChanged = preview.Changes.Any(c => c.Key == ListingFields.Inventory);

        // 2. Snapshot what Etsy holds right now.
        var inventory = await etsy.GetListingInventoryAsync(listing.EtsyListingId, auth, ct);
        var snapshotId = await SnapshotAsync(listing, inventory, "Before push", ct);

        // 3. Write. Once something is written, a later failure must still reconcile; otherwise the next import would
        // report the fields this push changed as drift (changed on Etsy) and block further pushes.
        var wrote = false;
        var uploaded = new Dictionary<string, long>(); // file name -> Etsy's new image id
        try
        {
            if (listingFields.Count > 0)
            {
                var form = ListingFormMapper.ToForm(local, listingFields);
                logger.LogInformation("Pushing {Fields} for listing {ListingId}", string.Join(", ", listingFields), listing.EtsyListingId);
                await etsy.UpdateListingAsync(listing.Shop.EtsyShopId, listing.EtsyListingId, form, auth, ct);
                wrote = true;
            }
            if (attributesChanged)
            {
                // Planned from the fresh read (step 1); after the listing fields, so a new category is in place.
                foreach (var call in AttributeEditor.Plan(model.Etsy[ListingFields.Attributes], local[ListingFields.Attributes]))
                {
                    logger.LogInformation("Attributes for listing {ListingId}: {Call}", listing.EtsyListingId,
                        call.Describe(listing.Shop.EtsyShopId, listing.EtsyListingId, $"property {call.PropertyId}"));
                    if (call.Kind == AttributeCallKind.Delete)
                        await etsy.DeleteListingPropertyAsync(listing.Shop.EtsyShopId, listing.EtsyListingId, call.PropertyId, auth, ct);
                    else
                        await etsy.UpdateListingPropertyAsync(listing.Shop.EtsyShopId, listing.EtsyListingId, call.PropertyId,
                            call.ValueIds, call.Values, call.ScaleId, auth, ct);
                    wrote = true;
                }
            }
            if (personalizationChanged)
            {
                var questions = local[ListingFields.Personalization] as JsonArray ?? [];
                logger.LogInformation("Pushing {Count} personalization questions for listing {ListingId}", questions.Count, listing.EtsyListingId);
                if (questions.Count == 0)
                    await etsy.DeleteListingPersonalizationAsync(listing.Shop.EtsyShopId, listing.EtsyListingId, auth, ct);
                else // ids from the fresh read, so the questions that stay are updated in place
                    await etsy.UpdateListingPersonalizationAsync(listing.Shop.EtsyShopId, listing.EtsyListingId,
                        PersonalizationEditor.Body(questions, listing.PersonalizationJson), auth, ct);
                wrote = true;
            }
            if (photosChanged)
                await PushPhotosAsync(listing, local[ListingFields.Images]!.AsArray(), auth, uploaded, () => wrote = true, ct);
            if (inventoryChanged)
            {
                var body = InventoryWriter.FromLocal(inventory, local[ListingFields.Inventory]!);
                logger.LogInformation("Pushing the inventory (variations, prices, processing) for listing {ListingId}", listing.EtsyListingId);
                await etsy.UpdateListingInventoryAsync(listing.EtsyListingId, body, auth, ct);
            }
        }
        catch (Exception ex) when (wrote && ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Push {PushId} failed after a partial write; reconciling listing {ListingId}", pushId, listing.EtsyListingId);
            var rest = await ReconcileAsync(pushId, listingId, snapshotId, uploaded, ct);
            var applied = preview.Changes.Select(c => c.Key).Except(rest).Select(ListingEditor.Label);
            var message = $"{ex.Message} Applied before the error: {string.Join(", ", applied)}; the rest is still in your edits.";
            await FinishAsync(pushId, ListingPushStatus.PartlyApplied, message);
            await audit.WriteAsync("listing.pushed", nameof(Listing), listing.EtsyListingId.ToString(), message, "system", ct);
            throw new JobPermanentFailureException(message, ex);
        }

        // 4. Re-read and reconcile: Etsy's new state is agreed; what Etsy didn't take stays in the draft.
        var notApplied = await ReconcileAsync(pushId, listingId, snapshotId, uploaded, ct);

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

    /// <summary>Re-reads the listing: Etsy's new state becomes agreed. Returns the draft fields Etsy doesn't hold.</summary>
    private async Task<IReadOnlyList<string>> ReconcileAsync(int pushId, int listingId, int snapshotId,
        IReadOnlyDictionary<string, long> uploaded, CancellationToken ct)
    {
        await importer.RefreshListingAsync(listingId, ct, raiseDriftEvents: false);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Listings.Include(l => l.EtsyState).FirstAsync(l => l.Id == listingId, ct);
        var newEtsy = JsonNode.Parse(row.EtsyState!.ManagedJson)!.AsObject();
        var after = ListingEditModel.Parse(row.EtsyState.ManagedJson, row.EtsyState.BaseJson, row.DraftJson);
        // Photos uploaded just now have their Etsy id; the draft still refers to them by file.
        if (uploaded.Count > 0 && after.Draft[ListingFields.Images] is JsonArray draftImages)
            after.Draft[ListingFields.Images] = PhotoEditor.AdoptUploads(draftImages, uploaded);
        // New variation values got their ids from Etsy just now; the draft has none for them yet.
        if (after.Draft[ListingFields.Inventory] is JsonObject draftInventory && newEtsy[ListingFields.Inventory] is { } etsyInventory)
            after.Draft[ListingFields.Inventory] = VariationEditor.AdoptValueIds(draftInventory, etsyInventory);
        var notApplied = after.AcceptPushed(newEtsy);
        ListingMapper.Apply(row, after);
        var push = await db.ListingPushes.FirstAsync(p => p.Id == pushId, ct);
        push.SnapshotId = snapshotId;
        await db.SaveChangesAsync(ct);
        // Uploaded photos come back as new image rows without files: fetch them (and their thumbnails) now, as an import does.
        if (uploaded.Count > 0) await importQueue.DownloadImagesAsync(row.ShopId, "push", $"push:{pushId}", ct);
        return notApplied;
    }

    /// <summary>
    /// Sends the photo calls (see <see cref="PhotoEditor.Plan"/>), one at a time, planned from a fresh read of Etsy's photos.
    /// A rejection throws, so nothing after it is sent. Moves never use overwrite (it replaces the photo at that position).
    /// </summary>
    private async Task PushPhotosAsync(Listing listing, JsonArray local, EtsyAuth auth, Dictionary<string, long> uploaded,
        Action markWrote, CancellationToken ct)
    {
        var current = (await etsy.GetListingImagesAsync(listing.EtsyListingId, auth, ct)).OrderBy(i => i.Rank)
            .Select(i => (i.ListingImageId, i.Rank, ListingMapper.Text(i.AltText))).ToList();
        var shopId = listing.Shop.EtsyShopId;
        var newIds = new Dictionary<long, long>(); // the plan's placeholder id of a new photo -> Etsy's id
        long Id(long planned) => planned > 0 ? planned : newIds[planned];
        string? Alt(string? alt) => string.IsNullOrEmpty(alt) ? null : alt;

        foreach (var call in PhotoEditor.Plan(current, local))
        {
            logger.LogInformation("Photos for listing {ListingId}: {Call}", listing.EtsyListingId, call.Describe(shopId, listing.EtsyListingId));
            switch (call.Kind)
            {
                case PhotoCallKind.Delete:
                    await etsy.DeleteListingImageAsync(shopId, listing.EtsyListingId, call.ImageId, auth, ct);
                    break;
                case PhotoCallKind.Upload:
                    var bytes = await File.ReadAllBytesAsync(images.PathFor(call.FileName!), ct);
                    var created = await etsy.UploadListingImageAsync(shopId, listing.EtsyListingId, bytes, call.FileName!, call.Rank,
                        Alt(call.AltText), auth, ct);
                    newIds[call.ImageId] = created.ListingImageId;
                    uploaded[call.FileName!] = created.ListingImageId;
                    break;
                default: // Rank and AltText: give the photo its rank, overwrite=false (overwrite would drop the photo in that slot)
                    await etsy.RepositionListingImageAsync(shopId, listing.EtsyListingId, Id(call.ImageId), call.Rank, overwrite: false,
                        Alt(call.AltText), auth, ct);
                    break;
            }
            markWrote();
        }
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
                case ListingFields.Inventory or ListingFields.Images or ListingFields.Attributes or ListingFields.Personalization:
                    continue; // written through their own calls
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
