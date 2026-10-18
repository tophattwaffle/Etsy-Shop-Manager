using EtsyShopManager.Core.Domain;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>
/// Sort and filters for the Open orders tab, as in Etsy's Seller app. Open orders are already in memory, so this is a
/// pure in-memory pass (unit tested directly). Shipping and destination come from the stored receipt.
/// </summary>
public sealed record OpenOrderFilter
{
    public static class Sorts
    {
        public const string ShipBy = "shipby", Newest = "newest", Oldest = "oldest";
    }

    public static readonly (string Key, string Label)[] SortOptions =
        [(Sorts.ShipBy, "Ship by date"), (Sorts.Newest, "Newest"), (Sorts.Oldest, "Oldest")];

    public string Sort { get; init; } = Sorts.ShipBy;

    /// <summary>The shipping method chosen at checkout (see <see cref="OrderPrintService.ShippingMethod"/>); null for any.</summary>
    public string? Shipping { get; init; }

    /// <summary>Destination country (ISO code); null for anywhere.</summary>
    public string? Destination { get; init; }

    public bool HasNote { get; init; }
    public bool Gift { get; init; }
    public bool Personalized { get; init; }

    public bool IsFiltered => Shipping is not null || Destination is not null || HasNote || Gift || Personalized;

    public static bool HasBuyerNote(Order o) => !string.IsNullOrWhiteSpace(o.MessageFromBuyer);

    public static bool IsPersonalized(Order o) => o.Items.Any(i =>
        i.Variations.Any(v => v.PropertyId == HaPayloadBuilder.PersonalizationPropertyId && v.Value.Trim().Length > 0));

    public bool Matches(Order o) =>
        (Shipping is null || OrderPrintService.ShippingMethod(o) == Shipping)
        && (Destination is null || string.Equals(OrderPrintService.CountryIso(o), Destination, StringComparison.OrdinalIgnoreCase))
        && (!HasNote || HasBuyerNote(o))
        && (!Gift || o.IsGift)
        && (!Personalized || IsPersonalized(o));

    public List<Order> Apply(IEnumerable<Order> orders)
    {
        var matching = orders.Where(Matches);
        return (Sort switch
        {
            Sorts.Newest => matching.OrderByDescending(o => o.EtsyCreatedAtUtc).ThenByDescending(o => o.EtsyReceiptId),
            Sorts.Oldest => matching.OrderBy(o => o.EtsyCreatedAtUtc is null).ThenBy(o => o.EtsyCreatedAtUtc).ThenBy(o => o.EtsyReceiptId),
            // Soonest deadline first, orders without one last (the order the page always had).
            _ => matching.OrderBy(o => o.ShipByUtc is null).ThenBy(o => o.ShipByUtc).ThenByDescending(o => o.EtsyCreatedAtUtc),
        }).ToList();
    }
}
