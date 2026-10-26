using System.Security.Claims;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>One field a push would change, in words.</summary>
public sealed record PendingFieldChange(string Label, string From, string To);

/// <summary>A listing whose local version differs from Etsy, with the editor's own preview of its push.</summary>
/// <param name="Reviewed">Stands for exactly these changes; a push is refused if the listing's changes are different by then.</param>
public sealed record PendingListing(int ListingId, string Title, int ShopId, string ShopName, long EtsyListingId, string? ThumbFileName,
    DateTime? EditedAtUtc, string? EditedBy, IReadOnlyList<PendingFieldChange> Changes, PushPreview Preview, string Reviewed)
{
    public bool CanPush => Preview.CanPush;
}

/// <summary>What happened to one listing when the selected ones were pushed.</summary>
public sealed record PendingPushResult(int ListingId, string Title, int? PushId, IReadOnlyList<string> Problems);

/// <summary>
/// Every listing with changes that aren't on Etsy yet, for the Pending changes page: each with the editor's push preview
/// (so listings changed on Etsy, or with any other blocker, show as blocked), and pushing the selected ones, each as its
/// own normal push (<see cref="ListingEditor.RequestPushAsync"/>, which checks the blockers again).
/// </summary>
public sealed class PendingChangesService(IDbContextFactory<AppDbContext> dbFactory, ListingEditor editor)
{
    /// <summary>How many listings the Pending changes page lists (<see cref="ListingChangeCounts.CountAsync"/>).</summary>
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await ListingChangeCounts.CountAsync(db, ct)).Pending;
    }

    public async Task<IReadOnlyList<PendingListing>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listings = await db.Listings.AsNoTracking()
            .Where(l => l.EtsyState != null && (l.HasDraft || l.HasDrift))
            .Select(l => new
            {
                l.Id, l.Title, l.ShopId, Shop = l.Shop.Name, l.EtsyListingId, l.CurrencyCode, l.DraftUpdatedAtUtc, l.DraftUpdatedBy,
                l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson,
                Thumb = l.Images.OrderBy(i => i.Rank).Select(i => i.ThumbFileName ?? i.FileName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        await SettleAsync(db, listings.Select(l => (l.Id, l.ManagedJson, l.BaseJson, l.DraftJson)), ct);
        var result = new List<PendingListing>();
        foreach (var l in listings.OrderByDescending(l => l.DraftUpdatedAtUtc).ThenBy(l => l.Title, StringComparer.OrdinalIgnoreCase))
        {
            var preview = await editor.PreviewAsync(l.Id, ct);
            if (preview.Changes.Count == 0) continue;
            var model = ListingEditModel.Parse(l.ManagedJson, l.BaseJson, l.DraftJson);
            var categories = new[] { model.Etsy["taxonomy_id"], model.Local("taxonomy_id") }.Where(v => v is not null).Select(v => (long)v!);
            var names = await EditorLookups.LoadAsync(db, l.ShopId, categories, l.CurrencyCode, ct);
            result.Add(new PendingListing(l.Id, l.Title, l.ShopId, l.Shop, l.EtsyListingId, l.Thumb, l.DraftUpdatedAtUtc, l.DraftUpdatedBy,
                [.. preview.Changes.Select(c => new PendingFieldChange(c.Label, names.Describe(c.Key, c.From), names.Describe(c.Key, c.To)))],
                preview, Fingerprint(preview)));
        }
        return result;
    }

    /// <summary>
    /// Queues the normal push for each listing reviewed on the page (listing id → <see cref="PendingListing.Reviewed"/>).
    /// A listing whose changes are different from what was reviewed is skipped, as is one with a blocker.
    /// </summary>
    public async Task<IReadOnlyList<PendingPushResult>> PushAsync(IReadOnlyDictionary<int, string> reviewed, ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        if (!user.IsInRole(Roles.Admin))
            throw new UnauthorizedAccessException("Only admins can push listings to Etsy.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = reviewed.Keys.ToList();
        var titles = await db.Listings.AsNoTracking().Where(l => ids.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Title, ct);

        var results = new List<PendingPushResult>();
        foreach (var (id, fingerprint) in reviewed)
        {
            if (!titles.TryGetValue(id, out var title)) continue;
            var preview = await editor.PreviewAsync(id, ct);
            if (Fingerprint(preview) != fingerprint)
            {
                results.Add(new PendingPushResult(id, title, null, ["Its changes are different from the ones you reviewed. Look at them again, then push."]));
                continue;
            }
            var (pushId, blockers) = await editor.RequestPushAsync(id, user.Identity?.Name, ct);
            results.Add(new PendingPushResult(id, title, pushId, blockers));
        }
        return results;
    }

    /// <summary>
    /// Stores the edit state again for flagged listings with nothing to push (their flags were set before Etsy came to
    /// hold their edits), so the listing pages stop showing them as edited or changed on Etsy.
    /// </summary>
    private static async Task SettleAsync(AppDbContext db, IEnumerable<(int Id, string Managed, string? Base, string? Draft)> listings,
        CancellationToken ct)
    {
        var settled = listings.Where(l => ListingEditModel.Parse(l.Managed, l.Base, l.Draft).PendingFields.Count == 0).Select(l => l.Id).ToList();
        if (settled.Count == 0) return;
        foreach (var listing in await db.Listings.Include(l => l.EtsyState).Where(l => settled.Contains(l.Id)).ToListAsync(ct))
            ListingMapper.UpdateEditState(listing);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The changes a preview would send, as one string (the fields with Etsy's value and the new one).</summary>
    private static string Fingerprint(PushPreview preview) =>
        string.Join("\n", preview.Changes.Select(c => $"{c.Key}\t{c.From?.ToJsonString()}\t{c.To?.ToJsonString()}"));
}
