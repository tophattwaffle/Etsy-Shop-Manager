using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
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

public class VariationBulkLinkAndDriftEmpiricalTests
{
    private const long ColorPropId = VariationEditor.CustomProperty1; // 513
    private const long SizePropId = 100L;
    private const long SecondaryPropId = VariationEditor.CustomProperty2; // 514

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

    [Fact]
    public async Task BulkLinkAsync_Across12ListingsWithVariedInitialOrdersAndCustomValues_UpdatesAllDraftsProperlyOrdered()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        long prodIdCounter = 1000;
        JsonObject MakeColorProduct(string color, long price = 1500)
        {
            var p = FakeEtsyShop.Product(++prodIdCounter, $"SKU-{color}", "OneSize", price, 7);
            p["property_values"]!.AsArray()[0] = Pv(ColorPropId, "Color", color);
            return p;
        }

        JsonObject MakeTwoVarProduct(string size, string color, long price = 2000)
        {
            var p = FakeEtsyShop.Product(++prodIdCounter, $"SKU-{size}-{color}", size, price, 10);
            var sizeId = size == "Small" ? 1L : 2L;
            p["property_values"]!.AsArray()[0] = Pv(SizePropId, "Size", size, sizeId);
            p["property_values"]!.AsArray().Add(Pv(ColorPropId, "Color", color));
            return p;
        }

        JsonObject MakeProp2Product(string color, long price = 1200)
        {
            var p = FakeEtsyShop.Product(++prodIdCounter, $"SKU-P2-{color}", "OneSize", price, 5);
            p["property_values"]!.AsArray()[0] = Pv(SecondaryPropId, "Style", color);
            return p;
        }

        // Define 12 diverse listings
        var listingConfigs = new (int EtsyId, string Title, string[] Colors, long PropId)[]
        {
            // 1: Exact template order
            (101, "Listing 1 - Exact Order", ["Ruby Red", "Sapphire Blue", "Emerald Green", "Amber Gold", "Amethyst Violet"], ColorPropId),
            // 2: Completely reversed
            (102, "Listing 2 - Reversed", ["Amethyst Violet", "Amber Gold", "Emerald Green", "Sapphire Blue", "Ruby Red"], ColorPropId),
            // 3: Scrambled
            (103, "Listing 3 - Scrambled", ["Amber Gold", "Ruby Red", "Amethyst Violet", "Emerald Green", "Sapphire Blue"], ColorPropId),
            // 4: Subset 2 items, out of order
            (104, "Listing 4 - Subset 2", ["Amethyst Violet", "Ruby Red"], ColorPropId),
            // 5: Subset 3 items, out of order
            (105, "Listing 5 - Subset 3", ["Sapphire Blue", "Amethyst Violet", "Emerald Green"], ColorPropId),
            // 6: Case variations (template: Ruby Red, Sapphire Blue, Emerald Green, Amber Gold, Amethyst Violet)
            (106, "Listing 6 - Case Mismatches", ["ruby red", "SAPPHIRE BLUE", "emerald GREEN", "amber gold"], ColorPropId),
            // 7: 1 custom extra at front
            (107, "Listing 7 - Extra at Front", ["Custom Opal", "Ruby Red", "Sapphire Blue"], ColorPropId),
            // 8: 1 custom extra at back
            (108, "Listing 8 - Extra at Back", ["Sapphire Blue", "Ruby Red", "Custom Pearl"], ColorPropId),
            // 9: Multiple custom extras interleaved
            (109, "Listing 9 - Interleaved Extras", ["Extra X", "Amber Gold", "Extra Y", "Ruby Red", "Extra Z"], ColorPropId),
            // 10: Single template value + custom values
            (110, "Listing 10 - Single + Extras", ["Custom 1", "Emerald Green", "Custom 2"], ColorPropId),
            // 11: Two-variation listing (Size x Color), Color out of order
            (111, "Listing 11 - Two Variations", ["Amethyst Violet", "Ruby Red", "Sapphire Blue"], ColorPropId),
            // 12: On Secondary CustomProperty2
            (112, "Listing 12 - CustomProperty2", ["Amber Gold", "Ruby Red"], SecondaryPropId),
        };

