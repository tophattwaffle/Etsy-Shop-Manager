using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class VariationListTests
{
    private const long Color = VariationEditor.CustomProperty1;

    /// <summary>
    /// Listing 1: Size (Small 19.99 / Large 29.99, price varies by size) × Color (Black, White, Red, Custom swirl), stock 5.
    /// Listing 2: only the fake's Size variation.
    /// </summary>
    private static async Task<(FakeEtsyShop Shop, int Colored, int Plain)> SetupAsync()
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Galaxy vase");
        long id = 10;
        JsonObject Product(string size, string color, long price)
        {
            var p = FakeEtsyShop.Product(++id, "", size, price, 5);
            p["property_values"]!.AsArray().Add(new JsonObject
            {
                ["property_id"] = Color, ["property_name"] = "Color", ["value_ids"] = new JsonArray(), ["values"] = new JsonArray(color),
            });
            return p;
        }
        string[] colors = ["Black", "White", "Red", "Custom swirl"];
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(100L),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray([.. colors.Select(c => (JsonNode)Product("Small", c, 1999)), .. colors.Select(c => (JsonNode)Product("Large", c, 2999))]),
        };
        shop.Listings.Add(listing);
        shop.Listings.Add(FakeEtsyShop.Listing(2, "Plain vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        var ids = await db.Listings.ToDictionaryAsync(l => l.EtsyListingId, l => l.Id);
        return (shop, ids[1], ids[2]);
    }

    private static VariationListService Lists(FakeEtsyShop s) => s.Provider.GetRequiredService<VariationListService>();
    private static ListingEditor Editor(FakeEtsyShop s) => s.Provider.GetRequiredService<ListingEditor>();

    private static async Task<JsonNode> InventoryAsync(FakeEtsyShop s, int id) => (await Editor(s).LoadAsync(id))!.Model.Local(ListingFields.Inventory)!;

    private static IReadOnlyList<string> Colors(JsonNode inventory) => VariationEditor.Variations(inventory).Single(v => v.PropertyId == Color).Values;

    private static async Task<int> PlaColorsAsync(FakeEtsyShop s) =>
        (await Lists(s).SaveAsync(null, "PLA Colors", [new(0, "Black", true), new(0, "White", true), new(0, "Red", true)], "tophattwaffle")).Id!.Value;

    [Fact]
    public async Task Matching_variations_are_offered_and_a_linked_listing_starts_in_line()
    {
        var (shop, colored, _) = await SetupAsync();
        using var _ = shop;
        var listId = await PlaColorsAsync(shop);

        var candidate = Assert.Single(await Lists(shop).CandidatesAsync(listId)); // the plain listing's Size doesn't match
        Assert.Equal((colored, Color, "Color", 3, 4), (candidate.ListingId, candidate.PropertyId, candidate.VariationName, candidate.InList, candidate.Values));
        Assert.Equal(1, await Lists(shop).LinkAsync(listId, [(colored, Color)], "tophattwaffle"));
        Assert.Empty(await Lists(shop).CandidatesAsync(listId));
        Assert.Equal(VariationListUseStatus.UpToDate, Assert.Single(await Lists(shop).UsesAsync(listId)).Status);

        Assert.Equal("\"Black\" is in the list twice.", (await Lists(shop).SaveAsync(listId, "PLA Colors", [new(1, "Black", true), new(0, "black", true)], "tophattwaffle")).Error);
        Assert.Equal("There's already a list called \"pla colors\".", (await Lists(shop).SaveAsync(null, "pla colors", [new(0, "X", true)], "tophattwaffle")).Error);
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task List_changes_become_drafts_that_add_retire_and_rename_and_push_through_the_reviewed_push()
    {
        var (shop, colored, _) = await SetupAsync();
        using var _ = shop;
        var listId = await PlaColorsAsync(shop);
        await Lists(shop).LinkAsync(listId, [(colored, Color)], "tophattwaffle");
        var list = (await Lists(shop).GetAsync(listId))!;
        var (black, white, red) = (list.Options[0], list.Options[1], list.Options[2]);

        // Retire Red, rename White, add Galaxy Purple (on) and Neon (off).
        await Lists(shop).SaveAsync(listId, "PLA Colors",
            [black, white with { Name = "Pearl White" }, red with { Enabled = false }, new(0, "Galaxy Purple", true), new(0, "Neon", false)], "tophattwaffle");
        var use = Assert.Single(await Lists(shop).UsesAsync(listId));
        Assert.Equal(VariationListUseStatus.HasChanges, use.Status);
        Assert.Equal(["Color:", "Renames White to Pearl White", "Makes Red unavailable", "Adds Galaxy Purple"], use.Changes);

        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tophattwaffle"));
        Assert.Empty(shop.Writes); // applying only writes drafts
        var inventory = await InventoryAsync(shop, colored);
        Assert.Equal(["Black", "Custom swirl", "Galaxy Purple", "Pearl White", "Red"], Colors(inventory).Order());
        Assert.False(VariationEditor.IsValueAvailable(inventory, Color, "Red"));
        Assert.True(VariationEditor.IsValueAvailable(inventory, Color, "Custom swirl")); // the listing's own value is untouched
        Assert.Equal(VariationListUseStatus.UpToDate, Assert.Single(await Lists(shop).UsesAsync(listId)).Status);

        var review = Assert.Single(await Lists(shop).ReviewAsync(listId));
        Assert.True(review.Preview.CanPush, string.Join("; ", review.Preview.Blockers));
        var pushId = Assert.Single(await Lists(shop).PushAsync(listId, "tophattwaffle")).PushId!.Value;
        await shop.Provider.GetRequiredService<PushListingJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1, JobType = PushListingJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId), JobJson.Options),
        }, CancellationToken.None);

        var body = JsonNode.Parse(shop.Writes.Single(w => w.Path == "/v3/application/listings/1/inventory").Body)!;
        var products = body["products"]!.AsArray().Select(p => p!).ToList();
        string ColorOf(JsonNode p) => (string)p["property_values"]!.AsArray().Single(v => (long)v!["property_id"]! == Color)!["values"]![0]!;
        string SizeOf(JsonNode p) => (string)p["property_values"]!.AsArray().Single(v => (long)v!["property_id"]! == 100)!["values"]![0]!;
        Assert.Equal(10, products.Count);
        Assert.All(products.Where(p => ColorOf(p) == "Red"), p => Assert.False((bool)p["offerings"]![0]!["is_enabled"]!));
        var galaxyLarge = products.Single(p => ColorOf(p) == "Galaxy Purple" && SizeOf(p) == "Large")["offerings"]![0]!;
        Assert.Equal((29.99m, 5), ((decimal)galaxyLarge["price"]!, (int)galaxyLarge["quantity"]!)); // the size's price, the listing's stock

        var listing = await shop.Db.CreateDbContext().Listings.SingleAsync(l => l.Id == colored);
        Assert.False(listing.HasDraft);
        Assert.Equal(VariationListUseStatus.UpToDate, Assert.Single(await Lists(shop).UsesAsync(listId)).Status);
    }

    [Fact]
    public async Task Hand_edits_removed_options_and_changes_on_Etsy_are_left_alone()
    {
        var (shop, colored, _) = await SetupAsync();
        using var _ = shop;
        var listId = await PlaColorsAsync(shop);
        await Lists(shop).LinkAsync(listId, [(colored, Color)], "tophattwaffle");
        var list = (await Lists(shop).GetAsync(listId))!;

        // Black is removed from the listing by hand; the list then renames Black and drops Red from the list.
        await Editor(shop).SaveAsync(colored, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Inventory] = VariationEditor.RemoveValue(await InventoryAsync(shop, colored), Color, "Black"),
        }, "tophattwaffle");
        await Lists(shop).SaveAsync(listId, "PLA Colors", [list.Options[0] with { Name = "Jet Black" }, list.Options[1]], "tophattwaffle");
        var use = Assert.Single(await Lists(shop).UsesAsync(listId));
        Assert.Equal(VariationListUseStatus.UpToDate, use.Status);
        Assert.Equal(["Black was removed from this listing by hand, so the list leaves it out."], use.Notes);
        Assert.Equal(0, await Lists(shop).ApplyAsync(listId, "tophattwaffle"));
        Assert.Contains("Red", Colors(await InventoryAsync(shop, colored))); // dropped from the list, kept on the listing

        // A change to the variations on etsy.com blocks the list for that listing until it's resolved.
        await Editor(shop).DiscardAsync(colored, null, "tophattwaffle");
        var etsyProducts = shop.ListingJson(1)["inventory"]!["products"]!.AsArray();
        etsyProducts.RemoveAt(etsyProducts.Count - 1);
        etsyProducts.RemoveAt(3); // "Custom swirl" gone from both sizes
        shop.ListingJson(1)["last_modified_timestamp"] = 1_760_000_500;
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        Assert.Equal(VariationListUseStatus.ChangedOnEtsy, Assert.Single(await Lists(shop).UsesAsync(listId)).Status);
        await Lists(shop).SaveAsync(listId, "PLA Colors", [list.Options[0], list.Options[1], new(0, "Blue", true)], "tophattwaffle");
        Assert.Equal(0, await Lists(shop).ApplyAsync(listId, "tophattwaffle"));
        Assert.Empty(shop.Writes);
    }
}
