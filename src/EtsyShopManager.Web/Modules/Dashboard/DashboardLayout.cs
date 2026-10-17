using System.Text.Json;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Web.Components.Dashboard;

namespace EtsyShopManager.Web.Modules.Dashboard;

/// <summary>
/// A kind of dashboard module: what it shows (<see cref="Component"/>), its size when added, and the smallest size at
/// which it still reads properly. Sizes are in grid cells: <see cref="DashboardModules.Columns"/> columns wide, rows of
/// a fixed height.
/// </summary>
public sealed record DashboardModuleKind(
    string Id, string Title, Type Component, int Width, int Height, int MinWidth, int MinHeight);

/// <summary>One module on a user's dashboard. The list order is the reading order (left to right, then down).</summary>
public sealed record DashboardItem(string Module, int Width, int Height);

/// <summary>The modules a dashboard can show, and the layout a new user starts with.</summary>
public static class DashboardModules
{
    public const int Columns = 12;
    public const int MaxHeight = 12;

    public static readonly IReadOnlyList<DashboardModuleKind> All =
    [
        new("shops", "Shops", typeof(ShopsTile), 6, 3, 4, 2),
        new("jobs", "Background jobs", typeof(JobsTile), 3, 3, 3, 3),
        new("etsy-api", "Etsy API", typeof(EtsyApiTile), 3, 3, 3, 3),
        new("open-orders", "Open orders", typeof(OpenOrdersTile), 6, 6, 4, 4),
        new("to-make", "To make", typeof(ToMakeTile), 6, 6, 4, 3),
    ];

    public static DashboardModuleKind? Find(string id) => All.FirstOrDefault(m => m.Id == id);

    public static IReadOnlyList<DashboardItem> Default { get; } = All.Select(m => new DashboardItem(m.Id, m.Width, m.Height)).ToList();

    /// <summary>
    /// A layout made safe to show: unknown modules and repeats dropped, every size clamped between the module's
    /// minimum and the grid's maximum (so a module is never too small to read).
    /// </summary>
    public static List<DashboardItem> Normalize(IEnumerable<DashboardItem>? items)
    {
        var seen = new HashSet<string>();
        var result = new List<DashboardItem>();
        foreach (var item in items ?? [])
        {
            if (Find(item.Module) is not { } kind || !seen.Add(kind.Id)) continue;
            result.Add(new DashboardItem(kind.Id,
                Math.Clamp(item.Width, kind.MinWidth, Columns),
                Math.Clamp(item.Height, kind.MinHeight, MaxHeight)));
        }
        return result;
    }
}

/// <summary>Each user's dashboard layout, kept as an app setting ("Dashboard.Layout.{userId}"), so no schema change.</summary>
public sealed class DashboardLayoutService(ISettingsStore settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string SettingKey(string userId) => $"Dashboard.Layout.{userId}";

    /// <summary>The user's saved layout, or the default one if they haven't saved one (or it can't be read).</summary>
    public async Task<List<DashboardItem>> GetAsync(string userId, CancellationToken ct = default)
    {
        if (await settings.GetAsync(SettingKey(userId), ct) is not { Length: > 0 } json) return [.. DashboardModules.Default];
        try
        {
            return DashboardModules.Normalize(JsonSerializer.Deserialize<List<DashboardItem>>(json, Json));
        }
        catch (JsonException)
        {
            return [.. DashboardModules.Default];
        }
    }

    public async Task SaveAsync(string userId, IEnumerable<DashboardItem> items, CancellationToken ct = default) =>
        await settings.SetAsync(SettingKey(userId), JsonSerializer.Serialize(DashboardModules.Normalize(items), Json),
            updatedBy: userId, ct: ct);

    /// <summary>Back to the default layout.</summary>
    public Task ResetAsync(string userId, CancellationToken ct = default) =>
        settings.SetAsync(SettingKey(userId), null, updatedBy: userId, ct: ct);
}

/// <summary>Registers the dashboard's layout store.</summary>
public sealed class DashboardModule : IModule
{
    public string Name => "Dashboard";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<DashboardLayoutService>();
}
