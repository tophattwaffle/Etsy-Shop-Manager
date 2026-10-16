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
        // Content-hashed, so a URL always means the same bytes: cache for a year.
        endpoints.MapGet(ImageStore.UrlPrefix + "{fileName}", (string fileName, ImageStore store, HttpContext http) =>
            {
                if (!store.Exists(fileName)) return Results.NotFound();
                http.Response.Headers[HeaderNames.CacheControl] = "private, max-age=31536000, immutable";
                var type = Path.GetExtension(fileName) switch
                {
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    _ => "image/jpeg",
                };
                return Results.File(store.PathFor(fileName), type);
            })
            .RequireAuthorization();
    }

    public IEnumerable<NavItem> NavItems =>
    [
        new("Listings", "listings", Icons.Material.Filled.Inventory2, Order: 20),
        new("Lookups", "lookups", Icons.Material.Filled.ListAlt, Order: 30),
    ];
}
