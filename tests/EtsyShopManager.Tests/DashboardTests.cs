using EtsyShopManager.Core.Services;
using EtsyShopManager.Web.Modules.Dashboard;
using Microsoft.AspNetCore.Components;

namespace EtsyShopManager.Tests;

public class DashboardTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string?> Values { get; } = [];
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(string key, string? value, bool encrypt = false, string? updatedBy = null, CancellationToken ct = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> HasValueAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.ContainsKey(key));
    }

    [Fact]
    public void Every_module_is_a_component_that_fits_its_own_minimum()
    {
        Assert.Equal(["shops", "jobs", "etsy-api", "open-orders", "to-make"], DashboardModules.All.Select(m => m.Id));
        foreach (var m in DashboardModules.All)
        {
            Assert.True(typeof(IComponent).IsAssignableFrom(m.Component), m.Id);
            Assert.InRange(m.MinWidth, 1, m.Width);
            Assert.InRange(m.MinHeight, 1, m.Height);
            Assert.InRange(m.Width, 1, DashboardModules.Columns);
        }
    }

    [Fact]
    public void Layouts_are_cleaned_before_use()
    {
        var layout = DashboardModules.Normalize(
        [
            new("to-make", 1, 1),          // too small: raised to its minimum
            new("gone", 4, 4),             // unknown module: dropped
            new("to-make", 6, 6),          // repeat: dropped
            new("shops", 99, 99),          // too big: capped at the grid
        ]);

        Assert.Equal([new DashboardItem("to-make", 4, 3), new DashboardItem("shops", DashboardModules.Columns, DashboardModules.MaxHeight)], layout);
    }

    [Fact]
    public async Task Each_user_keeps_their_own_layout_until_they_reset_it()
    {
        var layouts = new DashboardLayoutService(new MemorySettings());
        Assert.Equal(DashboardModules.Default, await layouts.GetAsync("tophattwaffle"));

        await layouts.SaveAsync("tophattwaffle", [new("open-orders", 12, 8), new("shops", 6, 3)]);

        Assert.Equal([new DashboardItem("open-orders", 12, 8), new DashboardItem("shops", 6, 3)], await layouts.GetAsync("tophattwaffle"));
        Assert.Equal(DashboardModules.Default, await layouts.GetAsync("someone-else"));

        await layouts.ResetAsync("tophattwaffle");
        Assert.Equal(DashboardModules.Default, await layouts.GetAsync("tophattwaffle"));
    }

    [Fact]
    public async Task Unsigned_in_users_share_the_anonymous_dashboard_layout()
    {
        var layouts = new DashboardLayoutService(new MemorySettings());
        var custom = new DashboardItem[] { new("to-make", 12, 8), new("open-orders", 12, 6) };

        await layouts.SaveAsync(null, custom);

        Assert.Equal(custom, await layouts.GetAsync(""));
        Assert.Equal(custom, await layouts.GetAsync("   "));
        Assert.Equal(custom, await layouts.GetAsync(DashboardLayoutService.AnonymousUserId));
        Assert.Equal(DashboardModules.Default, await layouts.GetAsync("tophattwaffle"));

        await layouts.ResetAsync("");
        Assert.Equal(DashboardModules.Default, await layouts.GetAsync(DashboardLayoutService.AnonymousUserId));
    }

    [Fact]
    public async Task An_unreadable_saved_layout_falls_back_to_the_default()
    {
        var settings = new MemorySettings();
        settings.Values[DashboardLayoutService.SettingKey("tophattwaffle")] = "{not json";
        Assert.Equal(DashboardModules.Default, await new DashboardLayoutService(settings).GetAsync("tophattwaffle"));
    }
}
