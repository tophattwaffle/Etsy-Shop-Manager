using EtsyShopManager.Core.Domain;
using MudBlazor;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>Order display helpers for the dashboard's order modules (the same rules as the Orders page).</summary>
public static class OrderDue
{
    /// <summary>"ship in 2d" / "overdue 5h", green, then yellow under 72 h and red (urgent) under 12 h.</summary>
    public static (string Text, Color Color, bool Urgent) For(DateTime? shipByUtc, DateTime nowUtc)
    {
        if (shipByUtc is null) return ("no ship-by", Color.Default, false);
        var hours = (shipByUtc.Value - nowUtc).TotalHours;
        var text = hours < 0 ? $"overdue {Span(-hours)}" : $"ship in {Span(hours)}";
        return hours <= 12 ? (text, Color.Error, true) : hours <= 72 ? (text, Color.Warning, false) : (text, Color.Success, false);
    }

    private static string Span(double hours) => hours >= 48 ? $"{hours / 24:0}d" : hours >= 1 ? $"{hours:0}h" : $"{hours * 60:0}m";

    /// <summary>A listing title up to its first "|" (Etsy titles often add keywords after one).</summary>
    public static string ShortTitle(string? title) => (title ?? "").Split('|')[0].Trim();

    /// <summary>An item's variations, and its personalization as one line.</summary>
    public static (List<OrderVariation> Variations, string? Personalization) Split(OrderItem i)
    {
        var personalization = i.Variations.Where(v => v.PropertyId == HaPayloadBuilder.PersonalizationPropertyId && v.Value.Length > 0)
            .Select(v => v.Name.Length > 0 && !v.Name.Trim().Equals("personalization", StringComparison.OrdinalIgnoreCase) ? $"{v.Name}: {v.Value}" : v.Value)
            .ToList();
        return (i.Variations.Where(v => v.PropertyId != HaPayloadBuilder.PersonalizationPropertyId && v.Value.Length > 0).ToList(),
            personalization.Count == 0 ? null : string.Join(" | ", personalization));
    }
}
