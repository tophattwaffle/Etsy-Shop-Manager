using System.Security.Claims;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>A listing changed on etsy.com since the app last agreed with Etsy, with the fields that need a choice.</summary>
/// <param name="Reviewed">Stands for exactly these conflicts (fields with both values); a bulk resolve skips the listing if
/// they are different by then.</param>
public sealed record EtsyConflictListing(int ListingId, string Title, int ShopId, string ShopName, long EtsyListingId, string? ThumbFileName,
    DateTime? EtsyUpdatedAtUtc, bool HasLocalEdits, IReadOnlyList<string> Fields, string Reviewed);

/// <summary>What happened to one listing when the selected ones were resolved: how many fields, or why it was skipped.</summary>
public sealed record EtsyConflictResolveResult(int ListingId, string Title, int Resolved, string? Problem);

/// <summary>
/// Every listing changed on etsy.com (drift: a tracked field where Etsy no longer matches the agreed version), for the
/// Changed on Etsy page. Each is resolved field by field in its editor (keep mine / take Etsy's); until then its pushes
/// are blocked.
/// </summary>
public sealed class EtsyConflictService(IDbContextFactory<AppDbContext> dbFactory, ListingEditor editor)
{
    /// <summary>How many listings the Changed on Etsy page lists (<see cref="ListingChangeCounts.CountAsync"/>).</summary>
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await ListingChangeCounts.CountAsync(db, ct)).ChangedOnEtsy;
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
                model.HasDraft, [.. model.DriftFields.Select(ListingEditor.Label)], Fingerprint(model)));
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

    /// <summary>
    /// Resolves every conflicting field of each listing reviewed on the page (listing id → <see cref="EtsyConflictListing.Reviewed"/>) the same
    /// way, through the editor's own per-field resolve. Local only: keep mine leaves the local values to be pushed later
    /// through the normal push, take Etsy's adopts Etsy's values. A listing whose conflicts are no longer the ones that
    /// were reviewed (Etsy changed it again, or it was resolved elsewhere) is skipped.
    /// </summary>
    public async Task<IReadOnlyList<EtsyConflictResolveResult>> ResolveAsync(IReadOnlyDictionary<int, string> reviewed,
        DriftResolution resolution, ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!user.IsInRole(Roles.Admin))
            throw new UnauthorizedAccessException("Only admins can resolve listings changed on Etsy.");
        var ids = reviewed.Keys.ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listings = await db.Listings.AsNoTracking().Where(l => ids.Contains(l.Id) && l.EtsyState != null)
            .Select(l => new { l.Id, l.Title, l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson })
            .ToDictionaryAsync(l => l.Id, ct);

        var results = new List<EtsyConflictResolveResult>();
        foreach (var (id, fingerprint) in reviewed)
        {
            if (!listings.TryGetValue(id, out var l)) continue;
            var model = ListingEditModel.Parse(l.ManagedJson, l.BaseJson, l.DraftJson);
            var current = model.DriftFields;
            if (Fingerprint(model) != fingerprint)
            {
                results.Add(new EtsyConflictResolveResult(id, l.Title, 0, current.Count == 0
                    ? "It was already resolved."
                    : "It changed since you looked at it. Open it and choose again."));
                continue;
            }
            foreach (var key in current) await editor.ResolveDriftAsync(id, key, resolution, user.Identity?.Name, ct);
            results.Add(new EtsyConflictResolveResult(id, l.Title, current.Count, null));
        }
        return results;
    }

    /// <summary>The conflicts as one string: each field with this app's value and Etsy's.</summary>
    private static string Fingerprint(ListingEditModel model) =>
        string.Join("\n", model.DriftFields.Select(k => $"{k}\t{model.Local(k)?.ToJsonString()}\t{model.Etsy[k]?.ToJsonString()}"));
}
