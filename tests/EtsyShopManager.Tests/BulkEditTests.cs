using System.Security.Claims;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Web.Modules.Editor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class BulkEditTests
{
    /// <summary>Two imported listings (Small 19.99, Large 29.99; tags vase, pla; material PLA; shipping 501, section 801).</summary>
    private static async Task<(FakeEtsyShop Shop, int First, int Second)> ImportedAsync()
    {
        var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Galaxy vase"));
        shop.Listings.Add(FakeEtsyShop.Listing(2, "Moon vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        var ids = await db.Listings.ToDictionaryAsync(l => l.EtsyListingId, l => l.Id);
        return (shop, ids[1], ids[2]);
    }

    private static BulkListingEditor Bulk(FakeEtsyShop s) => s.Provider.GetRequiredService<BulkListingEditor>();

    private static ClaimsPrincipal User(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "tophattwaffle"), new Claim(ClaimTypes.Role, role)], "test"));

    private static async Task<ListingEditModel> ModelAsync(FakeEtsyShop s, int id)
    {
        await using var db = s.Db.CreateDbContext();
        var l = await db.Listings.Include(x => x.EtsyState).SingleAsync(x => x.Id == id);
        return ListingEditModel.Parse(l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson);
    }

    private static List<long> Prices(JsonNode? inventory) =>
        [.. inventory!["products"]!.AsArray().Select(p => (long)p!["offerings"]![0]!["price_amount"]!).Order()];

    private static List<string> Words(JsonNode? array) => [.. array!.AsArray().Select(v => (string)v!)];

    [Theory]
    [InlineData("19.99", "10", null, PriceRounding.None, "21.99")] // 21.989
    [InlineData("19.99", "10", null, PriceRounding.To99, "21.99")]
    [InlineData("21.37", "0", null, PriceRounding.To99, "20.99")] // nearest .99, down as well as up
    [InlineData("29.99", "-10", null, PriceRounding.Whole, "27")] // 26.991
    [InlineData("19.99", null, "1.5", PriceRounding.None, "21.49")]
    [InlineData("0.40", "-75", null, PriceRounding.To99, "0.99")] // .99 rounding never goes below 0.99
    [InlineData("19.99", null, "1.37", PriceRounding.To99, "21.36")] // an amount is exact: rounding only follows a percent
    public void New_price_applies_the_change_then_the_rounding(string price, string? percent, string? amount, PriceRounding rounding, string expected)
    {
        static decimal? D(string? s) => s is null ? null : decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        var edit = new BulkEdit(BulkEditKind.Price, D(percent), D(amount), rounding);
        Assert.Equal(D(expected), BulkListingEditor.NewPrice(D(price)!.Value, edit));
    }

    [Fact]
    public void Incomplete_changes_say_what_is_missing()
    {
        Assert.NotNull(new BulkEdit(BulkEditKind.Price).Problem);
        Assert.NotNull(new BulkEdit(BulkEditKind.Price, Percent: -100).Problem);
        Assert.NotNull(new BulkEdit(BulkEditKind.Price, Percent: 0).Problem);
        Assert.Null(new BulkEdit(BulkEditKind.Price, Percent: 0, Rounding: PriceRounding.To99).Problem); // rounding alone is a change
        Assert.NotNull(new BulkEdit(BulkEditKind.Price, Amount: 0, Rounding: PriceRounding.To99).Problem); // but not after an amount
        Assert.NotNull(new BulkEdit(BulkEditKind.AddTags, Words: [" ", ""]).Problem);
        Assert.NotNull(new BulkEdit(BulkEditKind.Section, PerShop: new Dictionary<int, long?>()).Problem);
        Assert.Null(new BulkEdit(BulkEditKind.Section, PerShop: new Dictionary<int, long?> { [1] = null }).Problem);
    }

    [Fact]
    public async Task Price_change_previews_then_saves_unpushed_edits_and_sends_nothing()
    {
        var (shop, first, second) = await ImportedAsync();
        using var _ = shop;
        var edit = new BulkEdit(BulkEditKind.Price, Percent: 10, Rounding: PriceRounding.To99);

        var preview = await Bulk(shop).PreviewAsync([first, second], edit);
        Assert.Equal(2, preview.Count(r => r.Changes));
        Assert.Contains("21.99", preview[0].After);
        Assert.Contains("32.99", preview[0].After);
        Assert.Equal([1999, 2999], Prices((await ModelAsync(shop, first)).Local(ListingFields.Inventory))); // a preview saves nothing

        var changed = await Bulk(shop).ApplyAsync([first, second], edit, User(Roles.Editor));

        Assert.Equal([first, second], changed.Order());
        foreach (var id in changed)
        {
            var model = await ModelAsync(shop, id);
            Assert.Equal([2199, 3299], Prices(model.Local(ListingFields.Inventory)));
            Assert.Equal([ListingFields.Inventory], model.PendingFields);
        }
        Assert.Empty(shop.Writes);
        Assert.Empty(shop.Provider.GetRequiredService<RecordingJobQueue>().Requests);
    }

    [Fact]
    public async Task Pushing_after_a_bulk_edit_is_each_listings_normal_push()
    {
        var (shop, first, _) = await ImportedAsync();
        using var _s = shop;
        await Bulk(shop).ApplyAsync([first], new BulkEdit(BulkEditKind.Price, Amount: 1), User(Roles.Editor));

        var review = Assert.Single(await Bulk(shop).ReviewAsync([first]));
        Assert.True(review.Preview.CanPush, string.Join("; ", review.Preview.Blockers));
        var result = Assert.Single(await Bulk(shop).PushAsync([first], User(Roles.Editor)));
        Assert.NotNull(result.PushId);

        await shop.Provider.GetRequiredService<PushListingJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1, JobType = PushListingJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(result.PushId!.Value), JobJson.Options),
        }, CancellationToken.None);

        var put = Assert.Single(shop.Writes);
        Assert.Equal(("PUT", "/v3/application/listings/1/inventory"), (put.Method, put.Path));
        var prices = JsonNode.Parse(put.Body)!["products"]!.AsArray().Select(p => (decimal)p!["offerings"]![0]!["price"]!).ToList();
        Assert.Equal([20.99m, 30.99m], prices);
    }

    [Fact]
    public async Task Tags_are_added_once_and_listings_over_Etsys_limit_or_changed_on_Etsy_are_skipped()
    {
        var (shop, first, second) = await ImportedAsync();
        using var _ = shop;
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        // The second listing already has 12 tags, so adding two would make 14.
        await editor.SaveAsync(second, new Dictionary<string, JsonNode?>
        {
            ["tags"] = new JsonArray([.. Enumerable.Range(1, 12).Select(i => (JsonNode)$"tag {i}")]),
        }, "tophattwaffle");

        var rows = await Bulk(shop).PreviewAsync([first, second], new BulkEdit(BulkEditKind.AddTags, Words: ["Gift", "VASE", "gift"]));
        Assert.True(rows.Single(r => r.ListingId == first).Changes);
        Assert.Contains("Etsy allows 13", rows.Single(r => r.ListingId == second).Skipped);

        await Bulk(shop).ApplyAsync([first, second], new BulkEdit(BulkEditKind.AddTags, Words: ["Gift", "VASE", "gift"]), User(Roles.Admin));
        Assert.Equal(["vase", "pla", "Gift"], Words((await ModelAsync(shop, first)).Local("tags"))); // "VASE" is already there
        Assert.Equal(12, (await ModelAsync(shop, second)).Local("tags")!.AsArray().Count);

        // Tags changed on etsy.com: the listing is skipped until that's resolved in its editor.
        shop.ListingJson(1)["tags"] = new JsonArray("vase", "etsy edit");
        shop.ListingJson(1)["last_modified_timestamp"] = 1_760_000_500;
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        var row = Assert.Single(await Bulk(shop).PreviewAsync([first], new BulkEdit(BulkEditKind.RemoveTags, Words: ["gift"])));
        Assert.Contains("changed on Etsy", row.Skipped);
        Assert.Empty(await Bulk(shop).ApplyAsync([first], new BulkEdit(BulkEditKind.RemoveTags, Words: ["gift"]), User(Roles.Admin)));
    }

    [Fact]
    public async Task Materials_are_removed_ignoring_case()
    {
        var (shop, first, _) = await ImportedAsync();
        using var _s = shop;

        await Bulk(shop).ApplyAsync([first], new BulkEdit(BulkEditKind.RemoveMaterials, Words: ["pla"]), User(Roles.Editor));

        Assert.Empty(Words((await ModelAsync(shop, first)).Local("materials")));
    }

    [Fact]
    public async Task Shipping_profile_and_section_are_chosen_per_shop()
    {
        var (shop, first, second) = await ImportedAsync();
        using var _ = shop;
        await using (var db = shop.Db.CreateDbContext())
        {
            db.ShippingProfiles.Add(new ShippingProfile { ShopId = FakeEtsyShop.ShopId, EtsyShippingProfileId = 502, Title = "Express" });
            await db.SaveChangesAsync();
        }

        var shops = Assert.Single(await Bulk(shop).ShopsAsync([first, second]));
        Assert.Equal(2, shops.Listings);
        Assert.Equal(["Express", "Standard"], shops.ShippingProfiles.Values.Order());

        var notThisShops = new BulkEdit(BulkEditKind.ShippingProfile, PerShop: new Dictionary<int, long?> { [FakeEtsyShop.ShopId] = 999 });
        Assert.All(await Bulk(shop).PreviewAsync([first, second], notThisShops), r => Assert.NotNull(r.Skipped));
        var otherShop = new BulkEdit(BulkEditKind.ShippingProfile, PerShop: new Dictionary<int, long?> { [FakeEtsyShop.ShopId + 1] = 502 });
        Assert.All(await Bulk(shop).PreviewAsync([first], otherShop), r => Assert.Equal("Nothing chosen for this shop.", r.Skipped));

        await Bulk(shop).ApplyAsync([first, second],
            new BulkEdit(BulkEditKind.ShippingProfile, PerShop: new Dictionary<int, long?> { [FakeEtsyShop.ShopId] = 502 }), User(Roles.Editor));
        await Bulk(shop).ApplyAsync([first],
            new BulkEdit(BulkEditKind.Section, PerShop: new Dictionary<int, long?> { [FakeEtsyShop.ShopId] = null }), User(Roles.Editor));

        Assert.Equal(502, (long?)(await ModelAsync(shop, second)).Local("shipping_profile_id"));
        var model = await ModelAsync(shop, first);
        Assert.Equal(502, (long?)model.Local("shipping_profile_id"));
        Assert.Null(model.Local("shop_section_id")); // no section
        Assert.Equal(["shipping_profile_id", "shop_section_id"], model.PendingFields);
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task Viewers_cannot_bulk_edit_or_push()
    {
        var (shop, first, _) = await ImportedAsync();
        using var _s = shop;
        var edit = new BulkEdit(BulkEditKind.AddTags, Words: ["gift"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Bulk(shop).ApplyAsync([first], edit, User(Roles.Viewer)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Bulk(shop).PushAsync([first], User(Roles.Viewer)));

        Assert.False((await ModelAsync(shop, first)).HasDraft);
    }
}
