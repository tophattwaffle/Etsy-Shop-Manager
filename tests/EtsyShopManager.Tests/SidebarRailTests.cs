using System.Reflection;
using System.Security.Claims;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Web.Components.Layout;
using EtsyShopManager.Web.Modules.Navigation;
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
public class SidebarRailTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string WebProject = Path.Combine(RepoRoot, "src", "EtsyShopManager.Web");
    private static readonly string Components = Path.Combine(WebProject, "Components");

    [Fact]
    public void MainLayout_Configures_MudDrawer_In_Mini_Mode()
    {
        var layoutFile = Path.Combine(Components, "Layout", "MainLayout.razor");
        var content = File.ReadAllText(layoutFile);

        Assert.Contains("Variant=\"DrawerVariant.Mini\"", content);
        Assert.Contains("Mini=\"@(!_drawerOpen)\"", content);
        Assert.Contains("OnOpenDrawer=\"() => _drawerOpen = true\"", content);
        Assert.Contains("_drawerOpen = !EmbedRoutes.IsUnderIngress(Nav.BaseUri);", content);
    }

    [Fact]
    public void Css_Rules_Enforce_Mini_Rail_Isolation()
    {
        var cssFile = Path.Combine(WebProject, "wwwroot", "app.css");
        var content = File.ReadAllText(cssFile);

        Assert.Contains(".mud-drawer--closed.mud-drawer-mini .esm-nav", content);
        Assert.Contains(".mud-drawer--closed.mud-drawer-mini .esm-nav .mud-nav-link", content);
        Assert.Contains(".mud-drawer--closed.mud-drawer-mini .mud-nav-group .mud-nav-link-expand-icon { display: none; }", content);
        Assert.Contains(".mud-drawer--closed.mud-drawer-mini .mud-nav-group .mud-collapse-container { display: none; }", content);
        Assert.Contains(".mud-drawer--closed.mud-drawer-mini .esm-nav-foot { display: none; }", content);
        Assert.Contains(".mud-drawer--closed.mud-drawer-mini .esm-nav-edit { display: none; }", content);
    }

    [Fact]
    public void NavMenu_Source_Code_Guards_Mini_State()
    {
        var navMenuFile = Path.Combine(Components, "Layout", "NavMenu.razor");
        var content = File.ReadAllText(navMenuFile);

        Assert.Contains("[Parameter] public bool Mini { get; set; }", content);
        Assert.Contains("[Parameter] public EventCallback OnOpenDrawer { get; set; }", content);
        Assert.Contains("Expanded=\"@(Mini ? false : IsOpen(group, entry.Children))\"", content);
        Assert.Contains("@if (_user.IsInRole(Roles.Admin) && !Mini)", content);
        Assert.Contains("if (Mini && _editing) _editing = false;", content);
        Assert.Contains("title=\"@item.Title\"", content);
    }

    [Fact]
    public async Task NavMenu_When_Mini_Hides_Footer_And_Reorder_Button()
    {
        var admin = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, Roles.Admin),
        ], "TestAuth"));

        var htmlMini = await RenderNavMenuAsync(mini: true, user: admin);
        Assert.DoesNotContain("esm-nav-foot", htmlMini);
        Assert.DoesNotContain("Reorder", htmlMini);

        var htmlExpanded = await RenderNavMenuAsync(mini: false, user: admin);
        Assert.Contains("esm-nav-foot", htmlExpanded);
        Assert.Contains("Reorder", htmlExpanded);
    }

    [Fact]
    public async Task NavMenu_Clicking_Settings_When_Mini_Triggers_OnOpenDrawer_And_Sets_Open()
    {
        var navMenu = new NavMenu();
        navMenu.Mini = true;

        var openDrawerInvoked = false;
        navMenu.OnOpenDrawer = EventCallback.Factory.Create(this, () => openDrawerInvoked = true);

        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var clickMethod = typeof(NavMenu).GetMethod("HandleGroupClick", flags);
        Assert.NotNull(clickMethod);

        var task = (Task)clickMethod.Invoke(navMenu, ["Settings"])!;
        await task;

        Assert.True(openDrawerInvoked);

        var openField = typeof(NavMenu).GetField("_open", flags);
        Assert.NotNull(openField);
        var openDict = (Dictionary<string, bool>)openField.GetValue(navMenu)!;
        Assert.True(openDict.TryGetValue("Settings", out var isOpen) && isOpen);
    }

    [Fact]
    public async Task NavMenu_Expanding_Settings_When_Mini_Triggers_OnOpenDrawer_And_Sets_Open()
    {
        var navMenu = new NavMenu();
        navMenu.Mini = true;

        var openDrawerInvoked = false;
        navMenu.OnOpenDrawer = EventCallback.Factory.Create(this, () => openDrawerInvoked = true);

        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var expandMethod = typeof(NavMenu).GetMethod("HandleGroupExpandedChanged", flags);
        Assert.NotNull(expandMethod);

        var task = (Task)expandMethod.Invoke(navMenu, ["Settings", false])!;
        await task;

        Assert.True(openDrawerInvoked);

        var openField = typeof(NavMenu).GetField("_open", flags);
        Assert.NotNull(openField);
        var openDict = (Dictionary<string, bool>)openField.GetValue(navMenu)!;
        Assert.True(openDict.TryGetValue("Settings", out var isOpen) && isOpen);
    }

    private static async Task<string> RenderNavMenuAsync(bool mini, ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, NullJsRuntime>();
        services.AddSingleton<NavigationManager>(new TestNav("http://localhost/"));
        services.AddSingleton<ISettingsStore, InMemorySettingsStore>();
        services.AddSingleton<SidebarOrderService>();
        services.AddSingleton(new ModuleRegistry(new IModule[] { new TestModule() }));
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

    private sealed class TestModule : IModule
    {
        public string Name => "Test";
        public void ConfigureServices(IServiceCollection services, IConfiguration configuration) { }
        public IEnumerable<NavItem> NavItems =>
        [
            new("Dashboard", "", "i", Order: 0, MatchAll: true),
            new("Orders", "orders", "i", Order: 15),
            new("Settings", "admin/settings", "i", Order: 900, Group: NavGroups.Settings, Roles: Roles.Admin),
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
}
