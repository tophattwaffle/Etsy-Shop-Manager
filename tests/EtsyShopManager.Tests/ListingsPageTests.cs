using System.Globalization;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtsyShopManager.Tests;

public class ListingsPageTests
{
    [Fact]
    public async Task Facet_counts_ignore_their_own_filter_but_apply_the_others()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.AddRange([
            FakeEtsyShop.Listing(1, "Galaxy vase"),
            FakeEtsyShop.Listing(2, "Galaxy bowl", state: "draft"),
            FakeEtsyShop.Listing(3, "Plant pot"),
        ]);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using (var db = shop.Db.CreateDbContext())
            await db.Listings.Where(l => l.EtsyListingId == 3).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsMissingOnEtsy, true));
        var queries = shop.Provider.GetRequiredService<ListingQueries>();

        // Status "draft" selected: status counts still cover every state, the shop count only drafts.
        var facets = await queries.GetFacetsAsync(new ListingFilter { States = ["draft"] });
        Assert.Equal(1, facets.ByState["active"]);
        Assert.Equal(1, facets.ByState["draft"]);
        Assert.Equal(2, facets.OnEtsy);
        Assert.Equal(1, facets.Missing);
        Assert.Equal(1, facets.ByShop[FakeEtsyShop.ShopId]);

        // Search applies to every group.
        facets = await queries.GetFacetsAsync(new ListingFilter { Text = "plant" });
        Assert.Equal(0, facets.OnEtsy);
        Assert.Equal(1, facets.Missing);
    }

    [Fact]
    public async Task Listings_can_be_found_by_category()
    {
        using var shop = new FakeEtsyShop();
        var other = FakeEtsyShop.Listing(2, "Plant pot", state: "draft");
        other["taxonomy_id"] = 2002;
        shop.Listings.AddRange([FakeEtsyShop.Listing(1, "Galaxy vase"), other, FakeEtsyShop.Listing(3, "Moon vase")]);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        var queries = shop.Provider.GetRequiredService<ListingQueries>();

        var (vases, total) = await queries.SearchAsync(new ListingFilter { TaxonomyId = 1001 });
        Assert.Equal(2, total);
        Assert.Equal([1L, 3L], vases.Select(r => r.EtsyListingId).Order());
        Assert.Equal([2L], (await queries.SearchAsync(new ListingFilter { TaxonomyId = 2002 })).Rows.Select(r => r.EtsyListingId));
    }

    [Fact]
    public async Task Shop_icon_is_downloaded_once_and_served_from_the_image_store()
    {
        using var shop = new FakeEtsyShop();
        await using (var db = shop.Db.CreateDbContext())
            await db.Shops.ExecuteUpdateAsync(s => s.SetProperty(x => x.IconUrl, "https://i.etsystatic.com/isla/icon_fullxfull.jpg"));
        var icons = new ShopIconCache(shop.Db, shop.Provider.GetRequiredService<IHttpClientFactory>(), shop.Store,
            NullLogger<ShopIconCache>.Instance);

        var first = await icons.GetFileNameAsync(FakeEtsyShop.ShopId);
        var second = await icons.GetFileNameAsync(FakeEtsyShop.ShopId);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.True(shop.Store.Exists(first));
        Assert.Single(shop.Cdn.Requests);
        Assert.True((await shop.Provider.GetRequiredService<ListingQueries>().GetImportStatusAsync()).Single().HasIcon);
        Assert.Null(await icons.GetFileNameAsync(999));
    }

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(5 * 60, "5 min ago")]
    [InlineData(3 * 3600, "3 h ago")]
    [InlineData(30 * 3600, "yesterday")]
    [InlineData(4 * 86400, "4 days ago")]
    public void Ago_is_short_and_relative(int secondsAgo, string expected)
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, ListingFormat.Ago(now.AddSeconds(-secondsAgo), now));
    }

    [Fact]
    public void Price_uses_the_currency_symbol_where_there_is_a_clear_one()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.Equal("$24.00", ListingFormat.PriceWithSymbol(2400, 100, "USD"));
            Assert.Equal("24.00 CAD", ListingFormat.PriceWithSymbol(2400, 100, "CAD"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
