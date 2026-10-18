using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>A buyer's shipping address as lines to print, from the stored receipt.</summary>
public sealed record ShippingAddress(string? Name, IReadOnlyList<string> Lines);

/// <summary>One line of a pick list: an item and its variations across the selected orders.</summary>
/// <param name="Personalizations">Each personalization with its order and quantity, printed under the line.</param>
public sealed record PickListLine(string Title, IReadOnlyList<string> Variations, string? Sku, string? ImageUrl, int Quantity,
    IReadOnlyList<long> Receipts, IReadOnlyList<(long Receipt, int Quantity, string Text)> Personalizations);

/// <summary>
/// Pick lists and packing slips printed from the Orders page. Read-only: printing never writes to Etsy or marks an order
/// shipped. Everything comes from what the order poller already stores (the receipt JSON holds the address).
/// </summary>
public sealed class OrderPrintService(IDbContextFactory<AppDbContext> dbFactory, ISettingsStore settings, IAuditLog audit)
{
    public const int MaxThanksLength = 300;

    public static string ThanksKey(int shopId) => $"Shops.SlipThanks.{shopId}";

    /// <summary>The orders, in ship-by order (then receipt), with their items and shop.</summary>
    public async Task<List<Order>> GetOrdersAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = ids.ToList();
        var orders = await db.Orders.AsNoTracking().Include(o => o.Items).Include(o => o.Shop)
            .Where(o => list.Contains(o.Id)).ToListAsync(ct);
        return [.. orders.OrderBy(o => o.ShipByUtc ?? DateTime.MaxValue).ThenBy(o => o.EtsyReceiptId)];
    }

    /// <summary>Parses the comma-separated id list the Orders page puts in the print link.</summary>
    public static IReadOnlyList<int> ParseIds(string? ids) =>
        [.. (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : 0).Where(id => id > 0).Distinct()];

    public async Task<Dictionary<int, string>> GetThanksAsync(IEnumerable<int> shopIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, string>();
        foreach (var id in shopIds.Distinct())
            if (await settings.GetAsync(ThanksKey(id), ct) is { Length: > 0 } text) result[id] = text;
        return result;
    }

    public async Task<string?> SetThanksAsync(int shopId, string? text, string? user, CancellationToken ct = default)
    {
        var value = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (value?.Length > MaxThanksLength) return $"Keep the thank-you line to {MaxThanksLength} characters.";
        await settings.SetAsync(ThanksKey(shopId), value, updatedBy: user, ct: ct);
        await audit.WriteAsync("shop.slip-thanks", nameof(Shop), shopId.ToString(), value is null ? "cleared" : "set", user, ct);
        return null;
    }

    /// <summary>The shipping address from the receipt Etsy returned (its formatted address when there is one).</summary>
    public static ShippingAddress Address(Order order)
    {
        JsonObject? receipt;
        try { receipt = JsonNode.Parse(order.RawJson) as JsonObject; }
        catch (System.Text.Json.JsonException) { receipt = null; }
        string? S(string key) => receipt?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;
        var name = S("name") ?? order.BuyerName;
        if (S("formatted_address") is { } formatted)
        {
            var lines = formatted.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (lines.Count > 0 && string.Equals(lines[0], name, StringComparison.OrdinalIgnoreCase)) lines.RemoveAt(0);
            return new ShippingAddress(name, lines);
        }
        var cityLine = string.Join(" ", new[] { string.Join(", ", new[] { S("city"), S("state") }.Where(x => x is not null)), S("zip") }
            .Where(x => !string.IsNullOrEmpty(x)));
        return new ShippingAddress(name, [.. new[] { S("first_line"), S("second_line"), cityLine, S("country_iso") }
            .Where(x => !string.IsNullOrEmpty(x)).Select(x => x!)]);
    }

    /// <summary>An item's variations (as "Name: Value") and its personalization, split apart.</summary>
    public static (List<string> Variations, string? Personalization) Split(OrderItem item)
    {
        var personalization = item.Variations.Where(v => v.PropertyId == HaPayloadBuilder.PersonalizationPropertyId && v.Value.Length > 0)
            .Select(v => v.Value).ToList();
        return ([.. item.Variations.Where(v => v.PropertyId != HaPayloadBuilder.PersonalizationPropertyId && v.Value.Length > 0)
                .Select(v => v.Name.Length > 0 ? $"{v.Name}: {v.Value}" : v.Value)],
            personalization.Count == 0 ? null : string.Join(" | ", personalization));
    }

    /// <summary>The title without Etsy's keyword tail ("Mini sign | gift | decor" → "Mini sign").</summary>
    public static string ShortTitle(string? title) => (title ?? "").Split('|')[0].Trim();

    /// <summary>
    /// Every item of the orders grouped by listing and variations (personalization doesn't split a line; each one is listed
    /// under it with its order), most needed first.
    /// </summary>
    public static IReadOnlyList<PickListLine> PickList(IEnumerable<Order> orders) =>
        [.. orders.SelectMany(o => o.Items.Select(i => (Order: o, Item: i, Parts: Split(i))))
            .GroupBy(x => (x.Item.EtsyListingId, Title: ShortTitle(x.Item.Title), Variations: string.Join("\u0001", x.Parts.Variations)))
            .Select(g => new PickListLine(
                g.Key.Title,
                g.First().Parts.Variations,
                g.Select(x => x.Item.Sku).FirstOrDefault(s => !string.IsNullOrEmpty(s)),
                g.Select(x => x.Item.ImageUrl).FirstOrDefault(u => !string.IsNullOrEmpty(u)),
                g.Sum(x => Math.Max(1, x.Item.Quantity)),
                [.. g.Select(x => x.Order.EtsyReceiptId).Distinct().Order()],
                [.. g.Where(x => x.Parts.Personalization is not null)
                    .Select(x => (x.Order.EtsyReceiptId, Math.Max(1, x.Item.Quantity), x.Parts.Personalization!))]))
            .OrderByDescending(l => l.Quantity).ThenBy(l => l.Title, StringComparer.OrdinalIgnoreCase)];
}
