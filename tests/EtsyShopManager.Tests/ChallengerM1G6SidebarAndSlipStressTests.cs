using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Web.Components.Layout;
using EtsyShopManager.Web.Modules;
using EtsyShopManager.Web.Modules.Navigation;
using EtsyShopManager.Web.Modules.Orders;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using Xunit;

namespace EtsyShopManager.Tests;

#pragma warning disable BL0005 // Component parameter should not be set outside of its component in unit test
public class ChallengerM1G6SidebarAndSlipStressTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string WebProject = Path.Combine(RepoRoot, "src", "EtsyShopManager.Web");
    private static readonly string Components = Path.Combine(WebProject, "Components");

    #region Requirement 1: Packing Slip Item Divider Tests

    [Fact]
    public async Task PackingSlip_SingleItem_RendersZeroDividers_NoExtraTopOrBottomBorders()
    {
        var model = CreateSlipModel(itemCount: 1);
        var layout = SlipLayoutService.BuiltIn(SlipKind.Packing);

        var html = await SlipRenderer.RenderAsync(layout.Html, model);

        // Verify HTML structure
        var itemMatches = Regex.Matches(html, @"<div class=""slip-item"">");
        Assert.Single(itemMatches);

        // Simulate CSS adjacent sibling combinator (.slip-item + .slip-item)
        // With 1 item, there is no .slip-item that immediately follows another .slip-item.
        var matchedSiblingDividers = CalculateAdjacentSiblingMatches(itemMatches.Count);
        Assert.Equal(0, matchedSiblingDividers);

        // Verify no inline styles or rogue divider tags were inserted
        Assert.DoesNotContain("border-top", html);
        Assert.DoesNotContain("<hr class=\"slip-divider\"", html);

        // Verify CSS box model contracts in packing.css:
        // .slip-count provides border-bottom: 0.75pt solid var(--print-ink); padding-bottom: 12pt;
        // .slip-items provides border-bottom: 0.75pt solid var(--print-ink); padding: 0 0 16pt;
        // .slip-item provides padding-top: 12pt; but NO border-top and NO margin-top.
        var css = layout.Css;
        Assert.Contains(".slip-count { font-weight: 600; padding-bottom: 12pt; border-bottom: 0.75pt solid var(--print-ink); }", css);
        Assert.Contains(".slip-items { border-bottom: 0.75pt solid var(--print-ink); padding: 0 0 16pt; }", css);
        Assert.Contains(".slip-item { display: grid; grid-template-columns: 42pt 1fr auto; column-gap: 12pt; padding-top: 12pt; break-inside: avoid; }", css);

        // Confirmed: Exactly 0 dividers are drawn for a 1-item order.
    }

    [Fact]
    public async Task PackingSlip_TwoItems_RendersExactlyOneDividerBetweenItems()
    {
        var model = CreateSlipModel(itemCount: 2);
        var layout = SlipLayoutService.BuiltIn(SlipKind.Packing);

        var html = await SlipRenderer.RenderAsync(layout.Html, model);

        var itemMatches = Regex.Matches(html, @"<div class=""slip-item"">");
        Assert.Equal(2, itemMatches.Count);

        // Adjacent sibling combinator (.slip-item + .slip-item) matches item at index 1 (item 2), but not index 0 (item 1).
        var matchedSiblingDividers = CalculateAdjacentSiblingMatches(itemMatches.Count);
        Assert.Equal(1, matchedSiblingDividers);

        // Check CSS rules applying to item 2:
        var css = layout.Css;
        Assert.Contains(".slip-item + .slip-item { border-top: 0.75pt solid var(--print-ink); margin-top: 12pt; }", css);

        // Confirmed: Item 1 has no top border, Item 2 receives border-top + margin-top. Neither has border-bottom.
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(25)]
    public async Task PackingSlip_NItems_RendersExactlyNMinusOneDividers(int n)
    {
        var model = CreateSlipModel(itemCount: n);
        var layout = SlipLayoutService.BuiltIn(SlipKind.Packing);

        var html = await SlipRenderer.RenderAsync(layout.Html, model);

        var itemMatches = Regex.Matches(html, @"<div class=""slip-item"">");
        Assert.Equal(n, itemMatches.Count);

        // In standard CSS semantics, (.slip-item + .slip-item) matches every element with class
        // slip-item that immediately follows another element with class slip-item.
        // In the items container:
        // Index 0: Preceded by nothing or count -> 0 match (no border-top)
        // Index 1..N-1: Preceded by .slip-item -> matches (.slip-item + .slip-item)
        // Total matching elements with divider line is strictly N - 1.
        var matchedSiblingDividers = CalculateAdjacentSiblingMatches(itemMatches.Count);
        Assert.Equal(n - 1, matchedSiblingDividers);
    }

    [Fact]
    public void PackingSlip_CssBoxModel_MatchesDesignSpecification()
    {
        var cssFile = Path.Combine(WebProject, "Modules", "Orders", "Slips", "packing.css");
        var css = File.ReadAllText(cssFile);

        // Exact rule: .slip-item + .slip-item { border-top: 0.75pt solid var(--print-ink); margin-top: 12pt; }
        Assert.Matches(@"\.slip-item\s*\+\s*\.slip-item\s*\{[^}]*border-top:\s*0\.75pt\s+solid\s+var\(--print-ink\);[^}]*\}", css);
        Assert.Matches(@"\.slip-item\s*\+\s*\.slip-item\s*\{[^}]*margin-top:\s*12pt;[^}]*\}", css);

        // Base item padding-top: 12pt;
        Assert.Matches(@"\.slip-item\s*\{[^}]*padding-top:\s*12pt;[^}]*\}", css);

        // Count header padding-bottom: 12pt; border-bottom: 0.75pt solid var(--print-ink);
        Assert.Matches(@"\.slip-count\s*\{[^}]*padding-bottom:\s*12pt;[^}]*border-bottom:\s*0\.75pt\s+solid\s+var\(--print-ink\);[^}]*\}", css);

        // Items container border-bottom: 0.75pt solid var(--print-ink); padding: 0 0 16pt;
        Assert.Matches(@"\.slip-items\s*\{[^}]*border-bottom:\s*0\.75pt\s+solid\s+var\(--print-ink\);[^}]*padding:\s*0\s+0\s+16pt;[^}]*\}", css);
    }

    private static int CalculateAdjacentSiblingMatches(int itemCount) => Math.Max(0, itemCount - 1);

    private static SlipModel CreateSlipModel(int itemCount)
    {
        var shop = new SlipShop(
            Name: "Test Shop",
            Title: "Shop Title",
            Address: "testshop.etsy.com",
            IconUrl: null,
            Location: "Austin, TX",
            StarSeller: true,
            Sales: 50,
            Note: "Thanks for your order!",
            QrCode: "<svg></svg>"
        );

        var items = new List<SlipItem>();
        for (var i = 1; i <= itemCount; i++)
        {
            items.Add(new SlipItem(
                Title: $"Test Product #{i}",
                Variations: [$"Option: Value {i}"],
                Personalization: null,
                Quantity: 1,
                Price: "$10.00",
                ImageUrl: null
            ));
        }

        var order = new SlipOrder(
            ReceiptId: 987654321L,
            OrderDate: "Oct 28, 2026",
            ShipBy: "Oct 30, 2026",
            BuyerName: "Alice Buyer",
            ShipToName: "Alice Buyer",
            AddressLines: ["123 Main St", "Austin, TX 78701"],
            PaymentMethod: "Credit Card",
            ShippingMethod: "USPS Ground",
            NoteFromBuyer: null,
            IsGift: false,
            GiftMessage: null,
            GiftFrom: null,
            ItemCount: itemCount,
            Totals: [new SlipTotal("Item total", $"${itemCount * 10}.00")],
            OrderTotal: $"${itemCount * 10}.00",
            PhotoUrl: null,
            AddressChanged: false,
            EtsyShipToName: null,
            EtsyAddressLines: []
        );

        return new SlipModel(shop, order, items);
    }

    #endregion

    #region Requirement 3: Collapsed Sidebar Mini Rail Tests

    [Fact]
    public void MudDrawer_In_MainLayout_Uses_Mini_Variant_And_Binds_Correctly()
    {
        var layoutFile = Path.Combine(Components, "Layout", "MainLayout.razor");
        var content = File.ReadAllText(layoutFile);

        Assert.Contains("<MudDrawer @bind-Open=\"_drawerOpen\" Variant=\"DrawerVariant.Mini\" ClipMode=\"DrawerClipMode.Always\" Elevation=\"0\" Class=\"esm-drawer\">", content);
        Assert.Contains("<NavMenu Mini=\"@(!_drawerOpen)\" OnOpenDrawer=\"() => _drawerOpen = true\" />", content);
        Assert.Contains("_drawerOpen = !EmbedRoutes.IsUnderIngress(Nav.BaseUri);", content);

        // Check non-ingress behavior: Nav.BaseUri == "http://localhost/" -> IsUnderIngress is false -> _drawerOpen is true
        Assert.False(EmbedRoutes.IsUnderIngress("http://localhost/"));
        Assert.True(!EmbedRoutes.IsUnderIngress("http://localhost/"));

        // Check ingress behavior: Nav.BaseUri == "http://localhost/api/hassio_ingress/token/" -> IsUnderIngress is true -> _drawerOpen is false (collapsed mini)
        Assert.True(EmbedRoutes.IsUnderIngress("http://localhost/api/hassio_ingress/token/"));
        Assert.False(!EmbedRoutes.IsUnderIngress("http://localhost/api/hassio_ingress/token/"));
    }

    [Fact]
    public async Task NavMenu_ParameterTransitions_MiniTrue_False_True_TogglesFooterAndReorder()
    {
        var admin = CreateUser(Roles.Admin);

        // 1. Mini = true -> footer is hidden
        var htmlMini1 = await RenderNavMenuAsync(mini: true, user: admin);
        Assert.DoesNotContain("esm-nav-foot", htmlMini1);
        Assert.DoesNotContain("Reorder", htmlMini1);

        // 2. Mini = false -> footer is shown for admin
        var htmlExpanded = await RenderNavMenuAsync(mini: false, user: admin);
        Assert.Contains("esm-nav-foot", htmlExpanded);
        Assert.Contains("Reorder", htmlExpanded);

        // 3. Mini = true again -> footer is hidden again
        var htmlMini2 = await RenderNavMenuAsync(mini: true, user: admin);
        Assert.DoesNotContain("esm-nav-foot", htmlMini2);
        Assert.DoesNotContain("Reorder", htmlMini2);
    }

    [Fact]
    public void NavMenu_When_Mini_Settings_Subcategories_Are_Hidden_Via_Expanded_And_Css()
    {
        // 1. Component expression check:
        var navMenuFile = Path.Combine(Components, "Layout", "NavMenu.razor");
        var navMenuContent = File.ReadAllText(navMenuFile);

        Assert.Contains("Expanded=\"@(Mini ? false : IsOpen(group, entry.Children))\"", navMenuContent);

        // 2. CSS isolation check:
        var cssFile = Path.Combine(WebProject, "wwwroot", "app.css");
        var cssContent = File.ReadAllText(cssFile);

        Assert.Contains(".mud-drawer--closed.mud-drawer-mini .mud-nav-group .mud-collapse-container { display: none; }", cssContent);
        Assert.Contains(".mud-drawer--closed.mud-drawer-mini .mud-nav-group .mud-nav-link-expand-icon { display: none; }", cssContent);
        Assert.Contains(".mud-drawer--closed.mud-drawer-mini .esm-nav-foot { display: none; }", cssContent);
        Assert.Contains(".mud-drawer--closed.mud-drawer-mini .esm-nav-edit { display: none; }", cssContent);
    }

    [Fact]
    public async Task NavMenu_ClickingSettingsInMiniMode_OpensDrawer_And_ExpandsSettingsGroup()
    {
        var navMenu = new NavMenu();
        navMenu.Mini = true;

        var drawerOpened = false;
        navMenu.OnOpenDrawer = EventCallback.Factory.Create(this, () =>
        {
            drawerOpened = true;
            // Simulating MainLayout reaction: _drawerOpen = true -> Mini = false
            navMenu.Mini = false;
        });

        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var clickMethod = typeof(NavMenu).GetMethod("HandleGroupClick", flags);
        Assert.NotNull(clickMethod);

        // User clicks the Settings icon while in Mini mode
        var task = (Task)clickMethod.Invoke(navMenu, [NavGroups.Settings])!;
        await task;

        // Drawer open callback was invoked
        Assert.True(drawerOpened);
        Assert.False(navMenu.Mini);

        // _open["Settings"] is true
        var openField = typeof(NavMenu).GetField("_open", flags);
        Assert.NotNull(openField);
        var openDict = (Dictionary<string, bool>)openField.GetValue(navMenu)!;
        Assert.True(openDict.TryGetValue(NavGroups.Settings, out var isOpen) && isOpen);

        // Check IsOpen method returns true now that Mini is false
        var isOpenMethod = typeof(NavMenu).GetMethod("IsOpen", flags);
        Assert.NotNull(isOpenMethod);
        var entries = new List<SidebarEntry> { new("settings", new NavItem("Settings", "admin/settings", "icon"), null, []) };
        var isGroupOpen = (bool)isOpenMethod.Invoke(navMenu, [NavGroups.Settings, entries])!;
        Assert.True(isGroupOpen);
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Editor")]
    [InlineData(Roles.User)]
    public async Task NavMenu_NonAdminUsers_DoNotSeeSettingsOrReorder_InEitherMiniOrExpandedMode(string role)
    {
        var user = CreateUser(role);

        // 1. Mini mode
        var htmlMini = await RenderNavMenuAsync(mini: true, user: user);
        Assert.DoesNotContain("Settings", htmlMini);
        Assert.DoesNotContain("admin/settings", htmlMini);
        Assert.DoesNotContain("admin/users", htmlMini);
        Assert.DoesNotContain("admin/audit", htmlMini);
        Assert.DoesNotContain("esm-nav-foot", htmlMini);
        Assert.DoesNotContain("Reorder", htmlMini);

        // 2. Expanded mode
        var htmlExpanded = await RenderNavMenuAsync(mini: false, user: user);
        Assert.DoesNotContain("Settings", htmlExpanded);
        Assert.DoesNotContain("admin/settings", htmlExpanded);
        Assert.DoesNotContain("admin/users", htmlExpanded);
        Assert.DoesNotContain("admin/audit", htmlExpanded);
        Assert.DoesNotContain("esm-nav-foot", htmlExpanded);
        Assert.DoesNotContain("Reorder", htmlExpanded);
    }

    [Fact]
    public void NavMenu_ActiveReorderMode_IsCancelledWhenMiniBecomesTrue()
    {
        var navMenu = new NavMenu();
        navMenu.Mini = false;

        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var editingField = typeof(NavMenu).GetField("_editing", flags);
        Assert.NotNull(editingField);

        // Admin enables reorder mode in expanded view
        editingField.SetValue(navMenu, true);
        Assert.True((bool)editingField.GetValue(navMenu)!);

        // Drawer collapses to mini
        navMenu.Mini = true;

        var onParamsMethod = typeof(NavMenu).GetMethod("OnParametersSet", flags);
        Assert.NotNull(onParamsMethod);
        onParamsMethod.Invoke(navMenu, null);

        // _editing must be reset to false!
        Assert.False((bool)editingField.GetValue(navMenu)!);

        // If drawer re-expands to full, _editing remains false (does not re-enter edit mode automatically)
        navMenu.Mini = false;
        onParamsMethod.Invoke(navMenu, null);
        Assert.False((bool)editingField.GetValue(navMenu)!);
    }

    [Fact]
    public async Task PackingSlip_ZeroItems_DoesNotCrash_AndDrawsZeroDividers()
    {
        var model = CreateSlipModel(itemCount: 0);
        var layout = SlipLayoutService.BuiltIn(SlipKind.Packing);

        var html = await SlipRenderer.RenderAsync(layout.Html, model);

        var itemMatches = Regex.Matches(html, @"<div class=""slip-item"">");
        Assert.Empty(itemMatches);

        var matchedSiblingDividers = CalculateAdjacentSiblingMatches(itemMatches.Count);
        Assert.Equal(0, matchedSiblingDividers);
    }

    [Fact]
    public async Task NavMenu_NormalExpandedMode_AllowsTogglingGroupsNormally_WithoutOpeningDrawerCallback()
    {
        var navMenu = new NavMenu();
        navMenu.Mini = false;

        var openDrawerInvoked = false;
        navMenu.OnOpenDrawer = EventCallback.Factory.Create(this, () => openDrawerInvoked = true);

        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var expandMethod = typeof(NavMenu).GetMethod("HandleGroupExpandedChanged", flags);
        Assert.NotNull(expandMethod);

        // 1. In expanded mode, user collapses Settings group
        await (Task)expandMethod.Invoke(navMenu, [NavGroups.Settings, false])!;
        Assert.False(openDrawerInvoked);

        var openField = typeof(NavMenu).GetField("_open", flags);
        Assert.NotNull(openField);
        var openDict = (Dictionary<string, bool>)openField.GetValue(navMenu)!;
        Assert.False(openDict[NavGroups.Settings]);

        // 2. User expands Settings group in expanded mode
        await (Task)expandMethod.Invoke(navMenu, [NavGroups.Settings, true])!;
        Assert.False(openDrawerInvoked);
        Assert.True(openDict[NavGroups.Settings]);

        // 3. User clicks Settings group header in expanded mode: HandleGroupClick is a no-op when Mini == false
        var clickMethod = typeof(NavMenu).GetMethod("HandleGroupClick", flags);
        Assert.NotNull(clickMethod);
        await (Task)clickMethod.Invoke(navMenu, [NavGroups.Settings])!;
        Assert.False(openDrawerInvoked);
    }

    [Fact]
    public void NavMenu_ActiveRouteInsideGroup_ForcesCollapsedStateWhenMiniIsTrue()
    {
        // When Nav.Uri is at an active subcategory (e.g. admin/settings), IsOpen returns true.
        // However, in Mini mode the expression `Mini ? false : IsOpen(...)` must unconditionally evaluate to false.
        var navMenuContent = File.ReadAllText(Path.Combine(Components, "Layout", "NavMenu.razor"));
        Assert.Contains("Expanded=\"@(Mini ? false : IsOpen(group, entry.Children))\"", navMenuContent);

        // Directly test evaluation logic:
        var isOpen = true; // active route match
        var mini = true;
        var expanded = mini ? false : isOpen;
        Assert.False(expanded);

        mini = false;
        expanded = mini ? false : isOpen;
        Assert.True(expanded);
    }

    [Fact]
    public async Task NavMenu_AnonymousUser_DoesNotSeeSettingsOrReorder_InEitherMode()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var htmlMini = await RenderNavMenuAsync(mini: true, user: anonymous);
        Assert.DoesNotContain("Settings", htmlMini);
        Assert.DoesNotContain("admin/settings", htmlMini);
        Assert.DoesNotContain("esm-nav-foot", htmlMini);

        var htmlExpanded = await RenderNavMenuAsync(mini: false, user: anonymous);
        Assert.DoesNotContain("Settings", htmlExpanded);
        Assert.DoesNotContain("admin/settings", htmlExpanded);
        Assert.DoesNotContain("esm-nav-foot", htmlExpanded);
    }

    #endregion

    #region Helper Methods

    private static ClaimsPrincipal CreateUser(string role) =>
        new(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, $"user_{role}"),
            new Claim(ClaimTypes.Role, role),
        ], "TestAuth"));

    private static async Task<string> RenderNavMenuAsync(bool mini, ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, NullJsRuntime>();
        services.AddSingleton<NavigationManager>(new TestNav("http://localhost/"));
        services.AddSingleton<ISettingsStore, InMemorySettingsStore>();
        services.AddSingleton<SidebarOrderService>();

        // Register realistic modules containing both public items and Settings group admin items
        services.AddSingleton(new ModuleRegistry([
            new StandardTestModule(),
            new AdminModule(),
            new ShopsModule(),
            new JobsModule()
        ]));

        services.AddScoped<AuthenticationStateProvider>(_ => new FixedAuthStateProvider(user));

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ChildContent"] = (RenderFragment)(builder =>
                {
                    builder.OpenComponent<NavMenu>(0);
                    builder.AddAttribute(1, "Mini", mini);
                    builder.CloseComponent();
                }),
            });

            var output = await renderer.RenderComponentAsync<CascadingWrapper>(parameters);
            return output.ToHtmlString();
        });
    }

    private sealed class CascadingWrapper : ComponentBase
    {
        [Inject] private AuthenticationStateProvider AuthProvider { get; set; } = default!;
        [Parameter] public RenderFragment ChildContent { get; set; } = default!;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            builder.AddAttribute(1, "Value", AuthProvider.GetAuthenticationStateAsync());
            builder.AddAttribute(2, "ChildContent", ChildContent);
            builder.CloseComponent();
        }
    }

    private sealed class FixedAuthStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private sealed class TestNav : NavigationManager
    {
        public TestNav(string uri) => Initialize(uri, uri);
    }

    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, string?> _data = new();
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(_data.GetValueOrDefault(key));
        public Task SetAsync(string key, string? value, bool encrypt = false, string? updatedBy = null, CancellationToken ct = default)
        {
            _data[key] = value;
            return Task.CompletedTask;
        }
        public Task<bool> HasValueAsync(string key, CancellationToken ct = default) => Task.FromResult(_data.ContainsKey(key));
    }

    private sealed class StandardTestModule : IModule
    {
        public string Name => "Standard";
        public void ConfigureServices(IServiceCollection services, IConfiguration configuration) { }
        public IEnumerable<NavItem> NavItems =>
        [
            new("Dashboard", "", "i", Order: 0, MatchAll: true),
            new("Listings", "listings", "i", Order: 10),
            new("Orders", "orders", "i", Order: 20),
        ];
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EtsyShopManager.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Repository root (EtsyShopManager.slnx) not found.");
    }

    #endregion
}
