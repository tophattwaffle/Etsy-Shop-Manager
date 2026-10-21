using System.Reflection;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Web.Components.Pages.Templates;
using EtsyShopManager.Web.Modules;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Orders;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.AspNetCore.Components;

namespace EtsyShopManager.Tests;

/// <summary>The sidebar stays short: templates share one entry, and admin pages fold under one Settings heading.</summary>
public class NavigationTests
{
    private static readonly ModuleRegistry Registry = new(
    [
        new ShopsModule(), new ListingsModule(), new OrdersModule(),
        new EtsyShopManager.Web.Modules.Dashboard.DashboardModule(), new EtsyShopManager.Web.Modules.Stock.EditorModule(),
        new TemplatesModule(), new JobsModule(), new AdminModule(),
    ]);

    [Fact]
    public void Top_level_sidebar_has_one_Templates_entry_and_admin_pages_sit_under_Settings()
    {
        Assert.Equal(["Dashboard", "Orders", "Packing slips", "Listings", "Templates", "Lookups", "Order colors"],
            Registry.NavItems.Where(i => i.Group is null).Select(i => i.Title));
        Assert.Equal(["Shops", "General", "Jobs", "Users", "Audit log", "API keys", "Etsy writes", "Photo order test"],
            Registry.NavItems.Where(i => i.Group == NavGroups.Settings).Select(i => i.Title));
        Assert.All(Registry.NavItems.Where(i => i.Roles == Roles.Admin), i => Assert.Equal(NavGroups.Settings, i.Group));
    }

    [Fact]
    public void Template_pages_live_under_templates_and_the_old_addresses_still_open_them()
    {
        HasRoutes<Templates>("/templates", "/templates/{Tab}", "/text-blocks", "/variation-lists", "/photo-templates");
        HasRoutes<TextBlockPage>("/templates/text/new", "/templates/text/{Id:int}", "/text-blocks/new", "/text-blocks/{Id:int}");
        HasRoutes<VariationListPage>("/templates/variations/new", "/templates/variations/{Id:int}", "/variation-lists/new", "/variation-lists/{Id:int}");
        HasRoutes<PhotoTemplatePage>("/templates/photos/new", "/templates/photos/{Id:int}", "/photo-templates/new", "/photo-templates/{Id:int}");
    }

    [Fact]
    public void Packing_slip_pages_live_in_their_own_section()
    {
        HasRoutes<EtsyShopManager.Web.Components.Pages.PackingSlips.PackingSlips>("/packing-slips");
        HasRoutes<EtsyShopManager.Web.Components.Pages.PackingSlips.PackingSlipLayoutPage>("/packing-slips/layouts/new", "/packing-slips/layouts/{Id:int}");
        Assert.DoesNotContain("ShopSlipPanel", File.ReadAllText(Path.Combine(DesignRulesTests.Root, "src", "EtsyShopManager.Web", "Components", "Pages", "Shops.razor")));
    }

    private static void HasRoutes<T>(params string[] expected) =>
        Assert.Equal(expected.Order(), typeof(T).GetCustomAttributes<RouteAttribute>().Select(r => r.Template).Order());
}
