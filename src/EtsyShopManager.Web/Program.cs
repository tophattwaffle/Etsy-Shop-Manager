using System.Net;
using EtsyShopManager.Core.Configuration;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Components;
using EtsyShopManager.Web.Infrastructure;
using EtsyShopManager.Web.Infrastructure.Jobs;
using EtsyShopManager.Web.Infrastructure.Storage;
using EtsyShopManager.Web.Modules;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Orders;
using EtsyShopManager.Web.Modules.Shops;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting.WindowsServices;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // As a Windows Service the working directory is System32; load appsettings from the install folder.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});

builder.Services.AddWindowsService(o => o.ServiceName = "EtsyShopManager");

// ---- Storage (outside Dropbox) ------------------------------------------------------------------
var storage = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
storage.Validate();
storage.EnsureDirectories();
builder.Services.AddSingleton(storage);
builder.Logging.AddProvider(new FileLoggerProvider(storage.LogsDirectory));

// ---- Data protection: encrypts tokens/secrets and auth cookies. Keys live next to the DB. ------
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("EtsyShopManager")
    .PersistKeysToFileSystem(new DirectoryInfo(storage.KeysDirectory));
if (OperatingSystem.IsWindows())
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);

// ---- Database + Identity --------------------------------------------------------------------------
builder.Services.AddAppData(storage);

builder.Services.AddIdentity<AppUser, IdentityRole>(o =>
    {
        o.Password.RequiredLength = 10;
        o.Password.RequireDigit = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.SignIn.RequireConfirmedAccount = false;
        o.User.RequireUniqueEmail = false;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "esm.auth";
    o.LoginPath = "/account/login";
    o.LogoutPath = "/account/logout";
    o.AccessDeniedPath = "/account/access-denied";
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.SlidingExpiration = true;
});

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

// ---- UI ----------------------------------------------------------------------------------------------
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();

// ---- Reverse proxy (HAProxy terminates HTTPS) ---------------------------------------------------------
var trustedProxies = builder.Configuration.GetSection("ReverseProxy:TrustedProxies").Get<string[]>() ?? [];
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    foreach (var proxy in trustedProxies)
        if (IPAddress.TryParse(proxy, out var ip)) o.KnownProxies.Add(ip);
});

// ---- App services --------------------------------------------------------------------------------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDomainEventPublisher, DomainEventPublisher>();
builder.Services.AddSingleton<ISettingsStore, SettingsStore>();
builder.Services.AddSingleton<IAuditLog, AuditLog>();
builder.Services.AddSingleton<SetupState>();
builder.Services.AddJobInfrastructure(builder.Configuration);

builder.Services.AddSingleton<IEtsyCredentialsProvider, EtsyCredentialsProvider>();
builder.Services.AddSingleton<IEtsyTokenStore, ShopTokenStore>();
builder.Services.AddSingleton<IEtsyWriteJournal, EtsyWriteJournal>();
builder.Services.AddSingleton<ShopDefaultsService>();
builder.Services.AddSingleton<ListingCreationService>();
builder.Services.AddSingleton<VideoStore>();
builder.Services.AddEtsy(builder.Configuration);
if (builder.Environment.IsStaging())
{
    builder.Services.AddStagingEtsyLockout(); // the staging copy never reaches Etsy (docs/STAGING.md)
    builder.Services.AddStagingUspsLockout(); // the staging copy never reaches USPS
}

// ---- Feature modules ------------------------------------------------------------------------------
var modules = builder.AddModules(
    new ShopsModule(),
    new ListingsModule(),
    new OrdersModule(),
    new EtsyShopManager.Web.Modules.Dashboard.DashboardModule(),
    new EtsyShopManager.Web.Modules.Stock.EditorModule(),
    new EtsyShopManager.Web.Modules.Templates.TemplatesModule(),
    new JobsModule(),
    new AdminModule(),
    new EtsyShopManager.Web.Modules.Navigation.NavigationModule());

var app = builder.Build();

// ---- Startup: database, roles ----------------------------------------------------------------------
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
startupLogger.LogInformation("Etsy Shop Manager starting. Data root: {Root}", storage.ResolvedRoot);
if (trustedProxies.Length == 0)
    startupLogger.LogWarning("ReverseProxy:TrustedProxies is empty; X-Forwarded-* headers from HAProxy will be ignored.");

await app.Services.InitializeDatabaseAsync(startupLogger);
if (app.Environment.IsStaging())
    await StagingCopy.RemoveEtsySecretsAsync(app.Services, startupLogger); // before any job can run
await using (var scope = app.Services.CreateAsyncScope())
{
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
    foreach (var role in Roles.All)
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new IdentityRole(role));

    foreach (var legacyRole in new[] { "Editor", "Viewer" })
    {
        if (await roleManager.RoleExistsAsync(legacyRole))
        {
            var usersInRole = await userManager.GetUsersInRoleAsync(legacyRole);
            foreach (var user in usersInRole)
            {
                if (!await userManager.IsInRoleAsync(user, Roles.User))
                    await userManager.AddToRoleAsync(user, Roles.User);
                await userManager.RemoveFromRoleAsync(user, legacyRole);
            }
            var roleEntity = await roleManager.FindByNameAsync(legacyRole);
            if (roleEntity is not null)
                await roleManager.DeleteAsync(roleEntity);
        }
    }
}

// ---- Pipeline --------------------------------------------------------------------------------------
app.UseForwardedHeaders();
app.UseMiddleware<IngressPathBaseMiddleware>();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found");

app.UseMiddleware<SetupGateMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies([.. modules.ComponentAssemblies.Where(a => a != typeof(Program).Assembly)]);

app.MapAccountEndpoints();
app.MapModuleEndpoints(modules);
app.MapGet(VideoStore.UrlPrefix + "{fileName}", (string fileName, VideoStore store, HttpContext http) =>
{
    if (!store.Exists(fileName)) return Results.NotFound();
    http.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
    return Results.File(store.PathFor(fileName), VideoStore.ContentTypeFor(fileName), enableRangeProcessing: true);
}).AllowAnonymous();
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = AppVersion.Version, commit = AppVersion.Commit }))
    .AllowAnonymous();

await app.RunAsync();
