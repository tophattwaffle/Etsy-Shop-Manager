using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Etsy.Models;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EtsyShopManager.Tests;

public class ChallengerM5VariationReorderTests
{
    private const long SizePropId = 100L;
    private const long ColorPropId = VariationEditor.CustomProperty1;

    private static JsonObject Pv(long id, string? name, string value, params long[] ids) => new()
    {
        ["property_id"] = id,
        ["property_name"] = name,
        ["scale_id"] = null,
        ["value_ids"] = new JsonArray([.. ids.Select(i => (JsonNode)i)]),
        ["values"] = new JsonArray(value),
    };

    private static JsonObject Product(string sku, long price, bool enabled = true, params JsonObject[] pvs) => new()
    {
        ["sku"] = sku,
        ["property_values"] = new JsonArray(pvs),
        ["offerings"] = new JsonArray(new JsonObject
        {
            ["price_amount"] = price,
            ["price_divisor"] = 100,
            ["is_enabled"] = enabled,
            ["readiness_state_id"] = null,
        }),
    };

    private static JsonObject SingleVariationInventory() => VariationEditor.Canonical(new JsonObject
    {
        ["price_on_property"] = new JsonArray(SizePropId),
        ["quantity_on_property"] = new JsonArray(),
        ["sku_on_property"] = new JsonArray(SizePropId),
        ["readiness_state_on_property"] = new JsonArray(),
        ["products"] = new JsonArray(
            Product("SKU-S", 1000, true, Pv(SizePropId, "Size", "Small", 1)),
            Product("SKU-M", 1500, true, Pv(SizePropId, "Size", "Medium", 2)),
            Product("SKU-L", 2000, true, Pv(SizePropId, "Size", "Large", 3)),
            Product("SKU-XL", 2500, true, Pv(SizePropId, "Size", "X-Large", 4))),
    });

    private static JsonObject TwoVariationInventory() => VariationEditor.Canonical(new JsonObject
    {
        ["price_on_property"] = new JsonArray(SizePropId),
        ["quantity_on_property"] = new JsonArray(),
        ["sku_on_property"] = new JsonArray(SizePropId),
        ["readiness_state_on_property"] = new JsonArray(),
        ["products"] = new JsonArray(
            Product("SKU-S-BLK", 1000, true, Pv(SizePropId, "Size", "Small", 1), Pv(ColorPropId, "Color", "Black", 11)),
            Product("SKU-S-WHT", 1000, true, Pv(SizePropId, "Size", "Small", 1), Pv(ColorPropId, "Color", "White", 12)),
            Product("SKU-S-RED", 1000, true, Pv(SizePropId, "Size", "Small", 1), Pv(ColorPropId, "Color", "Red", 13)),
            Product("SKU-M-BLK", 1500, true, Pv(SizePropId, "Size", "Medium", 2), Pv(ColorPropId, "Color", "Black", 11)),
            Product("SKU-M-WHT", 1500, true, Pv(SizePropId, "Size", "Medium", 2), Pv(ColorPropId, "Color", "White", 12)),
            Product("SKU-M-RED", 1500, true, Pv(SizePropId, "Size", "Medium", 2), Pv(ColorPropId, "Color", "Red", 13)),
            Product("SKU-L-BLK", 2000, true, Pv(SizePropId, "Size", "Large", 3), Pv(ColorPropId, "Color", "Black", 11)),
            Product("SKU-L-WHT", 2000, true, Pv(SizePropId, "Size", "Large", 3), Pv(ColorPropId, "Color", "White", 12)),
            Product("SKU-L-RED", 2000, true, Pv(SizePropId, "Size", "Large", 3), Pv(ColorPropId, "Color", "Red", 13))),
    });

    private static async Task<(FakeEtsyShop Shop, int ListingId, int ListId)> SetupLinkedShopAsync()
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Linked Color Listing");
        long id = 10;
        JsonObject MakeProduct(string color)
        {
            var p = FakeEtsyShop.Product(++id, $"SKU-{color}", "OneSize", 1000, 5);
            p["property_values"]!.AsArray()[0] = new JsonObject
            {
                ["property_id"] = ColorPropId,
                ["property_name"] = "Color",
                ["scale_id"] = null,
                ["value_ids"] = new JsonArray(),
                ["values"] = new JsonArray(color),
            };
            return p;
        }

