using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Editor;

public sealed record FieldChange(string Key, string Label, JsonNode? From, JsonNode? To);

/// <summary>What a push would do. Nothing is sent while <see cref="Blockers"/> is non-empty.</summary>
public sealed record PushPreview(IReadOnlyList<FieldChange> Changes, IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings)
{
    public bool CanPush => Changes.Count > 0 && Blockers.Count == 0;
}

public enum DriftResolution { KeepMine, TakeEtsy }

public sealed record EditorSnapshot(Listing Listing, ListingEditModel Model, IReadOnlyList<ListingPush> RecentPushes);

/// <summary>
/// Local editing of listings: drafts, drift resolution, the push preview, and requesting pushes (which run as jobs).
/// Nothing here calls Etsy; <see cref="PushListingJob"/> does the writing.
/// </summary>
public sealed class ListingEditor(
    IDbContextFactory<AppDbContext> dbFactory,
    ImageStore images,
    IJobQueue jobs,
    IAuditLog audit,
    TimeProvider time,
    Infrastructure.Storage.VideoStore? videoStore = null)
{
    public const int MaxTitleLength = 140;
    public const int MaxTags = 13;
    public const int MaxTagLength = 20;
    public const int MaxMaterials = 13;

    public async Task<EditorSnapshot?> LoadAsync(int listingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listing = await db.Listings.AsNoTracking()
            .Include(l => l.Shop).ThenInclude(s => s.Token)
            .Include(l => l.EtsyState)
            .Include(l => l.Products).ThenInclude(p => p.Offerings)
            .Include(l => l.Images.OrderBy(i => i.Rank))
            .Include(l => l.Videos)
            .AsSplitQuery()
            .FirstOrDefaultAsync(l => l.Id == listingId, ct);
        if (listing?.EtsyState is null) return null;
        var pushes = await db.ListingPushes.AsNoTracking().Where(p => p.ListingId == listingId)
            .OrderByDescending(p => p.Id).Take(20).ToListAsync(ct);
        return new EditorSnapshot(listing, Model(listing), pushes);
    }

    private static ListingEditModel Model(Listing l) =>
        ListingEditModel.Parse(l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson);

    /// <summary>Saves local edits (field key → new value). A value equal to the agreed one removes that edit.</summary>
    public Task SaveAsync(int listingId, IReadOnlyDictionary<string, JsonNode?> changes, string? user, CancellationToken ct = default) =>
        UpdateAsync(listingId, user, "listing.edited", model =>
        {
            foreach (var (key, value) in changes) model.Set(key, value);
            return string.Join(", ", changes.Keys.Select(Label));
        }, ct, descriptionExplicitlyChanged: changes.ContainsKey("description"));

    /// <summary>Drops local edits: one field, or all when <paramref name="key"/> is null.</summary>
    public Task DiscardAsync(int listingId, string? key, string? user, CancellationToken ct = default) =>
        UpdateAsync(listingId, user, "listing.edits-discarded", model =>
        {
            foreach (var k in key is null ? model.Draft.Select(p => p.Key).ToList() : [key]) model.Discard(k);
            return key is null ? "all edits" : Label(key);
        }, ct, descriptionExplicitlyChanged: key is null || key == "description");

    public Task ResolveDriftAsync(int listingId, string key, DriftResolution resolution, string? user, CancellationToken ct = default) =>
        UpdateAsync(listingId, user, "listing.drift-resolved", model =>
        {
            if (resolution == DriftResolution.KeepMine) model.KeepMine(key);
            else model.TakeEtsy(key);
            return $"{Label(key)}: {(resolution == DriftResolution.KeepMine ? "kept local" : "took Etsy's")}";
        }, ct, descriptionExplicitlyChanged: key == "description");

    private async Task UpdateAsync(int listingId, string? user, string auditAction, Func<ListingEditModel, string> change,
        CancellationToken ct, bool descriptionExplicitlyChanged = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listing = await db.Listings.Include(l => l.Shop).Include(l => l.EtsyState).FirstOrDefaultAsync(l => l.Id == listingId, ct)
                      ?? throw new InvalidOperationException($"Listing {listingId} not found.");
        var model = Model(listing);
        var details = change(model);
        ListingMapper.Apply(listing, model);

        if (!descriptionExplicitlyChanged)
        {
            var descRow = await db.ListingDescriptions.FirstOrDefaultAsync(d => d.ListingId == listingId, ct);
            if (descRow is not null && PlaceholderEngine.HasPlaceholders(descRow.Source))
            {
                var blocks = await db.TextBlocks.AsNoTracking().ToDictionaryAsync(b => b.Slug, b => b.Text, ct);
                var context = PlaceholderEngine.CreateContext(listing, model, blocks);
                var (newRendered, _) = PlaceholderEngine.Render(descRow.Source, context);
                if (newRendered != descRow.Rendered)
                {
                    descRow.Rendered = newRendered;
                    descRow.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
                    model.Set("description", newRendered);
                    ListingMapper.Apply(listing, model);
                }
            }
        }

        listing.DraftUpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        listing.DraftUpdatedBy = user;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(auditAction, nameof(Listing), listing.EtsyListingId.ToString(), details, user, ct);
    }

    public async Task<PushPreview> PreviewAsync(int listingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listing = await db.Listings.AsNoTracking()
            .Include(l => l.Shop).ThenInclude(s => s.Token)
            .Include(l => l.EtsyState)
            .Include(l => l.Products).ThenInclude(p => p.Offerings).AsSplitQuery() // variation names and stock for the preview
            .FirstOrDefaultAsync(l => l.Id == listingId, ct)
            ?? throw new InvalidOperationException($"Listing {listingId} not found.");
        // Only while its job is still waiting or running: a job cancelled on the Jobs page, or one that timed out,
        // leaves its push unfinished and must not block the listing for good.
        var busy = await db.ListingPushes.AnyAsync(p => p.ListingId == listingId
            && (p.Status == ListingPushStatus.Pending || p.Status == ListingPushStatus.Running)
            && db.Jobs.Any(j => j.Id == p.JobId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running)), ct);
        var model = Model(listing);
        var properties = await AttributeEditor.LoadAsync(db, (long?)model.Local("taxonomy_id"), ct);
        var preview = Preview(listing, model, busy, file => images.Exists(file) || (videoStore?.Exists(file) ?? false), properties);
        if (!model.PendingFields.Contains(ListingFields.Images)) return preview;
        // A linked photo template's photo leaves the listing only by unlinking it.
        var templateBlockers = PhotoTemplateService.RemovalBlockers(model.Etsy[ListingFields.Images] as JsonArray ?? [],
            model.Local(ListingFields.Images) as JsonArray ?? [], await PhotoTemplateService.ListingLinksAsync(db, listingId, ct)).ToList();
        return templateBlockers.Count == 0 ? preview : preview with { Blockers = [.. preview.Blockers, .. templateBlockers] };
    }

    /// <summary>The preview for a model (pure, for tests and the UI).</summary>
    /// <param name="fileExists">Whether a photo added in the app is still in the image store (unchecked when null).</param>
    /// <param name="attributeProperties">The local category's attribute properties (null when they aren't in the app).</param>
    public static PushPreview Preview(Listing listing, ListingEditModel model, bool pushInProgress, Func<string, bool>? fileExists = null,
        IReadOnlyList<AttributeProperty>? attributeProperties = null)
    {
        var changes = model.PendingFields
            .Select(k => new FieldChange(k, Label(k), model.Etsy[k]?.DeepClone(), model.Local(k)?.DeepClone()))
            .ToList();
        var blockers = new List<string>();
        var warnings = new List<string>();

        var drift = model.DriftFields;
        if (drift.Count > 0)
            blockers.Add($"Changed on Etsy since the last sync: {string.Join(", ", drift.Select(Label))}. Choose keep mine or take Etsy's first.");
        if (listing.IsMissingOnEtsy) blockers.Add("This listing is no longer on Etsy.");
        if (NeedsRefresh(listing))
            blockers.Add("This listing was read from Etsy by an older version of the app. Refresh it from Etsy first (Import now).");
        if (listing.Shop is { } shop && (shop.Token is null || shop.Token.NeedsReauth || !shop.IsActive))
            blockers.Add($"{shop.Name} must be reconnected to Etsy first.");
        if (pushInProgress) blockers.Add("A push for this listing is already queued or running.");

        var local = model.LocalState();
        if (changes.Any(c => c.Key == "description"))
        {
            var desc = (string?)local["description"] ?? "";
            var allPlaceholders = System.Text.RegularExpressions.Regex.Matches(desc, @"\{\{\s*([a-zA-Z0-9_.-]+)\s*\}\}");
            var textMatches = allPlaceholders.Where(m => m.Groups[1].Value.StartsWith("text.", StringComparison.OrdinalIgnoreCase)).ToList();
            if (textMatches.Count > 0)
                blockers.Add($"The description has {string.Join(", ", textMatches.Select(m => m.Value).Distinct())}, which isn't a text template. " +
                             "Fix the placeholder or create the template.");

            var unresolvedMatches = allPlaceholders.Where(m => !m.Groups[1].Value.StartsWith("text.", StringComparison.OrdinalIgnoreCase)).ToList();
            if (unresolvedMatches.Count > 0)
                blockers.Add($"The description has unresolved placeholder(s): {string.Join(", ", unresolvedMatches.Select(m => m.Value).Distinct())}. " +
                             "Fix the placeholder or verify the field exists.");
        }
        if (changes.Any(c => c.Key == "title"))
            blockers.AddRange(ListingTextRules.TitleProblems((string?)local["title"] ?? ""));
        if (changes.Any(c => c.Key == "tags") && local["tags"] is JsonArray tags)
        {
            if (tags.Count > MaxTags) blockers.Add($"{tags.Count} tags; Etsy allows {MaxTags}.");
            foreach (var tag in tags.Select(t => (string?)t ?? "").Where(t => t.Length > MaxTagLength))
                blockers.Add($"The tag \"{tag}\" is longer than {MaxTagLength} characters.");
            blockers.AddRange(ListingTextRules.TagProblems(tags.Select(t => (string?)t ?? "")));
        }
        if (changes.Any(c => c.Key == "materials") && local["materials"] is JsonArray materials)
        {
            if (materials.Count > MaxMaterials) blockers.Add($"{materials.Count} materials; Etsy allows {MaxMaterials}.");
            blockers.AddRange(ListingTextRules.MaterialProblems(materials.Select(m => (string?)m ?? "")));
        }
        if (changes.Any(c => c.Key == ListingFields.Images) && local[ListingFields.Images] is JsonArray photos)
        {
            if (photos.Count == 0) blockers.Add("A listing needs at least one photo.");
            if (photos.Count > PhotoEditor.MaxPhotos) blockers.Add($"{photos.Count} photos; Etsy allows {PhotoEditor.MaxPhotos}.");
            foreach (var (p, i) in photos.Select((p, i) => (p!, i + 1)))
            {
                if (((string?)p["alt_text"] ?? "").Length > PhotoEditor.MaxAltTextLength)
                    blockers.Add($"Photo {i}'s alt text is longer than {PhotoEditor.MaxAltTextLength} characters.");
                if ((string?)p["file_name"] is { } file && fileExists is not null && !fileExists(file))
                    blockers.Add($"Photo {i} was added here but its file is gone; remove it and add it again.");
            }
            if (photos.Select(p => (long?)p!["etsy_image_id"] ?? 0).Where(id => id > 0).GroupBy(id => id).Any(g => g.Count() > 1))
                blockers.Add("The same photo appears twice.");
            // The exact calls, so the person sees what goes to Etsy.
            var etsyPhotos = PhotoEditor.FromManaged(model.Etsy[ListingFields.Images]);
            foreach (var (rank, ids) in PhotoEditor.Ties(etsyPhotos))
                warnings.Add($"On Etsy, photos {string.Join(" and ", ids.Select(id => $"#{id}"))} share position {rank}, so Etsy shows them " +
                             "in no fixed order; this push gives every photo its own position.");
            var calls = PhotoEditor.Plan(etsyPhotos, photos);
            warnings.Add($"Photos: {calls.Count} call{(calls.Count == 1 ? "" : "s")} to Etsy, one at a time, stopping at the first one Etsy rejects. " +
                         "Etsy doesn't renumber the other photos when one moves, so every photo not already in place gets its position.");
            warnings.AddRange(calls.Select(c => c.Describe(listing.Shop?.EtsyShopId ?? 0, listing.EtsyListingId)));
        }
        var shopId = listing.Shop?.EtsyShopId ?? 0;
        if (changes.Any(c => c.Key == ListingFields.Videos))
        {
            var videos = VideoPlan.Items(local[ListingFields.Videos]);
            if (videos.Count > VideoPlan.MaxVideos)
                blockers.Add($"Etsy allows {VideoPlan.MaxVideos} videos per listing; remove {videos.Count - VideoPlan.MaxVideos}.");
            if (fileExists is not null && videos.Any(v => VideoPlan.FileName(v) is { } f && VideoPlan.EtsyId(v) is null && !fileExists(f)))
                blockers.Add("The video file was added here but its file is gone; remove it and add it again.");

            var (deletes, uploads) = VideoPlan.Plan(model.Etsy[ListingFields.Videos], local[ListingFields.Videos]);
            var count = deletes.Count + uploads.Count;
            var path = $"/v3/application/shops/{shopId}/listings/{listing.EtsyListingId}/videos";
            warnings.Add($"Videos: {count} call{(count == 1 ? "" : "s")} to Etsy, removals first. Etsy adds new videos after the ones it keeps.");
            warnings.AddRange(deletes.Select(id => $"DELETE {path}/{id}  (take video #{id} off the listing)"));
            warnings.AddRange(uploads.Select(v => $"POST {path}  video={(string?)v["name"] ?? VideoPlan.FileName(v)}  (add the new video)"));
        }
        if (changes.Any(c => c.Key == ListingFields.Attributes))
        {
            string Name(long id) => attributeProperties?.FirstOrDefault(p => p.PropertyId == id)?.Label
                                    ?? listing.Attributes.FirstOrDefault(a => a.PropertyId == id)?.PropertyName ?? $"Property {id}";
            blockers.AddRange(AttributeEditor.Problems(model.Etsy[ListingFields.Attributes], local[ListingFields.Attributes], attributeProperties, Name));
            warnings.AddRange(AttributeEditor.Warnings(local[ListingFields.Attributes], attributeProperties,
                changes.Any(c => c.Key == "taxonomy_id"), Name));
            var calls = AttributeEditor.Plan(model.Etsy[ListingFields.Attributes], local[ListingFields.Attributes]);
            warnings.Add($"Category attributes: {calls.Count} call{(calls.Count == 1 ? "" : "s")} to Etsy, one per attribute changed, " +
                         "after the listing fields (so a new category is in place first).");
            warnings.AddRange(calls.Select(c => c.Describe(shopId, listing.EtsyListingId, Name(c.PropertyId))));
        }
        if (changes.Any(c => c.Key == ListingFields.Personalization))
        {
            blockers.AddRange(PersonalizationEditor.Problems(local[ListingFields.Personalization]));
            if (PersonalizationEditor.Call(model.Etsy[ListingFields.Personalization], local[ListingFields.Personalization], shopId,
                    listing.EtsyListingId) is { } call)
                warnings.Add($"Personalization: 1 call to Etsy, which replaces all of the listing's questions. {call}");
        }
        if (changes.Any(c => c.Key == ListingFields.Inventory) && local[ListingFields.Inventory] is { } inventory)
        {
            blockers.AddRange(InventoryProblems(inventory, InventoryRules.Names(listing)));
            if (model.Etsy[ListingFields.Inventory] is { } etsyInventory)
            {
                warnings.AddRange(VariationEditor.Describe(etsyInventory, inventory));
                var existing = (etsyInventory["products"]?.AsArray() ?? []).Select(InventoryWriter.ProductKey).ToHashSet();
                var added = (inventory["products"]?.AsArray() ?? []).Count(p => !existing.Contains(InventoryWriter.ProductKey(p)));
                if (added > 0)
                    warnings.Add($"{added} new combination{(added == 1 ? "" : "s")} start{(added == 1 ? "s" : "")} with a stock of " +
                                 $"{NewCombinationStock(listing)} (the listing's current stock; auto-refill keeps it topped up if it's on).");
            }
        }

        if (changes.Any(c => c.Key == ListingFields.State) && (string?)local[ListingFields.State] == "active"
            && listing.State is "inactive" or "expired" or "draft")
        {
            warnings.Add(listing.State == "draft"
                ? "Publishing a draft charges Etsy's listing fee."
                : $"Activating a{(listing.State == "expired" ? "n expired" : "n inactive")} listing may charge Etsy's renewal fee.");
        }
        if (changes.Any(c => c.Key == ListingFields.State) && (string?)local[ListingFields.State] is { } s && s is not ("active" or "inactive"))
            blockers.Add("The status can only be set to active or inactive.");

        return new PushPreview(changes, blockers, warnings);
    }

    /// <summary>What a new combination starts with, from the last import (the push uses a fresh read; see InventoryWriter).</summary>
    public static int NewCombinationStock(Listing listing)
    {
        var enabled = listing.Products.Where(p => !p.IsDeleted)
            .SelectMany(p => p.Offerings.Where(o => !o.IsDeleted && o.IsEnabled)).Select(o => o.Quantity).ToList();
        if (enabled.Count == 0) return Math.Max(1, listing.Quantity);
        return Math.Max(1, listing.QuantityOnProperty.Count == 0 ? enabled.Max() : enabled.Min());
    }

    /// <summary>The stored Etsy state predates the current managed-field definition (e.g. before text was HTML-decoded).</summary>
    public static bool NeedsRefresh(Listing listing) =>
        listing.EtsyState is { } s && s.ManagedVersion != ManagedListingState.CurrentVersion;

    /// <summary>Etsy requires one price (and one processing profile) for all variations unless it varies by a property.</summary>
    private static IEnumerable<string> InventoryProblems(JsonNode? inventory, IReadOnlyDictionary<long, string> names)
    {
        if (inventory is null) yield break;
        var products = (inventory["products"]?.AsArray() ?? []).Select(p => p!).ToList();
        var offerings = products.SelectMany(p => p["offerings"]?.AsArray() ?? []).Select(o => o!).ToList();

        // The variation structure: at most two variations, named, with distinct non-empty values, and every combination
        // of values present exactly once (Etsy's inventory is the full grid).
        var variations = VariationEditor.Variations(inventory);
        if (variations.Count > VariationEditor.MaxVariations)
            yield return $"{variations.Count} variations; Etsy allows {VariationEditor.MaxVariations}.";
        foreach (var v in variations.Where(v => v.IsCustom && string.IsNullOrWhiteSpace(v.Name)))
            yield return "Every variation needs a name.";
        if (variations.Select(v => v.Name?.Trim()).Where(n => n is { Length: > 0 }).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            yield return "Two variations have the same name.";
        foreach (var v in variations)
        {
            var label = v.Name ?? names.GetValueOrDefault(v.PropertyId, $"variation {v.PropertyId}");
            if (v.Values.Any(string.IsNullOrWhiteSpace)) yield return $"{label} has an empty value.";
            foreach (var dup in v.Values.GroupBy(x => x.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                yield return $"{label} has \"{dup.Key}\" more than once.";
            if (v.Values.Count > VariationEditor.MaxValues)
                yield return $"{label} has {v.Values.Count} options; Etsy allows at most {VariationEditor.MaxValues} in a variation. " +
                    "Remove some, or switch them off in its variation templates and apply again.";
        }
        var combinations = variations.Aggregate(1, (n, v) => n * v.Values.Count);
        if (products.Any(p => (p["property_values"]?.AsArray().Count ?? 0) != variations.Count)
            || products.Select(InventoryWriter.ProductKey).Distinct().Count() != products.Count
            || products.Count != combinations)
            yield return "Every combination of the variation values must appear exactly once; discard the variation changes and try again.";
        if (offerings.Count > 0 && offerings.All(o => (bool?)o["is_enabled"] == false))
            yield return "At least one combination must be available to buyers.";

        if (offerings.Any(o => (long?)o["price_amount"] is null or <= 0)) yield return "Every price must be more than zero.";
        // Variations that share the values a price varies by must share the price (all of them when it varies by nothing).
        if (InventoryRules.Groups(inventory, "price_on_property").Any(g => g.Offerings.Select(o => (long?)o["price_amount"]).Distinct().Count() > 1))
            yield return InventoryRules.PropertyNames(inventory, "price_on_property", names) is { Length: > 0 } by
                ? $"Variations with the same {by} must have the same price, because the price varies only by {by}."
                : "All variations must have the same price, because the price doesn't vary by variation on this listing.";
        if (InventoryRules.Groups(inventory, "readiness_state_on_property").Any(g => g.Offerings.Select(o => (long?)o["readiness_state_id"]).Distinct().Count() > 1))
            yield return InventoryRules.PropertyNames(inventory, "readiness_state_on_property", names) is { Length: > 0 } by
                ? $"Variations with the same {by} must have the same processing profile."
                : "All variations must have the same processing profile on this listing.";
    }

    /// <summary>Queues a push of the current local edits. Returns the push id, or the reasons it can't go.</summary>
    public async Task<(int? PushId, IReadOnlyList<string> Blockers)> RequestPushAsync(int listingId, string? user,
        CancellationToken ct = default)
    {
        var preview = await PreviewAsync(listingId, ct);
        if (preview.Changes.Count == 0) return (null, ["Nothing to push: the local version matches Etsy."]);
        if (!preview.CanPush) return (null, preview.Blockers);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listing = await db.Listings.AsNoTracking().FirstAsync(l => l.Id == listingId, ct);
        var push = new ListingPush
        {
            ListingId = listingId,
            Status = ListingPushStatus.Pending,
            RequestedBy = user,
            RequestedAtUtc = time.GetUtcNow().UtcDateTime,
            ChangesJson = JsonSerializer.Serialize(preview.Changes.Select(c => new { key = c.Key, label = c.Label, from = c.From, to = c.To })),
        };
        db.ListingPushes.Add(push);
        await db.SaveChangesAsync(ct);

        push.JobId = await jobs.EnqueueAsync(new JobRequest
        {
            JobType = PushListingJob.Type,
            ShopId = listing.ShopId,
            Payload = new PushPayload(push.Id),
            DedupeKey = $"{PushListingJob.Type}:{listingId}",
            Priority = 20, // a person is waiting for it
            MaxAttempts = 1, // writes are not retried blindly; the person can push again after reading the error
            CreatedBy = user,
        }, ct);
        if (push.JobId is null)
        {
            push.Status = ListingPushStatus.Failed;
            push.Error = "A push for this listing is already queued.";
            await db.SaveChangesAsync(ct);
            return (null, [push.Error]);
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("listing.push-requested", nameof(Listing), listing.EtsyListingId.ToString(),
            string.Join(", ", preview.Changes.Select(c => c.Label)), user, ct);
        return (push.Id, []);
    }

    /// <summary>
    /// Restores a snapshot into the draft: the editable fields get the values Etsy had before that push. Nothing is
    /// sent; the person reviews and pushes as usual.
    /// </summary>
    public async Task RestoreSnapshotAsync(int snapshotId, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var snapshot = await db.ListingSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == snapshotId, ct)
                       ?? throw new InvalidOperationException($"Snapshot {snapshotId} not found.");
        var old = JsonNode.Parse(snapshot.ManagedJson)!.AsObject();
        await UpdateAsync(snapshot.ListingId, user, "listing.snapshot-restored", model =>
        {
            foreach (var field in ListingFields.All.Where(f => f.Editable && old.ContainsKey(f.Key) && CurrentForm(f.Key, old[f.Key])))
                model.Set(field.Key, old[field.Key]);
            return $"snapshot {snapshotId} from {snapshot.TakenAtUtc.ToString("u", CultureInfo.InvariantCulture)} loaded into the draft";
        }, ct, descriptionExplicitlyChanged: old.ContainsKey("description"));
    }

    /// <summary>
    /// Whether a snapshot's value has today's managed form. Snapshots taken before managed-state version 5 hold
    /// personalization as Etsy's raw questions (or null for none) and attributes with their names; restoring those would
    /// read as "remove every question" or as a change to every attribute, so they're left as they are.
    /// </summary>
    private static bool CurrentForm(string key, JsonNode? value) => key switch
    {
        ListingFields.Personalization => value is JsonArray q && q.All(x => x is JsonObject o && !o.ContainsKey("question_id")),
        ListingFields.Attributes => value is JsonArray a && a.All(x => x is JsonObject o && !o.ContainsKey("property_name")),
        _ => true,
    };

    public static string Label(string key) => ListingFields.Find(key)?.Label ?? key;

    public static string DescribeVideo(JsonNode? videoNode)
    {
        if (videoNode is null) return "No video";
        if ((string?)videoNode["name"] is { Length: > 0 } name) return $"New video ({name})";
        if ((string?)videoNode["file_name"] is { Length: > 0 } file) return $"New video ({file})";
        if (long.TryParse(videoNode["etsy_video_id"]?.ToString(), out var id) && id > 0)
        {
            var state = (string?)videoNode["video_state"];
            return state is { Length: > 0 } ? $"Etsy video #{id} ({state})" : $"Etsy video #{id}";
        }
        return "Video";
    }

    /// <summary>A listing's videos in one line, in order.</summary>
    public static string DescribeVideos(JsonNode? videos) =>
        VideoPlan.Items(videos) is { Count: > 0 } items ? string.Join(" · ", items.Select(DescribeVideo)) : "No video";

    public static string DescribeVideoChange(JsonNode? from, JsonNode? to)
    {
        if (from is null && to is not null)
        {
            var name = (string?)to["name"] ?? (string?)to["file_name"] ?? "new video";
            return $"Add video: {name}";
        }
        if (from is not null && to is null)
        {
            var id = (long?)from["etsy_video_id"];
            return id is > 0 ? $"Remove video #{id}" : "Remove video";
        }
        if (from is not null && to is not null)
        {
            var id = (long?)from["etsy_video_id"];
            var name = (string?)to["name"] ?? (string?)to["file_name"] ?? "new video";
            return id is > 0 ? $"Replace video #{id} with {name}" : $"Replace video with {name}";
        }
        return "No change";
    }
}

public sealed record PushPayload(int PushId);
