using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class ChallengerM2R1EmpiricalTests
{
    private const long Color = VariationEditor.CustomProperty1;
    private const long Size = 100L;

    private static VariationListService Lists(FakeEtsyShop s) => s.Provider.GetRequiredService<VariationListService>();
    private static ListingEditor Editor(FakeEtsyShop s) => s.Provider.GetRequiredService<ListingEditor>();
    private static async Task<JsonNode> InventoryAsync(FakeEtsyShop s, int id) =>
        (await Editor(s).LoadAsync(id))!.Model.Local(ListingFields.Inventory)!;
    private static IReadOnlyList<string> Colors(JsonNode inventory) =>
        VariationEditor.Variations(inventory).Single(v => v.PropertyId == Color).Values;

    private static async Task<int> AddSingleVariationListingAsync(FakeEtsyShop shop, long listingId, string title, string[] colorNames)
    {
        var listing = FakeEtsyShop.Listing(listingId, title);
        long productId = listingId * 1000;
        JsonObject MakeProduct(string color) => new()
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
                ["price"] = FakeEtsyShop.Money(2000), // $20.00
            }),
        };
        listing["price"] = FakeEtsyShop.Money(2000);
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(Color),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray([.. colorNames.Select(c => (JsonNode)MakeProduct(c))]),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return await db.Listings.Where(l => l.EtsyListingId == listingId).Select(l => l.Id).SingleAsync();
    }

    private static async Task<int> AddTwoVariationListingAsync(FakeEtsyShop shop, long listingId, string title, string[] sizes, string[] colors)
    {
        var listing = FakeEtsyShop.Listing(listingId, title);
        long productId = listingId * 1000;
        JsonObject MakeProduct(string size, string color) => new()
        {
            ["product_id"] = ++productId,
            ["sku"] = $"SKU-{listingId}-{size}-{color}",
            ["is_deleted"] = false,
            ["property_values"] = new JsonArray(
                new JsonObject
                {
                    ["property_id"] = Size,
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
                ["price"] = FakeEtsyShop.Money(2500),
            }),
        };
        listing["price"] = FakeEtsyShop.Money(2500);
        var products = new List<JsonNode>();
        foreach (var s in sizes)
        {
            foreach (var c in colors)
            {
                products.Add(MakeProduct(s, c));
            }
        }
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(Size, Color),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray([.. products]),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return await db.Listings.Where(l => l.EtsyListingId == listingId).Select(l => l.Id).SingleAsync();
    }

    [Fact]
    public async Task All_five_options_removed_from_template_preserves_last_remaining_value_with_note()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // Listing has 5 options: O1, O2, O3, O4, O5
        string[] originalOptions = ["Opt1", "Opt2", "Opt3", "Opt4", "Opt5"];
        var listingId = await AddSingleVariationListingAsync(shop, 101, "Five Option Listing", originalOptions);

        var listId = (await Lists(shop).SaveAsync(null, "Five Options Template",
            [.. originalOptions.Select(o => new VariationListOption(0, o, true))], "tester")).Id!.Value;

        Assert.Equal(1, await Lists(shop).LinkAsync(listId, [(listingId, Color)], "tester"));

        // Now remove all 5 options from the template.
        // A template requires >= 1 option, so we add "UnrelatedOff" with Enabled=false.
        await Lists(shop).SaveAsync(listId, "Five Options Template",
            [new(0, "UnrelatedOff", false)], "tester");

        var use = Assert.Single(await Lists(shop).UsesAsync(listId));
        Assert.Equal(VariationListUseStatus.HasChanges, use.Status);

        // Changes must remove Opt1..Opt4
        Assert.Contains(use.Changes, c => c.Contains("Removes Opt1"));
        Assert.Contains(use.Changes, c => c.Contains("Removes Opt2"));
        Assert.Contains(use.Changes, c => c.Contains("Removes Opt3"));
        Assert.Contains(use.Changes, c => c.Contains("Removes Opt4"));
        Assert.DoesNotContain(use.Changes, c => c.Contains("Removes Opt5"));

        // Opt5 must have note about last remaining value
        Assert.Contains(use.Notes, n => n.Contains("Cannot remove Opt5: it is the last remaining value on this variation."));

        // Apply template
        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tester"));

        // Verify inventory: Opt5 remains as the sole value
        var inv = await InventoryAsync(shop, listingId);
        var currentColors = Colors(inv);
        Assert.Equal(["Opt5"], currentColors);

        // Verify AppliedOptions: Opt5 is retained in AppliedOptions
        await using (var db = shop.Db.CreateDbContext())
        {
            var link = await db.ListingVariationLists.SingleAsync(l => l.ListingId == listingId && l.VariationListId == listId);
            Assert.Contains(link.AppliedOptions, o => o.Name == "Opt5");
            Assert.DoesNotContain(link.AppliedOptions, o => o.Name == "Opt1");
            Assert.DoesNotContain(link.AppliedOptions, o => o.Name == "Opt2");
            Assert.DoesNotContain(link.AppliedOptions, o => o.Name == "Opt3");
            Assert.DoesNotContain(link.AppliedOptions, o => o.Name == "Opt4");
        }

        // Repeated apply check: Should not fail, status should be UpToDate, Opt5 preserved
        var repeatedUse = Assert.Single(await Lists(shop).UsesAsync(listId));
        Assert.Equal(VariationListUseStatus.UpToDate, repeatedUse.Status);
        Assert.Contains(repeatedUse.Notes, n => n.Contains("Cannot remove Opt5: it is the last remaining value on this variation."));
        Assert.Equal(0, await Lists(shop).ApplyAsync(listId, "tester"));

        var invAfterRepeated = await InventoryAsync(shop, listingId);
        Assert.Equal(["Opt5"], Colors(invAfterRepeated));
    }

    [Fact]
    public async Task All_options_removed_when_listing_has_custom_options_removes_all_template_options_and_preserves_custom()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // Listing has template options (Red, Blue, Green) AND custom unlinked options (CustomAlpha, CustomBeta)
        string[] allOptions = ["Red", "Blue", "Green", "CustomAlpha", "CustomBeta"];
        var listingId = await AddSingleVariationListingAsync(shop, 102, "Mixed Option Listing", allOptions);

        var listId = (await Lists(shop).SaveAsync(null, "Colors Template",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "tester")).Id!.Value;

        Assert.Equal(1, await Lists(shop).LinkAsync(listId, [(listingId, Color)], "tester"));

        // Verify only Red, Blue, Green are in AppliedOptions
        await using (var db = shop.Db.CreateDbContext())
        {
            var link = await db.ListingVariationLists.SingleAsync(l => l.ListingId == listingId && l.VariationListId == listId);
            Assert.Equal(3, link.AppliedOptions.Count);
            Assert.DoesNotContain(link.AppliedOptions, o => o.Name.StartsWith("Custom"));
        }

        // Remove ALL 3 options from template (replace with inactive "Placeholder")
        await Lists(shop).SaveAsync(listId, "Colors Template",
            [new(0, "Placeholder", false)], "tester");

        var use = Assert.Single(await Lists(shop).UsesAsync(listId));
        Assert.Equal(VariationListUseStatus.HasChanges, use.Status);
        // All 3 should be removed since CustomAlpha and CustomBeta still exist on the listing (> 1 value)
        Assert.Contains(use.Changes, c => c.Contains("Removes Red"));
        Assert.Contains(use.Changes, c => c.Contains("Removes Blue"));
        Assert.Contains(use.Changes, c => c.Contains("Removes Green"));
        Assert.Empty(use.Notes); // No "Cannot remove" note!

        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tester"));

        var inv = await InventoryAsync(shop, listingId);
        var currentColors = Colors(inv);
        Assert.Equal(["CustomAlpha", "CustomBeta"], currentColors.Order());
        Assert.DoesNotContain("Red", currentColors);
        Assert.DoesNotContain("Blue", currentColors);
        Assert.DoesNotContain("Green", currentColors);

        // Custom options remain fully available
        Assert.True(VariationEditor.IsValueAvailable(inv, Color, "CustomAlpha"));
        Assert.True(VariationEditor.IsValueAvailable(inv, Color, "CustomBeta"));
    }

    [Fact]
    public async Task Option_already_removed_by_hand_emits_note_and_cleans_applied_options_on_apply()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listingId = await AddSingleVariationListingAsync(shop, 103, "Hand Removal Listing", ["Red", "Blue", "Green"]);
        var listId = (await Lists(shop).SaveAsync(null, "Hand Removal Template",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "tester")).Id!.Value;

        Assert.Equal(1, await Lists(shop).LinkAsync(listId, [(listingId, Color)], "tester"));

        // Seller removes "Blue" by hand in editor
        var invBefore = await InventoryAsync(shop, listingId);
        await Editor(shop).SaveAsync(listingId, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Inventory] = VariationEditor.RemoveValue(invBefore, Color, "Blue"),
        }, "tester");

        // Now remove "Blue" from the template
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Hand Removal Template",
            [list.Options[0], list.Options[2]], "tester"); // Red and Green

        var use = Assert.Single(await Lists(shop).UsesAsync(listId));
        // Status should be UpToDate because inventory already matches the template
        Assert.Equal(VariationListUseStatus.UpToDate, use.Status);
        Assert.DoesNotContain(use.Changes, c => c.Contains("Removes Blue"));
        Assert.Contains(use.Notes, n => n == "Blue was already removed from this listing by hand.");

        // Applying
        Assert.Equal(0, await Lists(shop).ApplyAsync(listId, "tester"));

        // AppliedOptions must have cleaned up Blue
        await using (var db = shop.Db.CreateDbContext())
        {
            var link = await db.ListingVariationLists.SingleAsync(l => l.ListingId == listingId && l.VariationListId == listId);
            Assert.DoesNotContain(link.AppliedOptions, o => o.Name == "Blue");
            Assert.Contains(link.AppliedOptions, o => o.Name == "Red");
            Assert.Contains(link.AppliedOptions, o => o.Name == "Green");
        }

        // On subsequent check, note should be GONE because Blue is no longer in AppliedOptions
        var nextUse = Assert.Single(await Lists(shop).UsesAsync(listId));
        Assert.Equal(VariationListUseStatus.UpToDate, nextUse.Status);
        Assert.Empty(nextUse.Notes);
    }

    [Fact]
    public async Task Repeated_applies_are_completely_idempotent_and_stable()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listingId = await AddSingleVariationListingAsync(shop, 104, "Idempotency Listing", ["Red", "Blue", "Green", "CustomX"]);
        var listId = (await Lists(shop).SaveAsync(null, "Idempotency Template",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "tester")).Id!.Value;

        await Lists(shop).LinkAsync(listId, [(listingId, Color)], "tester");

        // Remove Blue
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Idempotency Template", [list.Options[0], list.Options[2]], "tester");

        // First apply
        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tester"));
        var inv1 = await InventoryAsync(shop, listingId);
        Assert.Equal(["CustomX", "Green", "Red"], Colors(inv1).Order());

        // Repeated applies: run 5 times
        for (int i = 0; i < 5; i++)
        {
            var uses = await Lists(shop).UsesAsync(listId);
            Assert.Equal(VariationListUseStatus.UpToDate, uses[0].Status);
            Assert.Empty(uses[0].Changes);

            var changedCount = await Lists(shop).ApplyAsync(listId, "tester");
            Assert.Equal(0, changedCount);

            var currentInv = await InventoryAsync(shop, listingId);
            Assert.Equal(["CustomX", "Green", "Red"], Colors(currentInv).Order());
        }
    }

    [Fact]
    public async Task Two_variation_listing_option_removal_cleans_combinations_and_preserves_other_variation()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // Size: Small, Large x Color: Red, Blue, Green (total 6 products)
        var listingId = await AddTwoVariationListingAsync(shop, 105, "Two Variation Shirt",
            ["Small", "Large"], ["Red", "Blue", "Green"]);

        var listId = (await Lists(shop).SaveAsync(null, "Shirt Colors",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "tester")).Id!.Value;

        Assert.Equal(1, await Lists(shop).LinkAsync(listId, [(listingId, Color)], "tester"));

        // Remove Blue from template
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Shirt Colors", [list.Options[0], list.Options[2]], "tester");

        var use = Assert.Single(await Lists(shop).UsesAsync(listId));
        Assert.Contains(use.Changes, c => c.Contains("Removes Blue"));

        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tester"));

        var inv = await InventoryAsync(shop, listingId);
        // Colors should only have Red and Green
        Assert.Equal(["Green", "Red"], Colors(inv).Order());

        // Size variation should be untouched with Small and Large
        var sizes = VariationEditor.Variations(inv).Single(v => v.PropertyId == Size).Values;
        Assert.Equal(["Large", "Small"], sizes.Order());

        // Products count should now be 2 sizes x 2 colors = 4 products
        var products = inv["products"]!.AsArray();
        Assert.Equal(4, products.Count);
        foreach (var p in products)
        {
            var pvs = p!["property_values"]!.AsArray();
            var cVal = (string)pvs.Single(v => (long)v!["property_id"]! == Color)!["values"]![0]!;
            Assert.NotEqual("Blue", cVal);
        }
    }

    [Fact]
    public async Task Two_variation_listing_all_options_removed_preserves_last_color_across_all_sizes()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listingId = await AddTwoVariationListingAsync(shop, 106, "Two Var Last Option",
            ["Small", "Large"], ["Red", "Blue"]);

        var listId = (await Lists(shop).SaveAsync(null, "Two Var Colors",
            [new(0, "Red", true), new(0, "Blue", true)], "tester")).Id!.Value;

        Assert.Equal(1, await Lists(shop).LinkAsync(listId, [(listingId, Color)], "tester"));

        // Remove both Red and Blue from template (replace with inactive Placeholder)
        await Lists(shop).SaveAsync(listId, "Two Var Colors", [new(0, "Placeholder", false)], "tester");

        var use = Assert.Single(await Lists(shop).UsesAsync(listId));
        Assert.Contains(use.Changes, c => c.Contains("Removes Red"));
        Assert.DoesNotContain(use.Changes, c => c.Contains("Removes Blue"));
        Assert.Contains(use.Notes, n => n.Contains("Cannot remove Blue: it is the last remaining value on this variation."));

        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tester"));

        var inv = await InventoryAsync(shop, listingId);
        Assert.Equal(["Blue"], Colors(inv));

        // Products should be 2: (Small, Blue) and (Large, Blue)
        var products = inv["products"]!.AsArray();
        Assert.Equal(2, products.Count);
    }

    [Fact]
    public async Task Retained_last_value_can_be_removed_in_subsequent_apply_if_seller_adds_another_value()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listingId = await AddSingleVariationListingAsync(shop, 107, "Single To Multi Listing", ["SoloColor"]);
        var listId = (await Lists(shop).SaveAsync(null, "Solo Template",
            [new(0, "SoloColor", true)], "tester")).Id!.Value;

        Assert.Equal(1, await Lists(shop).LinkAsync(listId, [(listingId, Color)], "tester"));

        // Try to remove SoloColor from template
        await Lists(shop).SaveAsync(listId, "Solo Template", [new(0, "Placeholder", false)], "tester");

        // First apply cannot remove it
        Assert.Equal(0, await Lists(shop).ApplyAsync(listId, "tester"));
        var inv1 = await InventoryAsync(shop, listingId);
        Assert.Equal(["SoloColor"], Colors(inv1));

        // Now seller adds "NewManualColor" by hand in editor
        var invWithManual = VariationEditor.AddValue(inv1, Color, "NewManualColor");
        await Editor(shop).SaveAsync(listingId, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Inventory] = invWithManual,
        }, "tester");

        // Now variation has 2 values: SoloColor and NewManualColor!
        // Template still doesn't have SoloColor, which is still in AppliedOptions.
        var use = Assert.Single(await Lists(shop).UsesAsync(listId));
        Assert.Equal(VariationListUseStatus.HasChanges, use.Status);
        Assert.Contains(use.Changes, c => c.Contains("Removes SoloColor"));
        Assert.Empty(use.Notes);

        // Second apply should now successfully remove SoloColor!
        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tester"));
        var inv2 = await InventoryAsync(shop, listingId);
        Assert.Equal(["NewManualColor"], Colors(inv2));
    }

    [Fact]
    public async Task Simultaneous_multi_listing_differential_removal_and_hand_edits()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // Listing A: Red, Blue, Green (Standard)
        var listingA = await AddSingleVariationListingAsync(shop, 108, "Listing A", ["Red", "Blue", "Green"]);
        // Listing B: Red, Blue, Green, CustomGold (Has custom)
        var listingB = await AddSingleVariationListingAsync(shop, 109, "Listing B", ["Red", "Blue", "Green", "CustomGold"]);
        // Listing C: Red, Green (Blue already removed by hand)
        var listingC = await AddSingleVariationListingAsync(shop, 110, "Listing C", ["Red", "Blue", "Green"]);

        var listId = (await Lists(shop).SaveAsync(null, "Shared Palette",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "tester")).Id!.Value;

        Assert.Equal(3, await Lists(shop).LinkAsync(listId, [(listingA, Color), (listingB, Color), (listingC, Color)], "tester"));

        // Listing C removes Blue by hand
        var invC = await InventoryAsync(shop, listingC);
        await Editor(shop).SaveAsync(listingC, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Inventory] = VariationEditor.RemoveValue(invC, Color, "Blue"),
        }, "tester");

        // Template removes Blue and Green
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Shared Palette", [list.Options[0]], "tester"); // Red only

        var uses = (await Lists(shop).UsesAsync(listId)).ToDictionary(u => u.ListingId);

        // Listing A: Removes Blue, Removes Green
        Assert.Contains(uses[listingA].Changes, c => c.Contains("Removes Blue"));
        Assert.Contains(uses[listingA].Changes, c => c.Contains("Removes Green"));

        // Listing B: Removes Blue, Removes Green
        Assert.Contains(uses[listingB].Changes, c => c.Contains("Removes Blue"));
        Assert.Contains(uses[listingB].Changes, c => c.Contains("Removes Green"));

        // Listing C: Note for Blue, Change for Removes Green
        Assert.Contains(uses[listingC].Notes, n => n.Contains("Blue was already removed from this listing by hand."));
        Assert.Contains(uses[listingC].Changes, c => c.Contains("Removes Green"));
        Assert.DoesNotContain(uses[listingC].Changes, c => c.Contains("Removes Blue"));

        // Apply all
        Assert.Equal(3, await Lists(shop).ApplyAsync(listId, "tester"));

        // Verify inventories
        var finalA = await InventoryAsync(shop, listingA);
        Assert.Equal(["Red"], Colors(finalA));

        var finalB = await InventoryAsync(shop, listingB);
        Assert.Equal(["CustomGold", "Red"], Colors(finalB).Order());

        var finalC = await InventoryAsync(shop, listingC);
        Assert.Equal(["Red"], Colors(finalC));
    }
}
