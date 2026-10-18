using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Orders;

public sealed record OrderShopStatus(int ShopId, long EtsyShopId, string ShopName, bool IsHealthy, int OpenOrders,
    DateTime? LastPolledAtUtc, bool BackfillDone, string? LastError);

/// <summary>Reads behind the Orders page and the Home Assistant API.</summary>
public sealed class OrderQueries(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    /// <summary>Unpaid or shipped orders can't be open, so filter in SQL; the status check runs in memory.</summary>
    private static IQueryable<Order> OpenCandidates(AppDbContext db) =>
        db.Orders.AsNoTracking().Where(o => o.IsPaid && !o.IsShipped);

    public async Task<List<Order>> GetOpenOrdersAsync(int? shopId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = OpenCandidates(db).Include(o => o.Items).Include(o => o.Shop).AsSplitQuery();
        if (shopId is { } id) q = q.Where(o => o.ShopId == id);
        var orders = await q.ToListAsync(ct);
        return orders.Where(o => o.IsOpen)
            .OrderBy(o => o.ShipByUtc is null).ThenBy(o => o.ShipByUtc).ThenByDescending(o => o.EtsyCreatedAtUtc)
            .ToList();
    }

    /// <summary>
    /// Each shop's home country (for the domestic/international filter): the origin country most of its shipping profiles
    /// ship from. Shops with no origin country on file are left out.
    /// </summary>
    public async Task<Dictionary<int, string>> GetHomeCountriesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var origins = await db.ShippingProfiles.AsNoTracking().Where(p => p.OriginCountryIso != null && p.OriginCountryIso != "")
            .Select(p => new { p.ShopId, p.OriginCountryIso }).ToListAsync(ct);
        return origins.GroupBy(p => p.ShopId).ToDictionary(g => g.Key,
            g => g.GroupBy(p => p.OriginCountryIso!.Trim().ToUpperInvariant()).OrderByDescending(c => c.Count()).ThenBy(c => c.Key).First().Key);
    }

    /// <summary>Order history columns the table can sort by (server-side, so the order covers every page).</summary>
    public static class HistorySort
    {
        public const string Date = "date", Receipt = "receipt", Buyer = "buyer", Items = "items", Total = "total",
            Status = "status", Shop = "shop";
    }

    public async Task<(List<Order> Orders, int Total)> GetHistoryAsync(int? shopId, string? text, int skip, int take,
        CancellationToken ct = default, string? sortBy = null, bool descending = true, bool giftsOnly = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<Order> q = db.Orders.AsNoTracking();
        if (shopId is { } id) q = q.Where(o => o.ShopId == id);
        if (giftsOnly) q = q.Where(o => o.IsGift);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var t = text.Trim();
            var pattern = "%" + t.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = long.TryParse(t, out var receiptId)
                ? q.Where(o => o.EtsyReceiptId == receiptId)
                : q.Where(o => EF.Functions.Like(o.BuyerName, pattern, "\\")
                               || o.Items.Any(i => EF.Functions.Like(i.Title, pattern, "\\") || EF.Functions.Like(i.Sku, pattern, "\\")));
        }
        var total = await q.CountAsync(ct);
        var d = descending;
        var ordered = sortBy switch
        {
            HistorySort.Receipt => d ? q.OrderByDescending(o => o.EtsyReceiptId) : q.OrderBy(o => o.EtsyReceiptId),
            HistorySort.Buyer => d ? q.OrderByDescending(o => EF.Functions.Collate(o.BuyerName!, "NOCASE")) : q.OrderBy(o => EF.Functions.Collate(o.BuyerName!, "NOCASE")),
            HistorySort.Items => d
                ? q.OrderByDescending(o => EF.Functions.Collate(o.Items.OrderBy(i => i.Id).Select(i => i.Title).FirstOrDefault()!, "NOCASE"))
                : q.OrderBy(o => EF.Functions.Collate(o.Items.OrderBy(i => i.Id).Select(i => i.Title).FirstOrDefault()!, "NOCASE")),
            HistorySort.Total => d
                ? q.OrderByDescending(o => (double?)o.Grandtotal / o.Divisor)
                : q.OrderBy(o => (double?)o.Grandtotal / o.Divisor),
            HistorySort.Status => d
                ? q.OrderByDescending(o => o.IsShipped).ThenByDescending(o => o.Status)
                : q.OrderBy(o => o.IsShipped).ThenBy(o => o.Status),
            HistorySort.Shop => d ? q.OrderByDescending(o => EF.Functions.Collate(o.Shop.Name, "NOCASE")) : q.OrderBy(o => EF.Functions.Collate(o.Shop.Name, "NOCASE")),
            _ => d ? q.OrderByDescending(o => o.EtsyCreatedAtUtc) : q.OrderBy(o => o.EtsyCreatedAtUtc),
        };
        var orders = await ordered.ThenByDescending(o => o.EtsyCreatedAtUtc).ThenByDescending(o => o.Id)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200))
            .Include(o => o.Items).Include(o => o.Shop).AsSplitQuery()
            .ToListAsync(ct);
        return (orders, total);
    }

    public async Task<List<OrderShopStatus>> GetShopStatusAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shops = await db.Shops.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.EtsyShopId, s.Name, Healthy = s.Token != null && !s.Token.NeedsReauth })
            .ToListAsync(ct);
        var sync = await db.ShopOrderSyncStates.AsNoTracking().ToDictionaryAsync(s => s.ShopId, ct);
        var open = (await OpenCandidates(db).Select(o => new { o.ShopId, o.Status }).ToListAsync(ct))
            .Where(o => !Order.ResolvedStatuses.Contains(o.Status.Trim().ToLowerInvariant()))
            .GroupBy(o => o.ShopId).ToDictionary(g => g.Key, g => g.Count());

        return shops.Select(s =>
        {
            sync.TryGetValue(s.Id, out var st);
            return new OrderShopStatus(s.Id, s.EtsyShopId, s.Name, s.Healthy, open.GetValueOrDefault(s.Id),
                st?.LastPolledAtUtc, st?.BackfillCompletedAtUtc is not null, st?.LastError);
        }).ToList();
    }

    // ---- Home Assistant API --------------------------------------------------------------------------------

    public async Task<JsonObject> HaShopsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shops = await db.Shops.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync(ct);
        var status = (await GetShopStatusAsync(ct)).ToDictionary(s => s.ShopId);
        return new JsonObject
        {
            ["shops"] = new JsonArray([.. shops.Select(s => (JsonNode)ShopHeader(s, status.GetValueOrDefault(s.Id)))]),
        };
    }

    /// <summary>Open orders for one shop (by Etsy shop id), or null if no such active shop.</summary>
    public async Task<JsonObject?> HaOpenOrdersAsync(long etsyShopId, CancellationToken ct = default)
    {
        var all = await HaOpenOrdersAllAsync(ct);
        return all["shops"]!.AsArray().Select(n => n!.AsObject())
            .FirstOrDefault(s => (string?)s["shop_id"] == etsyShopId.ToString())?.DeepClone().AsObject();
    }

    /// <summary>Every active shop's open orders in one response (what the HA integration polls).</summary>
    public async Task<JsonObject> HaOpenOrdersAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shops = await db.Shops.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync(ct);
        var status = (await GetShopStatusAsync(ct)).ToDictionary(s => s.ShopId);
        var orders = await GetOpenOrdersAsync(null, ct);

        var result = new JsonArray();
        foreach (var shop in shops)
        {
            var payload = ShopHeader(shop, status.GetValueOrDefault(shop.Id));
            foreach (var (key, value) in HaPayloadBuilder.OpenOrders(orders.Where(o => o.ShopId == shop.Id), TimeZoneInfo.Local))
                payload[key] = value?.DeepClone();
            result.Add(payload);
        }
        return new JsonObject
        {
            ["generated_at"] = time.GetUtcNow().ToUnixTimeSeconds(),
            ["shops"] = result,
        };
    }

    private static JsonObject ShopHeader(Shop shop, OrderShopStatus? status) => new()
    {
        ["shop_id"] = shop.EtsyShopId.ToString(),
        ["shop_name"] = shop.Name,
        ["title"] = shop.Title,
        ["url"] = shop.Url,
        ["currency_code"] = shop.CurrencyCode,
        ["open_orders_count"] = status?.OpenOrders ?? 0,
        ["orders_synced_at"] = status?.LastPolledAtUtc is { } polled
            ? new DateTimeOffset(DateTime.SpecifyKind(polled, DateTimeKind.Utc)).ToUnixTimeSeconds()
            : null,
        ["connected"] = status?.IsHealthy ?? false,
    };
}
