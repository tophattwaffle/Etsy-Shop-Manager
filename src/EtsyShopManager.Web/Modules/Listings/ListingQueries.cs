using EtsyShopManager.Core.Domain;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Listings;

public enum ListingSort { UpdatedDesc, TitleAsc, PriceAsc, PriceDesc, QuantityAsc, CreatedDesc }

public sealed record ListingFilter
{
    public int? ShopId { get; init; }
    public string? Text { get; init; }

    /// <summary>Etsy states to include; empty means all.</summary>
    public IReadOnlyCollection<string> States { get; init; } = [];

    /// <summary>null: both; true: only listings no longer on Etsy; false: only listings still on Etsy.</summary>
    public bool? Missing { get; init; } = false;

    public ListingSort Sort { get; init; } = ListingSort.UpdatedDesc;
    public int Skip { get; init; }
    public int Take { get; init; } = 50;
}

public sealed record ListingRow(
    int Id,
    int ShopId,
    string ShopName,
    long EtsyListingId,
    string Title,
    string State,
    long PriceAmount,
    int PriceDivisor,
    string? CurrencyCode,
    int Quantity,
    List<string> Skus,
    string? ThumbFileName,
    int ImageCount,
    int VariationCount,
    DateTime? EtsyUpdatedAtUtc,
    DateTime LastImportedAtUtc,
    bool IsMissingOnEtsy);

public sealed record ShopImportStatus(
    int ShopId,
    string ShopName,
    bool IsHealthy,
    int ListingCount,
    DateTime? LastCompletedAtUtc,
    DateTime? LookupsImportedAtUtc,
    bool IsRunOpen,
    DateTime? RunStartedAtUtc,
    string? LastError,
    int ImagesPending);

