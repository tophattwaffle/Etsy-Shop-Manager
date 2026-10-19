using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>
/// One To make row (identical items across the open orders, summed as the Home Assistant card sums them) with the order
/// items behind it, soonest ship-by first, so marking units made on the row marks the same items as on the order cards.
/// </summary>
public sealed record ToMakeRow(JsonObject Row, IReadOnlyList<OrderItem> Items)
{
    public int Quantity => Items.Sum(i => i.UnitsToMake);
    public int Made => Items.Sum(i => i.UnitsMade);
    public int Left => Quantity - Made;
    public bool IsMade => Left == 0;
}

public static class ToMakeList
{
    /// <summary>The To make rows for these orders, in the order of the payload's <c>items_needed</c>.</summary>
    public static List<ToMakeRow> Build(IReadOnlyList<Order> orders, TimeZoneInfo timeZone)
    {
        var open = orders.Where(o => o.IsOpen).ToList();
        var items = open
            .SelectMany(o => o.Items.Select(i => (Key: HaPayloadBuilder.ItemKey(HaPayloadBuilder.TransactionDetail(i, o, timeZone)), Item: i, Order: o)))
            .OrderBy(x => x.Item.ExpectedShipAtUtc ?? x.Order.ShipByUtc ?? DateTime.MaxValue)
            .ThenBy(x => x.Order.EtsyCreatedAtUtc ?? DateTime.MaxValue).ThenBy(x => x.Item.EtsyTransactionId)
            .ToLookup(x => x.Key, x => x.Item);
        return HaPayloadBuilder.OpenOrders(open, timeZone)["items_needed"]!.AsArray()
            .Select(n => n!.AsObject())
            .Select(row => new ToMakeRow(row, [.. items[HaPayloadBuilder.ItemKey(row)]]))
            .ToList();
    }
}

/// <summary>
/// Marks order items as made. Kept in this app only (nothing goes to Etsy); open pages hear about it through
/// <see cref="Changed"/>, so the Orders page, its To make tab and the dashboard agree in every open browser.
/// </summary>
public sealed class OrderMadeService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>Raised after made counts are saved, with the source passed to <see cref="SetMadeAsync"/>.</summary>
    public event Action<object?>? Changed;

    /// <summary>
    /// Spreads <paramref name="made"/> units over <paramref name="items"/> in the order given (each up to its quantity),
    /// so the first items fill first; the rest are unmarked.
    /// </summary>
    public static void Distribute(IReadOnlyList<OrderItem> items, int made)
    {
        var left = Math.Max(0, made);
        foreach (var item in items)
        {
            item.MadeQuantity = Math.Min(left, item.UnitsToMake);
            left -= item.MadeQuantity;
        }
    }

    /// <summary>Sets how many of these items' units are made (see <see cref="Distribute"/>) and saves it.</summary>
    public async Task SetMadeAsync(IReadOnlyList<OrderItem> items, int made, object? source = null, CancellationToken ct = default)
    {
        Distribute(items, made);
        var byId = items.ToDictionary(i => i.Id, i => i.MadeQuantity);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = byId.Keys.ToList();
        foreach (var row in await db.OrderItems.Where(i => ids.Contains(i.Id)).ToListAsync(ct))
            row.MadeQuantity = byId[row.Id];
        await db.SaveChangesAsync(ct);
        Notify(source);
    }

    /// <summary>
    /// Sets how many units of one order item are made, found by Etsy's receipt and transaction ids (the Home Assistant
    /// card's Made checkboxes). Returns the saved count (capped to the quantity), or null when there is no such item.
    /// </summary>
    public async Task<int?> SetMadeAsync(long etsyReceiptId, long etsyTransactionId, int made, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.OrderItems.FirstOrDefaultAsync(i => i.EtsyTransactionId == etsyTransactionId && i.Order.EtsyReceiptId == etsyReceiptId, ct);
        if (item is null) return null;
        item.MadeQuantity = Math.Clamp(made, 0, item.UnitsToMake);
        await db.SaveChangesAsync(ct);
        Notify(null);
        return item.MadeQuantity;
    }

    private void Notify(object? source)
    {
        // A page that went away mid-call must not fail the save for the page that made it.
        foreach (var handler in Changed?.GetInvocationList().Cast<Action<object?>>() ?? [])
        {
            try { handler(source); }
            catch (ObjectDisposedException) { }
        }
    }
}
