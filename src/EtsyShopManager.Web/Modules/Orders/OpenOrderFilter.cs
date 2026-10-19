using EtsyShopManager.Core.Domain;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>
/// Sort and filters for the Open orders tab, as in Etsy's Seller app. Open orders are already in memory, so this is a
/// pure in-memory pass (unit tested directly). Shipping method and destination come from the stored receipt.
/// </summary>
public sealed record OpenOrderFilter
{
    public static class Sorts
    {
        public const string ShipBy = "shipby", Newest = "newest", Oldest = "oldest";
    }

    public static readonly (string Key, string Label)[] SortOptions =
        [(Sorts.ShipBy, "Ship by date"), (Sorts.Newest, "Newest"), (Sorts.Oldest, "Oldest")];

    public static class Destinations
    {
        public const string Domestic = "domestic", International = "international";
    }

    /// <summary>Made marks (Orders page, To make, the dashboard and the HA card): every item made, or something still to make.</summary>
    public static class MadeStates
    {
        public const string AllMade = "made", ToMake = "tomake";
    }

    /// <summary>Used for a shop whose shipping profiles name no origin country.</summary>
    public const string DefaultHomeCountry = "US";

    public string Sort { get; init; } = Sorts.ShipBy;

    /// <summary>The shipping method chosen at checkout (see <see cref="OrderPrintService.ShippingMethod(Order)"/>); null for any.</summary>
    public string? Shipping { get; init; }

    /// <summary><see cref="Destinations"/>: to the shop's own country or abroad; null for anywhere.</summary>
    public string? Destination { get; init; }

    /// <summary><see cref="MadeStates"/>: orders with every item made, or with items still to make; null for either.</summary>
    public string? Made { get; init; }

    /// <summary>Each shop's home country (ISO code), from where its shipping profiles ship from.</summary>
    public IReadOnlyDictionary<int, string> HomeCountries { get; init; } = new Dictionary<int, string>();

    public bool HasNote { get; init; }
    public bool Gift { get; init; }
    public bool Personalized { get; init; }

    public bool IsFiltered => Shipping is not null || Destination is not null || Made is not null || HasNote || Gift || Personalized;

    public static bool HasBuyerNote(Order o) => !string.IsNullOrWhiteSpace(o.MessageFromBuyer);

    public static bool IsPersonalized(Order o) => o.Items.Any(i =>
        OrderOverrides.Variations(i).Any(v => v.PropertyId == HaPayloadBuilder.PersonalizationPropertyId && v.Value.Trim().Length > 0));

    /// <summary>Every unit of every item is marked made.</summary>
    public static bool IsAllMade(Order o) => o.Items.All(i => i.UnitsMade >= i.UnitsToMake);

    public string HomeCountry(int shopId) => HomeCountries.GetValueOrDefault(shopId) ?? DefaultHomeCountry;

    /// <summary>Whether the order goes to its shop's own country; null when the receipt has no country.</summary>
    public bool? IsDomestic(Order o) => OrderPrintService.CountryIso(o) is { } iso
        ? string.Equals(iso, HomeCountry(o.ShopId), StringComparison.OrdinalIgnoreCase)
        : null;

    public bool Matches(Order o) =>
        (Shipping is null || OrderPrintService.ShippingMethod(o) == Shipping)
        && (Destination is null || IsDomestic(o) == (Destination == Destinations.Domestic))
        && (Made is null || IsAllMade(o) == (Made == MadeStates.AllMade))
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
