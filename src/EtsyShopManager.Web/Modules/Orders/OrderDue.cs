using EtsyShopManager.Core.Domain;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>Order display helpers shared by the Orders page and the dashboard's order modules.</summary>
public static class OrderDue
{
    private const double GreenHours = 72, RedHours = 12;

    /// <summary>
    /// "ship in 2d" / "overdue 5h", urgent under 12 h. <c>Style</c> sets <c>--due</c>, how green the pill is: 100% at
    /// 72 h or more, 0% (red) at 12 h or less, blending through yellow in between, as the Home Assistant card's
    /// green_hours/red_hours do. Null without a ship-by date (a grey pill).
    /// </summary>
    public static (string Text, bool Urgent, string? Style) For(DateTime? shipByUtc, DateTime nowUtc)
    {
        if (shipByUtc is null) return ("no ship-by", false, null);
        var hours = (shipByUtc.Value - nowUtc).TotalHours;
        var text = hours < 0 ? $"overdue {Span(-hours)}" : $"ship in {Span(hours)}";
        var green = Math.Clamp((hours - RedHours) / (GreenHours - RedHours), 0, 1);
        return (text, hours <= RedHours, FormattableString.Invariant($"--due: {green * 100:0}%"));
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