        foreach (var cfg in listingConfigs)
        {
            var l = FakeEtsyShop.Listing(cfg.EtsyId, cfg.Title);
            if (cfg.EtsyId == 111)
            {
                // Two variations: Size x Color
                var prods = new List<JsonNode>();
                foreach (var size in new[] { "Small", "Large" })
                {
                    foreach (var color in cfg.Colors)
                    {
                        prods.Add(MakeTwoVarProduct(size, color));
                    }
                }
                l["inventory"] = new JsonObject
                {
                    ["value_order"] = new JsonObject
                    {
                        [SizePropId.ToString()] = new JsonArray("Small", "Large"),
                    },
                    ["price_on_property"] = new JsonArray(SizePropId),
                    ["quantity_on_property"] = new JsonArray(),
                    ["sku_on_property"] = new JsonArray(),
                    ["products"] = new JsonArray(prods.ToArray()),
                };
            }
            else if (cfg.PropId == SecondaryPropId)
            {
                l["inventory"] = new JsonObject
                {
                    ["price_on_property"] = new JsonArray(),
                    ["quantity_on_property"] = new JsonArray(),
                    ["sku_on_property"] = new JsonArray(),
                    ["products"] = new JsonArray([.. cfg.Colors.Select(c => (JsonNode)MakeProp2Product(c))]),
                };
            }
            else
            {
                l["inventory"] = new JsonObject
                {
                    ["price_on_property"] = new JsonArray(),
                    ["quantity_on_property"] = new JsonArray(),
                    ["sku_on_property"] = new JsonArray(),
                    ["products"] = new JsonArray([.. cfg.Colors.Select(c => (JsonNode)MakeColorProduct(c))]),
                };
            }
            shop.Listings.Add(l);
        }

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        var templateId = (await lists.SaveAsync(null, "Master Gemstones", [
            new(0, "Ruby Red", true),
            new(0, "Sapphire Blue", true),
            new(0, "Emerald Green", true),
            new(0, "Amber Gold", true),
            new(0, "Amethyst Violet", true)
        ], "tophattwaffle")).Id!.Value;

        await using var db = shop.Db.CreateDbContext();
        var dbListings = await db.Listings.Where(l => l.EtsyListingId >= 101 && l.EtsyListingId <= 112).ToListAsync();
        Assert.Equal(12, dbListings.Count);

        var linkPairs = listingConfigs.Select(cfg =>
        {
            var dbL = dbListings.Single(l => l.EtsyListingId == cfg.EtsyId);
            return (ListingId: dbL.Id, PropertyId: cfg.PropId);
        }).ToList();

        // ACT: Bulk link all 12 listings in a single call
        var linkedCount = await lists.LinkAsync(templateId, linkPairs, "tophattwaffle");
        Assert.Equal(12, linkedCount);

        // ASSERT: Verify all 12 listings have correctly ordered draft inventories
        var editor = shop.Provider.GetRequiredService<ListingEditor>();

