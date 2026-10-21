using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

/// <summary>
/// Empirical adversarial challenge tests for Requirement R2: Variation Template Pricing Strategies.
/// Verifies TakeHighest, TakeLowest, and PickPerListing across single- and 2-variation listings,
/// confirms strict preservation of existing option prices, verifies partitioning by other variation,
/// and verifies listings without per-variation pricing are excluded from pricing prompts.
/// </summary>
public class VariationPricingChallengeTests
{
    private const long ColorPropertyId = VariationEditor.CustomProperty1;
    private const long SizePropertyId = 100L;

    private static VariationListService Lists(FakeEtsyShop s) => s.Provider.GetRequiredService<VariationListService>();
    private static ListingEditor Editor(FakeEtsyShop s) => s.Provider.GetRequiredService<ListingEditor>();

    private static async Task<JsonNode> InventoryAsync(FakeEtsyShop s, int id) =>
        (await Editor(s).LoadAsync(id))!.Model.Local(ListingFields.Inventory)!;

    private static async Task<int> AddSingleVarListingWithPricesAsync(FakeEtsyShop shop, long listingId, string title,
        (string Color, long PriceInCents)[] options)
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
                ["property_id"] = ColorPropertyId,
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
        listing["price"] = FakeEtsyShop.Money(options[0].PriceInCents);
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(ColorPropertyId),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray([.. options.Select(o => (JsonNode)MakeProduct(o.Color, o.PriceInCents))]),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return await db.Listings.Where(l => l.EtsyListingId == listingId).Select(l => l.Id).SingleAsync();
    }

    private static async Task<int> AddDualVarListingWithPricesAsync(FakeEtsyShop shop, long listingId, string title,
        (string Size, string Color, long PriceInCents)[] combos, bool priceOnSize = true, bool priceOnColor = true)
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
                    ["property_id"] = SizePropertyId,
                    ["property_name"] = "Size",
                    ["value_ids"] = new JsonArray(),
                    ["values"] = new JsonArray(size),
                },
                new JsonObject
                {
                    ["property_id"] = ColorPropertyId,
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

        var priceOnArray = new JsonArray();
        if (priceOnSize) priceOnArray.Add(SizePropertyId);
        if (priceOnColor) priceOnArray.Add(ColorPropertyId);

        listing["price"] = FakeEtsyShop.Money(combos[0].PriceInCents);
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = priceOnArray,
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray([.. combos.Select(c => (JsonNode)MakeProduct(c.Size, c.Color, c.PriceInCents))]),
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
            var colorVal = (string)pvs.First(v => (long)v!["property_id"]! == ColorPropertyId)!["values"]![0]!;
            if (colorVal != color) return false;
            if (size is not null)
            {
                var sizeVal = (string)pvs.First(v => (long)v!["property_id"]! == SizePropertyId)!["values"]![0]!;
                if (sizeVal != size) return false;
            }
            return true;
        });
        var offering = match["offerings"]![0]!.AsObject();
        return VariationEditor.OfferingPrice(offering);
    }

    [Fact]
    public async Task Extreme1_SingleVariation_TakeHighest_Picks25_AndPreservesExisting()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // Existing options: Red $5.00, Blue $10.00, Green $25.00
        var listingId = await AddSingleVarListingWithPricesAsync(shop, 101, "Triple price item",
            [("Red", 500), ("Blue", 1000), ("Green", 2500)]);

        var listId = (await Lists(shop).SaveAsync(null, "Rainbow",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "tophattwaffle")).Id!.Value;

        await Lists(shop).LinkAsync(listId, [(listingId, ColorPropertyId)], "tophattwaffle");

        // Add Yellow to template
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Rainbow", [.. list.Options, new(0, "Yellow", true)], "tophattwaffle");

        // Verify ListingsNeedingPricingAsync
        var needing = await Lists(shop).ListingsNeedingPricingAsync(listId);
        var item = Assert.Single(needing);
        Assert.Equal((listingId, 5.00m, 25.00m), (item.ListingId, item.MinPrice, item.MaxPrice));
        Assert.Equal(["Yellow"], item.NewOptions);

        // Apply with TakeHighest
        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tophattwaffle", VariationPricingStrategy.TakeHighest));

        var inventory = await InventoryAsync(shop, listingId);
        // Existing option prices MUST NEVER be modified
        Assert.Equal(5.00m, OfferingPriceOf(inventory, "Red"));
        Assert.Equal(10.00m, OfferingPriceOf(inventory, "Blue"));
        Assert.Equal(25.00m, OfferingPriceOf(inventory, "Green"));

        // Newly added Yellow MUST take highest price ($25.00)
        Assert.Equal(25.00m, OfferingPriceOf(inventory, "Yellow"));
    }

    [Fact]
    public async Task Extreme2_SingleVariation_TakeLowest_Picks5_AndPreservesExisting()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // Existing options: Red $5.00, Blue $10.00, Green $25.00
        var listingId = await AddSingleVarListingWithPricesAsync(shop, 102, "Triple price item lowest",
            [("Red", 500), ("Blue", 1000), ("Green", 2500)]);

        var listId = (await Lists(shop).SaveAsync(null, "Rainbow 2",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "tophattwaffle")).Id!.Value;

        await Lists(shop).LinkAsync(listId, [(listingId, ColorPropertyId)], "tophattwaffle");

        // Add Yellow to template
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Rainbow 2", [.. list.Options, new(0, "Yellow", true)], "tophattwaffle");

        // Apply with TakeLowest
        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tophattwaffle", VariationPricingStrategy.TakeLowest));

        var inventory = await InventoryAsync(shop, listingId);
        // Existing option prices MUST NEVER be modified
        Assert.Equal(5.00m, OfferingPriceOf(inventory, "Red"));
        Assert.Equal(10.00m, OfferingPriceOf(inventory, "Blue"));
        Assert.Equal(25.00m, OfferingPriceOf(inventory, "Green"));

        // Newly added Yellow MUST take lowest price ($5.00)
        Assert.Equal(5.00m, OfferingPriceOf(inventory, "Yellow"));
    }

    [Fact]
    public async Task Extreme3_TwoVariations_BothVaryPrice_TakeHighest_PartitionsByOtherVariation()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // 3 Sizes x 2 Colors with asymmetric pricing:
        // Small: Red $5.00, Blue $12.00 -> max = $12.00
        // Medium: Red $15.00, Blue $18.00 -> max = $18.00
        // Large: Red $25.00, Blue $35.00 -> max = $35.00
        var listingId = await AddDualVarListingWithPricesAsync(shop, 103, "3x2 Grid Both Vary",
        [
            ("Small", "Red", 500),
            ("Small", "Blue", 1200),
            ("Medium", "Red", 1500),
            ("Medium", "Blue", 1800),
            ("Large", "Red", 2500),
            ("Large", "Blue", 3500),
        ]);

        var listId = (await Lists(shop).SaveAsync(null, "Color Grid",
            [new(0, "Red", true), new(0, "Blue", true)], "tophattwaffle")).Id!.Value;

        await Lists(shop).LinkAsync(listId, [(listingId, ColorPropertyId)], "tophattwaffle");

        // Add Green to template
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Color Grid", [.. list.Options, new(0, "Green", true)], "tophattwaffle");

        // Apply with TakeHighest
        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tophattwaffle", VariationPricingStrategy.TakeHighest));

        var inventory = await InventoryAsync(shop, listingId);

        // Verify existing option prices are completely untouched across all combinations
        Assert.Equal(5.00m, OfferingPriceOf(inventory, "Red", "Small"));
        Assert.Equal(12.00m, OfferingPriceOf(inventory, "Blue", "Small"));
        Assert.Equal(15.00m, OfferingPriceOf(inventory, "Red", "Medium"));
        Assert.Equal(18.00m, OfferingPriceOf(inventory, "Blue", "Medium"));
        Assert.Equal(25.00m, OfferingPriceOf(inventory, "Red", "Large"));
        Assert.Equal(35.00m, OfferingPriceOf(inventory, "Blue", "Large"));

        // Verify highest price is correctly partitioned per other-variation (Size):
        Assert.Equal(12.00m, OfferingPriceOf(inventory, "Green", "Small"));
        Assert.Equal(18.00m, OfferingPriceOf(inventory, "Green", "Medium"));
        Assert.Equal(35.00m, OfferingPriceOf(inventory, "Green", "Large"));
    }

    [Fact]
    public async Task Extreme4_TwoVariations_BothVaryPrice_TakeLowest_PartitionsByOtherVariation()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // 3 Sizes x 2 Colors with asymmetric pricing:
        // Small: Red $5.00, Blue $12.00 -> min = $5.00
        // Medium: Red $15.00, Blue $18.00 -> min = $15.00
        // Large: Red $25.00, Blue $35.00 -> min = $25.00
        var listingId = await AddDualVarListingWithPricesAsync(shop, 104, "3x2 Grid Both Vary Lowest",
        [
            ("Small", "Red", 500),
            ("Small", "Blue", 1200),
            ("Medium", "Red", 1500),
            ("Medium", "Blue", 1800),
            ("Large", "Red", 2500),
            ("Large", "Blue", 3500),
        ]);

        var listId = (await Lists(shop).SaveAsync(null, "Color Grid Lowest",
            [new(0, "Red", true), new(0, "Blue", true)], "tophattwaffle")).Id!.Value;

        await Lists(shop).LinkAsync(listId, [(listingId, ColorPropertyId)], "tophattwaffle");

        // Add Green to template
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Color Grid Lowest", [.. list.Options, new(0, "Green", true)], "tophattwaffle");

        // Apply with TakeLowest
        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tophattwaffle", VariationPricingStrategy.TakeLowest));

        var inventory = await InventoryAsync(shop, listingId);

        // Verify existing option prices are completely untouched across all combinations
        Assert.Equal(5.00m, OfferingPriceOf(inventory, "Red", "Small"));
        Assert.Equal(12.00m, OfferingPriceOf(inventory, "Blue", "Small"));
        Assert.Equal(15.00m, OfferingPriceOf(inventory, "Red", "Medium"));
        Assert.Equal(18.00m, OfferingPriceOf(inventory, "Blue", "Medium"));
        Assert.Equal(25.00m, OfferingPriceOf(inventory, "Red", "Large"));
        Assert.Equal(35.00m, OfferingPriceOf(inventory, "Blue", "Large"));

        // Verify lowest price is correctly partitioned per other-variation (Size):
        Assert.Equal(5.00m, OfferingPriceOf(inventory, "Green", "Small"));
        Assert.Equal(15.00m, OfferingPriceOf(inventory, "Green", "Medium"));
        Assert.Equal(25.00m, OfferingPriceOf(inventory, "Green", "Large"));
    }

    [Fact]
    public async Task Extreme5_PickPerListing_AcrossSingleAndDualVariationListings()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listing1 = await AddSingleVarListingWithPricesAsync(shop, 105, "Single Var Item",
            [("Red", 500), ("Blue", 1000)]);
        var listing2 = await AddDualVarListingWithPricesAsync(shop, 106, "Dual Var Item",
        [
            ("Small", "Red", 1200),
            ("Small", "Blue", 1600),
            ("Large", "Red", 2200),
            ("Large", "Blue", 2800),
        ]);

        var listId = (await Lists(shop).SaveAsync(null, "Shared Palette Pick",
            [new(0, "Red", true), new(0, "Blue", true)], "tophattwaffle")).Id!.Value;

        await Lists(shop).LinkAsync(listId, [(listing1, ColorPropertyId), (listing2, ColorPropertyId)], "tophattwaffle");

        // Add Yellow to template
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Shared Palette Pick", [.. list.Options, new(0, "Yellow", true)], "tophattwaffle");

        var customPrices = new Dictionary<int, decimal>
        {
            [listing1] = 42.50m,
            [listing2] = 77.25m,
        };

        Assert.Equal(2, await Lists(shop).ApplyAsync(listId, "tophattwaffle", VariationPricingStrategy.PickPerListing, customPrices));

        // Listing 1: Yellow should have exact custom price 42.50, existing untouched
        var inv1 = await InventoryAsync(shop, listing1);
        Assert.Equal(5.00m, OfferingPriceOf(inv1, "Red"));
        Assert.Equal(10.00m, OfferingPriceOf(inv1, "Blue"));
        Assert.Equal(42.50m, OfferingPriceOf(inv1, "Yellow"));

        // Listing 2: Yellow should have exact custom price 77.25 across all sizes, existing untouched
        var inv2 = await InventoryAsync(shop, listing2);
        Assert.Equal(12.00m, OfferingPriceOf(inv2, "Red", "Small"));
        Assert.Equal(16.00m, OfferingPriceOf(inv2, "Blue", "Small"));
        Assert.Equal(22.00m, OfferingPriceOf(inv2, "Red", "Large"));
        Assert.Equal(28.00m, OfferingPriceOf(inv2, "Blue", "Large"));

        Assert.Equal(77.25m, OfferingPriceOf(inv2, "Yellow", "Small"));
        Assert.Equal(77.25m, OfferingPriceOf(inv2, "Yellow", "Large"));
    }

    [Fact]
    public async Task Extreme6_MultipleNewOptionsSimultaneous_TakeHighestAndLowest()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listingId = await AddSingleVarListingWithPricesAsync(shop, 107, "Multi Option Item",
            [("Red", 500), ("Blue", 1000), ("Green", 2500)]);

        var listId = (await Lists(shop).SaveAsync(null, "Multi Palette",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "tophattwaffle")).Id!.Value;

        await Lists(shop).LinkAsync(listId, [(listingId, ColorPropertyId)], "tophattwaffle");

        // Add 3 new colors simultaneously: Yellow, Purple, Orange
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Multi Palette",
            [.. list.Options, new(0, "Yellow", true), new(0, "Purple", true), new(0, "Orange", true)], "tophattwaffle");

        var needing = await Lists(shop).ListingsNeedingPricingAsync(listId);
        var item = Assert.Single(needing);
        Assert.Equal(["Yellow", "Purple", "Orange"], item.NewOptions);

        // Apply with TakeHighest
        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tophattwaffle", VariationPricingStrategy.TakeHighest));

        var inv = await InventoryAsync(shop, listingId);
        Assert.Equal(5.00m, OfferingPriceOf(inv, "Red"));
        Assert.Equal(10.00m, OfferingPriceOf(inv, "Blue"));
        Assert.Equal(25.00m, OfferingPriceOf(inv, "Green"));

        // All 3 new options get highest price ($25.00)
        Assert.Equal(25.00m, OfferingPriceOf(inv, "Yellow"));
        Assert.Equal(25.00m, OfferingPriceOf(inv, "Purple"));
        Assert.Equal(25.00m, OfferingPriceOf(inv, "Orange"));
    }

    [Fact]
    public async Task Extreme7_ListingsWithoutPerVariationPricing_AreNotPromptedAndNotAffected()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // Listing A: 2-variation (Size x Color), but price varies ONLY by Size, NOT by Color!
        var listingDualOnlySizeVaries = await AddDualVarListingWithPricesAsync(shop, 108, "Size Only Varies",
            [
                ("Small", "Red", 1000),
                ("Small", "Blue", 1000),
                ("Large", "Red", 2000),
                ("Large", "Blue", 2000),
            ], priceOnSize: true, priceOnColor: false);

        // Listing B: 1-variation (Color), but price is UNIFORM (price_on_property is empty)
        var listingUniform = FakeEtsyShop.Listing(109, "Uniform Price Listing");
        long prodId = 10900;
        JsonObject MakeUniformProd(string color) => new()
        {
            ["product_id"] = ++prodId,
            ["sku"] = $"SKU-109-{color}",
            ["is_deleted"] = false,
            ["property_values"] = new JsonArray(new JsonObject
            {
                ["property_id"] = ColorPropertyId,
                ["property_name"] = "Color",
                ["value_ids"] = new JsonArray(),
                ["values"] = new JsonArray(color),
            }),
            ["offerings"] = new JsonArray(new JsonObject
            {
                ["offering_id"] = prodId * 10,
                ["quantity"] = 5,
                ["is_enabled"] = true,
                ["is_deleted"] = false,
                ["price"] = FakeEtsyShop.Money(1999),
            }),
        };
        listingUniform["price"] = FakeEtsyShop.Money(1999);
        listingUniform["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(), // EMPTY: no price variation
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(MakeUniformProd("Red"), MakeUniformProd("Blue")),
        };
        shop.Listings.Add(listingUniform);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        int listingUniformId;
        await using (var db = shop.Db.CreateDbContext())
            listingUniformId = await db.Listings.Where(l => l.EtsyListingId == 109).Select(l => l.Id).SingleAsync();

        // Template manages Color
        var listId = (await Lists(shop).SaveAsync(null, "Color List No Price Var",
            [new(0, "Red", true), new(0, "Blue", true)], "tophattwaffle")).Id!.Value;

        await Lists(shop).LinkAsync(listId, [(listingDualOnlySizeVaries, ColorPropertyId), (listingUniformId, ColorPropertyId)], "tophattwaffle");

        // Add "Emerald" to template
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Color List No Price Var", [.. list.Options, new(0, "Emerald", true)], "tophattwaffle");

        // Verify neither listing is returned by ListingsNeedingPricingAsync
        var needing = await Lists(shop).ListingsNeedingPricingAsync(listId);
        Assert.Empty(needing);

        // Apply template
        Assert.Equal(2, await Lists(shop).ApplyAsync(listId, "tophattwaffle", VariationPricingStrategy.TakeHighest));

        // Listing A (Size only varies): Emerald should inherit Small price ($10) and Large price ($20)
        var invA = await InventoryAsync(shop, listingDualOnlySizeVaries);
        Assert.Equal(10.00m, OfferingPriceOf(invA, "Emerald", "Small"));
        Assert.Equal(20.00m, OfferingPriceOf(invA, "Emerald", "Large"));
        Assert.Equal(10.00m, OfferingPriceOf(invA, "Red", "Small"));
        Assert.Equal(20.00m, OfferingPriceOf(invA, "Blue", "Large"));

        // Listing B (Uniform): Emerald should have 19.99m, price_on_property remains empty
        var invB = await InventoryAsync(shop, listingUniformId);
        Assert.Empty(invB["price_on_property"]!.AsArray());
        Assert.Equal(19.99m, OfferingPriceOf(invB, "Emerald"));
        Assert.Equal(19.99m, OfferingPriceOf(invB, "Red"));
        Assert.Equal(19.99m, OfferingPriceOf(invB, "Blue"));
    }

    [Fact]
    public async Task Extreme8_PickPerListing_MissingCustomPriceFallback_GracefullyUsesHighest()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listingId = await AddSingleVarListingWithPricesAsync(shop, 110, "Fallback Item",
            [("Red", 500), ("Blue", 1000), ("Green", 2500)]);

        var listId = (await Lists(shop).SaveAsync(null, "Fallback Palette",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "tophattwaffle")).Id!.Value;

        await Lists(shop).LinkAsync(listId, [(listingId, ColorPropertyId)], "tophattwaffle");

        // Add Yellow to template
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Fallback Palette", [.. list.Options, new(0, "Yellow", true)], "tophattwaffle");

        // Pass empty dictionary (or null) for PickPerListing
        var emptyPrices = new Dictionary<int, decimal>();
        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tophattwaffle", VariationPricingStrategy.PickPerListing, emptyPrices));

        var inv = await InventoryAsync(shop, listingId);
        // Falls back to highest price without crashing
        Assert.Equal(25.00m, OfferingPriceOf(inv, "Yellow"));
        Assert.Equal(5.00m, OfferingPriceOf(inv, "Red"));
        Assert.Equal(10.00m, OfferingPriceOf(inv, "Blue"));
        Assert.Equal(25.00m, OfferingPriceOf(inv, "Green"));
    }

    [Fact]
    public async Task Extreme9_DecimalPrecision_ExactCentPreservation()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // Existing options with odd cent amounts: $4.99, $12.49, $99.95
        var listingId = await AddSingleVarListingWithPricesAsync(shop, 111, "Precise Cents Item",
            [("Red", 499), ("Blue", 1249), ("Green", 9995)]);

        var listId = (await Lists(shop).SaveAsync(null, "Precise Palette",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "tophattwaffle")).Id!.Value;

        await Lists(shop).LinkAsync(listId, [(listingId, ColorPropertyId)], "tophattwaffle");

        // Add "Gold" with TakeLowest -> must be 4.99m
        var list = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Precise Palette", [.. list.Options, new(0, "Gold", true)], "tophattwaffle");

        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tophattwaffle", VariationPricingStrategy.TakeLowest));

        var inv = await InventoryAsync(shop, listingId);
        Assert.Equal(4.99m, OfferingPriceOf(inv, "Red"));
        Assert.Equal(12.49m, OfferingPriceOf(inv, "Blue"));
        Assert.Equal(99.95m, OfferingPriceOf(inv, "Green"));
        Assert.Equal(4.99m, OfferingPriceOf(inv, "Gold"));

        // Add "Diamond" with PickPerListing: $0.20 (minimum Etsy price)
        var list2 = (await Lists(shop).GetAsync(listId))!;
        await Lists(shop).SaveAsync(listId, "Precise Palette", [.. list2.Options, new(0, "Diamond", true)], "tophattwaffle");

        var custom = new Dictionary<int, decimal> { [listingId] = 0.20m };
        Assert.Equal(1, await Lists(shop).ApplyAsync(listId, "tophattwaffle", VariationPricingStrategy.PickPerListing, custom));

        var inv2 = await InventoryAsync(shop, listingId);
        Assert.Equal(4.99m, OfferingPriceOf(inv2, "Red"));
        Assert.Equal(12.49m, OfferingPriceOf(inv2, "Blue"));
        Assert.Equal(99.95m, OfferingPriceOf(inv2, "Green"));
        Assert.Equal(4.99m, OfferingPriceOf(inv2, "Gold"));
        Assert.Equal(0.20m, OfferingPriceOf(inv2, "Diamond"));
    }
}
