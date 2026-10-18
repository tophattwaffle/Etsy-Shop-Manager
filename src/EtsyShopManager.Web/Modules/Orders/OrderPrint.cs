using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>A buyer's shipping address as lines to print, from the stored receipt.</summary>
public sealed record ShippingAddress(string? Name, IReadOnlyList<string> Lines);

/// <summary>Payment, shipping method and totals for a packing slip (see <see cref="OrderPrintService.Details"/>).</summary>
public sealed record SlipDetails(string? PaymentMethod, string? ShippingMethod, IReadOnlyList<(string Label, string Amount)> Totals,
    string OrderTotal);

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
    public const int MaxNoteLength = 500;

    /// <summary>The shop's packing slip note ("A note from the shop"). The key is from when it was a thank-you line, so notes saved then still apply.</summary>
    public static string NoteKey(int shopId) => $"Shops.SlipThanks.{shopId}";

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

    public async Task<Dictionary<int, string>> GetNotesAsync(IEnumerable<int> shopIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, string>();
        foreach (var id in shopIds.Distinct())
            if (await settings.GetAsync(NoteKey(id), ct) is { Length: > 0 } text) result[id] = text;
        return result;
    }

    public async Task<string?> SetNoteAsync(int shopId, string? text, string? user, CancellationToken ct = default)
    {
        var value = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (value?.Length > MaxNoteLength) return $"Keep the packing slip note to {MaxNoteLength} characters.";
        await settings.SetAsync(NoteKey(shopId), value, updatedBy: user, ct: ct);
        await audit.WriteAsync("shop.slip-thanks", nameof(Shop), shopId.ToString(), value is null ? "cleared" : "set", user, ct);
        return null;
    }

    private static JsonObject? Receipt(Order order)
    {
        try { return JsonNode.Parse(order.RawJson) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    /// <summary>
    /// What Etsy's own packing slip shows besides the address and items, from the stored receipt (Etsy's ShopReceipt):
    /// payment method, the shipping method chosen at checkout (a transaction's shipping upgrade or method; null when Etsy
    /// sent neither) and the totals. The buyer's Etsy username isn't in a receipt, so it isn't shown.
    /// </summary>
    public static SlipDetails Details(Order order)
    {
        var receipt = Receipt(order);
        var payment = Text(receipt?["payment_method"]) switch
        {
            null => null,
            "paypal" => "Paid via PayPal",
            "check" => "Paid by check",
            "mo" => "Paid by money order",
            "other" => "Paid another way",
            _ => "Paid via Etsy Payments", // cards, Apple/Google Pay, Klarna, iDEAL, Sofort, bank transfer
        };
        var shipping = ShippingMethod(receipt);

        string? Money(string key, bool negative = false) => receipt?[key] is JsonObject m && (long?)m["amount"] is long amount and > 0
            ? (negative ? "-" : "") + Listings.ListingFormat.PriceWithSymbol(amount, (int?)m["divisor"] ?? order.Divisor, (string?)m["currency_code"] ?? order.CurrencyCode)
            : null;
        var itemTotal = Money("total_price")
            ?? Listings.ListingFormat.PriceWithSymbol(order.Items.Sum(i => i.PriceAmount * Math.Max(1, i.Quantity)), order.Items.FirstOrDefault()?.PriceDivisor ?? order.Divisor, order.CurrencyCode);
        var totals = new List<(string Label, string Amount)> { ("Item total", itemTotal) };
        foreach (var (key, label, negative) in new[] { ("discount_amt", "Discount", true), ("gift_wrap_price", "Gift wrap", false),
                     ("total_tax_cost", "Tax", false), ("total_vat_cost", "VAT", false), ("total_shipping_cost", "Shipping total", false) })
            if (Money(key, negative) is { } amount) totals.Add((label, amount));
        var grand = Money("grandtotal") ?? (order.Grandtotal is { } g ? Listings.ListingFormat.PriceWithSymbol(g, order.Divisor, order.CurrencyCode) : itemTotal);
        return new SlipDetails(payment, shipping, totals, grand);
    }

    /// <summary>The shipping method chosen at checkout: a transaction's shipping upgrade or method (null when Etsy sent neither).</summary>
    public static string? ShippingMethod(Order order) => ShippingMethod(Receipt(order));

    private static string? ShippingMethod(JsonObject? receipt) => (receipt?["transactions"] as JsonArray ?? [])
        .Select(t => Text(t?["shipping_upgrade"]) ?? Text(t?["shipping_method"])).FirstOrDefault(s => s is not null);

    /// <summary>The destination country's ISO code from the receipt (upper case; null when missing).</summary>
    public static string? CountryIso(Order order) => Text(Receipt(order)?["country_iso"])?.ToUpperInvariant();

    /// <summary>Who the gift is from, as the buyer wrote it at checkout (the receipt's gift_sender; null when not given).</summary>
    public static string? GiftSender(Order order) => Text(Receipt(order)?["gift_sender"]);

    /// <summary>
    /// A QR code (inline SVG, sized by CSS) linking to the shop's Etsy page, for the bottom of gift slips. Drawn here, so
    /// the printout needs nothing from outside the app.
    /// </summary>
    public static string ShopQrSvg(Shop shop)
    {
        using var data = new QRCoder.QRCodeGenerator().CreateQrCode($"https://{ShopAddress(shop)}", QRCoder.QRCodeGenerator.ECCLevel.M);
        return new QRCoder.SvgQRCode(data).GetGraphic(4, "#000000", "#ffffff", drawQuietZones: false,
            sizingMode: QRCoder.SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    /// <summary>The shop's Etsy address as Etsy prints it on slips ("name.etsy.com").</summary>
    public static string ShopAddress(Shop shop) => $"{shop.Name.ToLowerInvariant()}.etsy.com";

    /// <summary>The shipping address from the receipt Etsy returned (its formatted address when there is one).</summary>
    public static ShippingAddress Address(Order order)
    {
        var receipt = Receipt(order);
        string? S(string key) => Text(receipt?[key]);
        var name = S("name") ?? order.BuyerName;
        if (S("formatted_address") is { } formatted)
        {
            var lines = formatted.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (lines.Count > 0 && string.Equals(lines[0], name, StringComparison.OrdinalIgnoreCase)) lines.RemoveAt(0);
            return new ShippingAddress(name, lines);
        }
        var cityLine = string.Join(" ", new[] { string.Join(", ", new[] { S("city"), S("state") }.Where(x => x is not null)), S("zip") }
            .Where(x => !string.IsNullOrEmpty(x)));
        return new ShippingAddress(name, [.. new[] { S("first_line"), S("second_line"), cityLine, CountryName(S("country_iso")) }
            .Where(x => !string.IsNullOrEmpty(x)).Select(x => x!)]);
    }

    /// <summary>"US" → "United States" (as Etsy prints it); unknown codes as they are.</summary>
    public static string? CountryName(string? iso)
    {
        if (iso is null) return null;
        try { return new System.Globalization.RegionInfo(iso).EnglishName; }
        catch (ArgumentException) { return iso; }
    }

    /// <summary>The price of one item line as Etsy's slip shows it ("2 x $15.99").</summary>
    public static string LinePrice(OrderItem item) =>
        $"{Math.Max(1, item.Quantity)} x {Listings.ListingFormat.PriceWithSymbol(item.PriceAmount, item.PriceDivisor, item.CurrencyCode)}";

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
