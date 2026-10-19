using EtsyShopManager.Core.Modules;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Web.Modules.Navigation;

namespace EtsyShopManager.Tests;

/// <summary>Each user puts the sidebar in their own order; pages added later still show up near their default place.</summary>
public class SidebarOrderTests
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

    private static readonly NavItem[] Items =
    [
        new("Dashboard", "", "i", Order: 0, MatchAll: true),
        new("Orders", "orders", "i", Order: 15),
        new("Listings", "listings", "i", Order: 20),
        new("General", "admin/settings", "i", Order: 899, Group: NavGroups.Settings),
        new("Jobs", "jobs", "i", Order: 900, Group: NavGroups.Settings),
        new("Users", "admin/users", "i", Order: 910, Group: NavGroups.Settings),
    ];

    private static List<string> Titles(IEnumerable<SidebarEntry> entries) =>
        entries.SelectMany(e => e.Children.Select(c => "  " + c.Title).Prepend(e.Title)).ToList();

    [Fact]
    public void Without_a_saved_order_the_sidebar_keeps_the_default_order()
    {
        Assert.Equal(["Dashboard", "Orders", "Listings", "Settings", "  General", "  Jobs", "  Users"], Titles(SidebarLayout.Build(Items)));
    }

    [Fact]
    public void Pages_and_groups_move_among_their_neighbours_and_group_pages_within_the_group()
    {
        var sidebar = SidebarLayout.Build(Items);
        sidebar = SidebarLayout.MoveBy(sidebar, "group:Settings", -3);      // to the top
        sidebar = SidebarLayout.MoveBy(sidebar, "/jobs", 5);                 // last in its group, not out of it
        sidebar = SidebarLayout.MoveTo(sidebar, "/listings", "/");           // dropped on Dashboard: takes its place
        sidebar = SidebarLayout.MoveTo(sidebar, "/orders", "/admin/users");  // into another group: refused

        Assert.Equal(["Settings", "  General", "  Users", "  Jobs", "Listings", "Dashboard", "Orders"], Titles(sidebar));
        Assert.Equal(sidebar.Select(e => e.Key), SidebarLayout.Build(Items, SidebarLayout.Keys(sidebar)).Select(e => e.Key));
        Assert.Equal("group:Settings", SidebarLayout.ParentOf(sidebar, "/jobs"));
        Assert.Null(SidebarLayout.ParentOf(sidebar, "/orders"));
    }

    [Fact]
    public void A_page_missing_from_the_saved_order_goes_after_the_page_it_follows_by_default()
    {
        // Saved before Listings and Jobs existed; a page the user can't see (Lookups) is simply skipped.
        string[] saved = ["/orders", "/lookups", "/", "group:Settings", "/admin/users", "/admin/settings"];

        Assert.Equal(["Orders", "Listings", "Dashboard", "Settings", "  Users", "  General", "  Jobs"],
            Titles(SidebarLayout.Build(Items, saved)));
    }

    [Fact]
    public async Task Each_user_keeps_their_own_order_until_they_reset_it()
    {
        var settings = new MemorySettings();
        var orders = new SidebarOrderService(settings);
        Assert.Null(await orders.GetAsync("tophattwaffle"));

        await orders.SaveAsync("tophattwaffle", ["/orders", "/", "/orders"]);
        Assert.Equal(["/orders", "/"], await orders.GetAsync("tophattwaffle"));
        Assert.Null(await orders.GetAsync("someone-else"));

        await orders.ResetAsync("tophattwaffle");
        Assert.Null(await orders.GetAsync("tophattwaffle"));

        settings.Values[SidebarOrderService.SettingKey("tophattwaffle")] = "{not json";
        Assert.Null(await orders.GetAsync("tophattwaffle"));
    }
}
