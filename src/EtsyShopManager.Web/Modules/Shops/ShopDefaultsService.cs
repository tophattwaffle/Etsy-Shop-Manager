using System.Globalization;
using System.Text.Json;
using EtsyShopManager.Core.Services;

namespace EtsyShopManager.Web.Modules.Shops;

/// <summary>
/// Manages per-shop default profiles (shipping profile, processing profile, return policy, shop section, etc.).
/// Persisted in application settings under "Shops.Defaults.{shopId}".
/// </summary>
public sealed class ShopDefaultsService(ISettingsStore settings, IAuditLog audit)
{
    public static string SettingKey(int shopId) => $"Shops.Defaults.{shopId}";

    public async Task<ShopDefaults?> GetAsync(int shopId, CancellationToken ct = default)
    {
        var json = await settings.GetAsync(SettingKey(shopId), ct);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ShopDefaults>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(int shopId, ShopDefaults defaults, string? user, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(defaults);
        await settings.SetAsync(SettingKey(shopId), json, updatedBy: user, ct: ct);
        await audit.WriteAsync("shop.defaults-updated", "Shop", shopId.ToString(CultureInfo.InvariantCulture), json, user, ct);
    }
}
