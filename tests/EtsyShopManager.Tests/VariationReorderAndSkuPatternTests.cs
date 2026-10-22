using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Etsy.Models;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class VariationReorderAndSkuPatternTests
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
            Product("SKU-L", 2000, true, Pv(SizePropId, "Size", "Large", 3))),
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
            Product("SKU-L-BLK", 2000, true, Pv(SizePropId, "Size", "Large", 2), Pv(ColorPropId, "Color", "Black", 11)),
            Product("SKU-L-WHT", 2000, true, Pv(SizePropId, "Size", "Large", 2), Pv(ColorPropId, "Color", "White", 12)),
            Product("SKU-L-RED", 2000, true, Pv(SizePropId, "Size", "Large", 2), Pv(ColorPropId, "Color", "Red", 13))),
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

        string[] colors = ["Black", "White", "Red"];
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
            new(0, "Red", true)
        ], "tophattwaffle")).Id!.Value;

        await using var db = shop.Db.CreateDbContext();
        var dbListing = await db.Listings.SingleAsync(l => l.EtsyListingId == 1);
        await lists.LinkAsync(listId, [(dbListing.Id, ColorPropId)], "tophattwaffle");

        return (shop, dbListing.Id, listId);
    }

    [Fact]
    public void VariationEditor_ReorderValues_updates_value_order_and_product_grid_order_for_single_and_two_variation_listings()
    {
        // 1. Single variation listing reorder
        var single = SingleVariationInventory();
        var reorderedSingle = VariationEditor.ReorderValues(single, SizePropId, ["Large", "Small", "Medium"]);

        var order1 = VariationEditor.GetValueOrder(reorderedSingle, SizePropId);
        Assert.Equal(["Large", "Small", "Medium"], order1);

        var variations1 = VariationEditor.Variations(reorderedSingle);
        Assert.Equal(["Large", "Small", "Medium"], variations1.Single(v => v.PropertyId == SizePropId).Values);

        var products1 = reorderedSingle["products"]!.AsArray();
        Assert.Equal("Large", (string?)products1[0]!["property_values"]![0]!["values"]![0]);
        Assert.Equal("Small", (string?)products1[1]!["property_values"]![0]!["values"]![0]);
        Assert.Equal("Medium", (string?)products1[2]!["property_values"]![0]!["values"]![0]);

        // 2. Two variation listing reorder
        var two = TwoVariationInventory();
        var reorderedTwo = VariationEditor.ReorderValues(two, SizePropId, ["Large", "Small"]);
        reorderedTwo = VariationEditor.ReorderValues(reorderedTwo, ColorPropId, ["Red", "White", "Black"]);

        Assert.Equal(["Large", "Small"], VariationEditor.GetValueOrder(reorderedTwo, SizePropId));
        Assert.Equal(["Red", "White", "Black"], VariationEditor.GetValueOrder(reorderedTwo, ColorPropId));

        var products2 = reorderedTwo["products"]!.AsArray();
        Assert.Equal(6, products2.Count);

        // Expected grid order:
        // (Large, Red), (Large, White), (Large, Black), (Small, Red), (Small, White), (Small, Black)
        string Combo(JsonNode p)
        {
            var pvs = p["property_values"]!.AsArray();
            var size = pvs.FirstOrDefault(pv => (long)pv!["property_id"]! == SizePropId)!["values"]![0]!.ToString();
            var color = pvs.FirstOrDefault(pv => (long)pv!["property_id"]! == ColorPropId)!["values"]![0]!.ToString();
            return $"{size}-{color}";
        }

        var actualCombos = products2.Select(p => Combo(p!)).ToList();
        Assert.Equal([
            "Large-Red",
            "Large-White",
            "Large-Black",
            "Small-Red",
            "Small-White",
            "Small-Black"
        ], actualCombos);
    }

    [Fact]
    public void Canonical_preserves_value_order_sequence()
    {
        var inv = TwoVariationInventory();
        inv = VariationEditor.ReorderValues(inv, SizePropId, ["Large", "Small"]);
        inv = VariationEditor.ReorderValues(inv, ColorPropId, ["Red", "White", "Black"]);

        // Manually reverse the products array
        var originalProducts = inv["products"]!.AsArray().Select(p => p!.DeepClone()).Reverse().ToArray();
        inv["products"] = new JsonArray(originalProducts);

        // Run Canonical
        var canonical = VariationEditor.Canonical(inv);

        string Combo(JsonNode p)
        {
            var pvs = p["property_values"]!.AsArray();
            var size = pvs.FirstOrDefault(pv => (long)pv!["property_id"]! == SizePropId)!["values"]![0]!.ToString();
            var color = pvs.FirstOrDefault(pv => (long)pv!["property_id"]! == ColorPropId)!["values"]![0]!.ToString();
            return $"{size}-{color}";
        }

        var canonicalCombos = canonical["products"]!.AsArray().Select(p => Combo(p!)).ToList();
        Assert.Equal([
            "Large-Red",
            "Large-White",
            "Large-Black",
            "Small-Red",
            "Small-White",
            "Small-Black"
        ], canonicalCombos);
    }

    [Fact]
    public void InventoryWriter_FromLocal_emits_products_in_local_grid_sequence_while_matching_existing_Etsy_offering_ids_quantities_and_prices()
    {
        // Etsy inventory with original order: Small (id 101, qty 5), Medium (id 102, qty 15), Large (id 103, qty 25)
        var etsy = new EtsyInventory
        {
            PriceOnProperty = [SizePropId],
            Products =
            [
                new EtsyProduct
                {
                    ProductId = 101,
                    Sku = "OLD-S",
                    PropertyValues = [new EtsyPropertyValue { PropertyId = SizePropId, PropertyName = "Size", Values = ["Small"], ValueIds = [1] }],
                    Offerings = [new EtsyOffering { OfferingId = 1001, Quantity = 5, Price = new EtsyMoney { Amount = 1000, Divisor = 100 }, IsEnabled = true }]
                },
                new EtsyProduct
                {
                    ProductId = 102,
                    Sku = "OLD-M",
                    PropertyValues = [new EtsyPropertyValue { PropertyId = SizePropId, PropertyName = "Size", Values = ["Medium"], ValueIds = [2] }],
                    Offerings = [new EtsyOffering { OfferingId = 1002, Quantity = 15, Price = new EtsyMoney { Amount = 1500, Divisor = 100 }, IsEnabled = true }]
                },
                new EtsyProduct
                {
                    ProductId = 103,
                    Sku = "OLD-L",
                    PropertyValues = [new EtsyPropertyValue { PropertyId = SizePropId, PropertyName = "Size", Values = ["Large"], ValueIds = [3] }],
                    Offerings = [new EtsyOffering { OfferingId = 1003, Quantity = 25, Price = new EtsyMoney { Amount = 2000, Divisor = 100 }, IsEnabled = true }]
                }
            ]
        };

        // Local inventory reordered to: Large, Small, Medium
        var localInv = VariationEditor.ReorderValues(SingleVariationInventory(), SizePropId, ["Large", "Small", "Medium"]);

        var update = InventoryWriter.FromLocal(etsy, localInv);

        Assert.Equal(3, update.Products.Count);

        // Sequence must follow local grid: Large, Small, Medium
        Assert.Equal("Large", update.Products[0].PropertyValues.Single().Values.Single());
        Assert.Equal("Small", update.Products[1].PropertyValues.Single().Values.Single());
        Assert.Equal("Medium", update.Products[2].PropertyValues.Single().Values.Single());

        // Quantities from Etsy offerings are matched by variation key, not position
        Assert.Equal(25, update.Products[0].Offerings.Single().Quantity); // Large had 25
        Assert.Equal(5, update.Products[1].Offerings.Single().Quantity);  // Small had 5
        Assert.Equal(15, update.Products[2].Offerings.Single().Quantity); // Medium had 15

        // Prices from local offerings
        Assert.Equal(20.00m, update.Products[0].Offerings.Single().Price);
        Assert.Equal(10.00m, update.Products[1].Offerings.Single().Price);
        Assert.Equal(15.00m, update.Products[2].Offerings.Single().Price);
    }

    [Fact]
    public async Task VariationListService_Plan_detects_option_order_differences_and_reorders_listing_values()
    {
        var (shop, listingId, listId) = await SetupLinkedShopAsync();
        using var _ = shop;

        var lists = shop.Provider.GetRequiredService<VariationListService>();

        // Update the template options to Red, White, Black (order reversed)
        var template = await lists.GetAsync(listId);
        var optBlack = template!.Options.Single(o => o.Name == "Black");
        var optWhite = template.Options.Single(o => o.Name == "White");
        var optRed = template.Options.Single(o => o.Name == "Red");

        await lists.SaveAsync(listId, "Test Colors", [optRed, optWhite, optBlack], "tophattwaffle");

        // Fetch updated template and link
        var updatedTemplate = (await lists.GetAsync(listId))!;
        await using var db = shop.Db.CreateDbContext();
        var link = await db.ListingVariationLists.SingleAsync(l => l.ListingId == listingId && l.PropertyId == ColorPropId);

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var editModel = (await editor.LoadAsync(listingId))!.Model;
        var localInv = editModel.Local(ListingFields.Inventory)!;

        // Plan
        var plan = VariationListService.Plan(updatedTemplate, link, localInv);

        Assert.Contains(plan.Changes, c => c.Contains("Reorders options in Color"));
        var reorderedValues = VariationEditor.GetValueOrder(plan.Inventory, ColorPropId);
        Assert.Equal(["Red", "White", "Black"], reorderedValues);

        var productValues = plan.Inventory["products"]!.AsArray()
            .Select(p => p!["property_values"]![0]!["values"]![0]!.ToString())
            .ToList();
        Assert.Equal(["Red", "White", "Black"], productValues);
    }

    [Fact]
    public async Task VariationListService_ApplyAsync_applies_option_reordering_to_linked_listing()
    {
        var (shop, listingId, listId) = await SetupLinkedShopAsync();
        using var _ = shop;

        var lists = shop.Provider.GetRequiredService<VariationListService>();

        // Update template order to Red, White, Black
        var template = await lists.GetAsync(listId);
        var optBlack = template!.Options.Single(o => o.Name == "Black");
        var optWhite = template.Options.Single(o => o.Name == "White");
        var optRed = template.Options.Single(o => o.Name == "Red");
        await lists.SaveAsync(listId, "Test Colors", [optRed, optWhite, optBlack], "tophattwaffle");

        // Apply list
        var changedCount = await lists.ApplyAsync(listId, "tophattwaffle");
        Assert.Equal(1, changedCount);

        // Verify that the listing draft reflects the reordered options
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var draft = (await editor.LoadAsync(listingId))!.Model.Local(ListingFields.Inventory)!;

        var valueOrder = VariationEditor.GetValueOrder(draft, ColorPropId);
        Assert.Equal(["Red", "White", "Black"], valueOrder);

        var productValues = draft["products"]!.AsArray()
            .Select(p => p!["property_values"]![0]!["values"]![0]!.ToString())
            .ToList();
        Assert.Equal(["Red", "White", "Black"], productValues);
    }

    [Fact]
    public void SkuPatternResolver_GenerateSku_with_single_and_multiple_variations()
    {
        // 1. Multiple variations pattern: {listing.sku}-{size}-{color}
        var valuesMulti = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Size"] = "Large",
            ["Primary color"] = "Matte Black"
        };
        var skuMulti = SkuPatternResolver.GenerateSku("{listing.sku}-{size}-{color}", "VASE", valuesMulti);
        Assert.Equal("VASE-Large-Matte-Black", skuMulti);

        // Case-insensitivity in token: {LISTING.SKU}_{SIZE}
        var skuCase = SkuPatternResolver.GenerateSku("{LISTING.SKU}_{SIZE}", "VASE", valuesMulti);
        Assert.Equal("VASE_Large", skuCase);

        // Short {sku} token
        var skuShort = SkuPatternResolver.GenerateSku("{sku}-{size}", "VASE", valuesMulti);
        Assert.Equal("VASE-Large", skuShort);

        // Exact match on custom variation name
        var valuesCustom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Material Option"] = "3D Printed PETG"
        };
        var skuCustom = SkuPatternResolver.GenerateSku("ART-{materialoption}", "BASE", valuesCustom);
        Assert.Equal("ART-3D-Printed-PETG", skuCustom);
    }

    [Fact]
    public void SkuPatternResolver_SanitizeForSku_handles_spaces_slashes_quotes_and_symbols()
    {
        Assert.Equal("Matte-Black", SkuPatternResolver.SanitizeForSku("Matte Black"));
        Assert.Equal("1-2-inch", SkuPatternResolver.SanitizeForSku("1/2 inch"));
        Assert.Equal("1-4-inch", SkuPatternResolver.SanitizeForSku("1\\4 inch"));
        Assert.Equal("6-Bowl", SkuPatternResolver.SanitizeForSku("6\" Bowl"));
        Assert.Equal("Single-Quote", SkuPatternResolver.SanitizeForSku("'Single' Quote"));
        Assert.Equal("Red-White-Blue", SkuPatternResolver.SanitizeForSku("Red, White & Blue!"));
        Assert.Equal("Alpha-Beta", SkuPatternResolver.SanitizeForSku("Alpha -- Beta"));
        Assert.Equal("Leading-Trailing", SkuPatternResolver.SanitizeForSku("-Leading-Trailing-"));
        Assert.Equal("", SkuPatternResolver.SanitizeForSku("   "));
        Assert.Equal("", SkuPatternResolver.SanitizeForSku(""));
    }

    [Fact]
    public void SkuPatternResolver_ApplyPattern_applies_SKUs_across_all_products_in_inventory()
    {
        var inv = TwoVariationInventory();
        var pattern = "{listing.sku}-{size}-{color}";

        var result = SkuPatternResolver.ApplyPattern(inv, pattern, "VASE");

        Assert.Equal(pattern, (string?)result["sku_pattern"]);

        var skus = result["products"]!.AsArray().Select(p => (string?)p!["sku"]).ToList();
        Assert.Equal([
            "VASE-Small-Black",
            "VASE-Small-White",
            "VASE-Small-Red",
            "VASE-Large-Black",
            "VASE-Large-White",
            "VASE-Large-Red"
        ], skus);
    }

    [Fact]
    public void Reordering_variation_values_does_not_alter_existing_product_SKUs()
    {
        var inv = SingleVariationInventory();
        // Existing custom SKUs
        var productsBefore = inv["products"]!.AsArray();
        productsBefore[0]!["sku"] = "CUSTOM-SKU-S";
        productsBefore[1]!["sku"] = "CUSTOM-SKU-M";
        productsBefore[2]!["sku"] = "CUSTOM-SKU-L";

        // Reorder values to Large, Small, Medium
        var reordered = VariationEditor.ReorderValues(inv, SizePropId, ["Large", "Small", "Medium"]);

        var reorderedProducts = reordered["products"]!.AsArray();
        string SkuOf(string size)
        {
            var p = reorderedProducts.First(p =>
            {
                var values = p?["property_values"]?[0]?["values"]?.AsArray();
                return values != null && values.Count > 0 && (string?)values[0] == size;
            });
            return (string?)p?["sku"] ?? "";
        }

        // Each product must keep its exact SKU
        Assert.Equal("CUSTOM-SKU-L", SkuOf("Large"));
        Assert.Equal("CUSTOM-SKU-S", SkuOf("Small"));
        Assert.Equal("CUSTOM-SKU-M", SkuOf("Medium"));
    }
}
