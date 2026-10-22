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
using MudBlazor.Services;
using Xunit;

namespace EtsyShopManager.Tests;

public class ChallengerM12VariationOrderTests
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

    private static JsonObject Product(string sku, long price, int quantity = 5, bool enabled = true, params JsonObject[] pvs) => new()
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
            ["readiness_state_id"] = null,
        }),
    };

    private static JsonObject TwoVariationInventory() => VariationEditor.Canonical(new JsonObject
    {
        ["price_on_property"] = new JsonArray(SizePropId),
        ["quantity_on_property"] = new JsonArray(),
        ["sku_on_property"] = new JsonArray(SizePropId),
        ["readiness_state_on_property"] = new JsonArray(),
        ["products"] = new JsonArray(
            Product("SKU-S-RED", 1000, 10, true, Pv(SizePropId, "Size", "Small", 1), Pv(ColorPropId, "Color", "Red", 11)),
            Product("SKU-S-GRN", 1000, 11, true, Pv(SizePropId, "Size", "Small", 1), Pv(ColorPropId, "Color", "Green", 12)),
            Product("SKU-S-BLU", 1000, 12, true, Pv(SizePropId, "Size", "Small", 1), Pv(ColorPropId, "Color", "Blue", 13)),
            Product("SKU-M-RED", 1500, 20, true, Pv(SizePropId, "Size", "Medium", 2), Pv(ColorPropId, "Color", "Red", 11)),
            Product("SKU-M-GRN", 1500, 21, true, Pv(SizePropId, "Size", "Medium", 2), Pv(ColorPropId, "Color", "Green", 12)),
            Product("SKU-M-BLU", 1500, 22, true, Pv(SizePropId, "Size", "Medium", 2), Pv(ColorPropId, "Color", "Blue", 13)),
            Product("SKU-L-RED", 2000, 30, true, Pv(SizePropId, "Size", "Large", 3), Pv(ColorPropId, "Color", "Red", 11)),
            Product("SKU-L-GRN", 2000, 31, true, Pv(SizePropId, "Size", "Large", 3), Pv(ColorPropId, "Color", "Green", 12)),
            Product("SKU-L-BLU", 2000, 32, true, Pv(SizePropId, "Size", "Large", 3), Pv(ColorPropId, "Color", "Blue", 13))),
    });

    // =========================================================================
    // Area 1: Ordering Edge Cases
    // =========================================================================

    [Fact]
    public void OrderValuesByTemplate_TemplateContainsDuplicatesAndCasing_DoesNotDuplicateAndPreservesListingCasing()
    {
        // Template contains duplicated names with inconsistent casing, plus unused options
        var templateOptions = new[] { "red", "BLUE", "green", "Red", "Blue", "BLUE", "yellow" };
        var listingValues = new[] { "Green", "Blue", "Red", "CustomPurple" };

        var ordered = VariationEditor.OrderValuesByTemplate(listingValues, templateOptions);

        // Expected:
        // Matches in template order: "red" -> "Red", "BLUE" -> "Blue", "green" -> "Green"
        // Duplicates in template ("Red", "Blue", "BLUE") must NOT duplicate entries
        // Non-existent template option ("yellow") ignored
        // Extras ("CustomPurple") appended in original relative order
        Assert.Equal(["Red", "Blue", "Green", "CustomPurple"], ordered);
        Assert.Equal(listingValues.Length, ordered.Count);
        Assert.Equal(ordered.Distinct(StringComparer.OrdinalIgnoreCase).Count(), ordered.Count);
    }

    [Fact]
    public void OrderValuesByTemplate_ListingHasOnlyCustomOptions_PreservesOriginalOrderAndExtras()
    {
        var templateOptions = new[] { "OptionX", "OptionY", "OptionZ" };
        var listingValues = new[] { "CustomA", "CustomB", "CustomC" };

        var ordered = VariationEditor.OrderValuesByTemplate(listingValues, templateOptions);

        Assert.Equal(["CustomA", "CustomB", "CustomC"], ordered);
    }

    [Fact]
    public void OrderValuesByTemplate_EmptyInputs_ReturnsGracefully()
    {
        // Empty template
        var emptyTemplateResult = VariationEditor.OrderValuesByTemplate(["Small", "Medium", "Large"], []);
        Assert.Equal(["Small", "Medium", "Large"], emptyTemplateResult);

        // Empty listing
        var emptyListingResult = VariationEditor.OrderValuesByTemplate([], ["Small", "Medium"]);
        Assert.Empty(emptyListingResult);

        // Both empty
        var bothEmptyResult = VariationEditor.OrderValuesByTemplate([], []);
        Assert.Empty(bothEmptyResult);
    }

    [Fact]
    public void OrderValuesByTemplate_SingleOptionScenarios_OrdersCleanly()
    {
        // Single option in template, multiple on listing
        var t1 = VariationEditor.OrderValuesByTemplate(["Bronze", "Silver", "Gold", "Platinum"], ["Gold"]);
        Assert.Equal(["Gold", "Bronze", "Silver", "Platinum"], t1);

        // Multiple options in template, single on listing
        var t2 = VariationEditor.OrderValuesByTemplate(["Large"], ["Small", "Medium", "Large", "XL"]);
        Assert.Equal(["Large"], t2);

        // Single option on both, matching case-insensitively
        var t3 = VariationEditor.OrderValuesByTemplate(["solo-option"], ["SOLO-OPTION"]);
        Assert.Equal(["solo-option"], t3);
    }

    [Fact]
    public void ReorderValues_SingleOptionVariation_PreservesGridAndIntegrity()
    {
        var inv = VariationEditor.Canonical(new JsonObject
        {
            ["price_on_property"] = new JsonArray(SizePropId),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["readiness_state_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                Product("SKU-ONESIZE", 1000, 5, true, Pv(SizePropId, "Size", "OneSize", 1))),
        });

        var reordered = VariationEditor.ReorderValues(inv, SizePropId, ["OneSize"]);

        Assert.NotNull(reordered);
        var vo = VariationEditor.GetValueOrder(reordered, SizePropId);
        Assert.Equal(["OneSize"], vo);
        var products = reordered["products"]!.AsArray();
        Assert.Single(products);
    }

    // =========================================================================
    // Area 2: Multiple Variations on One Listing (Locked vs Unlocked)
    // =========================================================================

    [Fact]
    public async Task MultipleVariations_UI_Variation1LockedHidesButtons_Variation2UnlockedShowsButtons()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, TestJsRuntime>();
        services.AddSingleton<NavigationManager>(new TestNavigation("http://localhost/"));
        services.AddMudServices();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var inv = TwoVariationInventory();

        // Size is linked to "Apparel Sizes", Color is UNLINKED
        var links = new List<VariationListLink>
        {
            new(ListId: 42, ListName: "Apparel Sizes", PropertyId: SizePropId)
        };

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Inventory"] = inv,
                ["Links"] = (IReadOnlyList<VariationListLink>)links
            });
            var output = await renderer.RenderComponentAsync<VariationsEditor>(parameters);
            return output.ToHtmlString();
        });

        // 1. Size variation (PropertyId 100, linked/locked):
        // Move buttons MUST be hidden
        Assert.DoesNotContain("Move Small earlier", html);
        Assert.DoesNotContain("Move Small later", html);
        Assert.DoesNotContain("Move Medium earlier", html);
        Assert.DoesNotContain("Move Medium later", html);
        Assert.DoesNotContain("Move Large earlier", html);
        Assert.DoesNotContain("Move Large later", html);

        // Indicator MUST be displayed for Size
        Assert.Contains("Value order is controlled by template Apparel Sizes.", html);

        // 2. Color variation (PropertyId 513, unlinked/unlocked):
        // Move buttons MUST be present
        Assert.Contains("Move Green earlier", html);
        Assert.Contains("Move Green later", html);
        Assert.Contains("Move Red later", html);
        Assert.Contains("Move Blue earlier", html);
    }

    [Fact]
    public async Task MultipleVariations_MoveValueMethod_EnforcesLockOnVariation1WhileVariation2CanMove()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, TestJsRuntime>();
        services.AddSingleton<NavigationManager>(new TestNavigation("http://localhost/"));
        services.AddMudServices();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var inv = TwoVariationInventory();
        var links = new List<VariationListLink>
        {
            new(ListId: 42, ListName: "Apparel Sizes", PropertyId: SizePropId)
        };

        JsonObject? updatedInventory = null;

