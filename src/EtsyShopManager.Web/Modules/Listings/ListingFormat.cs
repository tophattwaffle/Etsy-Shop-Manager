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

    /// <summary>Etsy's style for the listing cards: "$24.00", or "24.00 CAD" for currencies without a clear symbol.</summary>
    public static string PriceWithSymbol(long amount, int divisor, string? currency)
    {
        var symbol = currency switch
        {
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            _ => null,
        };
        return symbol is null ? Price(amount, divisor, currency) : symbol + Price(amount, divisor, null);
    }

    /// <summary>A short relative time ("5 min ago", "3 h ago", "2 days ago"), or the date once it is over a week old.</summary>
    public static string Ago(DateTime? utc, DateTime nowUtc)
    {
        if (utc is null) return "never";
        var age = nowUtc - utc.Value;
        return age switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{(int)age.TotalMinutes} min ago",
            { TotalDays: < 1 } => $"{(int)age.TotalHours} h ago",
            { TotalDays: < 2 } => "yesterday",
            { TotalDays: < 7 } => $"{(int)age.TotalDays} days ago",
            _ => Local(utc, "yyyy-MM-dd"),
        };
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
