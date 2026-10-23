using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Models;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Listings;

public sealed record ProcessingProfileItem(
    int Id,
    int ShopId,
    long ReadinessStateId,
    string ReadinessState,
    int MinProcessingDays,
    int MaxProcessingDays,
    string? DisplayLabel,
    int ListingCount,
    DateTime ImportedAtUtc
);

public class ProcessingProfileInput
{
    public string ReadinessState { get; set; } = "ready_to_ship";
    public int MinProcessingTime { get; set; } = 1;
    public int MaxProcessingTime { get; set; } = 3;
    public string ProcessingTimeUnit { get; set; } = "days";

    public ProcessingProfileInput() { }
    public ProcessingProfileInput(string readinessState, int minProcessingTime, int maxProcessingTime, string processingTimeUnit = "days")
    {
        ReadinessState = readinessState;
        MinProcessingTime = minProcessingTime;
        MaxProcessingTime = maxProcessingTime;
        ProcessingTimeUnit = processingTimeUnit;
    }
}

public sealed class CreateProcessingProfileModel : ProcessingProfileInput
{
    public CreateProcessingProfileModel() { }
    public CreateProcessingProfileModel(string readinessState, int minProcessingTime, int maxProcessingTime, string processingTimeUnit = "days")
        : base(readinessState, minProcessingTime, maxProcessingTime, processingTimeUnit) { }
}

public sealed class UpdateProcessingProfileModel : ProcessingProfileInput
{
    public UpdateProcessingProfileModel() { }
    public UpdateProcessingProfileModel(string readinessState, int minProcessingTime, int maxProcessingTime, string processingTimeUnit = "days")
        : base(readinessState, minProcessingTime, maxProcessingTime, processingTimeUnit) { }
}

public sealed record ProcessingProfileApplyRow(
    int ListingId,
    string Title,
    string ShopName,
    string Before,
    string After,
    string? Skipped,
    bool Changes)
{
    public string? SkippedReason => Skipped;
    public bool WillChange => Changes;
}

public sealed record ProcessingProfileCandidate(
    int ListingId,
    string Title,
    int ShopId,
    string ShopName,
    long EtsyListingId,
    string State,
    bool IsMissingOnEtsy,
    IReadOnlyList<string> Skus,
    IReadOnlyList<string> Tags,
    long? CurrentReadinessStateId,
    string CurrentProfileName,
    bool VariesByVariation = false) : ITemplateListingItem;

