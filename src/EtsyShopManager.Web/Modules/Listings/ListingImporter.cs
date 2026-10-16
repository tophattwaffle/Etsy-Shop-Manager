using System.Text.Json;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Models;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Listings;

public sealed record ShopImportResult(string RunId, bool Resumed, int ListingsSeen, int MarkedMissing);

/// <summary>
/// Pulls one shop's listings (every state, with images, inventory and attributes) and lookups into the local DB.
/// <para>
/// Idempotent: listings, images, products and offerings are matched by Etsy id, so re-importing unchanged data
/// changes nothing. Resumable: the position (listing state + offset) is saved after every page, so a job that
/// is interrupted continues from that page instead of starting over.
/// </para>
/// </summary>
public sealed class ListingImporter(
    IDbContextFactory<AppDbContext> dbFactory,
    EtsyApiClient etsy,
    TimeProvider time,
    ILogger<ListingImporter> logger)
{
    /// <summary>An open run older than this is abandoned and a new one starts.</summary>
    public static readonly TimeSpan ResumeWindow = TimeSpan.FromHours(12);

    public int PageSize { get; set; } = EtsyApiClient.MaxListingsPageSize;

    internal sealed record Cursor
    {
        public bool LookupsDone { get; init; }
        public int StateIndex { get; init; }
        public int Offset { get; init; }
        public int Seen { get; init; }
    }

    public async Task<ShopImportResult> ImportShopAsync(int shopId, bool force, CancellationToken ct)
    {
        var (etsyShopId, runId, cursor, resumed, runForce) = await OpenRunAsync(shopId, force, ct);
        var auth = EtsyAuth.ForShop(shopId);
        logger.LogInformation("{Action} listing import {RunId} for shop {ShopId} at state {State}, offset {Offset}",
            resumed ? "Resuming" : "Starting", runId, shopId, cursor.StateIndex, cursor.Offset);

        if (!cursor.LookupsDone)
        {
            await ImportLookupsAsync(shopId, etsyShopId, ct);
            cursor = cursor with { LookupsDone = true };
            await SaveCursorAsync(shopId, cursor, ct);
        }

        var states = EtsyListingStates.All;
        while (cursor.StateIndex < states.Length)
        {
            var state = states[cursor.StateIndex];
            var page = await etsy.GetListingsByShopAsync(etsyShopId, state, cursor.Offset, PageSize, auth, ct);
            await UpsertPageAsync(shopId, etsyShopId, page.Results, runId, runForce, ct);

            var offset = cursor.Offset + page.Results.Count;
            var stateDone = page.Results.Count < PageSize || offset >= page.Count;
            cursor = stateDone
                ? cursor with { StateIndex = cursor.StateIndex + 1, Offset = 0, Seen = cursor.Seen + page.Results.Count }
                : cursor with { Offset = offset, Seen = cursor.Seen + page.Results.Count };
            await SaveCursorAsync(shopId, cursor, ct);
        }

        var missing = await CompleteRunAsync(shopId, runId, ct);
        logger.LogInformation("Listing import {RunId} for shop {ShopId} done: {Seen} listings, {Missing} newly missing on Etsy",
            runId, shopId, cursor.Seen, missing);
        return new ShopImportResult(runId, resumed, cursor.Seen, missing);
    }

    /// <summary>Records a failure on the shop's sync state (shown in the UI). The run stays open for a retry.</summary>
    public async Task RecordErrorAsync(int shopId, string error, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        await db.ShopSyncStates.Where(s => s.ShopId == shopId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.LastError, error.Length > 2000 ? error[..2000] : error)
            .SetProperty(x => x.LastErrorAtUtc, now), ct);
    }

    private async Task<(long EtsyShopId, string RunId, Cursor Cursor, bool Resumed, bool Force)> OpenRunAsync(
        int shopId, bool force, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shop = await db.Shops.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shopId, ct)
                   ?? throw new JobPermanentFailureException($"Shop {shopId} does not exist.");
        if (!shop.IsActive) throw new JobPermanentFailureException($"Shop {shop.Name} is disconnected.");

        var now = time.GetUtcNow().UtcDateTime;
        var sync = await db.ShopSyncStates.FirstOrDefaultAsync(s => s.ShopId == shopId, ct);
        if (sync is null)
        {
            sync = new ShopSyncState { ShopId = shopId };
            db.ShopSyncStates.Add(sync);
        }

        if (sync.IsRunOpen && sync.RunId is not null && sync.RunStartedAtUtc > now - ResumeWindow
            && TryReadCursor(sync.CursorJson) is { } open)
        {
            sync.RunForce |= force;
            await db.SaveChangesAsync(ct);
            return (shop.EtsyShopId, sync.RunId, open, true, sync.RunForce);
        }

        var cursor = new Cursor();
        sync.RunId = Guid.NewGuid().ToString("N");
        sync.RunStartedAtUtc = now;
        sync.RunForce = force;
        sync.CursorJson = JsonSerializer.Serialize(cursor, JobJson.Options);
        await db.SaveChangesAsync(ct);
        return (shop.EtsyShopId, sync.RunId, cursor, false, force);
    }

    private static Cursor? TryReadCursor(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<Cursor>(json, JobJson.Options); }
        catch (JsonException) { return null; }
    }

    private async Task SaveCursorAsync(int shopId, Cursor cursor, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(cursor, JobJson.Options);
        await db.ShopSyncStates.Where(s => s.ShopId == shopId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CursorJson, json), ct);
    }

    private async Task<int> CompleteRunAsync(int shopId, string runId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;

        // Every state was pulled completely, so a listing this run did not see is no longer on Etsy.
        var missing = await db.Listings
            .Where(l => l.ShopId == shopId && !l.IsMissingOnEtsy && l.LastSeenRunId != runId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsMissingOnEtsy, true), ct);

        var count = await db.Listings.CountAsync(l => l.ShopId == shopId && !l.IsMissingOnEtsy, ct);
        await db.ShopSyncStates.Where(s => s.ShopId == shopId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.CursorJson, (string?)null)
            .SetProperty(x => x.LastCompletedAtUtc, now)
            .SetProperty(x => x.LastListingCount, count)
            .SetProperty(x => x.LastError, (string?)null)
            .SetProperty(x => x.LastErrorAtUtc, (DateTime?)null), ct);
        return missing;
    }

    private async Task UpsertPageAsync(int shopId, long etsyShopId, List<EtsyRaw<EtsyListing>> page, string runId,
        bool force, CancellationToken ct)
    {
        if (page.Count == 0) return;
        var auth = EtsyAuth.ForShop(shopId);
        var now = time.GetUtcNow().UtcDateTime;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = page.Select(r => r.Value.ListingId).ToList();
        var existing = await db.Listings
            .Include(l => l.Images)
            .Include(l => l.Products).ThenInclude(p => p.Offerings)
            .Include(l => l.EtsyState)
            .AsSplitQuery()
            .Where(l => ids.Contains(l.EtsyListingId))
            .ToDictionaryAsync(l => l.EtsyListingId, ct);

        foreach (var (src, raw) in page)
        {
            if (!existing.TryGetValue(src.ListingId, out var listing))
            {
                listing = new Listing { ShopId = shopId, FirstImportedAtUtc = now };
                db.Listings.Add(listing);
                existing[src.ListingId] = listing;
            }
            else if (listing.ShopId != shopId)
            {
                logger.LogWarning("Listing {ListingId} moved from shop {Old} to shop {New}", src.ListingId, listing.ShopId, shopId);
                listing.ShopId = shopId;
            }

            var previousModified = listing.EtsyUpdatedAtUtc;
            ListingMapper.ApplyFields(listing, src);

            // The includes normally carry images and inventory. Fall back to the per-listing calls if they don't.
            var images = src.Images ?? await etsy.GetListingImagesAsync(src.ListingId, auth, ct);
            ListingMapper.SyncImages(listing, images);

            var inventory = src.Inventory ?? await etsy.GetListingInventoryAsync(src.ListingId, auth, ct);
            ListingMapper.SyncInventory(listing, inventory);

            // Attributes need one call per listing, so only fetch them when the listing changed.
            if (force || listing.AttributesFetchedAtUtc is null || previousModified != listing.EtsyUpdatedAtUtc)
            {
                var attributes = await etsy.GetListingPropertiesAsync(etsyShopId, src.ListingId, auth, ct);
                ListingMapper.SetAttributes(listing, attributes);
                listing.AttributesFetchedAtUtc = now;
            }

            ListingMapper.SetEtsyState(listing, raw, now);
            listing.LastSeenRunId = runId;
            listing.IsMissingOnEtsy = false;
            listing.LastImportedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Replaces the shop's cached shipping, processing and return profiles, sections and partners.</summary>
    public async Task ImportLookupsAsync(int shopId, long etsyShopId, CancellationToken ct)
    {
        var auth = EtsyAuth.ForShop(shopId);
        var shipping = await etsy.GetShippingProfilesAsync(etsyShopId, auth, ct);
        var processing = await etsy.GetProcessingProfilesAsync(etsyShopId, auth, ct);
        var returns = await etsy.GetReturnPoliciesAsync(etsyShopId, auth, ct);
        var sections = await etsy.GetShopSectionsAsync(etsyShopId, auth, ct);
        var partners = await etsy.GetProductionPartnersAsync(etsyShopId, auth, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;

        Sync(db.ShippingProfiles, await db.ShippingProfiles.Where(x => x.ShopId == shopId).ToListAsync(ct),
            shipping.Where(s => !s.Value.IsDeleted).ToList(), x => x.EtsyShippingProfileId, s => s.Value.ShippingProfileId,
            () => new ShippingProfile { ShopId = shopId },
            (x, s) =>
            {
                x.EtsyShippingProfileId = s.Value.ShippingProfileId;
                x.Title = s.Value.Title ?? $"Profile {s.Value.ShippingProfileId}";
                x.OriginCountryIso = s.Value.OriginCountryIso;
                x.OriginPostalCode = s.Value.OriginPostalCode;
                x.ProfileType = s.Value.ProfileType;
                x.DestinationCount = s.Value.Destinations?.Count ?? 0;
                x.UpgradeCount = s.Value.Upgrades?.Count ?? 0;
                x.RawJson = s.Json;
                x.ImportedAtUtc = now;
            });

        Sync(db.ProcessingProfiles, await db.ProcessingProfiles.Where(x => x.ShopId == shopId).ToListAsync(ct),
            processing, x => x.ReadinessStateId, p => p.Value.ReadinessStateId,
            () => new ProcessingProfile { ShopId = shopId },
            (x, p) =>
            {
                x.ReadinessStateId = p.Value.ReadinessStateId;
                x.ReadinessState = p.Value.ReadinessState ?? "";
                x.MinProcessingDays = p.Value.MinProcessingDays;
                x.MaxProcessingDays = p.Value.MaxProcessingDays;
                x.DisplayLabel = p.Value.ProcessingDaysDisplayLabel;
                x.RawJson = p.Json;
                x.ImportedAtUtc = now;
            });

        Sync(db.ReturnPolicies, await db.ReturnPolicies.Where(x => x.ShopId == shopId).ToListAsync(ct),
            returns, x => x.EtsyReturnPolicyId, r => r.Value.ReturnPolicyId,
            () => new ReturnPolicy { ShopId = shopId },
            (x, r) =>
            {
                x.EtsyReturnPolicyId = r.Value.ReturnPolicyId;
                x.AcceptsReturns = r.Value.AcceptsReturns;
                x.AcceptsExchanges = r.Value.AcceptsExchanges;
                x.ReturnDeadlineDays = r.Value.ReturnDeadline;
                x.RawJson = r.Json;
                x.ImportedAtUtc = now;
            });

        Sync(db.ShopSections, await db.ShopSections.Where(x => x.ShopId == shopId).ToListAsync(ct),
            sections, x => x.EtsySectionId, s => s.ShopSectionId,
            () => new ShopSection { ShopId = shopId },
            (x, s) =>
            {
                x.EtsySectionId = s.ShopSectionId;
                x.Title = s.Title ?? "";
                x.Rank = s.Rank;
                x.ActiveListingCount = s.ActiveListingCount;
                x.ImportedAtUtc = now;
            });

        Sync(db.ProductionPartners, await db.ProductionPartners.Where(x => x.ShopId == shopId).ToListAsync(ct),
            partners, x => x.EtsyPartnerId, p => p.ProductionPartnerId,
            () => new ProductionPartner { ShopId = shopId },
            (x, p) =>
            {
                x.EtsyPartnerId = p.ProductionPartnerId;
                x.Name = p.PartnerName ?? "";
                x.Location = p.Location;
                x.ImportedAtUtc = now;
            });

        var sync = await db.ShopSyncStates.FirstOrDefaultAsync(s => s.ShopId == shopId, ct);
        if (sync is null) db.ShopSyncStates.Add(sync = new ShopSyncState { ShopId = shopId });
        sync.LookupsImportedAtUtc = now;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Upserts by Etsy id and removes rows Etsy no longer returns.</summary>
    internal static void Sync<TEntity, TSource>(DbSet<TEntity> set, List<TEntity> current, IReadOnlyList<TSource> incoming,
        Func<TEntity, long> entityKey, Func<TSource, long> sourceKey, Func<TEntity> create, Action<TEntity, TSource> apply)
        where TEntity : class
    {
        var byKey = current.GroupBy(entityKey).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<long>();
        foreach (var item in incoming)
        {
            var key = sourceKey(item);
            if (!seen.Add(key)) continue;
            if (!byKey.TryGetValue(key, out var entity))
            {
                entity = create();
                set.Add(entity);
            }
            apply(entity, item);
        }
        set.RemoveRange(current.Where(e => !seen.Contains(entityKey(e))));
    }
}
