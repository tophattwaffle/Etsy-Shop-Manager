using System.Globalization;
using System.Text.RegularExpressions;
using EtsyShopManager.Core.Services;

namespace EtsyShopManager.Web.Modules.Shops;

/// <summary>
/// Per-shop order link template used on the Orders page so each shop's receipts can open in its own browser or profile
/// (for example <c>microsoft-edge:https://…</c> or <c>esm-chrome://Profile%201/https://…</c>), matching the Home
/// Assistant card's <c>order_url</c> behaviour. Stored in app settings (<c>Shops.OrderUrl.{id}</c>).
/// </summary>
public sealed partial class ShopOrderLinkService(ISettingsStore settings, IAuditLog audit)
{
    public const string Placeholder = "{receipt_id}";
    public const string DefaultTemplate = "https://www.etsy.com/your/orders/sold?order_id={receipt_id}";
    public const string EdgeTemplate = "microsoft-edge:https://www.etsy.com/your/orders/sold?order_id={receipt_id}";
    public const string ChromeScheme = "esm-chrome";
    public const string DefaultChromeProfile = "Default";

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]*:")]
    private static partial Regex SchemePrefix();

    [GeneratedRegex(@"^esm-chrome://([^/]+)/https://www\.etsy\.com/your/orders/sold\?order_id=\{receipt_id\}$", RegexOptions.IgnoreCase)]
    private static partial Regex ChromeTemplatePattern();

    public static string SettingKey(int shopId) => $"Shops.OrderUrl.{shopId}";

    public static string ChromeTemplate(string? profile)
    {
        var p = string.IsNullOrWhiteSpace(profile) ? DefaultChromeProfile : profile.Trim();
        return $"{ChromeScheme}://{Uri.EscapeDataString(p)}/{DefaultTemplate}";
    }

    public static bool TryGetChromeProfile(string? template, out string profile)
    {
        if (template is not null && ChromeTemplatePattern().Match(template.Trim()) is { Success: true } m)
        {
            profile = Uri.UnescapeDataString(m.Groups[1].Value);
            return true;
        }
        profile = DefaultChromeProfile;
        return false;
    }

    public static bool IsValidTemplate(string? value, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = null;
            return true;
        }

        var trimmed = value.Trim();
        if (!trimmed.Contains(Placeholder, StringComparison.OrdinalIgnoreCase) &&
            !trimmed.Contains("%7Breceipt_id%7D", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Order link template must include {Placeholder}.";
            return false;
        }

        if (!SchemePrefix().IsMatch(trimmed) ||
            trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))
        {
            error = "Order link template must start with https:, microsoft-edge:, esm-chrome: or another browser scheme.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Substitutes <c>{receipt_id}</c> (or its URL-encoded form) into the shop's template, or the default Etsy URL.</summary>
    public static string FormatUrl(string? template, long receiptId)
    {
        var t = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template.Trim();
        var id = receiptId.ToString(CultureInfo.InvariantCulture);
        return t.Replace(Placeholder, id, StringComparison.OrdinalIgnoreCase)
                .Replace("%7Breceipt_id%7D", id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Only web links (<c>http:</c>/<c>https:</c>) open in a new tab; OS protocol links (<c>microsoft-edge:</c>,
    /// <c>esm-chrome:</c>) hand off to another browser and would leave an empty tab behind if given <c>target="_blank"</c>.
    /// </summary>
    public static bool OpensInNewTab(string url) =>
        url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("http:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Custom order URL templates keyed by shop id; shops using the default web URL are absent.</summary>
    public async Task<Dictionary<int, string>> GetOverridesAsync(IEnumerable<int> shopIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, string>();
        foreach (var id in shopIds.Distinct())
            if (await settings.GetAsync(SettingKey(id), ct) is { } value && !string.IsNullOrWhiteSpace(value) && IsValidTemplate(value, out _))
                result[id] = value.Trim();
        return result;
    }

    /// <summary>Sets the shop's order link template, or with null/empty goes back to the default Etsy web link.</summary>
    public async Task SetAsync(int shopId, string? template, string? userName, CancellationToken ct = default)
    {
        if (!IsValidTemplate(template, out var error))
            throw new ArgumentException(error, nameof(template));

        var value = string.IsNullOrWhiteSpace(template) || string.Equals(template.Trim(), DefaultTemplate, StringComparison.OrdinalIgnoreCase)
            ? null
            : template.Trim();
        await settings.SetAsync(SettingKey(shopId), value, updatedBy: userName, ct: ct);
        await audit.WriteAsync("shop.order_url", "Shop", shopId.ToString(CultureInfo.InvariantCulture), value ?? "default", userName, ct);
    }

    /// <summary>
    /// One-line Windows PowerShell command that registers the <c>esm-chrome://</c> protocol handler under the current
    /// Windows user account (no elevation required) so Chrome or a specific Chrome profile can be launched from links.
    /// </summary>
    public static string WindowsChromeHandlerCommand =>
        """$dir = "$env:LOCALAPPDATA\EtsyShopManager"; New-Item -ItemType Directory -Force -Path $dir | Out-Null; Set-Content -Path "$dir\esm-chrome.ps1" -Value 'param([string]$u); if ($u -match "^esm-chrome://([^/]+)/(https?://.+)$") { $p = [uri]::UnescapeDataString($Matches[1]); $c = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe","${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe","$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1; if ($c) { Start-Process $c -ArgumentList "--profile-directory=`"$p`"", $Matches[2] } }'; New-Item -Path "HKCU:\Software\Classes\esm-chrome\shell\open\command" -Force | Out-Null; New-ItemProperty -Path "HKCU:\Software\Classes\esm-chrome" -Name "URL Protocol" -Value "" -Force | Out-Null; Set-ItemProperty -Path "HKCU:\Software\Classes\esm-chrome\shell\open\command" -Name "(Default)" -Value "powershell.exe -NoProfile -WindowStyle Hidden -File `"$dir\esm-chrome.ps1`" `"%1`"" """;
}