        string[] colors = ["Black", "White", "Red", "Blue"];
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray([.. colors.Select(c => (JsonNode)MakeProduct(c))]),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        var listId = (await lists.SaveAsync(null, "Test Colors", [
            new(0, "Black", true),
            new(0, "White", true),
            new(0, "Red", true),
            new(0, "Blue", true)
        ], "tophattwaffle")).Id!.Value;

        await lists.LinkAsync(listId, [(1, ColorPropId)], "tophattwaffle");
        return (shop, 1, listId);
    }

    // =========================================================================
    // 1. STRESS TESTS: VariationEditor.ReorderValues & Canonical
    // =========================================================================

    [Fact]
    public void ReorderValues_SingleVariation_AllPermutations_And_Integrity()
    {
        var inv = SingleVariationInventory();

        // 1. Reverse order: XL, L, M, S
        var reordered = VariationEditor.ReorderValues(inv, SizePropId, ["X-Large", "Large", "Medium", "Small"]);
        var valueOrder = VariationEditor.GetValueOrder(reordered, SizePropId);
        Assert.Equal(["X-Large", "Large", "Medium", "Small"], valueOrder);

        var products = reordered["products"]!.AsArray();
        var productSizes = products.Select(p => (string)p!["property_values"]![0]!["values"]![0]!).ToList();
        Assert.Equal(["X-Large", "Large", "Medium", "Small"], productSizes);

        // Verify product SKUs and prices remained with their respective sizes
        Assert.Equal("SKU-XL", (string?)products[0]!["sku"]);
        Assert.Equal(2500, (long)products[0]!["offerings"]![0]!["price_amount"]!);

        Assert.Equal("SKU-L", (string?)products[1]!["sku"]);
        Assert.Equal(2000, (long)products[1]!["offerings"]![0]!["price_amount"]!);

        Assert.Equal("SKU-M", (string?)products[2]!["sku"]);
        Assert.Equal(1500, (long)products[2]!["offerings"]![0]!["price_amount"]!);

        Assert.Equal("SKU-S", (string?)products[3]!["sku"]);
        Assert.Equal(1000, (long)products[3]!["offerings"]![0]!["price_amount"]!);

        // 2. Custom shuffle: Medium, X-Large, Small, Large
        var shuffled = VariationEditor.ReorderValues(reordered, SizePropId, ["Medium", "X-Large", "Small", "Large"]);
        var shuffledSizes = shuffled["products"]!.AsArray()
            .Select(p => (string)p!["property_values"]![0]!["values"]![0]!).ToList();
        Assert.Equal(["Medium", "X-Large", "Small", "Large"], shuffledSizes);

        // 3. Case-insensitive reorder input should be accepted
        var caseInsensitive = VariationEditor.ReorderValues(inv, SizePropId, ["x-large", "LARGE", "medium", "small"]);
        var caseOrder = VariationEditor.GetValueOrder(caseInsensitive, SizePropId);
        Assert.Equal(["x-large", "LARGE", "medium", "small"], caseOrder);
    }

    [Fact]
    public void ReorderValues_SingleVariation_Validation_ThrowsArgumentException()
    {
        var inv = SingleVariationInventory();

        // Missing an item
        Assert.Throws<ArgumentException>(() =>
            VariationEditor.ReorderValues(inv, SizePropId, ["Small", "Medium"]));

        // Duplicate item
        Assert.Throws<ArgumentException>(() =>
            VariationEditor.ReorderValues(inv, SizePropId, ["Small", "Small", "Medium", "Large"]));

        // Unknown item
        Assert.Throws<ArgumentException>(() =>
            VariationEditor.ReorderValues(inv, SizePropId, ["Small", "Medium", "Large", "XXL"]));

        // Non-existent property ID
        Assert.Throws<ArgumentException>(() =>
            VariationEditor.ReorderValues(inv, 9999L, ["A", "B"]));
    }

    [Fact]
    public void ReorderValues_TwoVariations_PrimaryAndSecondaryGridOrder()
    {
        var inv = TwoVariationInventory();

        // 1. Reorder primary variation (Size): Large, Small, Medium
        var reorderedSize = VariationEditor.ReorderValues(inv, SizePropId, ["Large", "Small", "Medium"]);
        var productsSize = reorderedSize["products"]!.AsArray();
        Assert.Equal(9, productsSize.Count);

        // Primary variation must dominate product sequence
        var primarySequence = productsSize.Select(p => (string)p!["property_values"]![0]!["values"]![0]!).ToList();
        Assert.Equal([
            "Large", "Large", "Large",
            "Small", "Small", "Small",
            "Medium", "Medium", "Medium"
        ], primarySequence);

        // 2. Reorder secondary variation (Color): Red, White, Black
        var reorderedBoth = VariationEditor.ReorderValues(reorderedSize, ColorPropId, ["Red", "White", "Black"]);
        var productsBoth = reorderedBoth["products"]!.AsArray();

        var combinations = productsBoth.Select(p => (
            Size: (string)p!["property_values"]![0]!["values"]![0]!,
            Color: (string)p!["property_values"]![1]!["values"]![0]!,
            Sku: (string?)p!["sku"] ?? ""
        )).ToList();

        var expectedCombinations = new List<(string Size, string Color, string Sku)>
        {
            ("Large", "Red", "SKU-L-RED"),
            ("Large", "White", "SKU-L-WHT"),
            ("Large", "Black", "SKU-L-BLK"),
            ("Small", "Red", "SKU-S-RED"),
            ("Small", "White", "SKU-S-WHT"),
            ("Small", "Black", "SKU-S-BLK"),
            ("Medium", "Red", "SKU-M-RED"),
            ("Medium", "White", "SKU-M-WHT"),
            ("Medium", "Black", "SKU-M-BLK"),
        };

        Assert.Equal(expectedCombinations, combinations);
    }

    [Fact]
    public void ReorderValues_FollowedBy_AddValue_RemoveValue_RenameValue_PreservesOrderIntegrity()
    {
        var inv = SingleVariationInventory();
        var reordered = VariationEditor.ReorderValues(inv, SizePropId, ["X-Large", "Large", "Medium", "Small"]);

        // 1. AddValue should append to value_order and place new product at end of grid
        var added = VariationEditor.AddValue(reordered, SizePropId, "XX-Large");
        var orderAfterAdd = VariationEditor.GetValueOrder(added, SizePropId);
        Assert.Equal(["X-Large", "Large", "Medium", "Small", "XX-Large"], orderAfterAdd);

        var sizesAfterAdd = added["products"]!.AsArray()
            .Select(p => (string)p!["property_values"]![0]!["values"]![0]!).ToList();
        Assert.Equal(["X-Large", "Large", "Medium", "Small", "XX-Large"], sizesAfterAdd);

        // 2. RenameValue should update value_order in place
        var renamed = VariationEditor.RenameValue(added, SizePropId, "X-Large", "XL");
        var orderAfterRename = VariationEditor.GetValueOrder(renamed, SizePropId);
        Assert.Equal(["XL", "Large", "Medium", "Small", "XX-Large"], orderAfterRename);

        var sizesAfterRename = renamed["products"]!.AsArray()
            .Select(p => (string)p!["property_values"]![0]!["values"]![0]!).ToList();
        Assert.Equal(["XL", "Large", "Medium", "Small", "XX-Large"], sizesAfterRename);

        // 3. RemoveValue should prune from value_order
        var removed = VariationEditor.RemoveValue(renamed, SizePropId, "Medium");
        var orderAfterRemove = VariationEditor.GetValueOrder(removed, SizePropId);
        Assert.Equal(["XL", "Large", "Small", "XX-Large"], orderAfterRemove);

        var sizesAfterRemove = removed["products"]!.AsArray()
            .Select(p => (string)p!["property_values"]![0]!["values"]![0]!).ToList();
        Assert.Equal(["XL", "Large", "Small", "XX-Large"], sizesAfterRemove);
    }

    // =========================================================================
    // 2. STRESS TESTS: InventoryWriter.FromLocal
    // =========================================================================

    [Fact]
    public void InventoryWriter_FromLocal_WithCustomValueOrder_PreservesLocalOrder_And_ExistingEtsyQuantities()
    {
        // Construct EtsyInventory with 3 products in Etsy order: Medium (qty=15), Small (qty=5), Large (qty=25)
        var etsyInventory = new EtsyInventory
        {
            Products = [
                new()
                {
                    ProductId = 101,
                    Sku = "SKU-M",
                    PropertyValues = [new() { PropertyId = SizePropId, PropertyName = "Size", Values = ["Medium"], ValueIds = [2] }],
                    Offerings = [new() { OfferingId = 1001, Price = new() { Amount = 1500, Divisor = 100 }, Quantity = 15, IsEnabled = true }]
                },
                new()
                {
                    ProductId = 102,
                    Sku = "SKU-S",
                    PropertyValues = [new() { PropertyId = SizePropId, PropertyName = "Size", Values = ["Small"], ValueIds = [1] }],
                    Offerings = [new() { OfferingId = 1002, Price = new() { Amount = 1000, Divisor = 100 }, Quantity = 5, IsEnabled = true }]
                },
                new()
                {
                    ProductId = 103,
                    Sku = "SKU-L",
                    PropertyValues = [new() { PropertyId = SizePropId, PropertyName = "Size", Values = ["Large"], ValueIds = [3] }],
                    Offerings = [new() { OfferingId = 1003, Price = new() { Amount = 2000, Divisor = 100 }, Quantity = 25, IsEnabled = true }]
                }
            ]
        };

        // Local inventory reordered to: Large, Small, Medium with custom prices in local
        var localInv = VariationEditor.ReorderValues(new JsonObject
        {
            ["price_on_property"] = new JsonArray(SizePropId),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(SizePropId),
            ["readiness_state_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                Product("SKU-S", 1200, true, Pv(SizePropId, "Size", "Small", 1)),
                Product("SKU-M", 1600, true, Pv(SizePropId, "Size", "Medium", 2)),
                Product("SKU-L", 2200, true, Pv(SizePropId, "Size", "Large", 3)))
        }, SizePropId, ["Large", "Small", "Medium"]);

        var update = InventoryWriter.FromLocal(etsyInventory, localInv);

        // Assert 1: Product order in update matches local grid sequence exactly (Large, Small, Medium)
        Assert.Equal(3, update.Products.Count);
        Assert.Equal("Large", update.Products[0].PropertyValues.Single().Values.Single());
        Assert.Equal("Small", update.Products[1].PropertyValues.Single().Values.Single());
        Assert.Equal("Medium", update.Products[2].PropertyValues.Single().Values.Single());

        // Assert 2: Quantities are 100% preserved from Etsy offerings by product key, NOT by position
        Assert.Equal(25, update.Products[0].Offerings.Single().Quantity); // Large on Etsy had 25
        Assert.Equal(5, update.Products[1].Offerings.Single().Quantity);  // Small on Etsy had 5
        Assert.Equal(15, update.Products[2].Offerings.Single().Quantity); // Medium on Etsy had 15

        // Assert 3: Prices come from local inventory
        Assert.Equal(22.00m, update.Products[0].Offerings.Single().Price);
        Assert.Equal(12.00m, update.Products[1].Offerings.Single().Price);
        Assert.Equal(16.00m, update.Products[2].Offerings.Single().Price);

        // Assert 4: Value IDs are preserved from local
        Assert.Equal([3L], update.Products[0].PropertyValues.Single().ValueIds);
        Assert.Equal([1L], update.Products[1].PropertyValues.Single().ValueIds);
        Assert.Equal([2L], update.Products[2].PropertyValues.Single().ValueIds);
    }

    [Fact]
    public void InventoryWriter_FromLocal_WithoutCustomValueOrder_RetainsEtsyOriginalSequence()
    {
        // Etsy products in order: Medium, Small, Large
        var etsyInventory = new EtsyInventory
        {
            Products = [
                new()
                {
                    ProductId = 101,
                    PropertyValues = [new() { PropertyId = SizePropId, PropertyName = "Size", Values = ["Medium"], ValueIds = [2] }],
                    Offerings = [new() { OfferingId = 1001, Price = new() { Amount = 1500, Divisor = 100 }, Quantity = 15, IsEnabled = true }]
                },
                new()
                {
                    ProductId = 102,
                    PropertyValues = [new() { PropertyId = SizePropId, PropertyName = "Size", Values = ["Small"], ValueIds = [1] }],
                    Offerings = [new() { OfferingId = 1002, Price = new() { Amount = 1000, Divisor = 100 }, Quantity = 5, IsEnabled = true }]
                },
                new()
                {
                    ProductId = 103,
                    PropertyValues = [new() { PropertyId = SizePropId, PropertyName = "Size", Values = ["Large"], ValueIds = [3] }],
                    Offerings = [new() { OfferingId = 1003, Price = new() { Amount = 2000, Divisor = 100 }, Quantity = 25, IsEnabled = true }]
                }
            ]
        };

        // Local inventory with NO value_order (e.g. freshly imported or edited without reordering)
        var localInv = new JsonObject
        {
            ["price_on_property"] = new JsonArray(SizePropId),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(SizePropId),
            ["readiness_state_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                Product("SKU-L", 2000, true, Pv(SizePropId, "Size", "Large", 3)),
                Product("SKU-M", 1500, true, Pv(SizePropId, "Size", "Medium", 2)),
                Product("SKU-S", 1000, true, Pv(SizePropId, "Size", "Small", 1)))
        };

        var update = InventoryWriter.FromLocal(etsyInventory, localInv);

        // Without custom value_order, Etsy's product order (Medium, Small, Large) must be retained
        Assert.Equal("Medium", update.Products[0].PropertyValues.Single().Values.Single());
        Assert.Equal("Small", update.Products[1].PropertyValues.Single().Values.Single());
        Assert.Equal("Large", update.Products[2].PropertyValues.Single().Values.Single());

        Assert.Equal(15, update.Products[0].Offerings.Single().Quantity);
        Assert.Equal(5, update.Products[1].Offerings.Single().Quantity);
        Assert.Equal(25, update.Products[2].Offerings.Single().Quantity);
    }

    [Fact]
    public void InventoryWriter_FromLocal_TwoVariations_Reordered_PreservesAllCombinationsAndQuantities()
    {
        var etsyInventory = new EtsyInventory
        {
            Products = [
                new()
                {
                    ProductId = 1,
                    PropertyValues = [
                        new() { PropertyId = SizePropId, PropertyName = "Size", Values = ["Small"], ValueIds = [1] },
                        new() { PropertyId = ColorPropId, PropertyName = "Color", Values = ["Black"], ValueIds = [11] }
                    ],
                    Offerings = [new() { OfferingId = 10, Quantity = 100, Price = new() { Amount = 1000, Divisor = 100 }, IsEnabled = true }]
                },
                new()
                {
                    ProductId = 2,
                    PropertyValues = [
                        new() { PropertyId = SizePropId, PropertyName = "Size", Values = ["Small"], ValueIds = [1] },
                        new() { PropertyId = ColorPropId, PropertyName = "Color", Values = ["White"], ValueIds = [12] }
                    ],
                    Offerings = [new() { OfferingId = 20, Quantity = 200, Price = new() { Amount = 1000, Divisor = 100 }, IsEnabled = true }]
                },
                new()
                {
                    ProductId = 3,
                    PropertyValues = [
                        new() { PropertyId = SizePropId, PropertyName = "Size", Values = ["Large"], ValueIds = [3] },
                        new() { PropertyId = ColorPropId, PropertyName = "Color", Values = ["Black"], ValueIds = [11] }
                    ],
                    Offerings = [new() { OfferingId = 30, Quantity = 300, Price = new() { Amount = 2000, Divisor = 100 }, IsEnabled = true }]
                },
                new()
                {
                    ProductId = 4,
                    PropertyValues = [
                        new() { PropertyId = SizePropId, PropertyName = "Size", Values = ["Large"], ValueIds = [3] },
                        new() { PropertyId = ColorPropId, PropertyName = "Color", Values = ["White"], ValueIds = [12] }
                    ],
                    Offerings = [new() { OfferingId = 40, Quantity = 400, Price = new() { Amount = 2000, Divisor = 100 }, IsEnabled = true }]
                }
            ]
        };

        var localInv = TwoVariationInventory();
        // Reorder Size to Large, Small, and Color to White, Black
        var reorderedSize = VariationEditor.ReorderValues(localInv, SizePropId, ["Large", "Small", "Medium"]);
        var reorderedBoth = VariationEditor.ReorderValues(reorderedSize, ColorPropId, ["White", "Black", "Red"]);

        var update = InventoryWriter.FromLocal(etsyInventory, reorderedBoth);

        // Sequence of first 4 products: (Large, White), (Large, Black), (Large, Red), (Small, White) ...
        var first = update.Products[0];
        Assert.Equal("Large", first.PropertyValues[0].Values.Single());
        Assert.Equal("White", first.PropertyValues[1].Values.Single());
        Assert.Equal(400, first.Offerings.Single().Quantity); // Large-White had 400 on Etsy

        var second = update.Products[1];
        Assert.Equal("Large", second.PropertyValues[0].Values.Single());
        Assert.Equal("Black", second.PropertyValues[1].Values.Single());
        Assert.Equal(300, second.Offerings.Single().Quantity); // Large-Black had 300 on Etsy
    }

    // =========================================================================
    // 3. STRESS TESTS: VariationLists.Plan & ApplyAsync Option Mutations
    // =========================================================================

    [Fact]
    public async Task VariationLists_Plan_OptionMutations_MoveToFront_MoveToBack_AddAndReorderSimultaneously()
    {
        var (shop, listingId, listId) = await SetupLinkedShopAsync();
        using var _ = shop;

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        await using var db = shop.Db.CreateDbContext();
        var link = await db.ListingVariationLists.SingleAsync(l => l.ListingId == listingId && l.PropertyId == ColorPropId);

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var editModel = (await editor.LoadAsync(listingId))!.Model;
        var localInv = editModel.Local(ListingFields.Inventory)!;

        // --- Scenario A: Move Blue to Front: [Blue, Black, White, Red] ---
        var templateA = (await lists.GetAsync(listId))!;
        var optBlack = templateA.Options.Single(o => o.Name == "Black");
        var optWhite = templateA.Options.Single(o => o.Name == "White");
        var optRed = templateA.Options.Single(o => o.Name == "Red");
        var optBlue = templateA.Options.Single(o => o.Name == "Blue");

        var planA = VariationListService.Plan(
            new VariationList { Id = templateA.Id, Name = templateA.Name, Options = [optBlue, optBlack, optWhite, optRed] },
            link,
            localInv
        );

        Assert.Contains(planA.Changes, c => c.Contains("Reorders options in Color"));
        var reorderedA = VariationEditor.GetValueOrder(planA.Inventory, ColorPropId);
        Assert.Equal(["Blue", "Black", "White", "Red"], reorderedA);

        // --- Scenario B: Move White to Back: [Black, Red, Blue, White] ---
        var planB = VariationListService.Plan(
            new VariationList { Id = templateA.Id, Name = templateA.Name, Options = [optBlack, optRed, optBlue, optWhite] },
            link,
            localInv
        );
        Assert.Contains(planB.Changes, c => c.Contains("Reorders options in Color"));
        var reorderedB = VariationEditor.GetValueOrder(planB.Inventory, ColorPropId);
        Assert.Equal(["Black", "Red", "Blue", "White"], reorderedB);

        // --- Scenario C: Add new option Yellow AND reorder simultaneously to [Yellow, Red, Blue, White, Black] ---
        var optYellow = new VariationListOption(99, "Yellow", true);
        var planC = VariationListService.Plan(
            new VariationList { Id = templateA.Id, Name = templateA.Name, Options = [optYellow, optRed, optBlue, optWhite, optBlack] },
            link,
            localInv
        );
        Assert.Contains(planC.Changes, c => c.Contains("Adds Yellow"));
        Assert.Contains(planC.Changes, c => c.Contains("Reorders options in Color"));
        var reorderedC = VariationEditor.GetValueOrder(planC.Inventory, ColorPropId);
        Assert.Equal(["Yellow", "Red", "Blue", "White", "Black"], reorderedC);
    }

    [Fact]
    public async Task VariationLists_Plan_PreservesExtraListingCustomValues_AfterTemplateOptions()
    {
        var (shop, listingId, listId) = await SetupLinkedShopAsync();
        using var _ = shop;

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        await using var db = shop.Db.CreateDbContext();
        var link = await db.ListingVariationLists.SingleAsync(l => l.ListingId == listingId && l.PropertyId == ColorPropId);

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var editModel = (await editor.LoadAsync(listingId))!.Model;
        var localInv = editModel.Local(ListingFields.Inventory)!;

        // Add a hand-made option "Glitter Rainbow" to the listing that is NOT in the template
        var invWithCustom = VariationEditor.AddValue(localInv, ColorPropId, "Glitter Rainbow");

        // Template reorders options to: [Red, Blue, White, Black]
        var template = (await lists.GetAsync(listId))!;
        var optBlack = template.Options.Single(o => o.Name == "Black");
        var optWhite = template.Options.Single(o => o.Name == "White");
        var optRed = template.Options.Single(o => o.Name == "Red");
        var optBlue = template.Options.Single(o => o.Name == "Blue");

        var plan = VariationListService.Plan(
            new VariationList { Id = template.Id, Name = template.Name, Options = [optRed, optBlue, optWhite, optBlack] },
            link,
            invWithCustom
        );

        Assert.Contains(plan.Changes, c => c.Contains("Reorders options in Color"));
        var reordered = VariationEditor.GetValueOrder(plan.Inventory, ColorPropId);

        // Template options must come first in new template order, followed by the listing's hand-made option
        Assert.Equal(["Red", "Blue", "White", "Black", "Glitter Rainbow"], reordered);
    }

    [Fact]
    public async Task VariationLists_ApplyAsync_Idempotency_And_DatabasePersistence()
    {
        var (shop, listingId, listId) = await SetupLinkedShopAsync();
        using var _ = shop;

        var lists = shop.Provider.GetRequiredService<VariationListService>();

        // Reorder template to [Blue, Red, White, Black]
        var template = (await lists.GetAsync(listId))!;
        var optBlack = template.Options.Single(o => o.Name == "Black");
        var optWhite = template.Options.Single(o => o.Name == "White");
        var optRed = template.Options.Single(o => o.Name == "Red");
        var optBlue = template.Options.Single(o => o.Name == "Blue");
        await lists.SaveAsync(listId, "Test Colors", [optBlue, optRed, optWhite, optBlack], "tophattwaffle");

        // First apply
        var changedCount1 = await lists.ApplyAsync(listId, "tophattwaffle");
        Assert.Equal(1, changedCount1);

        // Verify draft
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var draft = (await editor.LoadAsync(listingId))!.Model.Local(ListingFields.Inventory)!;
        var valueOrder = VariationEditor.GetValueOrder(draft, ColorPropId);
        Assert.Equal(["Blue", "Red", "White", "Black"], valueOrder);

        var productValues = draft["products"]!.AsArray()
            .Select(p => p!["property_values"]![0]!["values"]![0]!.ToString())
            .ToList();
        Assert.Equal(["Blue", "Red", "White", "Black"], productValues);

        // Verify link.AppliedOptions
        await using var db = shop.Db.CreateDbContext();
        var link = await db.ListingVariationLists.SingleAsync(l => l.ListingId == listingId && l.PropertyId == ColorPropId);
        var appliedNames = link.AppliedOptions.Select(a => a.Name).ToList();
        Assert.Equal(["Blue", "Red", "White", "Black"], appliedNames);

        // Second apply (must be completely idempotent and report 0 changes)
        var changedCount2 = await lists.ApplyAsync(listId, "tophattwaffle");
        Assert.Equal(0, changedCount2);
    }

    [Fact]
    public void ReorderValues_TwoVariations_OnlySecondaryVariationReordered()
    {
        var inv = TwoVariationInventory();
        // Only reorder Color (secondary variation) to Red, White, Black
        var reorderedColor = VariationEditor.ReorderValues(inv, ColorPropId, ["Red", "White", "Black"]);

        var colorOrder = VariationEditor.GetValueOrder(reorderedColor, ColorPropId);
        Assert.Equal(["Red", "White", "Black"], colorOrder);

        // Size was NOT reordered, should still return its values
        var sizeOrder = VariationEditor.GetValueOrder(reorderedColor, SizePropId);
        Assert.Equal(["Small", "Medium", "Large"], sizeOrder);
    }
}
