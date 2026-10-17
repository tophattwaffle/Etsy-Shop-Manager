using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Modules.Listings;
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
    IJobQueue jobs,
    IAuditLog audit,
    TimeProvider time)
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
        }, ct);

    /// <summary>Drops local edits: one field, or all when <paramref name="key"/> is null.</summary>
    public Task DiscardAsync(int listingId, string? key, string? user, CancellationToken ct = default) =>
        UpdateAsync(listingId, user, "listing.edits-discarded", model =>
        {
            foreach (var k in key is null ? model.Draft.Select(p => p.Key).ToList() : [key]) model.Discard(k);
            return key is null ? "all edits" : Label(key);
        }, ct);

    public Task ResolveDriftAsync(int listingId, string key, DriftResolution resolution, string? user, CancellationToken ct = default) =>
        UpdateAsync(listingId, user, "listing.drift-resolved", model =>
        {
            if (resolution == DriftResolution.KeepMine) model.KeepMine(key);
            else model.TakeEtsy(key);
            return $"{Label(key)}: {(resolution == DriftResolution.KeepMine ? "kept local" : "took Etsy's")}";
        }, ct);

    private async Task UpdateAsync(int listingId, string? user, string auditAction, Func<ListingEditModel, string> change,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listing = await db.Listings.Include(l => l.EtsyState).FirstOrDefaultAsync(l => l.Id == listingId, ct)
                      ?? throw new InvalidOperationException($"Listing {listingId} not found.");
        var model = Model(listing);
        var details = change(model);
        ListingMapper.Apply(listing, model);
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
        return Preview(listing, Model(listing), busy);
    }

    /// <summary>The preview for a model (pure, for tests and the UI).</summary>
    public static PushPreview Preview(Listing listing, ListingEditModel model, bool pushInProgress)
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
        if (changes.Any(c => c.Key == "title") && ((string?)local["title"] ?? "").Trim() is var title)
        {
            if (title.Length == 0) blockers.Add("The title can't be empty.");
            if (title.Length > MaxTitleLength) blockers.Add($"The title is {title.Length} characters; Etsy allows {MaxTitleLength}.");
        }
        if (changes.Any(c => c.Key == "tags") && local["tags"] is JsonArray tags)
        {
            if (tags.Count > MaxTags) blockers.Add($"{tags.Count} tags; Etsy allows {MaxTags}.");
            foreach (var tag in tags.Select(t => (string?)t ?? "").Where(t => t.Length > MaxTagLength))
                blockers.Add($"The tag \"{tag}\" is longer than {MaxTagLength} characters.");
        }
        if (changes.Any(c => c.Key == "materials") && local["materials"] is JsonArray materials && materials.Count > MaxMaterials)
            blockers.Add($"{materials.Count} materials; Etsy allows {MaxMaterials}.");
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
            foreach (var field in ListingFields.All.Where(f => f.Editable && old.ContainsKey(f.Key)))
                model.Set(field.Key, old[field.Key]);
            return $"snapshot {snapshotId} from {snapshot.TakenAtUtc.ToString("u", CultureInfo.InvariantCulture)} loaded into the draft";
        }, ct);
    }

    public static string Label(string key) => ListingFields.Find(key)?.Label ?? key;
}

public sealed record PushPayload(int PushId);
