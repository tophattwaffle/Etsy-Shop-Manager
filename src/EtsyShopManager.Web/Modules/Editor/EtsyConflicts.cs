using EtsyShopManager.Core.Listings;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>A listing changed on etsy.com since the app last agreed with Etsy, with the fields that need a choice.</summary>
public sealed record EtsyConflictListing(int ListingId, string Title, int ShopId, string ShopName, long EtsyListingId, string? ThumbFileName,
    DateTime? EtsyUpdatedAtUtc, bool HasLocalEdits, IReadOnlyList<string> Fields);

/// <summary>
/// Every listing changed on etsy.com (drift: a tracked field where Etsy no longer matches the agreed version), for the
/// Changed on Etsy page. Each is resolved field by field in its editor (keep mine / take Etsy's); until then its pushes
/// are blocked.
/// </summary>
public sealed class EtsyConflictService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>How many listings the Changed on Etsy page lists. The HasDrift flag only narrows the search.</summary>
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var states = await db.Listings.AsNoTracking().Where(l => l.EtsyState != null && l.HasDrift)
            .Select(l => new { l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson }).ToListAsync(ct);
        return states.Count(s => ListingEditModel.Parse(s.ManagedJson, s.BaseJson, s.DraftJson).DriftFields.Count > 0);
    }

    public async Task<IReadOnlyList<EtsyConflictListing>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listings = await db.Listings.AsNoTracking()
            .Where(l => l.EtsyState != null && l.HasDrift)
            .Select(l => new
            {
                l.Id, l.Title, l.ShopId, Shop = l.Shop.Name, l.EtsyListingId, l.EtsyUpdatedAtUtc,
                l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson,
                Thumb = l.Images.OrderBy(i => i.Rank).Select(i => i.ThumbFileName ?? i.FileName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var result = new List<EtsyConflictListing>();
        var stale = new List<int>();
        foreach (var l in listings)
        {
            var model = ListingEditModel.Parse(l.ManagedJson, l.BaseJson, l.DraftJson);
            if (model.DriftFields.Count == 0) { stale.Add(l.Id); continue; }
            result.Add(new EtsyConflictListing(l.Id, l.Title, l.ShopId, l.Shop, l.EtsyListingId, l.Thumb, l.EtsyUpdatedAtUtc,
                model.HasDraft, [.. model.DriftFields.Select(ListingEditor.Label)]));
        }

        // A flag left over from before the drift was settled: store the edit state again so the listing pages agree.
        if (stale.Count > 0)
        {
            foreach (var listing in await db.Listings.Include(l => l.EtsyState).Where(l => stale.Contains(l.Id)).ToListAsync(ct))
                ListingMapper.UpdateEditState(listing);
            await db.SaveChangesAsync(ct);
        }

        return [.. result.OrderByDescending(r => r.EtsyUpdatedAtUtc).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)];
    }
}
