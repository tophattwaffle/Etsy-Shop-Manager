using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Models;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Shops;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>
/// The choices for a clone. For the same shop the profiles default to the source's; for the other shop they must be
/// picked (pre-filled with the last choice used for that shop) because profile ids belong to a shop.
/// </summary>
public sealed record CloneSettings(int TargetShopId, string Title, long? ShippingProfileId, long? ReadinessStateId,
    long? ReturnPolicyId, long? ShopSectionId);

public sealed record ClonePlan(IReadOnlyList<string> Calls, IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings)
{
    public bool CanClone => Blockers.Count == 0;
}

public sealed record ClonePayload(int CloneId);

/// <summary>
/// Copies a listing (its local version, including edits not pushed yet) into a new Etsy <b>draft</b>, in the same shop or
/// the other one: listing fields, category attributes, personalization, variations with prices and stock, and photos in
/// order with alt text. Nothing is activated; Etsy charges its listing fee only when the draft is published.
/// </summary>
public sealed class ListingCloner(IDbContextFactory<AppDbContext> dbFactory, ImageStore images, IJobQueue jobs, ISettingsStore settings,
    IAuditLog audit, TimeProvider time, ShopDefaultsService? defaultsService = null, Infrastructure.Storage.VideoStore? videoStore = null)
{
    private readonly ShopDefaultsService _defaults = defaultsService ?? new ShopDefaultsService(settings, audit);

    public static string LastChoiceKey(int shopId) => $"Clone.LastChoice.{shopId}";

    /// <summary>What the clone dialog starts with for a target shop.</summary>
    public async Task<CloneSettings> DefaultsAsync(int sourceListingId, int targetShopId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await db.Listings.AsNoTracking().Include(l => l.EtsyState).FirstAsync(l => l.Id == sourceListingId, ct);
        var local = Local(source);
        if (source.ShopId == targetShopId)
            return new CloneSettings(targetShopId, Title(local), (long?)local["shipping_profile_id"], ListingReadiness(source, local),
                (long?)local["return_policy_id"], (long?)local["shop_section_id"]);

        var defaults = await _defaults.GetAsync(targetShopId, ct);
        var last = await settings.GetAsync(LastChoiceKey(targetShopId), ct) is { } json
            ? JsonSerializer.Deserialize<CloneSettings>(json) : null;
        return new CloneSettings(
            targetShopId,
            Title(local),
            defaults?.ShippingProfileId ?? last?.ShippingProfileId,
            defaults?.ReadinessStateId ?? last?.ReadinessStateId,
            defaults?.ReturnPolicyId ?? last?.ReturnPolicyId,
            defaults?.ShopSectionId ?? last?.ShopSectionId);
    }

    /// <summary>The review: every call the clone makes, what blocks it and what to know.</summary>
    public async Task<ClonePlan> PlanAsync(int sourceListingId, CloneSettings choice, CancellationToken ct = default)
    {
        var (source, target, lookups) = await LoadAsync(sourceListingId, choice.TargetShopId, ct);
        return Plan(source, target, lookups, choice, file => images.Exists(file) || (videoStore?.Exists(file) ?? false));
    }

    public static ClonePlan Plan(Listing source, Shop target, EditorLookups targetLookups, CloneSettings choice, Func<string, bool> fileExists)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        var local = Local(source);
        var sameShop = source.ShopId == target.Id;

        if (ListingEditor.NeedsRefresh(source))
            blockers.Add("This listing was read from Etsy by an older version of the app. Refresh it first (Import now).");
        if (target.Token is null || target.Token.NeedsReauth || !target.IsActive)
            blockers.Add($"{target.Name} must be reconnected to Etsy first.");
        var title = choice.Title.Trim();
        blockers.AddRange(ListingTextRules.TitleProblems(title));
        blockers.AddRange(ListingTextRules.TagProblems((local["tags"]?.AsArray() ?? []).Select(t => (string?)t ?? "")));
        blockers.AddRange(ListingTextRules.MaterialProblems((local["materials"]?.AsArray() ?? []).Select(m => (string?)m ?? "")));
        if (choice.ShippingProfileId is not { } ship || !targetLookups.ShippingProfiles.ContainsKey(ship))
            blockers.Add($"Choose a shipping profile of {target.Name}.");
        if (choice.ReadinessStateId is not { } ready || !targetLookups.ProcessingProfiles.ContainsKey(ready))
            blockers.Add($"Choose a processing profile of {target.Name}.");
        if (choice.ReturnPolicyId is { } ret && !targetLookups.ReturnPolicies.ContainsKey(ret))
            blockers.Add($"The return policy isn't one of {target.Name}'s.");
        if (choice.ShopSectionId is { } sec && !targetLookups.Sections.ContainsKey(sec))
            blockers.Add($"The section isn't one of {target.Name}'s.");

        var photos = Photos(source, local).ToList();
        var missing = photos.Select((p, i) => (p, i + 1)).Where(x => x.p.File is null || !fileExists(x.p.File)).Select(x => x.Item2).ToList();
        if (missing.Count > 0)
            blockers.Add($"{(missing.Count == 1 ? "Photo" : "Photos")} {string.Join(", ", missing)} {(missing.Count == 1 ? "isn't" : "aren't")} " +
                         "downloaded in the app yet; wait for the photo download (or import again) first.");
        if (photos.Count == 0) warnings.Add("The listing has no photos; the draft will have none.");
        blockers.AddRange(PersonalizationEditor.Problems(local[ListingFields.Personalization]));
        if (!sameShop && ((JsonArray?)local["production_partner_ids"])?.Count > 0)
            warnings.Add("Production partners belong to a shop, so they aren't copied to the other shop.");
        warnings.Add("The copy is created as a draft: not visible to buyers, and Etsy charges its listing fee only when you publish it.");

        var shop = target.EtsyShopId;
        var attributes = ((JsonArray?)local["attributes"])?.Count ?? 0;
        var variations = VariationEditor.Variations(local[ListingFields.Inventory] ?? new JsonObject()).Count;
        var calls = new List<string>
        {
            $"POST /v3/application/shops/{shop}/listings  (createDraftListing: \"{title}\", the listing fields, " +
            $"shipping {Name(targetLookups.ShippingProfiles, choice.ShippingProfileId)}, processing {Name(targetLookups.ProcessingProfiles, choice.ReadinessStateId)}" +
            $"{(choice.ReturnPolicyId is null ? "" : $", returns {Name(targetLookups.ReturnPolicies, choice.ReturnPolicyId)}")}" +
            $"{(choice.ShopSectionId is null ? "" : $", section {Name(targetLookups.Sections, choice.ShopSectionId)}")})",
        };
        if (attributes > 0) calls.Add($"PUT /v3/application/shops/{shop}/listings/(new)/properties/…  ×{attributes} (category attributes, one call each)");
        var questions = PersonalizationEditor.FromManaged(local[ListingFields.Personalization]).Count;
        if (questions > 0)
            calls.Add($"POST /v3/application/shops/{shop}/listings/(new)/personalization?supports_multiple_personalization_questions=true  " +
                      $"({questions} personalization question{(questions == 1 ? "" : "s")})");
        if (variations > 0)
        {
            var combinations = local[ListingFields.Inventory]?["products"]?.AsArray().Count ?? 0;
            calls.Add($"PUT /v3/application/listings/(new)/inventory  ({variations} variation{(variations == 1 ? "" : "s")}, {combinations} combinations with their prices and stock)");
        }
        foreach (var (p, i) in photos.Select((p, i) => (p, i + 1)))
            calls.Add($"POST /v3/application/shops/{shop}/listings/(new)/images  image={p.File}, rank={i}, overwrite=false" +
                      (string.IsNullOrEmpty(p.Alt) ? "" : $", alt_text={p.Alt}"));
        if (photos.Count > 1) calls.Add("…then, only if Etsy shows two photos at one position, give each photo its own position (overwrite=false).");
        var videoFile = VideoFile(source, local);
        if (!string.IsNullOrEmpty(videoFile) && fileExists(videoFile))
            calls.Add($"POST /v3/application/shops/{shop}/listings/(new)/videos  video={videoFile}");
        calls.Add("Then the new draft is imported into the app.");
        return new ClonePlan(calls, blockers, warnings);
    }

    /// <summary>Checks the plan and queues the clone. Returns the clone id, or why it can't go.</summary>
    public async Task<(int? CloneId, IReadOnlyList<string> Blockers)> RequestAsync(int sourceListingId, CloneSettings choice, string? user,
        CancellationToken ct = default)
    {
        var plan = await PlanAsync(sourceListingId, choice, ct);
        if (!plan.CanClone) return (null, plan.Blockers);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var clone = new ListingClone
        {
            SourceListingId = sourceListingId,
            TargetShopId = choice.TargetShopId,
            Status = ListingCloneStatus.Pending,
            RequestedBy = user,
            RequestedAtUtc = time.GetUtcNow().UtcDateTime,
            SettingsJson = JsonSerializer.Serialize(choice with { Title = choice.Title.Trim() }),
        };
        db.ListingClones.Add(clone);
        await db.SaveChangesAsync(ct);
        clone.JobId = await jobs.EnqueueAsync(new JobRequest
        {
            JobType = ListingCloneJob.Type,
            ShopId = choice.TargetShopId,
            Payload = new ClonePayload(clone.Id),
            Priority = 20,
            MaxAttempts = 1, // never create a second draft on its own
            CreatedBy = user,
        }, ct);
        await db.SaveChangesAsync(ct);
        var source = await db.Listings.AsNoTracking().Where(l => l.Id == sourceListingId).Select(l => l.EtsyListingId).FirstAsync(ct);
        await audit.WriteAsync("listing.clone-requested", nameof(Listing), source.ToString(CultureInfo.InvariantCulture),
            $"to shop {choice.TargetShopId}: {choice.Title.Trim()}", user, ct);
        // The next clone into this shop starts from these choices.
        await settings.SetAsync(LastChoiceKey(choice.TargetShopId), JsonSerializer.Serialize(choice with { Title = "" }), updatedBy: user, ct: ct);
        return (clone.Id, []);
    }

    internal async Task<(Listing Source, Shop Target, EditorLookups Lookups)> LoadAsync(int sourceListingId, int targetShopId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await db.Listings.AsNoTracking().Include(l => l.EtsyState).Include(l => l.Images).Include(l => l.Videos).Include(l => l.Products)
            .ThenInclude(p => p.Offerings).AsSplitQuery().FirstAsync(l => l.Id == sourceListingId, ct);
        var target = await db.Shops.AsNoTracking().Include(s => s.Token).FirstAsync(s => s.Id == targetShopId, ct);
        var lookups = await EditorLookups.LoadAsync(db, targetShopId, [], target.CurrencyCode, ct);
        return (source, target, lookups);
    }

    /// <summary>The source's local version: what Etsy has with the edits not pushed yet on top.</summary>
    internal static JsonObject Local(Listing source) =>
        ListingEditModel.Parse(source.EtsyState!.ManagedJson, source.EtsyState.BaseJson, source.DraftJson).LocalState();

    /// <summary>The photos to copy, in order, with the image store file each one is in.</summary>
    internal static IEnumerable<(string? File, string Alt)> Photos(Listing source, JsonObject local) =>
        (local[ListingFields.Images]?.AsArray() ?? []).Select(p =>
        {
            var file = (string?)p!["file_name"]
                       ?? source.Images.FirstOrDefault(i => i.EtsyImageId == (long?)p["etsy_image_id"])?.FileName;
            return (file, (string?)p["alt_text"] ?? "");
        });

    internal static string? VideoFile(Listing source, JsonObject local)
    {
        if (local.ContainsKey(ListingFields.Video))
        {
            var node = local[ListingFields.Video];
            if (node is null) return null;
            if ((string?)node["file_name"] is { Length: > 0 } file) return file;
        }
        return source.Videos.FirstOrDefault(v => v.VideoState != "deleted")?.FileName;
    }

    internal static long? ListingReadiness(Listing source, JsonObject local) =>
        source.ReadinessStateId
        ?? local[ListingFields.Inventory]?["products"]?.AsArray().SelectMany(p => p!["offerings"]?.AsArray() ?? [])
            .Select(o => (long?)o!["readiness_state_id"]).FirstOrDefault(r => r is not null);

    private static string Title(JsonObject local) => (string?)local["title"] ?? "";

    private static string Name(IReadOnlyDictionary<long, string> map, long? id) =>
        id is { } v ? map.GetValueOrDefault(v, $"#{v}") : "(not chosen)";
}

