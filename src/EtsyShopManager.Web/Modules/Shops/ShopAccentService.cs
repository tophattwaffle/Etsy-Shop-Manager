using System.Text.RegularExpressions;
using EtsyShopManager.Core.Services;

namespace EtsyShopManager.Web.Modules.Shops;

/// <summary>
/// A shop's accent color: the ring, edge and tint that mark what belongs to it. By default the browser reads it from
/// the shop's profile image (esm.dominantColor in app.js); an Admin can override it on the Shops page. Overrides are
/// app settings ("Shops.AccentColor.{id}"), so they need no schema change.
/// </summary>
public sealed partial class ShopAccentService(ISettingsStore settings, IAuditLog audit)
{
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexPattern();

    /// <summary>What the color picker starts at for a shop with no color yet (no image, or not read yet).</summary>
    public const string PickerDefault = "#808080";

    public static string SettingKey(int shopId) => $"Shops.AccentColor.{shopId}";

    public static bool IsHex(string? value) => value is not null && HexPattern().IsMatch(value.Trim());

    /// <summary>The overridden shops' colors ("#RRGGBB"); shops using the automatic color are absent.</summary>
    public async Task<Dictionary<int, string>> GetOverridesAsync(IEnumerable<int> shopIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, string>();
        foreach (var id in shopIds.Distinct())
            if (await settings.GetAsync(SettingKey(id), ct) is { } value && IsHex(value))
                result[id] = value.Trim().ToUpperInvariant();
        return result;
    }

    /// <summary>Sets the shop's color, or with null goes back to the color read from its profile image.</summary>
    public async Task SetAsync(int shopId, string? hex, string? userName, CancellationToken ct = default)
    {
        if (hex is not null && !IsHex(hex)) throw new ArgumentException($"\"{hex}\" isn't a color like #B87333.", nameof(hex));
        var value = hex?.Trim().ToUpperInvariant();
        await settings.SetAsync(SettingKey(shopId), value, updatedBy: userName, ct: ct);
        await audit.WriteAsync("shop.accent_color", "Shop", shopId.ToString(), value ?? "automatic", userName, ct);
    }
}
