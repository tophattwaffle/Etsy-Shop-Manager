using System.Security.Claims;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Modules;
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

    private static ClaimsPrincipal User(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "tophattwaffle"), new Claim(ClaimTypes.Role, role)], "test"));

    private static async Task<Dictionary<int, string>> ReviewedAsync(FakeEtsyShop shop) =>
        (await Conflicts(shop).ListAsync()).ToDictionary(r => r.ListingId, r => r.Reviewed);

    private static async Task<ListingEditModel> ModelAsync(FakeEtsyShop shop, int id)
    {
        await using var db = shop.Db.CreateDbContext();
        var l = await db.Listings.Include(x => x.EtsyState).SingleAsync(x => x.Id == id);
        return ListingEditModel.Parse(l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson);
    }

    [Fact]
    public async Task Bulk_keep_mine_resolves_every_field_locally_and_leaves_the_values_to_push()
    {
        var (shop, _, moon, star) = await ChangedAsync();
        using var _s = shop;

        var results = await Conflicts(shop).ResolveAsync(await ReviewedAsync(shop), DriftResolution.KeepMine, User(Roles.Admin));

        Assert.Equal([(moon, 2), (star, 1)], results.Select(r => (r.ListingId, r.Resolved)).Order());
        Assert.All(results, r => Assert.Null(r.Problem));
        Assert.Equal(0, await Conflicts(shop).CountAsync());
        var m = await ModelAsync(shop, moon);
        Assert.Empty(m.DriftFields);
        Assert.Equal("Moon vase, large", (string?)m.Local("title"));
        Assert.Contains("tags", m.PendingFields); // the app's tags go back to Etsy with the next push
        var s = await ModelAsync(shop, star);
        Assert.Equal("Star vase", (string?)s.Local("title"));
        Assert.Contains("title", s.PendingFields);
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task Bulk_take_Etsys_adopts_Etsys_values_and_keeps_other_local_edits()
    {
        var (shop, _, moon, star) = await ChangedAsync();
        using var _s = shop;

        await Conflicts(shop).ResolveAsync(await ReviewedAsync(shop), DriftResolution.TakeEtsy, User(Roles.Admin));

        Assert.Equal(0, await Conflicts(shop).CountAsync());
        var m = await ModelAsync(shop, moon);
        Assert.Equal(["vase", "etsy edit"], m.Local("tags")!.AsArray().Select(v => (string?)v));
        Assert.Equal(["title"], m.PendingFields); // the local title edit wasn't a conflict and stays
        var s = await ModelAsync(shop, star);
        Assert.Equal("Star vase, new on Etsy", (string?)s.Local("title"));
        Assert.Empty(s.PendingFields);
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task A_listing_changed_again_on_Etsy_after_it_was_reviewed_is_skipped()
    {
        var (shop, _, moon, star) = await ChangedAsync();
        using var _s = shop;
        var reviewed = await ReviewedAsync(shop);
        shop.ListingJson(3)["title"] = "Star vase, newer on Etsy";
        shop.ListingJson(3)["last_modified_timestamp"] = 1_760_000_700;
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var results = await Conflicts(shop).ResolveAsync(reviewed, DriftResolution.TakeEtsy, User(Roles.Admin));

        Assert.Null(results.Single(r => r.ListingId == moon).Problem);
        var skipped = results.Single(r => r.ListingId == star);
        Assert.Equal(0, skipped.Resolved);
        Assert.Contains("changed since you looked", skipped.Problem);
        Assert.Equal(["title"], (await ModelAsync(shop, star)).DriftFields);
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task Only_admins_can_bulk_resolve()
    {
        var (shop, _, _, _) = await ChangedAsync();
        using var _s = shop;
        var reviewed = await ReviewedAsync(shop);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Conflicts(shop).ResolveAsync(reviewed, DriftResolution.KeepMine, User(Roles.User)));
        Assert.Equal(2, await Conflicts(shop).CountAsync());
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
