using EtsyShopManager.Core.Domain;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>
/// Changes a buyer asked for after checkout, kept on the local order only (Etsy's API can't change an order): a new
/// personalization, other variation values, another shipping address. Everything that shows an order (Orders page, To make,
/// pick lists, packing slips, Home Assistant) reads items through <see cref="Variations"/>, so they all show the change.
/// </summary>
public static class OrderOverrides
{
    public const int MaxPersonalizationLength = 1024;
    public const int MaxAddressLength = 500;
    public const int MaxSellerNoteLength = 1000;
    public const int MaxNoteLength = 1000;

    /// <summary>
    /// The item's variations as they should be made: the seller's chosen values in place of the buyer's, and the seller's
    /// personalization in place of the buyer's (where the buyer's was, or last when there was none).
    /// </summary>
    public static List<OrderVariation> Variations(OrderItem item)
    {
        if (item.PersonalizationOverride is null && item.VariationOverrides.Count == 0) return item.Variations;

        var result = new List<OrderVariation>();
        var personalizationAt = -1;
        foreach (var v in item.Variations)
        {
            if (v.PropertyId == HaPayloadBuilder.PersonalizationPropertyId && item.PersonalizationOverride is not null)
            {
                if (personalizationAt < 0) personalizationAt = result.Count;
                continue;
            }
            result.Add(VariationOverride(item, v) ?? v);
        }

        if (item.PersonalizationOverride is { Length: > 0 } text)
        {
            var name = item.Variations.FirstOrDefault(v => v.PropertyId == HaPayloadBuilder.PersonalizationPropertyId)?.Name;
            result.Insert(personalizationAt < 0 ? result.Count : personalizationAt, new OrderVariation
            {
                PropertyId = HaPayloadBuilder.PersonalizationPropertyId,
                Name = string.IsNullOrEmpty(name) ? "Personalization" : name,
                Value = text,
            });
        }
        return result;
    }

    /// <summary>The seller's value for one of the buyer's variations, or null when the buyer's stands.</summary>
    public static OrderVariation? VariationOverride(OrderItem item, OrderVariation buyers) =>
        buyers.PropertyId == HaPayloadBuilder.PersonalizationPropertyId
            ? null
            : item.VariationOverrides.FirstOrDefault(o => o.PropertyId == buyers.PropertyId && o.Name == buyers.Name);

    /// <summary>The buyer's own personalization (several answers joined with " | "), or null when there was none.</summary>
    public static string? BuyersPersonalization(OrderItem item)
    {
        var values = item.Variations.Where(v => v.PropertyId == HaPayloadBuilder.PersonalizationPropertyId && v.Value.Length > 0)
            .Select(v => v.Value).ToList();
        return values.Count == 0 ? null : string.Join(" | ", values);
    }

    public static bool HasItemOverride(OrderItem item) => item.PersonalizationOverride is not null || item.VariationOverrides.Count > 0;

    public static bool HasOverride(Order order) =>
        order.MessageFromBuyerOverride is not null || order.AddressOverride is not null || order.Items.Any(HasItemOverride);

    /// <summary>
    /// The note from the buyer as it should be used: the seller's change when overridden (empty when cleared, returning
    /// null), otherwise the buyer's note from Etsy (null when none).
    /// </summary>
    public static string? Note(Order order) =>
        order.MessageFromBuyerOverride is not null
            ? (string.IsNullOrWhiteSpace(order.MessageFromBuyerOverride) ? null : order.MessageFromBuyerOverride)
            : (string.IsNullOrWhiteSpace(order.MessageFromBuyer) ? null : order.MessageFromBuyer);

    /// <summary>The overriding address as a name and lines (the first line is the name), or null when there's none.</summary>
    public static ShippingAddress? Address(Order order)
    {
        if (order.AddressOverride is null) return null;
        var lines = Lines(order.AddressOverride);
        return lines.Count == 0 ? null : new ShippingAddress(lines[0], [.. lines.Skip(1)]);
    }

    /// <summary>Address text as trimmed, non-empty lines.</summary>
    public static List<string> Lines(string? text) =>
        [.. (text ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>An address as the text the editor shows (the name, then each line).</summary>
    public static string AddressText(ShippingAddress address) =>
        string.Join("\n", (address.Name is null ? address.Lines : address.Lines.Prepend(address.Name)));
}