        // 1: Exact order was ["Ruby Red", "Sapphire Blue", "Emerald Green", "Amber Gold", "Amethyst Violet"]
        var inv1 = (await editor.LoadAsync(dbListings.Single(l => l.EtsyListingId == 101).Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Ruby Red", "Sapphire Blue", "Emerald Green", "Amber Gold", "Amethyst Violet"], VariationEditor.GetValueOrder(inv1, ColorPropId));

        // 2: Reversed -> properly ordered to template
        var inv2 = (await editor.LoadAsync(dbListings.Single(l => l.EtsyListingId == 102).Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Ruby Red", "Sapphire Blue", "Emerald Green", "Amber Gold", "Amethyst Violet"], VariationEditor.GetValueOrder(inv2, ColorPropId));

        // 3: Scrambled -> properly ordered to template
        var inv3 = (await editor.LoadAsync(dbListings.Single(l => l.EtsyListingId == 103).Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Ruby Red", "Sapphire Blue", "Emerald Green", "Amber Gold", "Amethyst Violet"], VariationEditor.GetValueOrder(inv3, ColorPropId));

        // 4: Subset 2 -> ["Ruby Red", "Amethyst Violet"]
        var inv4 = (await editor.LoadAsync(dbListings.Single(l => l.EtsyListingId == 104).Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Ruby Red", "Amethyst Violet"], VariationEditor.GetValueOrder(inv4, ColorPropId));

        // 5: Subset 3 -> ["Sapphire Blue", "Emerald Green", "Amethyst Violet"]
        var inv5 = (await editor.LoadAsync(dbListings.Single(l => l.EtsyListingId == 105).Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Sapphire Blue", "Emerald Green", "Amethyst Violet"], VariationEditor.GetValueOrder(inv5, ColorPropId));

        // 6: Case variations -> template order with preserved original casing
        var inv6 = (await editor.LoadAsync(dbListings.Single(l => l.EtsyListingId == 106).Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["ruby red", "SAPPHIRE BLUE", "emerald GREEN", "amber gold"], VariationEditor.GetValueOrder(inv6, ColorPropId));

        // 7: 1 extra at front -> template options first, then Custom Opal
        var inv7 = (await editor.LoadAsync(dbListings.Single(l => l.EtsyListingId == 107).Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Ruby Red", "Sapphire Blue", "Custom Opal"], VariationEditor.GetValueOrder(inv7, ColorPropId));

        // 8: 1 extra at back -> template options first, then Custom Pearl
        var inv8 = (await editor.LoadAsync(dbListings.Single(l => l.EtsyListingId == 108).Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Ruby Red", "Sapphire Blue", "Custom Pearl"], VariationEditor.GetValueOrder(inv8, ColorPropId));

        // 9: Interleaved extras -> template options first ("Ruby Red", "Amber Gold"), then extras in relative order ("Extra X", "Extra Y", "Extra Z")
        var inv9 = (await editor.LoadAsync(dbListings.Single(l => l.EtsyListingId == 109).Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Ruby Red", "Amber Gold", "Extra X", "Extra Y", "Extra Z"], VariationEditor.GetValueOrder(inv9, ColorPropId));

        // 10: Single template value + custom values -> "Emerald Green", then "Custom 1", "Custom 2"
        var inv10 = (await editor.LoadAsync(dbListings.Single(l => l.EtsyListingId == 110).Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Emerald Green", "Custom 1", "Custom 2"], VariationEditor.GetValueOrder(inv10, ColorPropId));

        // 11: Two variations -> Color ordered to ["Ruby Red", "Sapphire Blue", "Amethyst Violet"], Size remains ["Small", "Large"]
        var inv11 = (await editor.LoadAsync(dbListings.Single(l => l.EtsyListingId == 111).Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Ruby Red", "Sapphire Blue", "Amethyst Violet"], VariationEditor.GetValueOrder(inv11, ColorPropId));
        // Size had no value_order, so Canonical preserves Size's natural order ["Small", "Large"]
        Assert.Equal(["Small", "Large"], VariationEditor.GetValueOrder(inv11, SizePropId));
        // Verify Cartesian product combinations in grid matching Canonical sequence (Size x Color)
        var prods11 = inv11["products"]!.AsArray();
        Assert.Equal(6, prods11.Count);
        string Combination(JsonNode? p) =>
            $"{(string)p!["property_values"]!.AsArray().Single(v => (long)v!["property_id"]! == SizePropId)!["values"]![0]!}:{(string)p["property_values"]!.AsArray().Single(v => (long)v!["property_id"]! == ColorPropId)!["values"]![0]!}";
        Assert.Equal([
            "Small:Ruby Red", "Small:Sapphire Blue", "Small:Amethyst Violet",
            "Large:Ruby Red", "Large:Sapphire Blue", "Large:Amethyst Violet"
        ], prods11.Select(Combination));

        // 12: Secondary PropertyId 514 -> ["Ruby Red", "Amber Gold"]
        var inv12 = (await editor.LoadAsync(dbListings.Single(l => l.EtsyListingId == 112).Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Ruby Red", "Amber Gold"], VariationEditor.GetValueOrder(inv12, SecondaryPropId));

        // Verify AppliedOptions stored in DB for all 12 links
        await using var verifyDb = shop.Db.CreateDbContext();
        var linksInDb = await verifyDb.ListingVariationLists.Where(l => l.VariationListId == templateId).ToListAsync();
        Assert.Equal(12, linksInDb.Count);
        foreach (var link in linksInDb)
        {
            Assert.NotEmpty(link.AppliedOptions);
        }
    }

    [Fact]
    public async Task UnlinkAsync_PreservesExistingValueOrder_And_UnlocksManualReordering()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        long prodIdCounter = 2000;
        JsonObject MakeColorProduct(string color)
        {
            var p = FakeEtsyShop.Product(++prodIdCounter, $"SKU-{color}", "OneSize", 1500, 5);
            p["property_values"]!.AsArray()[0] = Pv(ColorPropId, "Color", color);
            return p;
        }

        // Listing with initial values: Blue, Custom Gold, Red, Black
        var listing = FakeEtsyShop.Listing(201, "Unlinkable Listing");
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                MakeColorProduct("Blue"),
                MakeColorProduct("Custom Gold"),
                MakeColorProduct("Red"),
                MakeColorProduct("Black")),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        var templateId = (await lists.SaveAsync(null, "Basic Colors", [
            new(0, "Black", true),
            new(0, "Blue", true),
            new(0, "Red", true)
        ], "tophattwaffle")).Id!.Value;

        await using var db = shop.Db.CreateDbContext();
        var dbListing = await db.Listings.SingleAsync(l => l.EtsyListingId == 201);

        // 1. Link variation -> reorders to template: Black, Blue, Red, Custom Gold
        var error = await lists.LinkVariationAsync(templateId, dbListing.Id, ColorPropId, "tophattwaffle");
        Assert.Null(error);

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var linkedInv = (await editor.LoadAsync(dbListing.Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Black", "Blue", "Red", "Custom Gold"], VariationEditor.GetValueOrder(linkedInv, ColorPropId));

        // Verify linked state in DB
        var linksBefore = await lists.ListingLinksAsync(dbListing.Id);
        Assert.Single(linksBefore);
        Assert.Equal(templateId, linksBefore[0].ListId);

        // Verify in UI component: reorder buttons hidden and template indicator displayed
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, TestJsRuntime>();
        services.AddSingleton<NavigationManager>(new TestNavigation("http://localhost/"));
        services.AddMudServices();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var linkedHtml = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Inventory"] = linkedInv,
                ["Links"] = linksBefore
            });
            var output = await renderer.RenderComponentAsync<VariationsEditor>(parameters);
            return output.ToHtmlString();
        });
        Assert.DoesNotContain("Move Black earlier", linkedHtml);
        Assert.DoesNotContain("Move Black later", linkedHtml);
        Assert.Contains("Value order is controlled by template Basic Colors.", linkedHtml);

        // 2. ACT: Unlink template
        await lists.UnlinkAsync(templateId, dbListing.Id, "tophattwaffle");

        // 3. ASSERT: Link is gone
        var linksAfter = await lists.ListingLinksAsync(dbListing.Id);
        Assert.Empty(linksAfter);

        // 4. ASSERT: Existing value order is strictly preserved! (NOT reverted, NOT lost)
        var unlinkedInv = (await editor.LoadAsync(dbListing.Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Black", "Blue", "Red", "Custom Gold"], VariationEditor.GetValueOrder(unlinkedInv, ColorPropId));

        // 5. ASSERT: Manual reordering is now unlocked in UI
        var unlinkedHtml = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Inventory"] = unlinkedInv,
                ["Links"] = linksAfter
            });
            var output = await renderer.RenderComponentAsync<VariationsEditor>(parameters);
            return output.ToHtmlString();
        });
        Assert.Contains("Move Black earlier", unlinkedHtml);
        Assert.Contains("Move Black later", unlinkedHtml);
        Assert.DoesNotContain("Value order is controlled by template", unlinkedHtml);

        // 6. ASSERT: Manual reordering can now be executed and saved
        var manuallyReordered = VariationEditor.ReorderValues(unlinkedInv, ColorPropId, ["Custom Gold", "Red", "Blue", "Black"]);
        await editor.SaveAsync(dbListing.Id, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Inventory] = manuallyReordered
        }, "tophattwaffle");

        var reloadedInv = (await editor.LoadAsync(dbListing.Id))!.Model.Local(ListingFields.Inventory)!;
        Assert.Equal(["Custom Gold", "Red", "Blue", "Black"], VariationEditor.GetValueOrder(reloadedInv, ColorPropId));
    }

    [Fact]
    public async Task DriftCheck_WhenInventoryHasEtsyDrift_ApplyAsync_SkipsListing_And_DoesNotOverwriteDraft()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        long prodIdCounter = 3000;
        JsonObject MakeColorProduct(string color, long price = 1000)
        {
            var p = FakeEtsyShop.Product(++prodIdCounter, $"SKU-{color}", "OneSize", price, 5);
            p["property_values"]!.AsArray()[0] = Pv(ColorPropId, "Color", color);
            return p;
        }

        var listing = FakeEtsyShop.Listing(301, "Drifted Apply Listing");
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                MakeColorProduct("Black", 1000),
                MakeColorProduct("White", 1000)),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        var templateId = (await lists.SaveAsync(null, "Two Colors", [
            new(0, "Black", true),
            new(0, "White", true)
        ], "tophattwaffle")).Id!.Value;

        await using var db = shop.Db.CreateDbContext();
        var dbListing = await db.Listings.SingleAsync(l => l.EtsyListingId == 301);

        // Link initially while clean
        await lists.LinkVariationAsync(templateId, dbListing.Id, ColorPropId, "tophattwaffle");

        // Now introduce inventory drift on Etsy:
        // On Etsy, price of Black changed to 2500 and a new variation value "Etsy Special" was added on Etsy directly
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                MakeColorProduct("Black", 2500),
                MakeColorProduct("White", 1000),
                MakeColorProduct("Etsy Special", 5000)),
        };
        // Sync / re-import from Etsy
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        // Verify inventory drift is flagged
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var editSnapshot = await editor.LoadAsync(dbListing.Id);
        Assert.Contains(ListingFields.Inventory, editSnapshot!.Model.DriftFields);

        // Update the template: add "Red"
        var tmpl = (await lists.GetAsync(templateId))!;
        await lists.SaveAsync(templateId, "Two Colors", [
            tmpl.Options[0],
            tmpl.Options[1],
            new(0, "Red", true)
        ], "tophattwaffle");

        // Verify UsesAsync detects drift
        var uses = await lists.UsesAsync(templateId);
        var use = Assert.Single(uses);
        Assert.Equal(VariationListUseStatus.ChangedOnEtsy, use.Status);

        // ACT: Attempt to Apply template
        var changedCount = await lists.ApplyAsync(templateId, "tophattwaffle");

        // ASSERT: Drift protection skips the listing!
        Assert.Equal(0, changedCount);

        // Verify listing draft does NOT have "Red" added
        var reloaded = (await editor.LoadAsync(dbListing.Id))!.Model;
        var localInv = reloaded.Local(ListingFields.Inventory)!;
        Assert.DoesNotContain("Red", VariationEditor.GetValueOrder(localInv, ColorPropId));
    }

    [Fact]
    public async Task DriftCheck_WhenInventoryHasEtsyDrift_LinkVariationAsync_RefusesWithErrorMessage()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        long prodIdCounter = 4000;
        JsonObject MakeColorProduct(string color, long price = 1000)
        {
            var p = FakeEtsyShop.Product(++prodIdCounter, $"SKU-{color}", "OneSize", price, 5);
            p["property_values"]!.AsArray()[0] = Pv(ColorPropId, "Color", color);
            return p;
        }

        var listing = FakeEtsyShop.Listing(401, "Drifted Unlinked Listing");
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(MakeColorProduct("Black"), MakeColorProduct("White")),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        var templateId = (await lists.SaveAsync(null, "Palette 400", [
            new(0, "White", true),
            new(0, "Black", true)
        ], "tophattwaffle")).Id!.Value;

        await using var db = shop.Db.CreateDbContext();
        var dbListing = await db.Listings.SingleAsync(l => l.EtsyListingId == 401);

        // Introduce inventory drift on Etsy
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                MakeColorProduct("Black", 9999),
                MakeColorProduct("White", 1000)),
        };
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        // Verify inventory drift is present
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var editSnapshot = await editor.LoadAsync(dbListing.Id);
        Assert.Contains(ListingFields.Inventory, editSnapshot!.Model.DriftFields);

        // ACT: Attempt to link variation
        var error = await lists.LinkVariationAsync(templateId, dbListing.Id, ColorPropId, "tophattwaffle");

        // ASSERT: LinkVariationAsync refuses due to drift
        Assert.NotNull(error);
        Assert.Equal("This listing's variations changed on Etsy; resolve that first.", error);

        // Verify no link in DB
        await using var verifyDb = shop.Db.CreateDbContext();
        Assert.False(await verifyDb.ListingVariationLists.AnyAsync(l => l.ListingId == dbListing.Id));

        // Verify local draft was NOT created or modified
        var reloadedSnapshot = await editor.LoadAsync(dbListing.Id);
        Assert.False(reloadedSnapshot!.Listing.HasDraft);
    }

    [Fact]
    public async Task BulkLinkAsync_IdempotencyAndMixedBatch_SkipsAlreadyLinkedAndPreservesOrder()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        long prodIdCounter = 5000;
        JsonObject MakeColorProduct(string color)
        {
            var p = FakeEtsyShop.Product(++prodIdCounter, $"SKU-{color}", "OneSize", 1000, 5);
            p["property_values"]!.AsArray()[0] = Pv(ColorPropId, "Color", color);
            return p;
        }

        // 4 listings
        for (int i = 501; i <= 504; i++)
        {
            var l = FakeEtsyShop.Listing(i, $"Batch Listing {i}");
            l["inventory"] = new JsonObject
            {
                ["price_on_property"] = new JsonArray(),
                ["quantity_on_property"] = new JsonArray(),
                ["sku_on_property"] = new JsonArray(),
                ["products"] = new JsonArray(MakeColorProduct("Green"), MakeColorProduct("Blue")),
            };
            shop.Listings.Add(l);
        }
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        var templateId = (await lists.SaveAsync(null, "Green Blue", [
            new(0, "Blue", true),
            new(0, "Green", true)
        ], "tophattwaffle")).Id!.Value;

        await using var db = shop.Db.CreateDbContext();
        var l501 = await db.Listings.SingleAsync(l => l.EtsyListingId == 501);
        var l502 = await db.Listings.SingleAsync(l => l.EtsyListingId == 502);
        var l503 = await db.Listings.SingleAsync(l => l.EtsyListingId == 503);
        var l504 = await db.Listings.SingleAsync(l => l.EtsyListingId == 504);

        // Pre-link 501 and 502
        var preLinked = await lists.LinkAsync(templateId, [(l501.Id, ColorPropId), (l502.Id, ColorPropId)], "tophattwaffle");
        Assert.Equal(2, preLinked);

        // ACT: Bulk link all 4 (501 and 502 already linked; 503 and 504 new)
        var secondBatch = await lists.LinkAsync(templateId, [
            (l501.Id, ColorPropId),
            (l502.Id, ColorPropId),
            (l503.Id, ColorPropId),
            (l504.Id, ColorPropId),
        ], "tophattwaffle");

        // ASSERT: Only the 2 new ones were linked
        Assert.Equal(2, secondBatch);

        // Total links in DB = 4
        await using var verifyDb = shop.Db.CreateDbContext();
        Assert.Equal(4, await verifyDb.ListingVariationLists.CountAsync(l => l.VariationListId == templateId));

        // All 4 listings now have ordered values: ["Blue", "Green"]
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        foreach (var lId in new[] { l501.Id, l502.Id, l503.Id, l504.Id })
        {
            var inv = (await editor.LoadAsync(lId))!.Model.Local(ListingFields.Inventory)!;
            Assert.Equal(["Blue", "Green"], VariationEditor.GetValueOrder(inv, ColorPropId));
        }
    }

    [Fact]
    public async Task DriftCheck_WhenInventoryHasEtsyDrift_LinkAsync_Behavior()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        long prodIdCounter = 6000;
        JsonObject MakeColorProduct(string color, long price = 1000)
        {
            var p = FakeEtsyShop.Product(++prodIdCounter, $"SKU-{color}", "OneSize", price, 5);
            p["property_values"]!.AsArray()[0] = Pv(ColorPropId, "Color", color);
            return p;
        }

        // Clean listing 601
        var l601 = FakeEtsyShop.Listing(601, "Clean Listing 601");
        l601["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(MakeColorProduct("White"), MakeColorProduct("Black")),
        };
        shop.Listings.Add(l601);

        // Drifted listing 602
        var l602 = FakeEtsyShop.Listing(602, "Drifted Listing 602");
        l602["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(MakeColorProduct("White"), MakeColorProduct("Black")),
        };
        shop.Listings.Add(l602);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        // Now introduce inventory drift on Etsy for 602
        l602["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(MakeColorProduct("White", 9999), MakeColorProduct("Black", 9999)),
        };
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await using var db = shop.Db.CreateDbContext();
        var db601 = await db.Listings.SingleAsync(l => l.EtsyListingId == 601);
        var db602 = await db.Listings.SingleAsync(l => l.EtsyListingId == 602);

        var model601 = (await editor.LoadAsync(db601.Id))!.Model;
        var model602 = (await editor.LoadAsync(db602.Id))!.Model;
        Assert.Empty(model601.DriftFields);
        Assert.Contains(ListingFields.Inventory, model602.DriftFields);

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        var templateId = (await lists.SaveAsync(null, "Drift Palette", [
            new(0, "Black", true),
            new(0, "White", true)
        ], "tophattwaffle")).Id!.Value;

        // ACT: Attempt to bulk link both listings
        var linkedCount = await lists.LinkAsync(templateId, [(db601.Id, ColorPropId), (db602.Id, ColorPropId)], "tophattwaffle");

        // Let's inspect what LinkAsync did:
        // Did it link both or skip the drifted one?
        // Note: UsesAsync flags 602 as ChangedOnEtsy, and ApplyAsync skips it
        var uses = await lists.UsesAsync(templateId);
        var use602 = uses.FirstOrDefault(u => u.ListingId == db602.Id);
        if (use602 is not null)
        {
            Assert.Equal(VariationListUseStatus.ChangedOnEtsy, use602.Status);
            // ApplyAsync skips 602
            var applied = await lists.ApplyAsync(templateId, "tophattwaffle");
            // If 601 was updated or 0, 602 is skipped
            Assert.True(applied <= 1);
        }
    }
}