/// <summary>Runs a clone (see <see cref="ListingCloner"/>): one call at a time, stopping at the first one Etsy rejects.</summary>
public sealed class ListingCloneJob(IDbContextFactory<AppDbContext> dbFactory, ListingCloner cloner, ListingImporter importer,
    ListingImportQueue importQueue, ImageStore images, EtsyApiClient etsy, IAuditLog audit, TimeProvider time,
    ILogger<ListingCloneJob> logger, Infrastructure.Storage.VideoStore? videoStore = null) : IJobHandler
{
    public const string Type = "etsy.listing.clone";
    public string JobType => Type;
    public string DisplayName => "Clone listing to a new draft";

    /// <summary>The listing fields copied as they are (the rest are chosen in the dialog or need care).</summary>
    private static readonly string[] CopiedFields =
    [
        "description", "who_made", "when_made", "taxonomy_id", "tags", "materials", "is_supply", "is_taxable",
        "should_auto_renew", "item_weight", "item_weight_unit", "item_length", "item_width", "item_height", "item_dimensions_unit",
    ];

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        var cloneId = context.GetPayload<ClonePayload>()?.CloneId ?? throw new JobPermanentFailureException("CloneId is required.");
        ListingClone clone;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            clone = await db.ListingClones.FirstOrDefaultAsync(c => c.Id == cloneId, ct)
                    ?? throw new JobPermanentFailureException($"Clone {cloneId} not found.");
            if (clone.Status != ListingCloneStatus.Pending)
                throw new JobPermanentFailureException($"Clone {cloneId} already ran ({clone.Status}); start a new one from the listing.");
            clone.Status = ListingCloneStatus.Running;
            await db.SaveChangesAsync(ct);
        }

        var log = new List<string>();
        long? newEtsyId = null;
        try
        {
            var choice = JsonSerializer.Deserialize<CloneSettings>(clone.SettingsJson)!;
            var (source, target, lookups) = await cloner.LoadAsync(clone.SourceListingId, clone.TargetShopId, ct);
            var plan = ListingCloner.Plan(source, target, lookups, choice, images.Exists);
            if (!plan.CanClone) throw new JobPermanentFailureException("Not cloned: " + string.Join(" ", plan.Blockers));

            var local = ListingCloner.Local(source);
            var sameShop = source.ShopId == target.Id;
            var auth = EtsyAuth.ForShop(target.Id);
            var shop = target.EtsyShopId;

            ListingDescription? sourceDesc;
            string? targetRendered = null;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                sourceDesc = await db.ListingDescriptions.AsNoTracking().FirstOrDefaultAsync(d => d.ListingId == clone.SourceListingId, ct);
                if (sourceDesc is not null)
                {
                    var textBlocks = await db.TextBlocks.AsNoTracking().ToDictionaryAsync(b => b.Slug, b => b.Text, ct);
                    var targetLocal = local.DeepClone().AsObject();
                    targetLocal["title"] = choice.Title;
                    var targetContext = new PlaceholderContext(target, targetLocal, source.Skus, textBlocks);
                    (targetRendered, _) = PlaceholderEngine.Render(sourceDesc.Source, targetContext);
                }
            }

            // 1. The draft with the listing fields.
            var form = ListingFormMapper.ToForm(local, CopiedFields).ToList();
            if (targetRendered is not null)
            {
                form.RemoveAll(kv => kv.Key == "description");
                form.Add(new("description", targetRendered));
            }
            form.Add(new("title", choice.Title));
            form.Add(new("price", BasePrice(source, local).ToString(CultureInfo.InvariantCulture)));
            form.Add(new("quantity", Math.Max(1, source.Quantity).ToString(CultureInfo.InvariantCulture)));
            form.Add(new("shipping_profile_id", choice.ShippingProfileId!.Value.ToString(CultureInfo.InvariantCulture)));
            form.Add(new("readiness_state_id", choice.ReadinessStateId!.Value.ToString(CultureInfo.InvariantCulture)));
            if (choice.ReturnPolicyId is { } ret) form.Add(new("return_policy_id", ret.ToString(CultureInfo.InvariantCulture)));
            if (choice.ShopSectionId is { } sec) form.Add(new("shop_section_id", sec.ToString(CultureInfo.InvariantCulture)));
            if (((JsonArray?)local["styles"])?.Count > 0) form.Add(new("styles", string.Join(",", local["styles"]!.AsArray().Select(s => (string?)s))));
            if (sameShop && ((JsonArray?)local["production_partner_ids"])?.Count > 0)
                form.Add(new("production_partner_ids", string.Join(",", local["production_partner_ids"]!.AsArray().Select(s => (long)s!))));
            if ((string?)local["listing_type"] is { Length: > 0 } type) form.Add(new("type", type));
            var created = await etsy.CreateDraftListingAsync(shop, form, auth, ct);
            newEtsyId = created.Value.ListingId;
            log.Add($"Created draft {newEtsyId} (\"{choice.Title}\").");
            await SaveAsync(cloneId, log, newEtsyId, null);

            // 2. Category attributes.
            foreach (var a in local["attributes"]?.AsArray() ?? [])
            {
                var propertyId = (long)a!["property_id"]!;
                await etsy.UpdateListingPropertyAsync(shop, newEtsyId.Value, propertyId,
                    [.. (a["value_ids"]?.AsArray() ?? []).Select(v => (long)v!)], [.. (a["values"]?.AsArray() ?? []).Select(v => (string)v!)],
                    (long?)a["scale_id"], auth, ct);
                log.Add($"Set attribute {propertyId.ToString(CultureInfo.InvariantCulture)} ({string.Join(", ", (a["values"]?.AsArray() ?? []).Select(v => (string?)v))}).");
            }

            // 2b. Personalization (new questions: no ids; they're the source's questions, not the draft's).
            if (local[ListingFields.Personalization] is JsonArray { Count: > 0 } questions)
            {
                await etsy.UpdateListingPersonalizationAsync(shop, newEtsyId.Value, PersonalizationEditor.Body(questions, null), auth, ct);
                log.Add($"Set {questions.Count} personalization question{(questions.Count == 1 ? "" : "s")}.");
            }

            // 3. Variations, prices and stock (from a fresh read of the source, so stock and order are Etsy's).
            if (VariationEditor.Variations(local[ListingFields.Inventory] ?? new JsonObject()).Count > 0)
            {
                var sourceInventory = await etsy.GetListingInventoryAsync(source.EtsyListingId, EtsyAuth.ForShop(source.ShopId), ct);
                var body = InventoryWriter.FromLocal(sourceInventory, local[ListingFields.Inventory]!);
                if (!sameShop)
                {
                    // Processing profiles belong to a shop: every combination gets the one chosen for the other shop.
                    body.ReadinessStateOnProperty = [];
                    foreach (var o in body.Products.SelectMany(p => p.Offerings)) o.ReadinessStateId = choice.ReadinessStateId;
                }
                await etsy.UpdateListingInventoryAsync(newEtsyId.Value, body, auth, ct);
                log.Add($"Set {body.Products.Count} combinations with prices and stock.");
            }

            // 4. Photos in order, then unique positions if Etsy shows a tie (it never renumbers on its own).
            var photos = ListingCloner.Photos(source, local).ToList();
            var uploaded = new JsonArray();
            foreach (var (p, i) in photos.Select((p, i) => (p, i + 1)))
            {
                var bytes = await File.ReadAllBytesAsync(images.PathFor(p.File!), ct);
                var image = await etsy.UploadListingImageAsync(shop, newEtsyId.Value, bytes, p.File!, i, string.IsNullOrEmpty(p.Alt) ? null : p.Alt, auth, ct);
                uploaded.Add(new JsonObject { ["etsy_image_id"] = image.ListingImageId, ["rank"] = i, ["alt_text"] = p.Alt });
                log.Add($"Uploaded photo {i}.");
            }
            if (photos.Count > 1)
            {
                var now = (await etsy.GetListingImagesAsync(newEtsyId.Value, auth, ct))
                    .Select(i => (i.ListingImageId, i.Rank, ListingMapper.Text(i.AltText))).ToList();
                foreach (var call in PhotoEditor.Plan(now, uploaded).Where(c => c.Kind == PhotoCallKind.Rank))
                {
                    await etsy.RepositionListingImageAsync(shop, newEtsyId.Value, call.ImageId, call.Rank, overwrite: false,
                        string.IsNullOrEmpty(call.AltText) ? null : call.AltText, auth, ct);
                    log.Add($"Gave photo #{call.ImageId} position {call.Rank}.");
                }
            }

            // 4b. Video (if source listing has a video and file exists locally, upload to target listing)
            var videoFile = ListingCloner.VideoFile(source, local);
            if (!string.IsNullOrEmpty(videoFile) && videoStore is not null && videoStore.Exists(videoFile))
            {
                var videoBytes = await File.ReadAllBytesAsync(videoStore.PathFor(videoFile), ct);
                await etsy.UploadListingVideoAsync(shop, newEtsyId.Value, videoBytes, videoFile, auth, ct);
                log.Add($"Uploaded video ({videoFile}).");
            }

            // 5. Into the app.
            var newId = await importer.ImportNewListingAsync(target.Id, newEtsyId.Value, ct);
            log.Add("Imported the new draft into the app.");
            if (sourceDesc is not null && targetRendered is not null)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var newDesc = new ListingDescription
                {
                    ListingId = newId,
                    Source = sourceDesc.Source,
                    Rendered = targetRendered,
                    UpdatedAtUtc = time.GetUtcNow().UtcDateTime,
                };
                db.ListingDescriptions.Add(newDesc);
                await db.SaveChangesAsync(ct);
            }
            // The draft's photos are new image rows: fetch their files now (as an import does), so the Listings page shows them.
            await importQueue.DownloadImagesAsync(target.Id, "clone", $"job:{context.JobId}", ct);
            await SaveAsync(cloneId, log, newEtsyId, newId, ListingCloneStatus.Succeeded);
            await audit.WriteAsync("listing.cloned", nameof(Listing), source.EtsyListingId.ToString(CultureInfo.InvariantCulture),
                $"to draft {newEtsyId} in shop {target.Name}", "system", ct);
            context.Result = $"draft {newEtsyId} created in {target.Name}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Clone {CloneId} stopped", cloneId);
            log.Add($"Stopped: {ex.Message}");
            var error = newEtsyId is null
                ? ex.Message
                : $"{ex.Message} The draft {newEtsyId} exists on Etsy but is incomplete; finish it on Etsy or delete it there.";
            await SaveAsync(cloneId, log, newEtsyId, null, ListingCloneStatus.Failed, error);
            throw ex is JobPermanentFailureException ? ex : new JobPermanentFailureException(error, ex);
        }
    }

    private static decimal BasePrice(Listing source, JsonObject local)
    {
        // Etsy's listing price is the lowest of its variations' prices (the inventory write sets each one).
        var prices = (local[ListingFields.Inventory]?["products"]?.AsArray() ?? [])
            .SelectMany(p => p!["offerings"]?.AsArray() ?? [])
            .Select(o => (decimal)((long?)o!["price_amount"] ?? 0) / ((int?)o["price_divisor"] is int d && d > 0 ? d : 100))
            .Where(p => p > 0)
            .ToList();
        return prices.Count > 0 ? prices.Min() : (decimal)source.PriceAmount / (source.PriceDivisor > 0 ? source.PriceDivisor : 100);
    }

    private async Task SaveAsync(int cloneId, List<string> log, long? newEtsyId, int? newListingId,
        ListingCloneStatus? status = null, string? error = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        var row = await db.ListingClones.FirstAsync(c => c.Id == cloneId, CancellationToken.None);
        row.Log = string.Join("\n", log);
        row.NewEtsyListingId = newEtsyId;
        if (newListingId is not null) row.NewListingId = newListingId;
        if (status is { } s)
        {
            row.Status = s;
            row.CompletedAtUtc = time.GetUtcNow().UtcDateTime;
            row.Error = error;
        }
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