/// <summary>
/// Manages Etsy processing profiles (readiness state definitions): listing, creation, updates, and bulk application
/// to listings as unpushed draft inventory edits.
/// </summary>
public sealed class ProcessingProfileService(
    IDbContextFactory<AppDbContext> dbFactory,
    EtsyApiClient etsy,
    ListingEditor editor,
    IAuditLog audit,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<IReadOnlyList<ProcessingProfileItem>> ListAsync(int shopId, CancellationToken ct = default) =>
        ListAsync((int?)shopId, ct);

    public async Task<IReadOnlyList<ProcessingProfileItem>> ListAsync(int? shopId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.ProcessingProfiles.AsNoTracking();
        if (shopId.HasValue)
            query = query.Where(p => p.ShopId == shopId.Value);

        var profiles = await query
            .OrderBy(p => p.MinProcessingDays)
            .ThenBy(p => p.MaxProcessingDays)
            .ToListAsync(ct);

        // Deduplicated listing usage: count a listing at most once per profile it uses
        var directQuery = db.Listings.AsNoTracking()
            .Where(l => !l.IsMissingOnEtsy && l.ReadinessStateId != null);
        if (shopId.HasValue)
            directQuery = directQuery.Where(l => l.ShopId == shopId.Value);

        var directListings = await directQuery
            .Select(l => new { ListingId = l.Id, ReadinessStateId = l.ReadinessStateId!.Value })
            .ToListAsync(ct);

        var offeringQuery = db.ListingOfferings.AsNoTracking()
            .Where(o => !o.Product.Listing.IsMissingOnEtsy && o.ReadinessStateId != null);
        if (shopId.HasValue)
            offeringQuery = offeringQuery.Where(o => o.Product.Listing.ShopId == shopId.Value);

        var offeringListings = await offeringQuery
            .Select(o => new { ListingId = o.Product.ListingId, ReadinessStateId = o.ReadinessStateId!.Value })
            .ToListAsync(ct);

        var usageCounts = directListings.Select(x => (x.ListingId, x.ReadinessStateId))
            .Concat(offeringListings.Select(x => (x.ListingId, x.ReadinessStateId)))
            .Distinct()
            .GroupBy(x => x.ReadinessStateId)
            .ToDictionary(g => g.Key, g => g.Count());

        return profiles.Select(p => new ProcessingProfileItem(
            p.Id,
            p.ShopId,
            p.ReadinessStateId,
            p.ReadinessState,
            p.MinProcessingDays,
            p.MaxProcessingDays,
            p.DisplayLabel,
            usageCounts.GetValueOrDefault(p.ReadinessStateId, 0),
            p.ImportedAtUtc
        )).ToList();
    }

    public Task<IReadOnlyList<ProcessingProfileItem>> GetProfilesAsync(int shopId, CancellationToken ct = default) =>
        ListAsync(shopId, ct);

    public async Task<ProcessingProfile> CreateAsync(int shopId, ProcessingProfileInput model, string? user, CancellationToken ct = default)
    {
        ValidateInput(model);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shop = await db.Shops.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shopId, ct)
            ?? throw new KeyNotFoundException($"Shop {shopId} not found.");

        var fields = new List<KeyValuePair<string, string>>
        {
            new("readiness_state", model.ReadinessState),
            new("min_processing_time", model.MinProcessingTime.ToString(CultureInfo.InvariantCulture)),
            new("max_processing_time", model.MaxProcessingTime.ToString(CultureInfo.InvariantCulture)),
            new("processing_time_unit", model.ProcessingTimeUnit),
        };

        var auth = EtsyAuth.ForShop(shopId);
        var raw = await etsy.CreateProcessingProfileAsync(shop.EtsyShopId, fields, auth, ct);
        var created = raw.Value;
        var now = _time.GetUtcNow().UtcDateTime;

        var entity = await db.ProcessingProfiles
            .FirstOrDefaultAsync(p => p.ShopId == shopId && p.ReadinessStateId == created.ReadinessStateId, ct);
        if (entity is null)
        {
            entity = new ProcessingProfile
            {
                ShopId = shopId,
                ReadinessStateId = created.ReadinessStateId,
            };
            db.ProcessingProfiles.Add(entity);
        }

        entity.ReadinessState = created.ReadinessState ?? model.ReadinessState;
        entity.MinProcessingDays = created.MinProcessingDays;
        entity.MaxProcessingDays = created.MaxProcessingDays;
        entity.DisplayLabel = created.ProcessingDaysDisplayLabel;
        entity.RawJson = raw.Json;
        entity.ImportedAtUtc = now;

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(
            action: "processing-profile.created",
            entityType: "processing-profile",
            entityId: created.ReadinessStateId.ToString(CultureInfo.InvariantCulture),
            details: $"Created processing profile '{entity.DisplayLabel ?? entity.ReadinessState}' ({created.ReadinessStateId}) for shop {shop.Name}",
            userName: user,
            ct: ct);

        return entity;
    }

    public Task<ProcessingProfile> CreateProfileAsync(int shopId, ProcessingProfileInput model, string? user, CancellationToken ct = default) =>
        CreateAsync(shopId, model, user, ct);

    public async Task<ProcessingProfile> UpdateAsync(int shopId, long readinessStateId, ProcessingProfileInput model, string? user, CancellationToken ct = default)
    {
        ValidateInput(model);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shop = await db.Shops.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shopId, ct)
            ?? throw new KeyNotFoundException($"Shop {shopId} not found.");

        var existing = await db.ProcessingProfiles
            .FirstOrDefaultAsync(p => p.ReadinessStateId == readinessStateId, ct);
        if (existing is not null && existing.ShopId != shopId)
            throw new InvalidOperationException($"Processing profile {readinessStateId} does not belong to shop {shopId}.");

        var fields = new List<KeyValuePair<string, string>>
        {
            new("readiness_state", model.ReadinessState),
            new("min_processing_time", model.MinProcessingTime.ToString(CultureInfo.InvariantCulture)),
            new("max_processing_time", model.MaxProcessingTime.ToString(CultureInfo.InvariantCulture)),
            new("processing_time_unit", model.ProcessingTimeUnit),
        };

        var auth = EtsyAuth.ForShop(shopId);
        var raw = await etsy.UpdateProcessingProfileAsync(shop.EtsyShopId, readinessStateId, fields, auth, ct);
        var updated = raw.Value;
        var now = _time.GetUtcNow().UtcDateTime;

        var entity = existing ?? await db.ProcessingProfiles
            .FirstOrDefaultAsync(p => p.ShopId == shopId && p.ReadinessStateId == readinessStateId, ct);
        if (entity is null)
        {
            entity = new ProcessingProfile
            {
                ShopId = shopId,
                ReadinessStateId = readinessStateId,
            };
            db.ProcessingProfiles.Add(entity);
        }

        entity.ReadinessState = updated.ReadinessState ?? model.ReadinessState;
        entity.MinProcessingDays = updated.MinProcessingDays;
        entity.MaxProcessingDays = updated.MaxProcessingDays;
        entity.DisplayLabel = updated.ProcessingDaysDisplayLabel;
        entity.RawJson = raw.Json;
        entity.ImportedAtUtc = now;

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(
            action: "processing-profile.updated",
            entityType: "processing-profile",
            entityId: readinessStateId.ToString(CultureInfo.InvariantCulture),
            details: $"Updated processing profile '{entity.DisplayLabel ?? entity.ReadinessState}' ({readinessStateId}) for shop {shop.Name}",
            userName: user,
            ct: ct);

        return entity;
    }

    public Task<ProcessingProfile> UpdateProfileAsync(int shopId, long readinessStateId, ProcessingProfileInput model, string? user, CancellationToken ct = default) =>
        UpdateAsync(shopId, readinessStateId, model, user, ct);

    public async Task<IReadOnlyList<ProcessingProfileApplyRow>> PreviewBulkApplyAsync(
        long targetReadinessStateId, IReadOnlyCollection<int> listingIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var plan = await PlanBulkApplyInternalAsync(db, targetReadinessStateId, listingIds, ct);
        return [.. plan.Select(p => p.Row)];
    }

    public Task<IReadOnlyList<ProcessingProfileApplyRow>> PreviewApplyAsync(
        int shopId, long targetReadinessStateId, IReadOnlyCollection<int> listingIds, CancellationToken ct = default) =>
        PreviewBulkApplyAsync(targetReadinessStateId, listingIds, ct);

    public async Task<IReadOnlyList<int>> ApplyAsync(
        long targetReadinessStateId, IReadOnlyCollection<int> listingIds, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var targetProfile = await db.ProcessingProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ReadinessStateId == targetReadinessStateId, ct)
            ?? throw new KeyNotFoundException($"Processing profile {targetReadinessStateId} not found.");

        var plan = await PlanBulkApplyInternalAsync(db, targetReadinessStateId, listingIds, ct);
        var changed = new List<int>();

        foreach (var (row, newInventory) in plan)
        {
            if (!row.Changes || newInventory is null) continue;
            await editor.SaveAsync(row.ListingId, new Dictionary<string, JsonNode?>
            {
                [ListingFields.Inventory] = newInventory
            }, user, ct);
            changed.Add(row.ListingId);
        }

        if (changed.Count > 0)
        {
            var targetName = targetProfile.DisplayLabel ?? targetProfile.ReadinessState;
            await audit.WriteAsync(
                action: "listing.bulk-edited",
                entityType: nameof(Listing),
                entityId: null,
                details: $"{changed.Count} listing(s) updated to processing profile '{targetName}' (#{targetReadinessStateId}) (unpushed edits)",
                userName: user,
                ct: ct);
        }

        return changed;
    }

    public Task<IReadOnlyList<int>> ApplyAsync(
        int shopId, long targetReadinessStateId, IReadOnlyCollection<int> listingIds, string? user, CancellationToken ct = default) =>
        ApplyAsync(targetReadinessStateId, listingIds, user, ct);

    public async Task<IReadOnlyList<ProcessingProfileCandidate>> GetCandidatesAsync(int? shopId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Listings.AsNoTracking()
            .Include(l => l.Shop)
            .Include(l => l.Products)
                .ThenInclude(p => p.Offerings)
            .AsQueryable();

        if (shopId.HasValue)
            query = query.Where(l => l.ShopId == shopId.Value);

        var listings = await query
            .OrderBy(l => l.Title)
            .ToListAsync(ct);

        var lookups = new Dictionary<int, EditorLookups>();
        var candidates = new List<ProcessingProfileCandidate>();

        foreach (var l in listings)
        {
            if (!lookups.TryGetValue(l.ShopId, out var names))
                lookups[l.ShopId] = names = await EditorLookups.LoadAsync(db, l.ShopId, [], l.CurrencyCode, ct);

            // Inspect active offerings across active products on each listing
            var distinctOfferingIds = l.Products
                .Where(p => !p.IsDeleted)
                .SelectMany(p => p.Offerings.Where(o => !o.IsDeleted && o.ReadinessStateId != null))
                .Select(o => o.ReadinessStateId!.Value)
                .Distinct()
                .ToList();

            long? readyId;
            string readyName;
            bool varies;

            if (distinctOfferingIds.Count > 1)
            {
                readyId = null;
                readyName = "Varies by variation";
                varies = true;
            }
            else if (distinctOfferingIds.Count == 1)
            {
                var singleId = distinctOfferingIds[0];
                readyId = singleId;
                readyName = names.ProcessingProfiles.GetValueOrDefault(singleId, $"#{singleId}");
                varies = false;
            }
            else if (l.ReadinessStateId.HasValue)
            {
                var singleId = l.ReadinessStateId.Value;
                readyId = singleId;
                readyName = names.ProcessingProfiles.GetValueOrDefault(singleId, $"#{singleId}");
                varies = false;
            }
            else
            {
                readyId = null;
                readyName = "No profile";
                varies = false;
            }

            candidates.Add(new ProcessingProfileCandidate(
                l.Id,
                l.Title,
                l.ShopId,
                l.Shop.Name,
                l.EtsyListingId,
                l.State,
                l.IsMissingOnEtsy,
                l.Skus ?? [],
                l.Tags ?? [],
                readyId,
                readyName,
                varies
            ));
        }

        return candidates;
    }

    private static void ValidateInput(ProcessingProfileInput model)
    {
        if (string.IsNullOrWhiteSpace(model.ReadinessState) ||
            (model.ReadinessState != "ready_to_ship" && model.ReadinessState != "made_to_order"))
        {
            throw new ArgumentException("Readiness state must be 'ready_to_ship' or 'made_to_order'.");
        }
        if (model.MinProcessingTime < 1)
        {
            throw new ArgumentException("Minimum processing time must be at least 1.");
        }
        if (model.MaxProcessingTime < model.MinProcessingTime)
        {
            throw new ArgumentException("Maximum processing time cannot be less than minimum processing time.");
        }
        if (string.IsNullOrWhiteSpace(model.ProcessingTimeUnit) ||
            (model.ProcessingTimeUnit != "days" && model.ProcessingTimeUnit != "weeks"))
        {
            throw new ArgumentException("Processing time unit must be 'days' or 'weeks'.");
        }
    }

    private static async Task<List<(ProcessingProfileApplyRow Row, JsonNode? NewInventory)>> PlanBulkApplyInternalAsync(
        AppDbContext db, long targetReadinessStateId, IReadOnlyCollection<int> listingIds, CancellationToken ct)
    {
        var ids = listingIds.ToList();
        var rawListings = await db.Listings.AsNoTracking()
            .Include(l => l.EtsyState)
            .Include(l => l.Shop)
            .Where(l => ids.Contains(l.Id))
            .ToListAsync(ct);
        var listings = rawListings.OrderBy(l => l.Title, StringComparer.OrdinalIgnoreCase).ToList();

        var targetProfile = await db.ProcessingProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ReadinessStateId == targetReadinessStateId, ct);
        var targetLabel = targetProfile?.DisplayLabel ?? targetProfile?.ReadinessState ?? $"#{targetReadinessStateId}";

        var lookups = new Dictionary<int, EditorLookups>();
        var result = new List<(ProcessingProfileApplyRow, JsonNode?)>();

        foreach (var l in listings)
        {
            if (!lookups.TryGetValue(l.ShopId, out var names))
                lookups[l.ShopId] = names = await EditorLookups.LoadAsync(db, l.ShopId, [], l.CurrencyCode, ct);

            ProcessingProfileApplyRow Row(string before, string after, string? skipped, bool willChange) =>
                new(l.Id, l.Title, l.Shop.Name, before, after, skipped, willChange);

            if (l.IsMissingOnEtsy)
            {
                result.Add((Row("—", targetLabel, "Gone from Etsy.", false), null));
                continue;
            }

            if (l.EtsyState is null)
            {
                result.Add((Row("—", targetLabel, "Not imported yet.", false), null));
                continue;
            }

            if (targetProfile != null && targetProfile.ShopId != l.ShopId)
            {
                result.Add((Row("—", targetLabel, "Target profile belongs to another shop.", false), null));
                continue;
            }

            var model = ListingEditModel.Parse(l.EtsyState.ManagedJson, l.EtsyState.BaseJson, l.DraftJson);

            // DRIFT PROTECTION: Variations, prices and processing changed on Etsy
            if (model.DriftFields.Contains(ListingFields.Inventory))
            {
                result.Add((Row("—", targetLabel, $"{ListingEditor.Label(ListingFields.Inventory)} changed on Etsy; resolve it in the editor first.", false), null));
                continue;
            }

            var current = model.Local(ListingFields.Inventory);
            if (current?["products"] is not JsonArray { Count: > 0 })
            {
                result.Add((Row("—", targetLabel, "No inventory imported.", false), null));
                continue;
            }

            var offerings = (current["products"]?.AsArray() ?? [])
                .SelectMany(p => p!["offerings"]?.AsArray() ?? [])
                .Select(o => o!)
                .ToList();

            var currentIds = offerings.Select(o => (long?)o["readiness_state_id"]).OfType<long>().Distinct().ToList();
            var beforeLabel = currentIds.Count switch
            {
                0 => "—",
                1 => names.ProcessingProfiles.GetValueOrDefault(currentIds[0], $"#{currentIds[0]}"),
                _ => string.Join(", ", currentIds.Select(id => names.ProcessingProfiles.GetValueOrDefault(id, $"#{id}"))),
            };

            // ALREADY USES PROFILE CHECK
            if (currentIds.Count == 1 && currentIds[0] == targetReadinessStateId)
            {
                result.Add((Row(beforeLabel, targetLabel, "Already uses this profile.", false), null));
                continue;
            }

            // Update offering["readiness_state_id"] on deep clone
            var newInventory = current.DeepClone();
            foreach (var offering in (newInventory["products"]?.AsArray() ?? []).SelectMany(p => p!["offerings"]?.AsArray() ?? []).Select(o => o!))
            {
                offering["readiness_state_id"] = targetReadinessStateId;
            }

            result.Add((Row(beforeLabel, targetLabel, null, true), newInventory));
        }

        return result;
    }
}
