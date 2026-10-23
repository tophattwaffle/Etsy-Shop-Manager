using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Web.Infrastructure.Jobs;
using Microsoft.Net.Http.Headers;
using MudBlazor;

namespace EtsyShopManager.Web.Modules.Listings;

/// <summary>Phase 1: imports listings, images, inventory, lookups and taxonomy, and serves read-only browsing.</summary>
public sealed class ListingsModule : IModule
{
    public string Name => "Listings";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ImageStore>();
        services.AddSingleton<ListingImporter>();
        services.AddSingleton<TaxonomyImporter>();
        services.AddSingleton<ImageDownloader>();
        services.AddSingleton<ListingImportQueue>();
        services.AddSingleton<ListingQueries>();
        services.AddSingleton<ShopIconCache>();
        services.AddSingleton<ProcessingProfileService>();

        services.AddJobHandler<ImportShopListingsJob>();
        services.AddJobHandler<ImportShopLookupsJob>();
        services.AddJobHandler<DownloadListingImagesJob>();
        services.AddJobHandler<RefreshTaxonomyJob>();
        services.AddJobHandler<ImportAllShopsJob>();
        services.AddScoped<IDomainEventHandler<ShopConnected>, ImportOnShopConnected>();

        services.AddRecurringJob(new RecurringJobDefinition("Import listings (all shops)", ImportAllShopsJob.Type, TimeSpan.FromHours(24))
        {
            InitialDelay = TimeSpan.FromMinutes(15),
        });
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Content-hashed, so a URL always means the same bytes: cache for a year. No sign-in needed: the names are
        // unguessable SHA-256 hashes, and the same photos are public on Etsy's CDN anyway.
        endpoints.MapGet(ImageStore.UrlPrefix + "{fileName}", (string fileName, ImageStore store, HttpContext http) =>
            {
                if (!store.Exists(fileName)) return Results.NotFound();
                http.Response.Headers[HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
                return Results.File(store.PathFor(fileName), ContentTypeFor(fileName));
            })
            .AllowAnonymous();

        // Signed-in only: unlike the hashed photo names, shop ids are guessable.
        endpoints.MapGet(ShopIconCache.UrlPrefix + "{shopId:int}", async (int shopId, ShopIconCache icons, ImageStore store,
                HttpContext http, CancellationToken ct) =>
            {
                if (await icons.GetFileNameAsync(shopId, ct) is not { } fileName) return Results.NotFound();
                http.Response.Headers[HeaderNames.CacheControl] = "private, max-age=3600";
                return Results.File(store.PathFor(fileName), ContentTypeFor(fileName));
            })
            .RequireAuthorization();
    }

    private static string ContentTypeFor(string fileName) => Path.GetExtension(fileName) switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    public IEnumerable<NavItem> NavItems =>
    [
        new("Listings", "listings", Icons.Material.Filled.Inventory2, Order: 20),
        new("Processing profiles", "processing-profiles", Icons.Material.Filled.Schedule, Order: 28),
        new("Lookups", "lookups", Icons.Material.Filled.ListAlt, Order: 30),
    ];
}
