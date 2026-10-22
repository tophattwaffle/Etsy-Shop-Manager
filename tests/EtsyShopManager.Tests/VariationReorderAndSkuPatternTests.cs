using System.IO;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Etsy.Models;
using EtsyShopManager.Web.Components.Pages.Listings;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;

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

    [Theory]
    [InlineData(
        new[] { "White", "Black", "Red" },
        new[] { "Black", "White", "Red" },
        new[] { "Black", "White", "Red" })]
    [InlineData(
        new[] { "Custom Z", "White", "Custom A", "Black", "Custom M" },
        new[] { "Black", "White" },
        new[] { "Black", "White", "Custom Z", "Custom A", "Custom M" })]
    [InlineData(
        new[] { "Small", "Large" },
        new[] { "Large", "Medium", "Small" },
        new[] { "Large", "Small" })]
    [InlineData(
        new[] { "Option A", "Option B" },
        new[] { "Option X", "Option Y" },
        new[] { "Option A", "Option B" })]
    public void OrderValuesByTemplate_OrdersTemplateOptionsFirst_FollowedByCustomExtrasInRelativeOrder(
        string[] current, string[] template, string[] expected)
    {
        var actual = VariationEditor.OrderValuesByTemplate(current, template);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void OrderValuesByTemplate_CaseInsensitiveMatching_PreservesListingCasing()
    {
        string[] current = ["matte black", "SNOW WHITE", "Ruby Red"];
        string[] template = ["Snow White", "Matte Black", "Ruby Red"];

        var actual = VariationEditor.OrderValuesByTemplate(current, template);

        // Matching is case-insensitive, preserving existing listing casing
        Assert.Equal(["SNOW WHITE", "matte black", "Ruby Red"], actual);
    }

    [Fact]
    public void OrderValuesByTemplate_BoundaryCases_EmptyAndSingleValues()
    {
        Assert.Empty(VariationEditor.OrderValuesByTemplate([], ["Black", "White"]));
        Assert.Equal(["Black"], VariationEditor.OrderValuesByTemplate(["Black"], ["White", "Black"]));
        Assert.Equal(["Black", "White"], VariationEditor.OrderValuesByTemplate(["Black", "White"], []));
    }

    [Fact]
    public async Task LinkVariationAsync_ReordersListingDraft_WhenListingValuesInitiallyOutOfTemplateOrder()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listing = FakeEtsyShop.Listing(1, "Out of Order Color Listing");
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

        // Initial Etsy order on listing: White, Black, Red
        string[] initialColors = ["White", "Black", "Red"];
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray([.. initialColors.Select(c => (JsonNode)MakeProduct(c))]),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        // Template defines order: Black, White, Red
        var listId = (await lists.SaveAsync(null, "Standard Palette", [
            new(0, "Black", true),
            new(0, "White", true),
            new(0, "Red", true)
        ], "tophattwaffle")).Id!.Value;

        await using var db = shop.Db.CreateDbContext();
        var dbListing = await db.Listings.SingleAsync(l => l.EtsyListingId == 1);

        // Act: Link variation from editor
        var linkError = await lists.LinkVariationAsync(listId, dbListing.Id, ColorPropId, "tophattwaffle");
        Assert.Null(linkError);

        // Assert: Draft was created and reordered to template order
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var draftInv = (await editor.LoadAsync(dbListing.Id))!.Model.Local(ListingFields.Inventory)!;

        var valueOrder = VariationEditor.GetValueOrder(draftInv, ColorPropId);
        Assert.Equal(["Black", "White", "Red"], valueOrder);

        var productValues = draftInv["products"]!.AsArray()
            .Select(p => p!["property_values"]![0]!["values"]![0]!.ToString())
            .ToList();
        Assert.Equal(["Black", "White", "Red"], productValues);

        // Verify AppliedOptions on link in DB
        await using var verifyDb = shop.Db.CreateDbContext();
        var link = await verifyDb.ListingVariationLists.SingleAsync(l => l.ListingId == dbListing.Id && l.PropertyId == ColorPropId);
        Assert.Equal(["Black", "White", "Red"], link.AppliedOptions.Select(o => o.Name));
    }

    [Fact]
    public async Task LinkVariationAsync_WithListingSpecificExtraValues_AppendsExtrasAfterTemplateOptions()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listing = FakeEtsyShop.Listing(2, "Listing with Extras");
        long id = 20;
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

        // Initial listing values: Custom Swirl, White, Handmade Glitter, Black
        string[] initialColors = ["Custom Swirl", "White", "Handmade Glitter", "Black"];
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray([.. initialColors.Select(c => (JsonNode)MakeProduct(c))]),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        // Template has only Black and White
        var listId = (await lists.SaveAsync(null, "Monochrome", [
            new(0, "Black", true),
            new(0, "White", true)
        ], "tophattwaffle")).Id!.Value;

        await using var db = shop.Db.CreateDbContext();
        var dbListing = await db.Listings.SingleAsync(l => l.EtsyListingId == 2);

        // Act
        var error = await lists.LinkVariationAsync(listId, dbListing.Id, ColorPropId, "tophattwaffle");
        Assert.Null(error);

        // Assert: Template options come first, followed by custom extras in relative order
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var draftInv = (await editor.LoadAsync(dbListing.Id))!.Model.Local(ListingFields.Inventory)!;

        var valueOrder = VariationEditor.GetValueOrder(draftInv, ColorPropId);
        Assert.Equal(["Black", "White", "Custom Swirl", "Handmade Glitter"], valueOrder);
    }

    [Fact]
    public async Task LinkAsync_BulkLinking_ReordersAllCandidateListingsWithOutOfOrderValues()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        long id = 50;
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

        // Listing 1: White, Black
        var l1 = FakeEtsyShop.Listing(11, "Listing 1");
        l1["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(MakeProduct("White"), MakeProduct("Black")),
        };
        shop.Listings.Add(l1);

        // Listing 2: Red, White, Black
        var l2 = FakeEtsyShop.Listing(12, "Listing 2");
        l2["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(MakeProduct("Red"), MakeProduct("White"), MakeProduct("Black")),
        };
        shop.Listings.Add(l2);

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        var listId = (await lists.SaveAsync(null, "Trio Colors", [
            new(0, "Black", true),
            new(0, "White", true),
            new(0, "Red", true)
        ], "tophattwaffle")).Id!.Value;

        await using var db = shop.Db.CreateDbContext();
        var dbL1 = await db.Listings.SingleAsync(l => l.EtsyListingId == 11);
        var dbL2 = await db.Listings.SingleAsync(l => l.EtsyListingId == 12);

        // Act: Bulk link
        var linkedCount = await lists.LinkAsync(listId, [(dbL1.Id, ColorPropId), (dbL2.Id, ColorPropId)], "tophattwaffle");
        Assert.Equal(2, linkedCount);

        // Assert: Both listings have reordered draft inventory
        var editor = shop.Provider.GetRequiredService<ListingEditor>();

        var inv1 = (await editor.LoadAsync(dbL1.Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Black", "White"], VariationEditor.GetValueOrder(inv1, ColorPropId));

        var inv2 = (await editor.LoadAsync(dbL2.Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Black", "White", "Red"], VariationEditor.GetValueOrder(inv2, ColorPropId));
    }

    [Fact]
    public void Plan_DetectsOrderDivergence_WhenInventoryHasNullValueOrder()
    {
        // Raw inventory with products in order [White, Black, Red], and NO value_order
        var inv = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                Product("SKU-W", 1000, true, Pv(ColorPropId, "Color", "White", 1)),
                Product("SKU-B", 1000, true, Pv(ColorPropId, "Color", "Black", 2)),
                Product("SKU-R", 1000, true, Pv(ColorPropId, "Color", "Red", 3)))
        };
        Assert.Null(inv["value_order"]);

        var template = new VariationList
        {
            Id = 1,
            Name = "Colors",
            Options = [new(1, "Black", true), new(2, "White", true), new(3, "Red", true)]
        };
        var link = new ListingVariationList
        {
            ListingId = 10,
            PropertyId = ColorPropId,
            AppliedOptions = [new(1, "Black", true), new(2, "White", true), new(3, "Red", true)]
        };

        var plan = VariationListService.Plan(template, link, inv);

        // Must detect order mismatch even though value_order was null
        Assert.Contains(plan.Changes, c => c.Contains("Reorders options in Color"));
        var reordered = VariationEditor.GetValueOrder(plan.Inventory, ColorPropId);
        Assert.Equal(["Black", "White", "Red"], reordered);
    }

    [Fact]
    public async Task ApplyAsync_AppliesReordering_WhenValueOrderIsNullInitially()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listing = FakeEtsyShop.Listing(1, "Fresh Imported Listing");
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

        // Imported products in order: White, Black, Red (NO value_order)
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(MakeProduct("White"), MakeProduct("Black"), MakeProduct("Red")),
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

        // Manually insert link to simulate link established prior to template reorder
        db.ListingVariationLists.Add(new ListingVariationList
        {
            ListingId = dbListing.Id,
            VariationListId = listId,
            PropertyId = ColorPropId,
            AppliedOptions = [new(1, "White", true), new(2, "Black", true), new(3, "Red", true)],
            LinkedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        // Act: Apply template
        var changedCount = await lists.ApplyAsync(listId, "tophattwaffle");
        Assert.Equal(1, changedCount);

        // Assert: Draft now has value_order set to template order
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var draftInv = (await editor.LoadAsync(dbListing.Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Black", "White", "Red"], VariationEditor.GetValueOrder(draftInv, ColorPropId));

        // Subsequent apply is idempotent
        var reApply = await lists.ApplyAsync(listId, "tophattwaffle");
        Assert.Equal(0, reApply);
    }

    [Fact]
    public void VariationsEditor_LocksReordering_And_ShowsTemplateIndicator_WhenLinked()
    {
        var variationsEditorPath = Path.Combine(DesignRulesTests.Root, "src", "EtsyShopManager.Web", "Components", "Pages", "Listings", "VariationsEditor.razor");
        Assert.True(File.Exists(variationsEditorPath));
        var content = File.ReadAllText(variationsEditorPath);

        // 1. Accepts Links parameter
        Assert.Contains("public IReadOnlyList<VariationListLink>? Links", content);

        // 2. Contains order locked helper or logic
        Assert.Contains("IsOrderLocked", content);

        // 3. Move Earlier and Move Later buttons are gated on !locked
        Assert.Matches(@"!\s*(locked|IsOrderLocked\([^)]+\))", content);

        // 4. Displays template indicator mentioning template name
        Assert.Contains("Value order is controlled by template @link.ListName", content);

        // 5. EditListing passes Links parameter to VariationsEditor
        var editListingPath = Path.Combine(DesignRulesTests.Root, "src", "EtsyShopManager.Web", "Components", "Pages", "Listings", "EditListing.razor");
        var editContent = File.ReadAllText(editListingPath);
        Assert.Contains("Links=\"_links\"", editContent);
    }

    private sealed class TestJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string uri) => Initialize("http://localhost/", uri);
    }

    [Fact]
    public async Task VariationsEditor_renders_reorder_buttons_when_unlinked_and_hides_them_with_indicator_when_linked()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, TestJsRuntime>();
        services.AddSingleton<NavigationManager>(new TestNavigation("http://localhost/"));
        services.AddMudServices();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var inv = TwoVariationInventory(); // Size (100) and Color (513)

        // 1. Unlinked test
        var unlinkedHtml = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Inventory"] = inv,
                ["Links"] = (IReadOnlyList<VariationListLink>?)null
            });
            var output = await renderer.RenderComponentAsync<VariationsEditor>(parameters);
            return output.ToHtmlString();
        });

        Assert.Contains("Move Small earlier", unlinkedHtml);
        Assert.Contains("Move Small later", unlinkedHtml);
        Assert.DoesNotContain("Value order is controlled by template", unlinkedHtml);

        // 2. Linked test (Size linked to "Standard Sizes")
        var links = new List<VariationListLink>
        {
            new(ListId: 1, ListName: "Standard Sizes", PropertyId: SizePropId)
        };

        var linkedHtml = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Inventory"] = inv,
                ["Links"] = (IReadOnlyList<VariationListLink>)links
            });
            var output = await renderer.RenderComponentAsync<VariationsEditor>(parameters);
            return output.ToHtmlString();
        });

        // Size variation: move buttons hidden, indicator present
        Assert.DoesNotContain("Move Small earlier", linkedHtml);
        Assert.DoesNotContain("Move Small later", linkedHtml);
        Assert.Contains("Value order is controlled by template Standard Sizes.", linkedHtml);

        // Color variation (unlinked): move buttons remain present
        Assert.Contains("Move Black earlier", linkedHtml);
        Assert.Contains("Move Black later", linkedHtml);
    }

    [Fact]
    public async Task LinkedVariation_AllowsEditingOfferingPrices_Skus_ReadinessState_And_Availability()
    {
        var (shop, listingId, listId) = await SetupLinkedShopAsync();
        using var _ = shop;

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var editModel = (await editor.LoadAsync(listingId))!.Model;
        var inv = (JsonObject)editModel.Local(ListingFields.Inventory)!;

        // 1. Edit Price of Black offering
        var products = inv["products"]!.AsArray();
        var blackProduct = products.First(p => (string)p!["property_values"]![0]!["values"]![0]! == "Black")!.AsObject();
        blackProduct["offerings"]![0]!["price_amount"] = 2499; // $24.99

        // 2. Edit Availability of White offering (switch off)
        var whiteProduct = products.First(p => (string)p!["property_values"]![0]!["values"]![0]! == "White")!.AsObject();
        whiteProduct["offerings"]![0]!["is_enabled"] = false;

        // 3. Edit Readiness State of Red offering
        var redProduct = products.First(p => (string)p!["property_values"]![0]!["values"]![0]! == "Red")!.AsObject();
        redProduct["offerings"]![0]!["readiness_state_id"] = 601L;

        // 4. Apply SKU pattern
        inv = SkuPatternResolver.ApplyPattern(inv, "{listing.sku}-{color}", "VASE");

        // Save draft
        await editor.SaveAsync(listingId, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Inventory] = inv
        }, "tophattwaffle");

        // Reload and assert
        var reloaded = (await editor.LoadAsync(listingId))!.Model.Local(ListingFields.Inventory)!;
        var reloadedProducts = reloaded["products"]!.AsArray();

        var reloadedBlack = reloadedProducts.First(p => (string)p!["property_values"]![0]!["values"]![0]! == "Black");
        Assert.Equal(2499, (long)reloadedBlack!["offerings"]![0]!["price_amount"]!);
        Assert.Equal("VASE-Black", (string?)reloadedBlack["sku"]);

        var reloadedWhite = reloadedProducts.First(p => (string)p!["property_values"]![0]!["values"]![0]! == "White");
        Assert.False((bool)reloadedWhite!["offerings"]![0]!["is_enabled"]!);
        Assert.Equal("VASE-White", (string?)reloadedWhite["sku"]);

        var reloadedRed = reloadedProducts.First(p => (string)p!["property_values"]![0]!["values"]![0]! == "Red");
        Assert.Equal(601L, (long)reloadedRed!["offerings"]![0]!["readiness_state_id"]!);
        Assert.Equal("VASE-Red", (string?)reloadedRed["sku"]);

        // Value order must remain unchanged
        Assert.Equal(["Black", "White", "Red"], VariationEditor.GetValueOrder(reloaded, ColorPropId));

        // Assert link is intact
        await using var db = shop.Db.CreateDbContext();
        Assert.True(await db.ListingVariationLists.AnyAsync(l => l.ListingId == listingId && l.VariationListId == listId));
    }

    [Fact]
    public async Task LinkedVariation_AddNewCustomValue_AppendsAfterTemplateOptions()
    {
        var (shop, listingId, listId) = await SetupLinkedShopAsync();
        using var _ = shop;

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var editModel = (await editor.LoadAsync(listingId))!.Model;
        var inv = editModel.Local(ListingFields.Inventory)!;

        // Add custom listing-specific value "Glitter Gold"
        var updatedInv = VariationEditor.AddValue(inv, ColorPropId, "Glitter Gold", "{listing.sku}-{color}", "VASE");

        await editor.SaveAsync(listingId, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Inventory] = updatedInv
        }, "tophattwaffle");

        var reloaded = (await editor.LoadAsync(listingId))!.Model.Local(ListingFields.Inventory)!;
        var valueOrder = VariationEditor.GetValueOrder(reloaded, ColorPropId);

        // Template options first, then new custom value
        Assert.Equal(["Black", "White", "Red", "Glitter Gold"], valueOrder);
    }

    [Fact]
    public async Task PushListingJob_PushesTemplateOrderedInventoryToEtsy_PreservingOfferingQuantitiesAndIds()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listing = FakeEtsyShop.Listing(1, "Push Order Listing");
        long id = 10;
        JsonObject MakeProduct(string color, long offeringId, int quantity, long priceAmount)
        {
            var p = FakeEtsyShop.Product(++id, $"SKU-{color}", "OneSize", priceAmount, quantity);
            p["property_values"]!.AsArray()[0] = new JsonObject
            {
                ["property_id"] = ColorPropId,
                ["property_name"] = "Color",
                ["scale_id"] = null,
                ["value_ids"] = new JsonArray(),
                ["values"] = new JsonArray(color),
            };
            p["offerings"]![0]!["offering_id"] = offeringId;
            p["offerings"]![0]!["quantity"] = quantity;
            p["offerings"]![0]!["price_amount"] = priceAmount;
            return p;
        }

        // Etsy inventory: White (id 101, qty 5), Black (id 102, qty 15), Red (id 103, qty 25)
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(ColorPropId),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                MakeProduct("White", 101, 5, 1500),
                MakeProduct("Black", 102, 15, 1200),
                MakeProduct("Red", 103, 25, 2000)),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        // Template order: Black, White, Red
        var listId = (await lists.SaveAsync(null, "Push Colors", [
            new(0, "Black", true),
            new(0, "White", true),
            new(0, "Red", true)
        ], "tophattwaffle")).Id!.Value;

        await using var db = shop.Db.CreateDbContext();
        var dbListing = await db.Listings.SingleAsync(l => l.EtsyListingId == 1);

        // Link variation (automatically reorders draft to Black, White, Red)
        await lists.LinkVariationAsync(listId, dbListing.Id, ColorPropId, "tophattwaffle");

        // Request and execute push
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var (pushId, blockers) = await editor.RequestPushAsync(dbListing.Id, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(pushId);

        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 1,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId.Value), JobJson.Options),
        }, CancellationToken.None);

        // Verify Etsy write call
        var invWrite = Assert.Single(shop.Writes, w => w.Method == "PUT" && w.Path.EndsWith("/inventory"));
        var body = JsonNode.Parse(invWrite.Body)!;
        var products = body["products"]!.AsArray();

        Assert.Equal(3, products.Count);

        // Product sequence in Etsy PUT body MUST match template order: Black, White, Red
        Assert.Equal("Black", (string?)products[0]!["property_values"]![0]!["values"]![0]);
        Assert.Equal("White", (string?)products[1]!["property_values"]![0]!["values"]![0]);
        Assert.Equal("Red", (string?)products[2]!["property_values"]![0]!["values"]![0]);

        // Quantities must match by variation key: Black had 15, White had 5, Red had 25
        Assert.Equal(15, (int)products[0]!["offerings"]![0]!["quantity"]!);
        Assert.Equal(5, (int)products[1]!["offerings"]![0]!["quantity"]!);
        Assert.Equal(25, (int)products[2]!["offerings"]![0]!["quantity"]!);
    }
}

