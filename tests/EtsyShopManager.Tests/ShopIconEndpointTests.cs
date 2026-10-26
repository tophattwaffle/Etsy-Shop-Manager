using EtsyShopManager.Web.Modules.Listings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

/// <summary>Shop icons load without signing in, like the Orders, Listings and printout pages that show them.</summary>
public class ShopIconEndpointTests
{
    [Fact]
    public void Shop_icons_need_no_sign_in()
    {
        var builder = WebApplication.CreateBuilder();
        // Only so the endpoints see these as services, not request bodies; nothing is called.
        builder.Services.AddSingleton<ImageStore>(_ => null!);
        builder.Services.AddSingleton<ShopIconCache>(_ => null!);
        var app = builder.Build();
        new ListingsModule().MapEndpoints(app);
        var icon = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == ShopIconCache.UrlPrefix + "{shopId:int}");
        // Headless Edge prints slips for automatic printing without a sign-in, and the pages are open to everyone.
        Assert.NotNull(icon.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.Null(icon.Metadata.GetMetadata<IAuthorizeData>());
    }
}
