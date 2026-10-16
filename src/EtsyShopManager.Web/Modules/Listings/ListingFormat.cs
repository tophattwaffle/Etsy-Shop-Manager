using System.Globalization;
using MudBlazor;

namespace EtsyShopManager.Web.Modules.Listings;

/// <summary>Display helpers shared by the listing pages.</summary>
public static class ListingFormat
{
    public static string Price(long amount, int divisor, string? currency)
    {
        var value = divisor > 0 ? (decimal)amount / divisor : amount;
        var digits = divisor > 0 ? (int)Math.Round(Math.Log10(divisor)) : 0;
        return value.ToString("N" + digits, CultureInfo.CurrentCulture) + (currency is null ? "" : " " + currency);
    }

    public static Color StateColor(string state) => state switch
    {
        "active" => Color.Success,
        "draft" => Color.Info,
        "sold_out" => Color.Warning,
        "inactive" or "expired" => Color.Default,
        _ => Color.Default,
    };

    public static string StateLabel(string state) => state switch
    {
        "sold_out" => "sold out",
        _ => state,
    };

    public static string Local(DateTime? utc, string format = "yyyy-MM-dd HH:mm") =>
        utc is null ? "—" : utc.Value.ToLocalTime().ToString(format, CultureInfo.CurrentCulture);

    public static string Humanize(string? value) =>
        string.IsNullOrEmpty(value) ? "—" : value.Replace('_', ' ');
}
