using System.IO;
using System.Reflection;
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
using MudBlazor;
using MudBlazor.Services;
using Xunit;

namespace EtsyShopManager.Tests;

/// <summary>
/// Empirical Challenger tests for Milestone 15:
/// Adversarially tests that offering combination rows in the inventory table remain 100% editable
/// when a variation is locked to a template (prices, SKUs, readiness state/processing profiles,
/// availability flags, and price/processing varies toggles).
/// </summary>
public class ChallengerM15LockedVariationInventoryTests
{
    private const long SizePropId = 100L;
    private const long ColorPropId = VariationEditor.CustomProperty1;

    private sealed class TestJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string uri) => Initialize("http://localhost/", uri);
    }

    private static JsonObject Pv(long id, string? name, string value, params long[] ids) => new()
    {
        ["property_id"] = id,
        ["property_name"] = name,
        ["scale_id"] = null,
        ["value_ids"] = new JsonArray([.. ids.Select(i => (JsonNode)i)]),
        ["values"] = new JsonArray(value),
    };

    private static JsonObject Product(string sku, long price, int quantity = 5, bool enabled = true, long? readinessId = null, params JsonObject[] pvs) => new()
    {
        ["sku"] = sku,
        ["property_values"] = new JsonArray(pvs),
        ["offerings"] = new JsonArray(new JsonObject
        {
            ["offering_id"] = null,
            ["price_amount"] = price,
            ["price_divisor"] = 100,
            ["quantity"] = quantity,
            ["is_enabled"] = enabled,
            ["readiness_state_id"] = readinessId is null ? null : (JsonNode)readinessId.Value,
        }),
    };

    private static JsonObject SingleVariationInventory() => VariationEditor.Canonical(new JsonObject
    {
        ["price_on_property"] = new JsonArray(ColorPropId),
        ["quantity_on_property"] = new JsonArray(),
        ["sku_on_property"] = new JsonArray(),
        ["readiness_state_on_property"] = new JsonArray(),
        ["products"] = new JsonArray(
            Product("SKU-RED", 1000, 5, true, null, Pv(ColorPropId, "Color", "Red", 1)),
            Product("SKU-GRN", 1200, 5, true, null, Pv(ColorPropId, "Color", "Green", 2)),
            Product("SKU-BLU", 1500, 5, true, null, Pv(ColorPropId, "Color", "Blue", 3))),
    });

    private static JsonObject TwoVariationInventory() => VariationEditor.Canonical(new JsonObject
    {
        ["price_on_property"] = new JsonArray(SizePropId),
        ["quantity_on_property"] = new JsonArray(),
        ["sku_on_property"] = new JsonArray(),
        ["readiness_state_on_property"] = new JsonArray(),
        ["products"] = new JsonArray(
            Product("SKU-S-RED", 1000, 5, true, null, Pv(SizePropId, "Size", "Small", 1), Pv(ColorPropId, "Color", "Red", 11)),
            Product("SKU-S-BLU", 1000, 5, true, null, Pv(SizePropId, "Size", "Small", 1), Pv(ColorPropId, "Color", "Blue", 12)),
            Product("SKU-L-RED", 2000, 5, true, null, Pv(SizePropId, "Size", "Large", 2), Pv(ColorPropId, "Color", "Red", 11)),
            Product("SKU-L-BLU", 2000, 5, true, null, Pv(SizePropId, "Size", "Large", 2), Pv(ColorPropId, "Color", "Blue", 12))),
    });

    private static EtsyInventory ToEtsyInventory(JsonObject localInv)
    {
        long prodId = 1000;
        long offId = 5000;
        return new EtsyInventory
        {
            PriceOnProperty = [.. (localInv["price_on_property"]?.AsArray() ?? []).Select(x => (long)x!)],
            QuantityOnProperty = [.. (localInv["quantity_on_property"]?.AsArray() ?? []).Select(x => (long)x!)],
            SkuOnProperty = [.. (localInv["sku_on_property"]?.AsArray() ?? []).Select(x => (long)x!)],
            ReadinessStateOnProperty = [.. (localInv["readiness_state_on_property"]?.AsArray() ?? []).Select(x => (long)x!)],
            Products = [.. (localInv["products"]?.AsArray() ?? []).Select(pNode =>
            {
                var p = pNode!.AsObject();
                return new EtsyProduct
                {
                    ProductId = ++prodId,
                    Sku = (string?)p["sku"] ?? "",
                    IsDeleted = false,
                    PropertyValues = [.. (p["property_values"]?.AsArray() ?? []).Select(vNode =>
                    {
                        var v = vNode!.AsObject();
                        return new EtsyPropertyValue
                        {
                            PropertyId = (long)v["property_id"]!,
                            PropertyName = (string?)v["property_name"],
                            ScaleId = (long?)v["scale_id"],
                            ValueIds = [.. (v["value_ids"]?.AsArray() ?? []).Select(x => (long)x!)],
                            Values = [.. (v["values"]?.AsArray() ?? []).Select(x => (string)x!)],
                        };
                    })],
                    Offerings = [.. (p["offerings"]?.AsArray() ?? []).Select(oNode =>
                    {
                        var o = oNode!.AsObject();
                        return new EtsyOffering
                        {
                            OfferingId = ++offId,
                            Price = new EtsyMoney
                            {
                                Amount = (long)o["price_amount"]!,
                                Divisor = (int?)o["price_divisor"] ?? 100,
                                CurrencyCode = "USD",
                            },
                            Quantity = (int?)o["quantity"] ?? 5,
                            IsEnabled = (bool?)o["is_enabled"] ?? true,
                            IsDeleted = false,
                            ReadinessStateId = (long?)o["readiness_state_id"],
                        };
                    })],
                };
            })],
        };
    }

    // =========================================================================
    // 1. Changing Offering Prices on Locked Variations
    // =========================================================================

    [Fact]
    public void LockedVariation_ChangeOfferingPrices_SingleVariation_UpdatesLocalJsonAndGeneratesValidPayload()
    {
        var localInv = SingleVariationInventory();
        var links = new[] { new VariationListLink(10, "Color Template", ColorPropId) };

        // Ensure Color is locked
        Assert.True(VariationEditor.IsLocked(ColorPropId, links));

        // 1. Change prices on individual offering combination rows: Red -> $10.50, Green -> $15.75, Blue -> $22.00
        var products = localInv["products"]!.AsArray();
        products[0]!["offerings"]![0]!["price_amount"] = 1050L;
        products[1]!["offerings"]![0]!["price_amount"] = 1575L;
        products[2]!["offerings"]![0]!["price_amount"] = 2200L;

        // Verify local JSON values
        Assert.Equal(1050L, (long)products[0]!["offerings"]![0]!["price_amount"]!);
        Assert.Equal(1575L, (long)products[1]!["offerings"]![0]!["price_amount"]!);
        Assert.Equal(2200L, (long)products[2]!["offerings"]![0]!["price_amount"]!);

        // 2. Generate update payload via InventoryWriter.FromLocal
        var etsyInv = ToEtsyInventory(SingleVariationInventory());
        var update = InventoryWriter.FromLocal(etsyInv, localInv);

        Assert.Contains(ColorPropId, update.PriceOnProperty);
        Assert.Equal(3, update.Products.Count);
        Assert.Equal(10.50m, update.Products[0].Offerings[0].Price);
        Assert.Equal(15.75m, update.Products[1].Offerings[0].Price);
        Assert.Equal(22.00m, update.Products[2].Offerings[0].Price);
    }

    [Fact]
    public void LockedVariation_ChangeOfferingPrices_DualVariation_MixedLockedAndUnlocked_PreservesAllPrices()
    {
        var localInv = TwoVariationInventory();
        // Color is locked to template, Size is unlocked
        var links = new[] { new VariationListLink(10, "Color Palette", ColorPropId) };
        Assert.True(VariationEditor.IsLocked(ColorPropId, links));
        Assert.False(VariationEditor.IsLocked(SizePropId, links));

        // Set price varying by both Size and Color
        localInv["price_on_property"] = new JsonArray(SizePropId, ColorPropId);

        // Update 4 combinations with distinct prices
        var products = localInv["products"]!.AsArray();
        // S-RED: $12.00
        products[0]!["offerings"]![0]!["price_amount"] = 1200L;
        // S-BLU: $14.50
        products[1]!["offerings"]![0]!["price_amount"] = 1450L;
        // L-RED: $24.00
        products[2]!["offerings"]![0]!["price_amount"] = 2400L;
        // L-BLU: $29.99
        products[3]!["offerings"]![0]!["price_amount"] = 2999L;

        var etsyInv = ToEtsyInventory(TwoVariationInventory());
        var update = InventoryWriter.FromLocal(etsyInv, localInv);

        Assert.Equal(2, update.PriceOnProperty.Count);
        Assert.Contains(SizePropId, update.PriceOnProperty);
        Assert.Contains(ColorPropId, update.PriceOnProperty);

        Assert.Equal(12.00m, update.Products[0].Offerings[0].Price);
        Assert.Equal(14.50m, update.Products[1].Offerings[0].Price);
        Assert.Equal(24.00m, update.Products[2].Offerings[0].Price);
        Assert.Equal(29.99m, update.Products[3].Offerings[0].Price);
    }

    [Fact]
    public void LockedVariation_ChangeOfferingPrices_BoundaryPrices_MinAndHighPrices_Succeeds()
    {
        var localInv = SingleVariationInventory();
        var links = new[] { new VariationListLink(10, "Template", ColorPropId) };
        Assert.True(VariationEditor.IsLocked(ColorPropId, links));

        var products = localInv["products"]!.AsArray();
        // Minimum allowable price in Etsy ($0.20)
        products[0]!["offerings"]![0]!["price_amount"] = 20L;
        // High price ($9999.99)
        products[1]!["offerings"]![0]!["price_amount"] = 999999L;

        var etsyInv = ToEtsyInventory(SingleVariationInventory());
        var update = InventoryWriter.FromLocal(etsyInv, localInv);

        Assert.Equal(0.20m, update.Products[0].Offerings[0].Price);
        Assert.Equal(9999.99m, update.Products[1].Offerings[0].Price);
    }

    // =========================================================================
    // 2. Changing Offering SKUs on Locked Variations
    // =========================================================================

    [Fact]
    public void LockedVariation_ChangeOfferingSkus_DirectEditAndSkuPattern_GeneratesValidPayloadWithNewSkus()
    {
        var localInv = SingleVariationInventory();
        var links = new[] { new VariationListLink(10, "Template", ColorPropId) };
        Assert.True(VariationEditor.IsLocked(ColorPropId, links));

        // 1. Direct SKU assignment on combination rows
        var products = localInv["products"]!.AsArray();
        products[0]!["sku"] = "DIRECT-SKU-CRIMSON";
        products[1]!["sku"] = "DIRECT-SKU-EMERALD";
        products[2]!["sku"] = "DIRECT-SKU-SAPPHIRE";

        var etsyInv = ToEtsyInventory(SingleVariationInventory());
        var update = InventoryWriter.FromLocal(etsyInv, localInv);

        Assert.Equal("DIRECT-SKU-CRIMSON", update.Products[0].Sku);
        Assert.Equal("DIRECT-SKU-EMERALD", update.Products[1].Sku);
        Assert.Equal("DIRECT-SKU-SAPPHIRE", update.Products[2].Sku);

        // 2. Applying SKU Pattern across locked variation combinations
        var pattern = "{listing.sku}-{color}";
        var patternedInv = SkuPatternResolver.ApplyPattern((JsonObject)localInv.DeepClone(), pattern, "BASE-TSHIRT");

        Assert.Equal(pattern, (string?)patternedInv["sku_pattern"]);
        var patternedProducts = patternedInv["products"]!.AsArray();
        Assert.Equal("BASE-TSHIRT-Red", (string?)patternedProducts[0]!["sku"]);
        Assert.Equal("BASE-TSHIRT-Green", (string?)patternedProducts[1]!["sku"]);
        Assert.Equal("BASE-TSHIRT-Blue", (string?)patternedProducts[2]!["sku"]);

        var patternedUpdate = InventoryWriter.FromLocal(etsyInv, patternedInv);
        Assert.Equal("BASE-TSHIRT-Red", patternedUpdate.Products[0].Sku);
        Assert.Equal("BASE-TSHIRT-Green", patternedUpdate.Products[1].Sku);
        Assert.Equal("BASE-TSHIRT-Blue", patternedUpdate.Products[2].Sku);
    }

    [Fact]
    public void LockedVariation_ChangeOfferingSkus_DualVariation_SkuPattern_GeneratesAllGridSkus()
    {
        var localInv = TwoVariationInventory();
        var links = new[] { new VariationListLink(10, "Color Palette", ColorPropId) };
        Assert.True(VariationEditor.IsLocked(ColorPropId, links));

        var pattern = "{sku}_{size}_{color}";
        var patternedInv = SkuPatternResolver.ApplyPattern((JsonObject)localInv.DeepClone(), pattern, "TEE");

        var pArray = patternedInv["products"]!.AsArray();
        Assert.Equal("TEE_Small_Red", (string?)pArray[0]!["sku"]);
        Assert.Equal("TEE_Small_Blue", (string?)pArray[1]!["sku"]);
        Assert.Equal("TEE_Large_Red", (string?)pArray[2]!["sku"]);
        Assert.Equal("TEE_Large_Blue", (string?)pArray[3]!["sku"]);

        var etsyInv = ToEtsyInventory(TwoVariationInventory());
        var update = InventoryWriter.FromLocal(etsyInv, patternedInv);
        Assert.Equal("TEE_Small_Red", update.Products[0].Sku);
        Assert.Equal("TEE_Small_Blue", update.Products[1].Sku);
        Assert.Equal("TEE_Large_Red", update.Products[2].Sku);
        Assert.Equal("TEE_Large_Blue", update.Products[3].Sku);
    }

    // =========================================================================
    // 3. Changing Offering Processing Profiles (Readiness States)
    // =========================================================================

    [Fact]
    public void LockedVariation_ChangeOfferingProcessingProfiles_UpdatesReadinessStateIdsInPayload()
    {
        var localInv = SingleVariationInventory();
        var links = new[] { new VariationListLink(10, "Template", ColorPropId) };
        Assert.True(VariationEditor.IsLocked(ColorPropId, links));

        // Enable readiness_state_on_property for Color
        localInv["readiness_state_on_property"] = new JsonArray(ColorPropId);

        var products = localInv["products"]!.AsArray();
        // Red: Profile 601 (Ready to ship)
        products[0]!["offerings"]![0]!["readiness_state_id"] = 601L;
        // Green: Profile 602 (Made to order)
        products[1]!["offerings"]![0]!["readiness_state_id"] = 602L;
        // Blue: null (no profile)
        products[2]!["offerings"]![0]!["readiness_state_id"] = null;

        var etsyInv = ToEtsyInventory(SingleVariationInventory());
        var update = InventoryWriter.FromLocal(etsyInv, localInv);

        Assert.Contains(ColorPropId, update.ReadinessStateOnProperty);
        Assert.Equal(601L, update.Products[0].Offerings[0].ReadinessStateId);
        Assert.Equal(602L, update.Products[1].Offerings[0].ReadinessStateId);
        Assert.Null(update.Products[2].Offerings[0].ReadinessStateId);
    }

    // =========================================================================
    // 4. Toggling Offering Availability (is_available / is_enabled)
    // =========================================================================

    [Fact]
    public void LockedVariation_ToggleOfferingAvailability_SetAvailableAndSetValueAvailable_ReflectedInPayload()
    {
        var localInv = SingleVariationInventory();
        var links = new[] { new VariationListLink(10, "Template", ColorPropId) };
        Assert.True(VariationEditor.IsLocked(ColorPropId, links));

        // 1. Toggle single combination availability: Disable Green (index 1)
        var withDisabledGreen = VariationEditor.SetAvailable(localInv, 1, false);
        var pArray = withDisabledGreen["products"]!.AsArray();
        Assert.True((bool)pArray[0]!["offerings"]![0]!["is_enabled"]!);
        Assert.False((bool)pArray[1]!["offerings"]![0]!["is_enabled"]!);
        Assert.True((bool)pArray[2]!["offerings"]![0]!["is_enabled"]!);

        var etsyInv = ToEtsyInventory(SingleVariationInventory());
        var update1 = InventoryWriter.FromLocal(etsyInv, withDisabledGreen);
        Assert.True(update1.Products[0].Offerings[0].IsEnabled);
        Assert.False(update1.Products[1].Offerings[0].IsEnabled);
        Assert.True(update1.Products[2].Offerings[0].IsEnabled);

        // 2. Re-enable Green (index 1)
        var withReenabledGreen = VariationEditor.SetAvailable(withDisabledGreen, 1, true);
        var pArrayReenabled = withReenabledGreen["products"]!.AsArray();
        Assert.True((bool)pArrayReenabled[1]!["offerings"]![0]!["is_enabled"]!);

        var updateReenabled = InventoryWriter.FromLocal(etsyInv, withReenabledGreen);
        Assert.True(updateReenabled.Products[1].Offerings[0].IsEnabled);

        // 3. SetValueAvailable on locked variation (e.g. filament color retired)
        var twoVar = TwoVariationInventory();
        var withDisabledRedValue = VariationEditor.SetValueAvailable(twoVar, ColorPropId, "Red", false);

        // In TwoVariationInventory: S-RED (idx 0) and L-RED (idx 2) have Red
        var twoVarProducts = withDisabledRedValue["products"]!.AsArray();
        Assert.False((bool)twoVarProducts[0]!["offerings"]![0]!["is_enabled"]!); // S-RED
        Assert.True((bool)twoVarProducts[1]!["offerings"]![0]!["is_enabled"]!);  // S-BLU
        Assert.False((bool)twoVarProducts[2]!["offerings"]![0]!["is_enabled"]!); // L-RED
        Assert.True((bool)twoVarProducts[3]!["offerings"]![0]!["is_enabled"]!);  // L-BLU

        Assert.False(VariationEditor.IsValueAvailable(withDisabledRedValue, ColorPropId, "Red"));
        Assert.True(VariationEditor.IsValueAvailable(withDisabledRedValue, ColorPropId, "Blue"));

        var etsyTwoVar = ToEtsyInventory(TwoVariationInventory());
        var updateTwoVar = InventoryWriter.FromLocal(etsyTwoVar, withDisabledRedValue);
        Assert.False(updateTwoVar.Products[0].Offerings[0].IsEnabled);
        Assert.True(updateTwoVar.Products[1].Offerings[0].IsEnabled);
        Assert.False(updateTwoVar.Products[2].Offerings[0].IsEnabled);
        Assert.True(updateTwoVar.Products[3].Offerings[0].IsEnabled);
    }

    // =========================================================================
    // 5. Toggling Price Varies and Processing Varies on Locked Variation
    // =========================================================================

    [Fact]
    public void LockedVariation_TogglePriceVaries_CollapsesAndRestoresPricesCleanly()
    {
        var localInv = SingleVariationInventory();
        var links = new[] { new VariationListLink(10, "Template", ColorPropId) };
        Assert.True(VariationEditor.IsLocked(ColorPropId, links));

        // Start with distinct prices (10.00, 12.00, 15.00) and PriceOnProperty = [ColorPropId]
        Assert.Contains((localInv["price_on_property"] as JsonArray)!, x => (long)x! == ColorPropId);

        // 1. Turn Price Varies OFF on the locked variation
        var priceFixedInv = VariationEditor.SetVaries(localInv, VariationEditor.PriceOnProperty, ColorPropId, false);

        // PriceOnProperty must now be empty
        Assert.DoesNotContain((priceFixedInv["price_on_property"] as JsonArray)!, x => (long)x! == ColorPropId);
        Assert.Empty((priceFixedInv["price_on_property"] as JsonArray)!);

        // All products must collapse to the leading offering price (1000L / $10.00)
        var fixedProducts = priceFixedInv["products"]!.AsArray();
        Assert.All(fixedProducts, p => Assert.Equal(1000L, (long)p!["offerings"]![0]!["price_amount"]!));

        var etsyInv = ToEtsyInventory(SingleVariationInventory());
        var updateFixed = InventoryWriter.FromLocal(etsyInv, priceFixedInv);
        Assert.Empty(updateFixed.PriceOnProperty);
        Assert.All(updateFixed.Products, p => Assert.Equal(10.00m, p.Offerings[0].Price));

        // 2. Turn Price Varies back ON on the locked variation
        var priceVariesInv = VariationEditor.SetVaries(priceFixedInv, VariationEditor.PriceOnProperty, ColorPropId, true);
        Assert.Contains((priceVariesInv["price_on_property"] as JsonArray)!, x => (long)x! == ColorPropId);

        var updateVaries = InventoryWriter.FromLocal(etsyInv, priceVariesInv);
        Assert.Contains(ColorPropId, updateVaries.PriceOnProperty);
    }

    [Fact]
    public void LockedVariation_ToggleProcessingVaries_CollapsesAndRestoresReadinessProfilesCleanly()
    {
        var localInv = SingleVariationInventory();
        var links = new[] { new VariationListLink(10, "Template", ColorPropId) };
        Assert.True(VariationEditor.IsLocked(ColorPropId, links));

        // Set distinct processing profiles with Processing Varies ON
        var procVariesInv = VariationEditor.SetVaries(localInv, VariationEditor.ProcessingOnProperty, ColorPropId, true);
        var products = procVariesInv["products"]!.AsArray();
        products[0]!["offerings"]![0]!["readiness_state_id"] = 601L;
        products[1]!["offerings"]![0]!["readiness_state_id"] = 602L;
        products[2]!["offerings"]![0]!["readiness_state_id"] = 603L;

        Assert.Contains((procVariesInv["readiness_state_on_property"] as JsonArray)!, x => (long)x! == ColorPropId);

        // 1. Turn Processing Varies OFF on the locked variation
        var procFixedInv = VariationEditor.SetVaries(procVariesInv, VariationEditor.ProcessingOnProperty, ColorPropId, false);

        Assert.Empty((procFixedInv["readiness_state_on_property"] as JsonArray)!);
        // All products must collapse to the leading offering readiness state (601L)
        var fixedProducts = procFixedInv["products"]!.AsArray();
        Assert.All(fixedProducts, p => Assert.Equal(601L, (long)p!["offerings"]![0]!["readiness_state_id"]!));

        var etsyInv = ToEtsyInventory(SingleVariationInventory());
        var updateFixed = InventoryWriter.FromLocal(etsyInv, procFixedInv);
        Assert.Empty(updateFixed.ReadinessStateOnProperty);
        Assert.All(updateFixed.Products, p => Assert.Equal(601L, p.Offerings[0].ReadinessStateId));

        // 2. Turn Processing Varies back ON
        var procReVariesInv = VariationEditor.SetVaries(procFixedInv, VariationEditor.ProcessingOnProperty, ColorPropId, true);
        Assert.Contains((procReVariesInv["readiness_state_on_property"] as JsonArray)!, x => (long)x! == ColorPropId);

        var updateReVaries = InventoryWriter.FromLocal(etsyInv, procReVariesInv);
        Assert.Contains(ColorPropId, updateReVaries.ReadinessStateOnProperty);
    }

    // =========================================================================
    // 6. Adversarial Boundary: All Offerings Disabled Preview Blocker
    // =========================================================================

    [Fact]
    public async Task LockedVariation_OfferingsAllDisabled_BlocksPush_ReenablingAllowsPush()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listing = FakeEtsyShop.Listing(300, "All Offerings Disabled Test Listing");
        long id = 500;
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

        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(MakeProduct("Red"), MakeProduct("Blue")),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        var dbListing = await db.Listings.SingleAsync(l => l.EtsyListingId == 300);

        // Link Color variation to template
        var lists = shop.Provider.GetRequiredService<VariationListService>();
        var listId = (await lists.SaveAsync(null, "Template Colors", [new(0, "Red", true), new(0, "Blue", true)], "tophattwaffle")).Id!.Value;
        await lists.LinkVariationAsync(listId, dbListing.Id, ColorPropId, "tophattwaffle");

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var snapshot = (await editor.LoadAsync(dbListing.Id))!;
        var localInv = snapshot.Model.Local(ListingFields.Inventory)!.DeepClone().AsObject();

        // 1. Disable ALL offerings
        localInv = VariationEditor.SetAvailable(localInv, 0, false);
        localInv = VariationEditor.SetAvailable(localInv, 1, false);

        await editor.SaveAsync(dbListing.Id, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Inventory] = localInv
        }, "tester");

        var preview = await editor.PreviewAsync(dbListing.Id);
        Assert.False(preview.CanPush);
        Assert.Contains(preview.Blockers, b => b.Contains("available to buyers", StringComparison.OrdinalIgnoreCase));

        // 2. Re-enable at least one offering (Red)
        localInv = VariationEditor.SetAvailable(localInv, 0, true);
        await editor.SaveAsync(dbListing.Id, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Inventory] = localInv
        }, "tester");

        var previewAfter = await editor.PreviewAsync(dbListing.Id);
        Assert.True(previewAfter.CanPush, string.Join("; ", previewAfter.Blockers));
        Assert.DoesNotContain(previewAfter.Blockers, b => b.Contains("available to buyers", StringComparison.OrdinalIgnoreCase));
    }

    // =========================================================================
    // 7. Structural Modifications Strictly Blocked While Offering Edits Allowed
    // =========================================================================

    [Fact]
    public void LockedVariation_StructuralMutationsRemainStrictlyBlockedWhileOfferingEditsSucceed()
    {
        var localInv = SingleVariationInventory();
        var links = new[] { new VariationListLink(10, "Template", ColorPropId) };
        long[] lockedIds = [ColorPropId];

        // 1. All structural changes throw InvalidOperationException
        Assert.Throws<InvalidOperationException>(() => VariationEditor.AddValue(localInv, ColorPropId, "Purple", lockedPropertyIds: lockedIds));
        Assert.Throws<InvalidOperationException>(() => VariationEditor.RemoveValue(localInv, ColorPropId, "Red", lockedPropertyIds: lockedIds));
        Assert.Throws<InvalidOperationException>(() => VariationEditor.RenameValue(localInv, ColorPropId, "Red", "Crimson", lockedPropertyIds: lockedIds));
        Assert.Throws<InvalidOperationException>(() => VariationEditor.RemoveVariation(localInv, ColorPropId, lockedPropertyIds: lockedIds));
        Assert.Throws<InvalidOperationException>(() => VariationEditor.ReorderValues(localInv, ColorPropId, ["Blue", "Green", "Red"], lockedPropertyIds: lockedIds));
        Assert.Throws<InvalidOperationException>(() => VariationEditor.RenameVariation(localInv, ColorPropId, "Shade", lockedPropertyIds: lockedIds));

        // 2. On the EXACT same inventory, all offering updates succeed without throwing
        var editedPrices = (JsonObject)localInv.DeepClone();
        editedPrices["products"]![0]!["offerings"]![0]!["price_amount"] = 3300L;
        Assert.Equal(3300L, (long)editedPrices["products"]![0]!["offerings"]![0]!["price_amount"]!);

        var editedSkus = SkuPatternResolver.ApplyPattern((JsonObject)localInv.DeepClone(), "{listing.sku}-{color}", "SKU123");
        Assert.NotNull(editedSkus);

        var toggledAvail = VariationEditor.SetAvailable(localInv, 0, false);
        Assert.False((bool)toggledAvail["products"]![0]!["offerings"]![0]!["is_enabled"]!);

        var toggledPriceVaries = VariationEditor.SetVaries(localInv, VariationEditor.PriceOnProperty, ColorPropId, false);
        Assert.NotNull(toggledPriceVaries);

        var toggledProcVaries = VariationEditor.SetVaries(localInv, VariationEditor.ProcessingOnProperty, ColorPropId, true);
        Assert.NotNull(toggledProcVaries);
    }

    // =========================================================================
    // 8. End-to-End Push with FakeEtsyShop
    // =========================================================================

    [Fact]
    public async Task LockedVariation_EndToEndPush_WithEditedPricesSkusProcessingAndAvailability_SucceedsAgainstFakeEtsyShop()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listing = FakeEtsyShop.Listing(500, "Full End-To-End Push Listing");
        long id = 1000;
        JsonObject MakeProduct(string color, long price)
        {
            var p = FakeEtsyShop.Product(++id, $"OLD-SKU-{color}", "OneSize", price, 5);
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

        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(ColorPropId),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["readiness_state_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                MakeProduct("Red", 1000),
                MakeProduct("Green", 1200),
                MakeProduct("Blue", 1500)),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        var dbListing = await db.Listings.SingleAsync(l => l.EtsyListingId == 500);

        // Link Color variation to template
        var lists = shop.Provider.GetRequiredService<VariationListService>();
        var listId = (await lists.SaveAsync(null, "E2E Template Colors", [
            new(0, "Red", true),
            new(0, "Green", true),
            new(0, "Blue", true)
        ], "tophattwaffle")).Id!.Value;
        await lists.LinkVariationAsync(listId, dbListing.Id, ColorPropId, "tophattwaffle");

        // Load editor snapshot and modify draft inventory
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var snapshot = (await editor.LoadAsync(dbListing.Id))!;
        var localInv = snapshot.Model.Local(ListingFields.Inventory)!.DeepClone().AsObject();

        // 1. Change offering prices: Red -> $18.50, Green -> $24.00, Blue -> $31.99
        var prods = localInv["products"]!.AsArray();
        prods[0]!["offerings"]![0]!["price_amount"] = 1850L;
        prods[1]!["offerings"]![0]!["price_amount"] = 2400L;
        prods[2]!["offerings"]![0]!["price_amount"] = 3199L;

        // 2. Change offering SKUs via pattern
        localInv = SkuPatternResolver.ApplyPattern(localInv, "ART-{color}", "ART");

        // 3. Set Processing Varies ON and set readiness state IDs
        localInv = VariationEditor.SetVaries(localInv, VariationEditor.ProcessingOnProperty, ColorPropId, true);
        var prodsAfterProc = localInv["products"]!.AsArray();
        prodsAfterProc[0]!["offerings"]![0]!["readiness_state_id"] = 601L; // Red
        prodsAfterProc[1]!["offerings"]![0]!["readiness_state_id"] = 601L; // Green
        prodsAfterProc[2]!["offerings"]![0]!["readiness_state_id"] = null; // Blue

        // 4. Toggle availability: Blue is disabled (is_enabled = false)
        localInv = VariationEditor.SetAvailable(localInv, 2, false);

        // Save draft edits
        await editor.SaveAsync(dbListing.Id, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Inventory] = localInv
        }, "tophattwaffle");

        // Verify preview allows push
        var preview = await editor.PreviewAsync(dbListing.Id);
        Assert.True(preview.CanPush, string.Join("; ", preview.Blockers));
        Assert.Contains(preview.Changes, c => c.Key == ListingFields.Inventory);

        // Request push and execute job
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

        // Intercept recorded PUT /v3/application/listings/500/inventory
        var invWrite = Assert.Single(shop.Writes, w => w.Method == "PUT" && w.Path.EndsWith("/inventory"));
        var body = JsonNode.Parse(invWrite.Body)!;

        // Assert price_on_property and readiness_state_on_property
        var pop = body["price_on_property"]!.AsArray().Select(x => (long)x!).ToArray();
        Assert.Contains(ColorPropId, pop);

        var rop = body["readiness_state_on_property"]!.AsArray().Select(x => (long)x!).ToArray();
        Assert.Contains(ColorPropId, rop);

        var putProducts = body["products"]!.AsArray();
        Assert.Equal(3, putProducts.Count);

        // Verify Red
        Assert.Equal("ART-Red", (string?)putProducts[0]!["sku"]);
        Assert.Equal(18.50m, (decimal)putProducts[0]!["offerings"]![0]!["price"]!);
        Assert.True((bool)putProducts[0]!["offerings"]![0]!["is_enabled"]!);
        Assert.Equal(601L, (long)putProducts[0]!["offerings"]![0]!["readiness_state_id"]!);

        // Verify Green
        Assert.Equal("ART-Green", (string?)putProducts[1]!["sku"]);
        Assert.Equal(24.00m, (decimal)putProducts[1]!["offerings"]![0]!["price"]!);
        Assert.True((bool)putProducts[1]!["offerings"]![0]!["is_enabled"]!);
        Assert.Equal(601L, (long)putProducts[1]!["offerings"]![0]!["readiness_state_id"]!);

        // Verify Blue (disabled)
        Assert.Equal("ART-Blue", (string?)putProducts[2]!["sku"]);
        Assert.Equal(31.99m, (decimal)putProducts[2]!["offerings"]![0]!["price"]!);
        Assert.False((bool)putProducts[2]!["offerings"]![0]!["is_enabled"]!);
        Assert.Null((long?)putProducts[2]!["offerings"]![0]!["readiness_state_id"]);

        // Verify reconciliation: draft cleared, no drift
        var reloadedListing = await db.Listings.Include(l => l.EtsyState).SingleAsync(l => l.EtsyListingId == 500);
        Assert.False(reloadedListing.HasDraft, "Draft must be reconciled cleanly after successful push.");
        var editModel = ListingEditModel.Parse(reloadedListing.EtsyState!.ManagedJson, reloadedListing.EtsyState.BaseJson, reloadedListing.DraftJson);
        Assert.DoesNotContain(ListingFields.Inventory, editModel.DriftFields);
    }

    // =========================================================================
    // 9. UI Component Tests: VariationsEditor.razor
    // =========================================================================


    [Fact]
    public async Task VariationsEditor_UI_VariesCheckboxes_RemainEnabled_WhenVariationLocked()
    {
        var inv = SingleVariationInventory();
        var links = new[] { new VariationListLink(10, "Template Colors", ColorPropId) };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, TestJsRuntime>();
        services.AddSingleton<NavigationManager>(new TestNavigation("http://localhost/"));
        services.AddMudServices();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Inventory"] = inv,
                ["Links"] = links,
            });
            var comp = await renderer.RenderComponentAsync<VariationsEditor>(parameters);
            return comp.ToHtmlString();
        });

        // Verify Price varies and Processing varies checkboxes exist and are NOT disabled
        Assert.Contains("Price varies", html);
        Assert.Contains("Processing varies", html);
        Assert.DoesNotMatch(@"<input[^>]*disabled[^>]*aria-label=""Price varies""", html);
        Assert.DoesNotMatch(@"<input[^>]*disabled[^>]*aria-label=""Processing varies""", html);

        // Verify that toggling them via VariationEditor.SetVaries produces the expected inventory
        var toggledPrice = VariationEditor.SetVaries(inv, VariationEditor.PriceOnProperty, ColorPropId, false);
        Assert.Empty((toggledPrice["price_on_property"] as JsonArray)!);

        var toggledProc = VariationEditor.SetVaries(inv, VariationEditor.ProcessingOnProperty, ColorPropId, true);
        Assert.Contains((toggledProc["readiness_state_on_property"] as JsonArray)!, x => (long)x! == ColorPropId);
    }

    [Fact]
    public async Task VariationsEditor_UI_SkuPatternSection_RemainsEnabled_WhenVariationLocked()
    {
        var inv = SingleVariationInventory();
        var links = new[] { new VariationListLink(10, "Template Colors", ColorPropId) };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, TestJsRuntime>();
        services.AddSingleton<NavigationManager>(new TestNavigation("http://localhost/"));
        services.AddMudServices();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Inventory"] = inv,
                ["Links"] = links,
                ["SkuPattern"] = "{sku}-{color}",
                ["BaseSku"] = "TSHIRT",
            });
            var comp = await renderer.RenderComponentAsync<VariationsEditor>(parameters);
            return comp.ToHtmlString();
        });

        // 1. Sku pattern section rendered
        Assert.Contains("SKU pattern", html);
        Assert.Contains("Apply Pattern to SKUs", html);
        Assert.Contains("Preview: TSHIRT-Red", html);

        // 2. The Apply Pattern button must NOT be disabled when pattern is present
        var btnIdx = html.IndexOf("Apply Pattern to SKUs");
        var openBtn = html.LastIndexOf("<button", btnIdx);
        var closeBtn = html.IndexOf("</button>", btnIdx);
        var buttonHtml = html.Substring(openBtn, closeBtn - openBtn + 9);
        Assert.DoesNotContain("disabled", buttonHtml);

        // 3. Applying the pattern generates valid combination SKUs
        var updated = SkuPatternResolver.ApplyPattern((JsonObject)inv.DeepClone(), "{sku}-{color}", "TSHIRT");
        var products = updated["products"]!.AsArray();
        Assert.Equal("TSHIRT-Red", (string?)products[0]!["sku"]);
        Assert.Equal("TSHIRT-Green", (string?)products[1]!["sku"]);
        Assert.Equal("TSHIRT-Blue", (string?)products[2]!["sku"]);
    }
}
