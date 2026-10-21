#pragma warning disable BL0005
using System.Reflection;
using System.Security.Claims;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Web.Components.Pages;
using EtsyShopManager.Web.Components.Pages.Admin;
using EtsyShopManager.Web.Components.Pages.Listings;
using EtsyShopManager.Web.Components.Pages.Orders;
using EtsyShopManager.Web.Components.Pages.PackingSlips;
using EtsyShopManager.Web.Components.Pages.Setup;
using EtsyShopManager.Web.Components.Pages.Templates;
using EtsyShopManager.Web.Modules;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Navigation;
using EtsyShopManager.Web.Modules.Orders;
using EtsyShopManager.Web.Modules.Stock;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EtsyShopManager.Tests;

public class AuthBypassEmpiricalTests
{
    private static ClaimsPrincipal AnonymousUser => new(new ClaimsIdentity());
    private static ClaimsPrincipal StandardUser => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "worker"), new Claim(ClaimTypes.Role, Roles.User)], "auth"));
    private static ClaimsPrincipal LegacyEditor => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "legacy"), new Claim(ClaimTypes.Role, "Editor")], "auth"));
    private static ClaimsPrincipal LegacyViewer => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "legacy"), new Claim(ClaimTypes.Role, "Viewer")], "auth"));
    private static ClaimsPrincipal AttackerUser => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "attacker"), new Claim(ClaimTypes.Role, "SuperUser")], "auth"));
    private static ClaimsPrincipal AdminUser => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, Roles.Admin)], "auth"));

    private static async Task<(FakeEtsyShop Shop, int First, int Second)> SetupShopWithListingsAsync()
    {
        var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(101, "Vase Alpha"));
        shop.Listings.Add(FakeEtsyShop.Listing(102, "Vase Beta"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        var ids = await db.Listings.ToDictionaryAsync(l => l.EtsyListingId, l => l.Id);
        return (shop, ids[101], ids[102]);
    }

    // -------------------------------------------------------------------------
    // 1. Service-Level Authorization (StockRefill, BulkEdit, PendingChanges)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StockRefill_ApplyBulkAsync_Throws_For_All_Non_Admin_Principals()
    {
        var (shop, first, _) = await SetupShopWithListingsAsync();
        using var _ = shop;
        var refiller = shop.Provider.GetRequiredService<StockRefiller>();
        var change = new BulkRefillChange(true, 5, 500);

        // Anonymous user
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            refiller.ApplyBulkAsync([first], change, AnonymousUser));

        // Standard User
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            refiller.ApplyBulkAsync([first], change, StandardUser));

        // Legacy Editor
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            refiller.ApplyBulkAsync([first], change, LegacyEditor));

        // Legacy Viewer
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            refiller.ApplyBulkAsync([first], change, LegacyViewer));

        // Arbitrary Role
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            refiller.ApplyBulkAsync([first], change, AttackerUser));

        // Admin succeeds
        var changed = await refiller.ApplyBulkAsync([first], change, AdminUser);
        Assert.True(changed >= 0);
    }

    [Fact]
    public async Task BulkEdit_ApplyAsync_Throws_For_All_Non_Admin_Principals()
    {
        var (shop, first, _) = await SetupShopWithListingsAsync();
        using var _ = shop;
        var bulkEditor = shop.Provider.GetRequiredService<BulkListingEditor>();
        var edit = new BulkEdit(BulkEditKind.AddTags, Words: ["challenge-tag"]);

        // Anonymous
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bulkEditor.ApplyAsync([first], edit, AnonymousUser));

        // Standard User
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bulkEditor.ApplyAsync([first], edit, StandardUser));

        // Legacy Editor
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bulkEditor.ApplyAsync([first], edit, LegacyEditor));

        // Legacy Viewer
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bulkEditor.ApplyAsync([first], edit, LegacyViewer));

        // Attacker
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bulkEditor.ApplyAsync([first], edit, AttackerUser));

        // Admin succeeds
        var changed = await bulkEditor.ApplyAsync([first], edit, AdminUser);
        Assert.NotNull(changed);
    }

    [Fact]
    public async Task BulkEdit_PushAsync_Throws_For_All_Non_Admin_Principals()
    {
        var (shop, first, _) = await SetupShopWithListingsAsync();
        using var _ = shop;
        var bulkEditor = shop.Provider.GetRequiredService<BulkListingEditor>();

        // Anonymous
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bulkEditor.PushAsync([first], AnonymousUser));

        // Standard User
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bulkEditor.PushAsync([first], StandardUser));

        // Legacy Editor
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bulkEditor.PushAsync([first], LegacyEditor));

        // Legacy Viewer
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bulkEditor.PushAsync([first], LegacyViewer));

        // Attacker
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bulkEditor.PushAsync([first], AttackerUser));

        // Admin succeeds
        var pushed = await bulkEditor.PushAsync([first], AdminUser);
        Assert.NotNull(pushed);
    }

    [Fact]
    public async Task PendingChanges_PushAsync_Throws_For_All_Non_Admin_Principals()
    {
        var (shop, first, _) = await SetupShopWithListingsAsync();
        using var _ = shop;
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(first, new Dictionary<string, JsonNode?> { ["title"] = "Updated Title" }, "admin");

        var pendingService = shop.Provider.GetRequiredService<PendingChangesService>();
        var items = await pendingService.ListAsync();
        var target = Assert.Single(items, i => i.ListingId == first);
        var reviewedDict = new Dictionary<int, string> { [first] = target.Reviewed };

        // Anonymous
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            pendingService.PushAsync(reviewedDict, AnonymousUser));

        // Standard User
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            pendingService.PushAsync(reviewedDict, StandardUser));

        // Legacy Editor
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            pendingService.PushAsync(reviewedDict, LegacyEditor));

        // Legacy Viewer
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            pendingService.PushAsync(reviewedDict, LegacyViewer));

        // Attacker
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            pendingService.PushAsync(reviewedDict, AttackerUser));

        // Admin succeeds
        var results = await pendingService.PushAsync(reviewedDict, AdminUser);
        Assert.NotNull(results);
    }

    // -------------------------------------------------------------------------
    // 2. Route & Component Security: Restricted vs Public Pages
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(typeof(Shops))]
    [InlineData(typeof(EditListing))]
    [InlineData(typeof(CloneListing))]
    [InlineData(typeof(PendingChanges))]
    [InlineData(typeof(Jobs))]
    [InlineData(typeof(Settings))]
    [InlineData(typeof(Users))]
    [InlineData(typeof(Audit))]
    [InlineData(typeof(ApiKeys))]
    [InlineData(typeof(EtsyWrites))]
    [InlineData(typeof(PhotoOrderTestPage))]
    [InlineData(typeof(SetupEtsy))]
    [InlineData(typeof(PackingSlipLayoutPage))]
    public void RestrictedRoutes_Strictly_Require_Roles_Admin(Type componentType)
    {
        var authAttr = componentType.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authAttr);
        Assert.Equal(Roles.Admin, authAttr.Roles);
    }

    [Theory]
    [InlineData(typeof(Home))]
    [InlineData(typeof(Orders))]
    [InlineData(typeof(PrintPickList))]
    [InlineData(typeof(PrintPackingSlips))]
    [InlineData(typeof(PackingSlips))]
    [InlineData(typeof(Listings))]
    [InlineData(typeof(ListingDetail))]
    [InlineData(typeof(Lookups))]
    [InlineData(typeof(Templates))]
    [InlineData(typeof(OrderColors))]
    [InlineData(typeof(TextBlockPage))]
    [InlineData(typeof(VariationListPage))]
    [InlineData(typeof(PhotoTemplatePage))]
    public void Public_And_Viewable_Pages_Do_Not_Have_Class_Level_Authorize(Type componentType)
    {
        var authAttr = componentType.GetCustomAttribute<AuthorizeAttribute>();
        Assert.Null(authAttr);
    }

    // -------------------------------------------------------------------------
    // 3. Template Creation Route Protection (/templates/*/new) vs View Routes
    // -------------------------------------------------------------------------

    private sealed class CapturingNavigationManager : NavigationManager
    {
        public string? NavigatedUri { get; private set; }
        public bool NavigatedForceLoad { get; private set; }

        public CapturingNavigationManager(string baseUri, string uri)
        {
            Initialize(baseUri, uri);
        }

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            NavigatedUri = uri;
            NavigatedForceLoad = options.ForceLoad;
        }
    }

    private static void InjectProperty(object target, string propName, object value)
    {
        var prop = target.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property {propName} not found on {target.GetType().Name}");
        prop.SetValue(target, value);
    }

    private static object? GetFieldOrProperty(object target, string memberName)
    {
        var field = target.GetType().GetField(memberName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field is not null) return field.GetValue(target);
        var prop = target.GetType().GetProperty(memberName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (prop is not null) return prop.GetValue(target);
        throw new InvalidOperationException($"Member {memberName} not found on {target.GetType().Name}");
    }

    private static async Task InvokeOnParametersSetAsync(object target)
    {
        var method = target.GetType().GetMethod("OnParametersSetAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"OnParametersSetAsync not found on {target.GetType().Name}");
        var task = (Task)method.Invoke(target, null)!;
        await task;
    }

    [Fact]
    public async Task TextBlockPage_CreationRoute_Blocks_Anonymous_And_NonAdmin()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;
        var blocks = shop.Provider.GetRequiredService<TextBlockService>();

        // 1. Anonymous on /templates/text/new -> Redirect to account/login
        {
            var page = new TextBlockPage();
            var nav = new CapturingNavigationManager("http://localhost/", "http://localhost/templates/text/new");
            InjectProperty(page, "Nav", nav);
            InjectProperty(page, "Blocks", blocks);
            InjectProperty(page, "AuthState", Task.FromResult(new AuthenticationState(AnonymousUser)));
            page.Id = null;

            await InvokeOnParametersSetAsync(page);

            Assert.NotNull(nav.NavigatedUri);
            Assert.StartsWith("account/login?returnUrl=", nav.NavigatedUri);
            Assert.True(nav.NavigatedForceLoad);
        }

        // 2. User role on /templates/text/new -> Redirect to account/access-denied
        {
            var page = new TextBlockPage();
            var nav = new CapturingNavigationManager("http://localhost/", "http://localhost/templates/text/new");
            InjectProperty(page, "Nav", nav);
            InjectProperty(page, "Blocks", blocks);
            InjectProperty(page, "AuthState", Task.FromResult(new AuthenticationState(StandardUser)));
            page.Id = null;

            await InvokeOnParametersSetAsync(page);

            Assert.Equal("account/access-denied", nav.NavigatedUri);
            Assert.True(nav.NavigatedForceLoad);
        }

        // 3. Admin role on /templates/text/new -> Allowed (no redirect)
        {
            var page = new TextBlockPage();
            var nav = new CapturingNavigationManager("http://localhost/", "http://localhost/templates/text/new");
            InjectProperty(page, "Nav", nav);
            InjectProperty(page, "Blocks", blocks);
            InjectProperty(page, "AuthState", Task.FromResult(new AuthenticationState(AdminUser)));
            page.Id = null;

            await InvokeOnParametersSetAsync(page);

            Assert.Null(nav.NavigatedUri);
            Assert.True((bool)GetFieldOrProperty(page, "_canEdit")!);
        }

        // 4. Anonymous visiting existing template (/templates/text/999) -> No redirect, ReadOnly mode
        {
            var page = new TextBlockPage();
            var nav = new CapturingNavigationManager("http://localhost/", "http://localhost/templates/text/999");
            InjectProperty(page, "Nav", nav);
            InjectProperty(page, "Blocks", blocks);
            InjectProperty(page, "AuthState", Task.FromResult(new AuthenticationState(AnonymousUser)));
            page.Id = 999;

            await InvokeOnParametersSetAsync(page);

            Assert.Null(nav.NavigatedUri); // No redirect
            Assert.False((bool)GetFieldOrProperty(page, "_canEdit")!); // ReadOnly
        }
    }

    [Fact]
    public async Task VariationListPage_CreationRoute_Blocks_Anonymous_And_NonAdmin()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;
        var lists = shop.Provider.GetRequiredService<VariationListService>();

        // 1. Anonymous on /templates/variations/new -> Redirect to account/login
        {
            var page = new VariationListPage();
            var nav = new CapturingNavigationManager("http://localhost/", "http://localhost/templates/variations/new");
            InjectProperty(page, "Nav", nav);
            InjectProperty(page, "Lists", lists);
            InjectProperty(page, "AuthState", Task.FromResult(new AuthenticationState(AnonymousUser)));
            page.Id = null;

            await InvokeOnParametersSetAsync(page);

            Assert.NotNull(nav.NavigatedUri);
            Assert.StartsWith("account/login?returnUrl=", nav.NavigatedUri);
            Assert.True(nav.NavigatedForceLoad);
        }

        // 2. User role on /templates/variations/new -> Redirect to account/access-denied
        {
            var page = new VariationListPage();
            var nav = new CapturingNavigationManager("http://localhost/", "http://localhost/templates/variations/new");
            InjectProperty(page, "Nav", nav);
            InjectProperty(page, "Lists", lists);
            InjectProperty(page, "AuthState", Task.FromResult(new AuthenticationState(StandardUser)));
            page.Id = null;

            await InvokeOnParametersSetAsync(page);

            Assert.Equal("account/access-denied", nav.NavigatedUri);
            Assert.True(nav.NavigatedForceLoad);
        }

        // 3. Admin role on /templates/variations/new -> Allowed (no redirect)
        {
            var page = new VariationListPage();
            var nav = new CapturingNavigationManager("http://localhost/", "http://localhost/templates/variations/new");
            InjectProperty(page, "Nav", nav);
            InjectProperty(page, "Lists", lists);
            InjectProperty(page, "AuthState", Task.FromResult(new AuthenticationState(AdminUser)));
            page.Id = null;

            await InvokeOnParametersSetAsync(page);

            Assert.Null(nav.NavigatedUri);
            Assert.True((bool)GetFieldOrProperty(page, "_canEdit")!);
        }

        // 4. Anonymous visiting existing template (/templates/variations/999) -> No redirect, ReadOnly mode
        {
            var page = new VariationListPage();
            var nav = new CapturingNavigationManager("http://localhost/", "http://localhost/templates/variations/999");
            InjectProperty(page, "Nav", nav);
            InjectProperty(page, "Lists", lists);
            InjectProperty(page, "AuthState", Task.FromResult(new AuthenticationState(AnonymousUser)));
            page.Id = 999;

            await InvokeOnParametersSetAsync(page);

            Assert.Null(nav.NavigatedUri); // No redirect
            Assert.False((bool)GetFieldOrProperty(page, "_canEdit")!); // ReadOnly
        }
    }

    [Fact]
    public async Task PhotoTemplatePage_CreationRoute_Blocks_Anonymous_And_NonAdmin()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;
        var templates = shop.Provider.GetRequiredService<PhotoTemplateService>();

        // 1. Anonymous on /templates/photos/new -> Redirect to account/login
        {
            var page = new PhotoTemplatePage();
            var nav = new CapturingNavigationManager("http://localhost/", "http://localhost/templates/photos/new");
            InjectProperty(page, "Nav", nav);
            InjectProperty(page, "Templates", templates);
            InjectProperty(page, "AuthState", Task.FromResult(new AuthenticationState(AnonymousUser)));
            page.Id = null;

            await InvokeOnParametersSetAsync(page);

            Assert.NotNull(nav.NavigatedUri);
            Assert.StartsWith("account/login?returnUrl=", nav.NavigatedUri);
            Assert.True(nav.NavigatedForceLoad);
        }

        // 2. User role on /templates/photos/new -> Redirect to account/access-denied
        {
            var page = new PhotoTemplatePage();
            var nav = new CapturingNavigationManager("http://localhost/", "http://localhost/templates/photos/new");
            InjectProperty(page, "Nav", nav);
            InjectProperty(page, "Templates", templates);
            InjectProperty(page, "AuthState", Task.FromResult(new AuthenticationState(StandardUser)));
            page.Id = null;

            await InvokeOnParametersSetAsync(page);

            Assert.Equal("account/access-denied", nav.NavigatedUri);
            Assert.True(nav.NavigatedForceLoad);
        }

        // 3. Admin role on /templates/photos/new -> Allowed (no redirect)
        {
            var page = new PhotoTemplatePage();
            var nav = new CapturingNavigationManager("http://localhost/", "http://localhost/templates/photos/new");
            InjectProperty(page, "Nav", nav);
            InjectProperty(page, "Templates", templates);
            InjectProperty(page, "AuthState", Task.FromResult(new AuthenticationState(AdminUser)));
            page.Id = null;

            await InvokeOnParametersSetAsync(page);

            Assert.Null(nav.NavigatedUri);
            Assert.True((bool)GetFieldOrProperty(page, "_canEdit")!);
        }

        // 4. Anonymous visiting existing template (/templates/photos/999) -> No redirect, ReadOnly mode
        {
            var page = new PhotoTemplatePage();
            var nav = new CapturingNavigationManager("http://localhost/", "http://localhost/templates/photos/999");
            InjectProperty(page, "Nav", nav);
            InjectProperty(page, "Templates", templates);
            InjectProperty(page, "AuthState", Task.FromResult(new AuthenticationState(AnonymousUser)));
            page.Id = 999;

            await InvokeOnParametersSetAsync(page);

            Assert.Null(nav.NavigatedUri); // No redirect
            Assert.False((bool)GetFieldOrProperty(page, "_canEdit")!); // ReadOnly
        }
    }

    // -------------------------------------------------------------------------
    // 4. Navigation & Settings Category Lockdown
    // -------------------------------------------------------------------------

    [Fact]
    public void Navigation_SettingsGroup_Hidden_For_Anonymous_And_User()
    {
        var registry = new ModuleRegistry([
            new ShopsModule(), new ListingsModule(), new OrdersModule(),
            new EtsyShopManager.Web.Modules.Dashboard.DashboardModule(), new EtsyShopManager.Web.Modules.Stock.EditorModule(),
            new TemplatesModule(), new JobsModule(), new AdminModule(),
        ]);

        bool CanSee(NavItem item, ClaimsPrincipal p) => item.Roles is null || item.Roles.Split(',').Any(r => p.IsInRole(r.Trim()));

        // Anonymous
        var anonEntries = SidebarLayout.Build(registry.NavItems.Where(i => CanSee(i, AnonymousUser)));
        Assert.DoesNotContain(anonEntries, e => e.Group == NavGroups.Settings);
        Assert.DoesNotContain(anonEntries, e => e.Title == "Shops");

        // Standard User
        var userEntries = SidebarLayout.Build(registry.NavItems.Where(i => CanSee(i, StandardUser)));
        Assert.DoesNotContain(userEntries, e => e.Group == NavGroups.Settings);
        Assert.DoesNotContain(userEntries, e => e.Title == "Shops");

        // Admin User
        var adminEntries = SidebarLayout.Build(registry.NavItems.Where(i => CanSee(i, AdminUser)));
        var settingsGroup = Assert.Single(adminEntries, e => e.Group == NavGroups.Settings);
        Assert.Contains(settingsGroup.Children, c => c.Title == "Shops");
        Assert.Equal(8, settingsGroup.Children.Count);
    }

    [Fact]
    public void OrderColors_At_Top_Level_Accessible_To_Anonymous_And_User()
    {
        var registry = new ModuleRegistry([
            new ShopsModule(), new ListingsModule(), new OrdersModule(),
            new EtsyShopManager.Web.Modules.Dashboard.DashboardModule(), new EtsyShopManager.Web.Modules.Stock.EditorModule(),
            new TemplatesModule(), new JobsModule(), new AdminModule(),
        ]);

        var orderColorsNav = Assert.Single(registry.NavItems, i => i.Title == "Order colors");
        Assert.Null(orderColorsNav.Group); // Top level
        Assert.Null(orderColorsNav.Roles); // No admin requirement
    }

    // -------------------------------------------------------------------------
    // 5. Read-Only Enforcement in Viewable Razor Components
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("Listings.razor", "AuthorizeView Roles=\"@Roles.Admin\"")]
    [InlineData("ListingDetail.razor", "AuthorizeView Roles=\"@Roles.Admin\"")]
    [InlineData("Lookups.razor", "AuthorizeView Roles=\"@Roles.Admin\"")]
    [InlineData("Templates.razor", "AuthorizeView Roles=\"@Roles.Admin\"")]
    [InlineData("TextBlockPage.razor", "AuthorizeView Roles=\"@Roles.Admin\"")]
    [InlineData("VariationListPage.razor", "AuthorizeView Roles=\"@Roles.Admin\"")]
    [InlineData("PhotoTemplatePage.razor", "AuthorizeView Roles=\"@Roles.Admin\"")]
    [InlineData("OrderColors.razor", "AuthorizeView Roles=\"@Roles.Admin\"")]
    [InlineData("RefillPanel.razor", "AuthorizeView Roles=\"@Roles.Admin\"")]
    [InlineData("Orders.razor", "AuthorizeView Roles=\"@Roles.Admin\"")]
    public void Razor_Viewable_Components_Protect_Mutations_With_Admin_Role(string componentFileName, string expectedGuard)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var files = Directory.GetFiles(Path.Combine(root, "src", "EtsyShopManager.Web"), componentFileName, SearchOption.AllDirectories);
        var file = Assert.Single(files);
        var content = File.ReadAllText(file);
        Assert.Contains(expectedGuard, content);
    }

    [Theory]
    [InlineData("Orders.razor")]
    [InlineData("OpenOrdersTile.razor")]
    [InlineData("ToMakeTile.razor")]
    public void Order_Made_Toggles_Are_Enabled_For_User_Role(string componentFileName)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var file = Assert.Single(Directory.GetFiles(Path.Combine(root, "src", "EtsyShopManager.Web"), componentFileName, SearchOption.AllDirectories));
        var content = File.ReadAllText(file);
        Assert.Contains("_canMark = true;", content);
        Assert.DoesNotContain("_canMark = user.IsInRole(Roles.Admin)", content);
    }
}
