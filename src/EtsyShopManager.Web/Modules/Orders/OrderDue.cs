using EtsyShopManager.Core.Domain;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>Order display helpers shared by the Orders page and the dashboard's order modules.</summary>
public static class OrderDue
{
    private const double GreenHours = 72, RedHours = 12;

    /// <summary>
    /// The ship-by pill as the Home Assistant card shows it: "2d 5h", "5h 12m" or "40m" to go, "… late" once past, "—"
    /// without a date; urgent under 12 h. <c>Style</c> sets <c>--due-hue</c>, the card's colour: 120 (green) at 72 h or
    /// more, 0 (red) at 12 h or less, linear in between (the card's green_hours/red_hours defaults).
    /// </summary>
    public static (string Text, bool Urgent, string? Style) For(DateTime? shipByUtc, DateTime nowUtc)
    {
        if (shipByUtc is null) return ("—", false, null);
        var seconds = (shipByUtc.Value - nowUtc).TotalSeconds;
        var hours = seconds / 3600;
        var hue = Math.Round(120 * Math.Clamp((hours - RedHours) / (GreenHours - RedHours), 0, 1), MidpointRounding.AwayFromZero);
        var span = Span(Math.Abs(seconds));
        return (seconds < 0 ? $"{span} late" : span, hours < RedHours, FormattableString.Invariant($"--due-hue: {hue}"));
    }

    private static string Span(double seconds)
    {
        var d = (long)(seconds / 86400);
        var h = (long)(seconds % 86400 / 3600);
        var m = (long)(seconds % 3600 / 60);
        return d > 0 ? $"{d}d {h}h" : h > 0 ? $"{h}h {m}m" : $"{m}m";
    }

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
