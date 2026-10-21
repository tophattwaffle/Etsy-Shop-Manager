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

        Assert.Equal("\"Black\" is in the template twice.", (await Lists(shop).SaveAsync(listId, "PLA Colors", [new(1, "Black", true), new(0, "black", true)], "tophattwaffle")).Error);
        Assert.Equal("There's already a template called \"pla colors\".", (await Lists(shop).SaveAsync(null, "pla colors", [new(0, "X", true)], "tophattwaffle")).Error);
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

    /// <summary>Adds listing 3, "Test listing", with no variations: one product at 19.99, stock 5.</summary>
    private static async Task<int> AddListingWithoutVariationsAsync(FakeEtsyShop shop)
    {
        var listing = FakeEtsyShop.Listing(3, "Test listing");
        var product = FakeEtsyShop.Product(301, "", "", 1999, 5);
        product["property_values"] = new JsonArray();
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(), ["quantity_on_property"] = new JsonArray(), ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(product),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return await db.Listings.Where(l => l.EtsyListingId == 3).Select(l => l.Id).SingleAsync();
    }

    private static async Task PushAsync(FakeEtsyShop shop, int pushId) =>
        await shop.Provider.GetRequiredService<PushListingJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1, JobType = PushListingJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId), JobJson.Options),
        }, CancellationToken.None);

    [Fact]
    public async Task A_list_added_to_a_listing_without_variations_becomes_a_linked_variation_pushed_through_the_reviewed_push()
    {
        var (shop, colored, plain) = await SetupAsync();
        using var _ = shop;
        var test = await AddListingWithoutVariationsAsync(shop);
        var listId = (await Lists(shop).SaveAsync(null, "PLA Colors", [new(0, "Black", true), new(0, "White", true), new(0, "Red", false)], "tophattwaffle")).Id!.Value;
        await Lists(shop).LinkAsync(listId, [(colored, Color)], "tophattwaffle");

        // Any listing with room for a variation is offered, whatever its values (the colored one already has two).
        Assert.Equal([plain, test], (await Lists(shop).AddableAsync(listId)).Select(a => a.ListingId).Order());
        Assert.Equal("Color", await Lists(shop).SuggestedVariationNameAsync(listId)); // the name its linked listings use

        var (preview, error) = await Lists(shop).PreviewAddAsync(listId, test, "Color");
        Assert.Null(error);
        Assert.Equal(["Black", "White"], preview!.Values); // Red is switched off
        Assert.Equal(2, preview.Combinations);
        Assert.False((await Editor(shop).LoadAsync(test))!.Listing.HasDraft); // a preview saves nothing

        var (plan, addError) = await Lists(shop).AddToListingAsync(listId, test, "Color", "tophattwaffle");
        Assert.Null(addError);
        Assert.Empty(shop.Writes); // only a draft
        var inventory = await InventoryAsync(shop, test);
        var variation = Assert.Single(VariationEditor.Variations(inventory));
        Assert.Equal((plan!.PropertyId, "Color"), (variation.PropertyId, variation.Name));
        Assert.Equal(["Black", "White"], variation.Values);
        Assert.True(variation.IsCustom);
        Assert.Empty(inventory["price_on_property"]!.AsArray()); // one price for every color
        var use = Assert.Single(await Lists(shop).UsesAsync(listId), u => u.ListingId == test);
        Assert.Equal((VariationListUseStatus.UpToDate, true), (use.Status, use.InventoryPending));
        Assert.Equal("Test listing already uses this template.", (await Lists(shop).AddToListingAsync(listId, test, "Color", "tophattwaffle")).Error);

        var review = Assert.Single(await Lists(shop).ReviewAsync(listId));
        Assert.True(review.Preview.CanPush, string.Join("; ", review.Preview.Blockers));
        await PushAsync(shop, Assert.Single(await Lists(shop).PushAsync(listId, "tophattwaffle")).PushId!.Value);
        var body = JsonNode.Parse(shop.Writes.Single(w => w.Path == "/v3/application/listings/3/inventory").Body)!;
        var products = body["products"]!.AsArray().Select(p => p!).ToList();
        Assert.Equal(["Black", "White"], products.Select(p => (string)p["property_values"]![0]!["values"]![0]!).Order());
        Assert.All(products, p => Assert.Equal((plan.PropertyId, 19.99m, 5),
            ((long)p["property_values"]![0]!["property_id"]!, (decimal)p["offerings"]![0]!["price"]!, (int)p["offerings"]![0]!["quantity"]!)));
        Assert.False((await Editor(shop).LoadAsync(test))!.Listing.HasDraft);

        // Later changes to the list follow, like for any linked listing.
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "PLA Colors", [.. list.Options, new(0, "Galaxy Purple", true)], "tophattwaffle");
        Assert.Equal(["Color:", "Adds Galaxy Purple"], Assert.Single(await Lists(shop).UsesAsync(listId), u => u.ListingId == test).Changes);
    }

    [Fact]
    public async Task Adding_a_list_next_to_another_variation_multiplies_the_combinations_and_says_why_it_cannot()
    {
        var (shop, colored, plain) = await SetupAsync();
        using var _ = shop;
        var listId = await PlaColorsAsync(shop);

        // The plain listing has Size (price varies by size): each size takes each color, prices still follow the size.
        Assert.Equal("Size", Assert.Single(await Lists(shop).AddableAsync(listId), a => a.ListingId == plain).Variations.Single());
        Assert.Equal("There is already a variation called \"Size\".", (await Lists(shop).PreviewAddAsync(listId, plain, "Size")).Error);
        var (plan, error) = await Lists(shop).AddToListingAsync(listId, plain, "Color", "tophattwaffle");
        Assert.Null(error);
        Assert.Equal(6, plan!.Combinations);
        var inventory = await InventoryAsync(shop, plain);
        Assert.Equal([100L], inventory["price_on_property"]!.AsArray().Select(x => (long)x!));
        Assert.Equal(6, inventory["products"]!.AsArray().Count);

        // Two variations already: blocked with the reason. Too many options: blocked before anything is saved.
        var other = (await Lists(shop).SaveAsync(null, "Sizes", [new(0, "XL", true), new(0, "XXL", true)], "tophattwaffle")).Id!.Value;
        Assert.Equal("This listing already has 2 variations (Size and Color); remove one in the editor first.",
            (await Lists(shop).PreviewAddAsync(other, colored, "Fit")).Error);
        var many = (await Lists(shop).SaveAsync(null, "Many", [.. Enumerable.Range(1, 71).Select(i => new VariationListOption(0, $"Option {i}", true))], "tophattwaffle")).Id!.Value;
        var test = await AddListingWithoutVariationsAsync(shop);
        Assert.Equal("The template has 71 options switched on; Etsy allows at most 70 in a variation.", (await Lists(shop).PreviewAddAsync(many, test, "Option")).Error);
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task A_variation_is_linked_from_the_editor_with_its_match_shown_and_applied_to_that_listing_only()
    {
        var (shop, colored, plain) = await SetupAsync();
        using var _ = shop;
        var colors = await PlaColorsAsync(shop);
        var sizes = (await Lists(shop).SaveAsync(null, "Sizes", [new(0, "Small", true), new(0, "Large", true), new(0, "XL", true)], "tophattwaffle")).Id!.Value;

        // Any list can be chosen; a poor match is linkable but warned about.
        var (poor, poorError) = await Lists(shop).PreviewLinkAsync(colors, plain, 100);
        Assert.Null(poorError);
        Assert.Equal((0, 2), (poor!.InList, poor.Values));
        Assert.Equal("None of Size's values are in PLA Colors; applying it adds all of the template's options next to them.", poor.Warning);
        Assert.Equal(["Size:", "Adds Black", "Adds White", "Adds Red"], poor.Changes);

        var (good, _) = await Lists(shop).PreviewLinkAsync(colors, colored, Color);
        Assert.Equal((3, 4, null), (good!.InList, good.Values, good.Warning));
        Assert.Empty(good.Changes);
        Assert.Null(await Lists(shop).LinkVariationAsync(colors, colored, Color, "tophattwaffle"));
        Assert.Equal("Color already follows PLA Colors; unlink it first.", (await Lists(shop).PreviewLinkAsync(sizes, colored, Color)).Error);

        // Linking records the link only; "Apply list now" changes that listing and no other.
        var (sizePreview, _) = await Lists(shop).PreviewLinkAsync(sizes, colored, 100);
        Assert.Equal(["Size:", "Adds XL"], sizePreview!.Changes);
        Assert.Null(await Lists(shop).LinkVariationAsync(sizes, colored, 100, "tophattwaffle"));
        Assert.Null(await Lists(shop).LinkVariationAsync(sizes, plain, 100, "tophattwaffle"));
        Assert.Equal([("PLA Colors", Color), ("Sizes", 100L)],
            (await Lists(shop).ListingLinksAsync(colored)).Select(l => (l.ListName, l.PropertyId)).OrderBy(l => l.ListName));
        Assert.Equal(1, await Lists(shop).ApplyAsync(sizes, "tophattwaffle", listingId: colored));
        Assert.Contains("XL", VariationEditor.Variations(await InventoryAsync(shop, colored)).Single(v => v.PropertyId == 100).Values);
        Assert.DoesNotContain("XL", VariationEditor.Variations(await InventoryAsync(shop, plain)).Single().Values);
        Assert.Empty(shop.Writes);

        // Unlinking keeps the values as they are.
        await Lists(shop).UnlinkAsync(sizes, colored, "tophattwaffle");
        Assert.Contains("XL", VariationEditor.Variations(await InventoryAsync(shop, colored)).Single(v => v.PropertyId == 100).Values);
        Assert.Equal("PLA Colors", Assert.Single(await Lists(shop).ListingLinksAsync(colored)).ListName);
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
        Assert.Equal(VariationListUseStatus.HasChanges, use.Status);
        Assert.Equal(["Color:", "Removes Red"], use.Changes);
        Assert.Equal(["Black was removed from this listing by hand, so the template leaves it out."], use.Notes);
        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tophattwaffle"));
        Assert.DoesNotContain("Red", Colors(await InventoryAsync(shop, colored))); // dropped from the list, removed from listing
        Assert.Contains("Custom swirl", Colors(await InventoryAsync(shop, colored))); // custom unlinked value preserved

        // A change to the variations on etsy.com blocks the list for that listing until it's resolved.
        await Editor(shop).DiscardAsync(colored, null, "tophattwaffle");
        var etsyProducts = shop.ListingJson(1)["inventory"]!["products"]!.AsArray();
        etsyProducts.RemoveAt(etsyProducts.Count - 1);
        etsyProducts.RemoveAt(3); // "Custom swirl" gone from both sizes
        shop.ListingJson(1)["last_modified_timestamp"] = 1_760_000_500;
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        Assert.Equal(VariationListUseStatus.ChangedOnEtsy, Assert.Single(await Lists(shop).UsesAsync(listId)).Status);
        var sizes = (await Lists(shop).SaveAsync(null, "Sizes", [new(0, "Small", true)], "tophattwaffle")).Id!.Value;
        Assert.Equal("This listing's variations changed on Etsy; resolve that first.", await Lists(shop).LinkVariationAsync(sizes, colored, 100, "tophattwaffle"));
        await Lists(shop).SaveAsync(listId, "PLA Colors", [list.Options[0], list.Options[1], new(0, "Blue", true)], "tophattwaffle");
        Assert.Equal(0, await Lists(shop).ApplyAsync(listId, "tophattwaffle"));
        Assert.Empty(shop.Writes);
    }

    private static async Task<int> AddSingleVariationListingAsync(FakeEtsyShop shop, long listingId, string title, (string Color, long Price)[] options)
    {
        var listing = FakeEtsyShop.Listing(listingId, title);
        long productId = listingId * 100;
        JsonObject MakeProduct(string color, long price) => new()
        {
            ["product_id"] = ++productId,
            ["sku"] = $"SKU-{listingId}-{color}",
            ["is_deleted"] = false,
            ["property_values"] = new JsonArray(new JsonObject
            {
                ["property_id"] = Color,
                ["property_name"] = "Color",
                ["value_ids"] = new JsonArray(),
                ["values"] = new JsonArray(color),
            }),
            ["offerings"] = new JsonArray(new JsonObject
            {
                ["offering_id"] = productId * 10,
                ["quantity"] = 5,
                ["is_enabled"] = true,
                ["is_deleted"] = false,
                ["price"] = FakeEtsyShop.Money(price),
            }),
        };
        listing["price"] = FakeEtsyShop.Money(options[0].Price);
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(Color),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray([.. options.Select(o => (JsonNode)MakeProduct(o.Color, o.Price))]),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return await db.Listings.Where(l => l.EtsyListingId == listingId).Select(l => l.Id).SingleAsync();
    }

    private static async Task<int> AddTwoVariationListingAsync(FakeEtsyShop shop, long listingId, string title, (string Size, string Color, long Price)[] combos)
    {
        var listing = FakeEtsyShop.Listing(listingId, title);
        long productId = listingId * 100;
        JsonObject MakeProduct(string size, string color, long price) => new()
        {
            ["product_id"] = ++productId,
            ["sku"] = $"SKU-{listingId}-{size}-{color}",
            ["is_deleted"] = false,
            ["property_values"] = new JsonArray(
                new JsonObject
                {
                    ["property_id"] = 100L,
                    ["property_name"] = "Size",
                    ["value_ids"] = new JsonArray(),
                    ["values"] = new JsonArray(size),
                },
                new JsonObject
                {
                    ["property_id"] = Color,
                    ["property_name"] = "Color",
                    ["value_ids"] = new JsonArray(),
                    ["values"] = new JsonArray(color),
                }),
            ["offerings"] = new JsonArray(new JsonObject
            {
                ["offering_id"] = productId * 10,
                ["quantity"] = 5,
                ["is_enabled"] = true,
                ["is_deleted"] = false,
                ["price"] = FakeEtsyShop.Money(price),
            }),
        };
        listing["price"] = FakeEtsyShop.Money(combos[0].Price);
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(100L, Color),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray([.. combos.Select(c => (JsonNode)MakeProduct(c.Size, c.Color, c.Price))]),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return await db.Listings.Where(l => l.EtsyListingId == listingId).Select(l => l.Id).SingleAsync();
    }

    private static decimal OfferingPriceOf(JsonNode inventory, string color, string? size = null)
    {
        var products = inventory["products"]!.AsArray().Select(p => p!.AsObject());
        var match = products.First(p =>
        {
            var pvs = p["property_values"]!.AsArray();
            var colorVal = (string)pvs.First(v => (long)v!["property_id"]! == Color)!["values"]![0]!;
            if (colorVal != color) return false;
            if (size is not null)
            {
                var sizeVal = (string)pvs.First(v => (long)v!["property_id"]! == 100L)!["values"]![0]!;
                if (sizeVal != size) return false;
            }
            return true;
        });
        var offering = match["offerings"]![0]!.AsObject();
        return VariationEditor.OfferingPrice(offering);
    }

    [Fact]
    public async Task Option_removal_from_template_removes_from_linked_listings_and_preserves_custom_values()
    {
        var (shop, colored, _) = await SetupAsync();
        using var _ = shop;
        var listId = await PlaColorsAsync(shop);
        await Lists(shop).LinkAsync(listId, [(colored, Color)], "tophattwaffle");
        var list = (await Lists(shop).GetAsync(listId))!;

        // Template has Black, White, Red. Listing also has Custom swirl.
        // Remove White and Red from the template.
        await Lists(shop).SaveAsync(listId, "PLA Colors", [list.Options[0]], "tophattwaffle");

        var use = Assert.Single(await Lists(shop).UsesAsync(listId));
        Assert.Equal(VariationListUseStatus.HasChanges, use.Status);
        Assert.Equal(["Color:", "Removes White", "Removes Red"], use.Changes);

        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tophattwaffle"));
        var inventory = await InventoryAsync(shop, colored);
        var colors = Colors(inventory);
        Assert.Equal(["Black", "Custom swirl"], colors.Order());
        Assert.DoesNotContain("White", colors);
        Assert.DoesNotContain("Red", colors);
        Assert.Contains("Custom swirl", colors);

        var updatedUse = Assert.Single(await Lists(shop).UsesAsync(listId));
        Assert.Equal(VariationListUseStatus.UpToDate, updatedUse.Status);
    }

    [Fact]
    public async Task Option_removal_handles_already_removed_by_hand_gracefully()
    {
        var (shop, colored, _) = await SetupAsync();
        using var _ = shop;
        var listId = await PlaColorsAsync(shop);
        await Lists(shop).LinkAsync(listId, [(colored, Color)], "tophattwaffle");
        var list = (await Lists(shop).GetAsync(listId))!;

        // Remove Red from the listing by hand first.
        await Editor(shop).SaveAsync(colored, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Inventory] = VariationEditor.RemoveValue(await InventoryAsync(shop, colored), Color, "Red"),
        }, "tophattwaffle");

        // Now drop Red from the template.
        await Lists(shop).SaveAsync(listId, "PLA Colors", [list.Options[0], list.Options[1]], "tophattwaffle");

        var use = Assert.Single(await Lists(shop).UsesAsync(listId));
        Assert.Equal(VariationListUseStatus.UpToDate, use.Status);
        Assert.Equal(["Red was already removed from this listing by hand."], use.Notes);
        Assert.DoesNotContain(use.Changes, c => c.Contains("Removes Red"));

        // Applying changes 0 listings and drops Red from AppliedOptions.
        Assert.Equal(0, await Lists(shop).ApplyAsync(listId, "tophattwaffle"));
        await using var db = shop.Db.CreateDbContext();
        var link = await db.ListingVariationLists.SingleAsync(l => l.ListingId == colored && l.VariationListId == listId);
        Assert.DoesNotContain(link.AppliedOptions, o => o.Name == "Red");
    }

    [Fact]
    public async Task Option_removal_refuses_to_remove_last_remaining_value_on_variation()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;
        var listingId = await AddSingleVariationListingAsync(shop, 10, "Single option item", [("OnlyOne", 1500)]);

        // Create template with OnlyOne (on) and Extra (off, so it's not on the listing).
        var listId = (await Lists(shop).SaveAsync(null, "SingleList",
            [new(0, "OnlyOne", true), new(0, "Extra", false)], "tophattwaffle")).Id!.Value;

        // Link the listing
        Assert.Equal(1, await Lists(shop).LinkAsync(listId, [(listingId, Color)], "tophattwaffle"));

        var list = (await Lists(shop).GetAsync(listId))!;
        // Remove OnlyOne from template, leaving only Extra (off).
        await Lists(shop).SaveAsync(listId, "SingleList", [list.Options[1]], "tophattwaffle");

        var use = Assert.Single(await Lists(shop).UsesAsync(listId));
        Assert.Equal(VariationListUseStatus.UpToDate, use.Status);
        Assert.Equal(["Cannot remove OnlyOne: it is the last remaining value on this variation."], use.Notes);

        // Applying leaves OnlyOne on the listing and in AppliedOptions.
        Assert.Equal(0, await Lists(shop).ApplyAsync(listId, "tophattwaffle"));
        var inventory = await InventoryAsync(shop, listingId);
        Assert.Contains("OnlyOne", Colors(inventory));
    }

    [Fact]
    public async Task Adding_options_with_take_highest_price_sets_max_price()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;
        var listingId = await AddSingleVariationListingAsync(shop, 20, "Pricing test item",
            [("Red", 500), ("Blue", 1000)]); // $5.00 and $10.00

        var listId = (await Lists(shop).SaveAsync(null, "Test Colors",
            [new(0, "Red", true), new(0, "Blue", true)], "tophattwaffle")).Id!.Value;

        await Lists(shop).LinkAsync(listId, [(listingId, Color)], "tophattwaffle");

        // Add Green to template
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Test Colors", [.. list.Options, new(0, "Green", true)], "tophattwaffle");

        var needing = await Lists(shop).ListingsNeedingPricingAsync(listId);
        var item = Assert.Single(needing);
        Assert.Equal((listingId, 5.00m, 10.00m), (item.ListingId, item.MinPrice, item.MaxPrice));
        Assert.Equal(["Green"], item.NewOptions);

        // Apply with TakeHighest
        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tophattwaffle", VariationPricingStrategy.TakeHighest));

        var inventory = await InventoryAsync(shop, listingId);
        Assert.Equal(5.00m, OfferingPriceOf(inventory, "Red"));
        Assert.Equal(10.00m, OfferingPriceOf(inventory, "Blue"));
        Assert.Equal(10.00m, OfferingPriceOf(inventory, "Green")); // takes highest (10.00)
    }

    [Fact]
    public async Task Adding_options_with_take_lowest_price_sets_min_price()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;
        var listingId = await AddSingleVariationListingAsync(shop, 21, "Pricing test item 2",
            [("Red", 500), ("Blue", 1000)]);

        var listId = (await Lists(shop).SaveAsync(null, "Test Colors 2",
            [new(0, "Red", true), new(0, "Blue", true)], "tophattwaffle")).Id!.Value;

        await Lists(shop).LinkAsync(listId, [(listingId, Color)], "tophattwaffle");

        // Add Green to template
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Test Colors 2", [.. list.Options, new(0, "Green", true)], "tophattwaffle");

        // Apply with TakeLowest
        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tophattwaffle", VariationPricingStrategy.TakeLowest));

        var inventory = await InventoryAsync(shop, listingId);
        Assert.Equal(5.00m, OfferingPriceOf(inventory, "Red"));
        Assert.Equal(10.00m, OfferingPriceOf(inventory, "Blue"));
        Assert.Equal(5.00m, OfferingPriceOf(inventory, "Green")); // takes lowest (5.00)
    }

    [Fact]
    public async Task Adding_options_with_pick_per_listing_applies_specified_prices()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;
        var listingA = await AddSingleVariationListingAsync(shop, 31, "Listing A",
            [("Red", 500), ("Blue", 1000)]); // 5.00 / 10.00
        var listingB = await AddSingleVariationListingAsync(shop, 32, "Listing B",
            [("Red", 1500), ("Blue", 3000)]); // 15.00 / 30.00

        var listId = (await Lists(shop).SaveAsync(null, "Shared Palette",
            [new(0, "Red", true), new(0, "Blue", true)], "tophattwaffle")).Id!.Value;

        await Lists(shop).LinkAsync(listId, [(listingA, Color), (listingB, Color)], "tophattwaffle");

        // Add Green to template
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Shared Palette", [.. list.Options, new(0, "Green", true)], "tophattwaffle");

        var needing = await Lists(shop).ListingsNeedingPricingAsync(listId);
        Assert.Equal(2, needing.Count);

        var customPrices = new Dictionary<int, decimal>
        {
            [listingA] = 8.50m,
            [listingB] = 22.00m,
        };

        Assert.Equal(2, await Lists(shop).ApplyAsync(listId, "tophattwaffle", VariationPricingStrategy.PickPerListing, customPrices));

        var invA = await InventoryAsync(shop, listingA);
        Assert.Equal(5.00m, OfferingPriceOf(invA, "Red"));
        Assert.Equal(10.00m, OfferingPriceOf(invA, "Blue"));
        Assert.Equal(8.50m, OfferingPriceOf(invA, "Green")); // user specified 8.50

        var invB = await InventoryAsync(shop, listingB);
        Assert.Equal(15.00m, OfferingPriceOf(invB, "Red"));
        Assert.Equal(30.00m, OfferingPriceOf(invB, "Blue"));
        Assert.Equal(22.00m, OfferingPriceOf(invB, "Green")); // user specified 22.00
    }

    [Fact]
    public async Task Adding_options_to_listing_with_two_variations_both_varying_price()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;
        var listingId = await AddTwoVariationListingAsync(shop, 40, "Two variations both vary",
        [
            ("Small", "Red", 500),
            ("Small", "Blue", 1000),
            ("Large", "Red", 1500),
            ("Large", "Blue", 3000),
        ]);

        var listId = (await Lists(shop).SaveAsync(null, "Color Varies Both",
            [new(0, "Red", true), new(0, "Blue", true)], "tophattwaffle")).Id!.Value;

        await Lists(shop).LinkAsync(listId, [(listingId, Color)], "tophattwaffle");

        // Add Green to template
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Color Varies Both", [.. list.Options, new(0, "Green", true)], "tophattwaffle");

        // Apply with TakeHighest
        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tophattwaffle", VariationPricingStrategy.TakeHighest));

        var inventory = await InventoryAsync(shop, listingId);
        // Existing prices untouched
        Assert.Equal(5.00m, OfferingPriceOf(inventory, "Red", "Small"));
        Assert.Equal(10.00m, OfferingPriceOf(inventory, "Blue", "Small"));
        Assert.Equal(15.00m, OfferingPriceOf(inventory, "Red", "Large"));
        Assert.Equal(30.00m, OfferingPriceOf(inventory, "Blue", "Large"));

        // Newly added Green takes highest within each other-variation group
        Assert.Equal(10.00m, OfferingPriceOf(inventory, "Green", "Small")); // max(5, 10)
        Assert.Equal(30.00m, OfferingPriceOf(inventory, "Green", "Large")); // max(15, 30)
    }

    [Fact]
    public async Task Adding_options_to_listing_with_two_variations_both_varying_price_take_lowest()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;
        var listingId = await AddTwoVariationListingAsync(shop, 41, "Two variations both vary lowest",
        [
            ("Small", "Red", 500),
            ("Small", "Blue", 1000),
            ("Large", "Red", 1500),
            ("Large", "Blue", 3000),
        ]);

        var listId = (await Lists(shop).SaveAsync(null, "Color Varies Both 2",
            [new(0, "Red", true), new(0, "Blue", true)], "tophattwaffle")).Id!.Value;

        await Lists(shop).LinkAsync(listId, [(listingId, Color)], "tophattwaffle");

        // Add Green to template
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Color Varies Both 2", [.. list.Options, new(0, "Green", true)], "tophattwaffle");

        // Apply with TakeLowest
        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tophattwaffle", VariationPricingStrategy.TakeLowest));

        var inventory = await InventoryAsync(shop, listingId);
        // Existing prices untouched
        Assert.Equal(5.00m, OfferingPriceOf(inventory, "Red", "Small"));
        Assert.Equal(10.00m, OfferingPriceOf(inventory, "Blue", "Small"));
        Assert.Equal(15.00m, OfferingPriceOf(inventory, "Red", "Large"));
        Assert.Equal(30.00m, OfferingPriceOf(inventory, "Blue", "Large"));

        // Newly added Green takes lowest within each other-variation group
        Assert.Equal(5.00m, OfferingPriceOf(inventory, "Green", "Small")); // min(5, 10)
        Assert.Equal(15.00m, OfferingPriceOf(inventory, "Green", "Large")); // min(15, 30)
    }
}
