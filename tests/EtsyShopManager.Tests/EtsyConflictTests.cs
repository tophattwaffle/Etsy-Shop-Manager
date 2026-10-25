using System.Text.Json.Nodes;
using EtsyShopManager.Web.Modules.Editor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class EtsyConflictTests
{
    /// <summary>Three imported listings: Galaxy vase is only edited here; Moon vase is edited here and then its tags and
    /// materials are changed on etsy.com; Star vase gets a new title on etsy.com only.</summary>
    private static async Task<(FakeEtsyShop Shop, int Galaxy, int Moon, int Star)> ChangedAsync()
    {
        var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Galaxy vase"));
        shop.Listings.Add(FakeEtsyShop.Listing(2, "Moon vase"));
        shop.Listings.Add(FakeEtsyShop.Listing(3, "Star vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        var ids = await db.Listings.ToDictionaryAsync(l => l.EtsyListingId, l => l.Id);

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(ids[1], new Dictionary<string, JsonNode?> { ["title"] = "Galaxy vase, large" }, "tophattwaffle");
        await editor.SaveAsync(ids[2], new Dictionary<string, JsonNode?> { ["title"] = "Moon vase, large" }, "tophattwaffle");
        shop.ListingJson(2)["tags"] = new JsonArray("vase", "etsy edit");
        shop.ListingJson(2)["materials"] = new JsonArray("stoneware");
        shop.ListingJson(2)["last_modified_timestamp"] = 1_760_000_500;
        shop.ListingJson(3)["title"] = "Star vase, new on Etsy";
        shop.ListingJson(3)["last_modified_timestamp"] = 1_760_000_600;
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        return (shop, ids[1], ids[2], ids[3]);
    }

    private static EtsyConflictService Conflicts(FakeEtsyShop s) => s.Provider.GetRequiredService<EtsyConflictService>();

    [Fact]
    public async Task Lists_only_the_listings_changed_on_Etsy_with_the_fields_that_need_a_choice()
    {
        var (shop, _, moon, star) = await ChangedAsync();
        using var _s = shop;

        Assert.Equal(2, await Conflicts(shop).CountAsync());
        var rows = await Conflicts(shop).ListAsync();

        Assert.Equal([moon, star], rows.Select(r => r.ListingId).Order());
        var m = rows.Single(r => r.ListingId == moon);
        Assert.Equal(["Tags", "Materials"], m.Fields);
        Assert.True(m.HasLocalEdits);
        var s = rows.Single(r => r.ListingId == star);
        Assert.Equal(["Title"], s.Fields);
        Assert.False(s.HasLocalEdits);
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task The_count_drops_once_every_field_of_a_listing_is_resolved()
    {
        var (shop, _, moon, star) = await ChangedAsync();
        using var _s = shop;
        var editor = shop.Provider.GetRequiredService<ListingEditor>();

        await editor.ResolveDriftAsync(moon, "tags", DriftResolution.KeepMine, "tophattwaffle");
        Assert.Equal(2, await Conflicts(shop).CountAsync());
        Assert.Equal(["Materials"], (await Conflicts(shop).ListAsync()).Single(r => r.ListingId == moon).Fields);

        await editor.ResolveDriftAsync(moon, "materials", DriftResolution.TakeEtsy, "tophattwaffle");
        Assert.Equal(1, await Conflicts(shop).CountAsync());
        await editor.ResolveDriftAsync(star, "title", DriftResolution.TakeEtsy, "tophattwaffle");
        Assert.Equal(0, await Conflicts(shop).CountAsync());
        Assert.Empty(await Conflicts(shop).ListAsync());

        await using var db = shop.Db.CreateDbContext();
        Assert.False(await db.Listings.AnyAsync(l => l.HasDrift));
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task A_stale_flag_is_neither_counted_nor_listed_and_gets_cleared()
    {
        var (shop, galaxy, _, _) = await ChangedAsync();
        using var _s = shop;
        await using (var db = shop.Db.CreateDbContext())
        {
            // Flagged although Etsy matches the agreed version (e.g. set before the drift was settled).
            await db.Listings.Where(l => l.Id == galaxy).ExecuteUpdateAsync(u => u.SetProperty(l => l.HasDrift, true));
        }

        Assert.Equal(2, await Conflicts(shop).CountAsync());
        Assert.DoesNotContain(await Conflicts(shop).ListAsync(), r => r.ListingId == galaxy);
        await using var check = shop.Db.CreateDbContext();
        Assert.False((await check.Listings.SingleAsync(l => l.Id == galaxy)).HasDrift);
    }
}