#pragma warning disable BL0005
        var editor = new VariationsEditor
        {
            Inventory = inv,
            Links = links,
            InventoryChanged = EventCallback.Factory.Create<JsonObject>(this, (JsonObject i) => updatedInventory = i)
        };
#pragma warning restore BL0005

        var onParametersSet = typeof(VariationsEditor).GetMethod("OnParametersSet", BindingFlags.Instance | BindingFlags.NonPublic);
        onParametersSet?.Invoke(editor, null);

        // Attempt to invoke MoveValue on Size (PropertyId 100, locked) via reflection
        var moveValueMethod = typeof(VariationsEditor).GetMethod("MoveValue", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(moveValueMethod);

        // Try to move Small (index 0) later (+1) on SizePropId
        var taskLocked = (Task)moveValueMethod.Invoke(editor, [SizePropId, 0, 1])!;
        await taskLocked;

        // EventCallback must NOT have been called because IsOrderLocked returned true
        Assert.Null(updatedInventory);

        // Now invoke MoveValue on Color (PropertyId 513, unlocked)
        var taskUnlocked = (Task)moveValueMethod.Invoke(editor, [ColorPropId, 0, 1])!;
        await taskUnlocked;

        // EventCallback MUST have fired with reordered Color values
        Assert.NotNull(updatedInventory);
        var newColorOrder = VariationEditor.GetValueOrder(updatedInventory, ColorPropId);
        Assert.Equal(["Green", "Red", "Blue"], newColorOrder);

        // Size order must remain strictly unchanged
        var sizeOrder = VariationEditor.GetValueOrder(updatedInventory, SizePropId);
        Assert.Equal(["Small", "Medium", "Large"], sizeOrder);
    }

    [Fact]
    public void MultipleVariations_ReorderingVariation2_KeepsVariation1LockedAndGridCartesian()
    {
        var inv = TwoVariationInventory();

        // Simulate Size being locked to template order: Large, Medium, Small
        inv = VariationEditor.ReorderValues(inv, SizePropId, ["Large", "Medium", "Small"]);
        Assert.Equal(["Large", "Medium", "Small"], VariationEditor.GetValueOrder(inv, SizePropId));

        // Reorder Color variation independently to Blue, Green, Red
        var updated = VariationEditor.ReorderValues(inv, ColorPropId, ["Blue", "Green", "Red"]);

        // 1. Both orders are preserved in value_order
        Assert.Equal(["Large", "Medium", "Small"], VariationEditor.GetValueOrder(updated, SizePropId));
        Assert.Equal(["Blue", "Green", "Red"], VariationEditor.GetValueOrder(updated, ColorPropId));

        // 2. Products grid is strictly sorted by Cartesian product of (Size, Color)
        var products = updated["products"]!.AsArray();
        Assert.Equal(9, products.Count);

        string Comb(JsonNode? p)
        {
            var pvs = p!["property_values"]!.AsArray();
            var size = pvs.First(v => (long)v!["property_id"]! == SizePropId)!["values"]![0]!.ToString();
            var color = pvs.First(v => (long)v!["property_id"]! == ColorPropId)!["values"]![0]!.ToString();
            return $"{size}-{color}";
        }

        var actualGridSequence = products.Select(Comb).ToList();
        var expectedGridSequence = new List<string>
        {
            "Large-Blue", "Large-Green", "Large-Red",
            "Medium-Blue", "Medium-Green", "Medium-Red",
            "Small-Blue", "Small-Green", "Small-Red"
        };
        Assert.Equal(expectedGridSequence, actualGridSequence);

        // 3. Offering values (price and quantity) follow the specific combination
        var largeBlue = products.First(p => Comb(p) == "Large-Blue");
        Assert.Equal(2000, (long)largeBlue!["offerings"]![0]!["price_amount"]!);
        Assert.Equal(32, (int)largeBlue!["offerings"]![0]!["quantity"]!);

        var smallRed = products.First(p => Comb(p) == "Small-Red");
        Assert.Equal(1000, (long)smallRed!["offerings"]![0]!["price_amount"]!);
        Assert.Equal(10, (int)smallRed!["offerings"]![0]!["quantity"]!);
    }

    [Fact]
    public async Task LinkVariationAsync_ListingWithOnlyCustomOptions_SucceedsWithoutCorruptingOrder()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listing = FakeEtsyShop.Listing(10, "Custom Options Listing");
        long id = 100;
        JsonObject MakeProduct(string opt)
        {
            var p = FakeEtsyShop.Product(++id, $"SKU-{opt}", "Custom", 1000, 5);
            p["property_values"]!.AsArray()[0] = new JsonObject
            {
                ["property_id"] = ColorPropId,
                ["property_name"] = "Color",
                ["scale_id"] = null,
                ["value_ids"] = new JsonArray(),
                ["values"] = new JsonArray(opt),
            };
            return p;
        }

        string[] customColors = ["Glitter Sparkle", "Midnight Velvet", "Sunset Neon"];
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray([.. customColors.Select(c => (JsonNode)MakeProduct(c))]),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        var listId = (await lists.SaveAsync(null, "Standard Palette", [
            new(0, "Standard Red", true),
            new(0, "Standard Blue", true)
        ], "tophattwaffle")).Id!.Value;

        await using var db = shop.Db.CreateDbContext();
        var dbListing = await db.Listings.SingleAsync(l => l.EtsyListingId == 10);

        // Linking a variation where 0 options match the template
        var error = await lists.LinkVariationAsync(listId, dbListing.Id, ColorPropId, "tophattwaffle");
        Assert.Null(error);

        // Listing retains all 3 custom options in original relative order
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var loaded = (await editor.LoadAsync(dbListing.Id))!.Model;
        var inv = loaded.Local(ListingFields.Inventory)!;
        var valueOrder = VariationEditor.GetValueOrder(inv, ColorPropId);
        Assert.Equal(["Glitter Sparkle", "Midnight Velvet", "Sunset Neon"], valueOrder);
    }

    // =========================================================================
    // Area 3: Push Verification (Two Variations, Template + Hand Ordered Grid)
    // =========================================================================

    [Fact]
    public void Canonical_WhenOnlySecondVariationHasValueOrder_PreservesPrimaryVariationOrder()
    {
        // 2-variation inventory: Size (100) and Color (513)
        var inv = TwoVariationInventory();
        var variations = VariationEditor.Variations(inv);
        Assert.Equal(SizePropId, variations[0].PropertyId);
        Assert.Equal(ColorPropId, variations[1].PropertyId);

        // Reorder ONLY the second variation (Color)
        var updated = VariationEditor.ReorderValues(inv, ColorPropId, ["Blue", "Green", "Red"]);

        // When only the second variation has value_order, Canonical falls back to the primary
        // variation's natural order, preserving the Size x Color Cartesian product sequence.
        var products = updated["products"]!.AsArray();
        string Comb(JsonNode? p)
        {
            var pvs = p!["property_values"]!.AsArray();
            var size = pvs.First(v => (long)v!["property_id"]! == SizePropId)!["values"]![0]!.ToString();
            var color = pvs.First(v => (long)v!["property_id"]! == ColorPropId)!["values"]![0]!.ToString();
            return $"{size}-{color}";
        }

        var actualSequence = products.Select(Comb).ToList();

        var expectedNaturalSequence = new List<string>
        {
            "Small-Blue", "Small-Green", "Small-Red",
            "Medium-Blue", "Medium-Green", "Medium-Red",
            "Large-Blue", "Large-Green", "Large-Red"
        };
        Assert.Equal(expectedNaturalSequence, actualSequence);
    }

    [Fact]
    public async Task PushListingJob_TwoVariationListing_SendsProductsInExactCanonicalGridOrderToFakeEtsyShop()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listing = FakeEtsyShop.Listing(20, "2-Variation Push Listing");
        long prodId = 200;

        JsonObject MakeEtsyProduct(string size, string color, long offeringId, int quantity, long priceAmount)
        {
            var p = FakeEtsyShop.Product(++prodId, $"SKU-{size}-{color}", "SizeColor", priceAmount, quantity);
            p["property_values"] = new JsonArray(
                new JsonObject
                {
                    ["property_id"] = SizePropId,
                    ["property_name"] = "Size",
                    ["scale_id"] = null,
                    ["value_ids"] = new JsonArray(size == "Small" ? 1001L : size == "Medium" ? 1002L : 1003L),
                    ["values"] = new JsonArray(size),
                },
                new JsonObject
                {
                    ["property_id"] = ColorPropId,
                    ["property_name"] = "Color",
                    ["scale_id"] = null,
                    ["value_ids"] = new JsonArray(color == "Black" ? 2001L : 2002L),
                    ["values"] = new JsonArray(color),
                }
            );
            p["offerings"]![0]!["offering_id"] = offeringId;
            p["offerings"]![0]!["quantity"] = quantity;
            p["offerings"]![0]!["price_amount"] = priceAmount;
            return p;
        }

        // Intentionally scrambled order on Etsy:
        // Medium-White, Large-Black, Small-White, Medium-Black, Small-Black, Large-White
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(SizePropId),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                MakeEtsyProduct("Medium", "White", 501, 15, 1500),
                MakeEtsyProduct("Large", "Black", 502, 31, 2000),
                MakeEtsyProduct("Small", "White", 503, 11, 1000),
                MakeEtsyProduct("Medium", "Black", 504, 16, 1500),
                MakeEtsyProduct("Small", "Black", 505, 12, 1000),
                MakeEtsyProduct("Large", "White", 506, 30, 2000)),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        // Template order for Size: Large, Medium, Small (different from Etsy's natural order Small, Medium, Large)
        var listId = (await lists.SaveAsync(null, "Push Sizes Template", [
            new(0, "Large", true),
            new(0, "Medium", true),
            new(0, "Small", true)
        ], "tophattwaffle")).Id!.Value;

        await using var db = shop.Db.CreateDbContext();
        var dbListing = await db.Listings.SingleAsync(l => l.EtsyListingId == 20);

        // 1. Link Size variation (reorders Size to Large, Medium, Small)
        await lists.LinkVariationAsync(listId, dbListing.Id, SizePropId, "tophattwaffle");

        // 2. Also reorder Color variation locally to Black, White
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var loaded = (await editor.LoadAsync(dbListing.Id))!.Model;
        var currentInv = loaded.Local(ListingFields.Inventory)!;
        var reorderedColorInv = VariationEditor.ReorderValues(currentInv, ColorPropId, ["Black", "White"]);
        await editor.SaveAsync(dbListing.Id, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Inventory] = reorderedColorInv
        }, "tophattwaffle");

        // 3. Request push
        var (pushId, blockers) = await editor.RequestPushAsync(dbListing.Id, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(pushId);

        // 4. Execute PushListingJob
        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 1,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId.Value), JobJson.Options),
        }, CancellationToken.None);

        // 5. Inspect recorded Etsy PUT write
        var invWrite = Assert.Single(shop.Writes, w => w.Method == "PUT" && w.Path.EndsWith("/inventory"));
        var body = JsonNode.Parse(invWrite.Body)!;
        var products = body["products"]!.AsArray();

        Assert.Equal(6, products.Count);

        string GetKey(JsonNode? p)
        {
            var pvs = p!["property_values"]!.AsArray();
            var size = pvs.First(v => (long)v!["property_id"]! == SizePropId)!["values"]![0]!.ToString();
            var color = pvs.First(v => (long)v!["property_id"]! == ColorPropId)!["values"]![0]!.ToString();
            return $"{size}-{color}";
        }

        // Expected sequence: Size order (Large, Medium, Small) x Color order (Black, White)
        var expectedOrder = new[]
        {
            "Large-Black",
            "Large-White",
            "Medium-Black",
            "Medium-White",
            "Small-Black",
            "Small-White"
        };
        var actualOrder = products.Select(GetKey).ToArray();
        Assert.Equal(expectedOrder, actualOrder);

        // Verify offering IDs and quantities are accurately matched by combination key:
        // Large-Black: offering ID 502, quantity 31
        Assert.Equal(31, (int)products[0]!["offerings"]![0]!["quantity"]!);
        // Large-White: offering ID 506, quantity 30
        Assert.Equal(30, (int)products[1]!["offerings"]![0]!["quantity"]!);
        // Medium-Black: offering ID 504, quantity 16
        Assert.Equal(16, (int)products[2]!["offerings"]![0]!["quantity"]!);
        // Medium-White: offering ID 501, quantity 15
        Assert.Equal(15, (int)products[3]!["offerings"]![0]!["quantity"]!);
        // Small-Black: offering ID 505, quantity 12
        Assert.Equal(12, (int)products[4]!["offerings"]![0]!["quantity"]!);
        // Small-White: offering ID 503, quantity 11
        Assert.Equal(11, (int)products[5]!["offerings"]![0]!["quantity"]!);

        // 6. Verify post-push reconciliation
        var reloadedListing = await db.Listings.Include(l => l.EtsyState).SingleAsync(l => l.EtsyListingId == 20);
        Assert.False(reloadedListing.HasDraft, "Draft must be cleanly reconciled with no leftover unpushed state.");
        var editModel = ListingEditModel.Parse(reloadedListing.EtsyState!.ManagedJson, reloadedListing.EtsyState.BaseJson, reloadedListing.DraftJson);
        Assert.DoesNotContain(ListingFields.Inventory, editModel.DriftFields);
    }

    [Fact]
    public async Task UnlinkAsync_RestoresReorderButtonsInUIAndAllowsManualReordering()
    {
        var (shop, listingId, listId) = await SetupLinkedShopAsync();
        using var _ = shop;

        var lists = shop.Provider.GetRequiredService<VariationListService>();

        // Unlink variation
        await lists.UnlinkAsync(listId, listingId, "tophattwaffle");

        // Verify db row deleted
        await using var db = shop.Db.CreateDbContext();
        Assert.False(await db.ListingVariationLists.AnyAsync(l => l.ListingId == listingId && l.VariationListId == listId));

        // Reload links
        var activeLinks = await lists.ListingLinksAsync(listingId);
        Assert.Empty(activeLinks);

        // Render VariationsEditor with empty links
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, TestJsRuntime>();
        services.AddSingleton<NavigationManager>(new TestNavigation("http://localhost/"));
        services.AddMudServices();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var editModel = (await editor.LoadAsync(listingId))!.Model;
        var inv = editModel.Local(ListingFields.Inventory)!;

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Inventory"] = inv,
                ["Links"] = activeLinks
            });
            var output = await renderer.RenderComponentAsync<VariationsEditor>(parameters);
            return output.ToHtmlString();
        });

        // Now move buttons MUST be rendered again
        Assert.Contains("Move White earlier", html);
        Assert.Contains("Move White later", html);

        // Template indicator caption MUST NOT be present
        Assert.DoesNotContain("Value order is controlled by template", html);
    }

    private static async Task<(FakeEtsyShop Shop, int ListingId, int ListId)> SetupLinkedShopAsync()
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(100, "Setup Linked Shop Listing");
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

        string[] colors = ["White", "Black", "Red"];
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
        var listId = (await lists.SaveAsync(null, "Test Color Template", [
            new(0, "Black", true),
            new(0, "White", true),
            new(0, "Red", true)
        ], "tophattwaffle")).Id!.Value;

        await using var db = shop.Db.CreateDbContext();
        var dbListing = await db.Listings.SingleAsync(l => l.EtsyListingId == 100);
        await lists.LinkVariationAsync(listId, dbListing.Id, ColorPropId, "tophattwaffle");

        return (shop, dbListing.Id, listId);
    }
}
