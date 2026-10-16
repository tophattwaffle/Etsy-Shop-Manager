using System.Net;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

/// <summary>Regression tests for issues found in the codebase review (2026-10-16).</summary>
public class CodebaseReviewTests
{
    [Fact]
    public async Task Photo_gone_from_Etsy_is_recorded_without_failing_the_job()
    {
        using var db = new TestDb();
        using var storage = new TempStorage();
        var cdn = new ScriptedHandler((req, _) => req.RequestUri!.AbsolutePath.Contains("gone")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("JPEG"u8.ToArray()) });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new ImmediateTimeProvider());
        services.AddSingleton<IDbContextFactory<AppDbContext>>(db);
        services.AddSingleton(storage.Options);
        services.AddSingleton<ImageStore>();
        services.AddSingleton<ImageDownloader>();
        services.AddHttpClient(EtsyServiceCollectionExtensions.EtsyImageClientName).ConfigurePrimaryHttpMessageHandler(() => cdn);
        await using var provider = services.BuildServiceProvider();

        await using (var ctx = db.CreateDbContext())
        {
            var shop = new Shop { EtsyShopId = 42, Name = "S", ConnectedAtUtc = DateTime.UtcNow };
            ctx.Listings.Add(new Listing
            {
                Shop = shop, EtsyListingId = 1, Title = "x",
                Images =
                [
                    new ListingImage { EtsyImageId = 1, Rank = 1, SourceUrl = "https://i.etsystatic.com/ok/full.jpg" },
                    new ListingImage { EtsyImageId = 2, Rank = 2, SourceUrl = "https://i.etsystatic.com/gone/full.jpg" },
                ],
            });
            await ctx.SaveChangesAsync();
        }

        var result = await provider.GetRequiredService<ImageDownloader>().DownloadForShopAsync(1, CancellationToken.None);

        Assert.Equal(1, result.Downloaded);
        Assert.Equal(0, result.Failed); // the job only throws (and retries) on Failed > 0
        Assert.Equal(1, result.Gone);
        await using var check = db.CreateDbContext();
        Assert.Contains("404", (await check.ListingImages.SingleAsync(i => i.EtsyImageId == 2)).DownloadError);
    }

    [Fact]
    public async Task One_listing_without_readable_attributes_does_not_fail_the_shop_import()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.AddRange([FakeEtsyShop.Listing(1, "One"), FakeEtsyShop.Listing(2, "Two")]);
        shop.ListingsWithoutProperties.Add(2);

        var result = await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        Assert.Equal(2, result.ListingsSeen);
        await using var db = shop.Db.CreateDbContext();
        var two = await db.Listings.SingleAsync(l => l.EtsyListingId == 2);
        Assert.Null(two.AttributesFetchedAtUtc); // retried on the next import
        Assert.NotNull((await db.Listings.SingleAsync(l => l.EtsyListingId == 1)).AttributesFetchedAtUtc);
    }

    [Fact]
    public async Task Numeric_search_also_matches_SKUs()
    {
        using var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(7, "Vase");
        listing["skus"] = new System.Text.Json.Nodes.JsonArray("12345");
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var (rows, _) = await shop.Provider.GetRequiredService<ListingQueries>().SearchAsync(new ListingFilter { Text = "12345" });

        Assert.Equal([7L], rows.Select(r => r.EtsyListingId));
    }
}