/// <summary>Read-only queries behind the listings and lookups pages.</summary>
public sealed class ListingQueries(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<(List<ListingRow> Rows, int Total)> SearchAsync(ListingFilter filter, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = Filter(db.Listings.AsNoTracking(), filter);
        var total = await q.CountAsync(ct);

        IOrderedQueryable<Listing> ordered = filter.Sort switch
        {
            ListingSort.TitleAsc => q.OrderBy(l => l.Title),
            ListingSort.PriceAsc => q.OrderBy(l => (double)l.PriceAmount / l.PriceDivisor),
            ListingSort.PriceDesc => q.OrderByDescending(l => (double)l.PriceAmount / l.PriceDivisor),
            ListingSort.QuantityAsc => q.OrderBy(l => l.Quantity),
            ListingSort.CreatedDesc => q.OrderByDescending(l => l.EtsyCreatedAtUtc),
            _ => q.OrderByDescending(l => l.EtsyUpdatedAtUtc),
        };

        var rows = await ordered.ThenBy(l => l.Id)
            .Skip(Math.Max(0, filter.Skip)).Take(Math.Clamp(filter.Take, 1, 500))
            .Select(l => new ListingRow(
                l.Id, l.ShopId, l.Shop.Name, l.EtsyListingId, l.Title, l.State,
                l.PriceAmount, l.PriceDivisor, l.CurrencyCode, l.Quantity, l.Skus,
                l.Images.OrderBy(i => i.Rank).Select(i => i.ThumbFileName ?? i.FileName).FirstOrDefault(),
                l.Images.Count,
                l.Products.Count(p => !p.IsDeleted),
                l.EtsyUpdatedAtUtc, l.LastImportedAtUtc, l.IsMissingOnEtsy))
            .ToListAsync(ct);
        return (rows, total);
    }

    internal static IQueryable<Listing> Filter(IQueryable<Listing> q, ListingFilter f)
    {
        if (f.ShopId is { } shopId) q = q.Where(l => l.ShopId == shopId);
        if (f.States.Count > 0)
        {
            var states = f.States.ToList();
            q = q.Where(l => states.Contains(l.State));
        }
        if (f.Missing is { } missing) q = q.Where(l => l.IsMissingOnEtsy == missing);

        var text = f.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            if (long.TryParse(text, out var etsyId))
            {
                q = q.Where(l => l.EtsyListingId == etsyId || EF.Functions.Like(l.Title, Like(text), "\\"));
            }
            else
            {
                // SQLite LIKE is case-insensitive for ASCII.
                var pattern = Like(text);
                q = q.Where(l => EF.Functions.Like(l.Title, pattern, "\\")
                                 || l.Skus.Any(s => EF.Functions.Like(s, pattern, "\\"))
                                 || l.Tags.Any(t => EF.Functions.Like(t, pattern, "\\")));
            }
        }
        return q;
    }

    private static string Like(string text) =>
        "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public async Task<Listing?> GetDetailAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Listings.AsNoTracking()
            .Include(l => l.Shop)
            .Include(l => l.Images.OrderBy(i => i.Rank))
            .Include(l => l.Products).ThenInclude(p => p.Offerings)
            .Include(l => l.EtsyState)
            .AsSplitQuery()
            .FirstOrDefaultAsync(l => l.Id == id, ct);
    }

    public async Task<ListingLookupNames> GetLookupNamesAsync(Listing l, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return new ListingLookupNames(
            ShippingProfile: l.ShippingProfileId is { } sp
                ? await db.ShippingProfiles.Where(x => x.ShopId == l.ShopId && x.EtsyShippingProfileId == sp)
                    .Select(x => x.Title).FirstOrDefaultAsync(ct)
                : null,
            ProcessingProfile: l.ReadinessStateId is { } rs
                ? await db.ProcessingProfiles.Where(x => x.ShopId == l.ShopId && x.ReadinessStateId == rs)
                    .Select(x => x.DisplayLabel ?? x.ReadinessState).FirstOrDefaultAsync(ct)
                : null,
            ReturnPolicy: l.ReturnPolicyId is { } rp
                ? await db.ReturnPolicies.Where(x => x.ShopId == l.ShopId && x.EtsyReturnPolicyId == rp)
                    .Select(x => ReturnPolicySummary(x)).FirstOrDefaultAsync(ct)
                : null,
            Section: l.ShopSectionId is { } sec
                ? await db.ShopSections.Where(x => x.ShopId == l.ShopId && x.EtsySectionId == sec)
                    .Select(x => x.Title).FirstOrDefaultAsync(ct)
                : null,
            TaxonomyPath: l.TaxonomyId is { } tx
                ? await db.TaxonomyNodes.Where(x => x.Id == tx).Select(x => x.FullPath).FirstOrDefaultAsync(ct)
                : null,
            ProductionPartners: l.ProductionPartnerIds.Count == 0
                ? []
                : await db.ProductionPartners.Where(x => x.ShopId == l.ShopId && l.ProductionPartnerIds.Contains(x.EtsyPartnerId))
                    .Select(x => x.Name).ToListAsync(ct),
            ProcessingLabels: await db.ProcessingProfiles.Where(x => x.ShopId == l.ShopId)
                .ToDictionaryAsync(x => x.ReadinessStateId, x => x.DisplayLabel ?? x.ReadinessState, ct));
    }

    public static string ReturnPolicySummary(ReturnPolicy p) =>
        (p.AcceptsReturns, p.AcceptsExchanges) switch
        {
            (true, true) => $"Returns and exchanges, {p.ReturnDeadlineDays} days",
            (true, false) => $"Returns, {p.ReturnDeadlineDays} days",
            (false, true) => $"Exchanges, {p.ReturnDeadlineDays} days",
            _ => "No returns or exchanges",
        };

    public async Task<List<ShopImportStatus>> GetImportStatusAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shops = await db.Shops.AsNoTracking().OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Id,
                s.Name,
                Healthy = s.IsActive && s.Token != null && !s.Token.NeedsReauth,
                Count = db.Listings.Count(l => l.ShopId == s.Id && !l.IsMissingOnEtsy),
                Pending = db.ListingImages.Count(i => i.Listing.ShopId == s.Id && i.FileName == null),
            })
            .ToListAsync(ct);
        var sync = await db.ShopSyncStates.AsNoTracking().ToDictionaryAsync(s => s.ShopId, ct);

        return shops.Select(s =>
        {
            sync.TryGetValue(s.Id, out var st);
            return new ShopImportStatus(s.Id, s.Name, s.Healthy, s.Count, st?.LastCompletedAtUtc, st?.LookupsImportedAtUtc,
                st?.IsRunOpen ?? false, st?.RunStartedAtUtc, st?.LastError, s.Pending);
        }).ToList();
    }
}

public sealed record ListingLookupNames(
    string? ShippingProfile,
    string? ProcessingProfile,
    string? ReturnPolicy,
    string? Section,
    string? TaxonomyPath,
    List<string> ProductionPartners,
    Dictionary<long, string> ProcessingLabels);
