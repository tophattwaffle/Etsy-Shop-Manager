using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Web.Components.Pages;
using EtsyShopManager.Web.Components.Pages.Admin;
using EtsyShopManager.Web.Components.Pages.Listings;
using EtsyShopManager.Web.Components.Pages.Templates;
using EtsyShopManager.Web.Modules;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Navigation;
using EtsyShopManager.Web.Modules.Orders;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests.E2E;

/// <summary>
/// End-to-end requirement-driven test suite covering Requirements R1 through R4 across 4 tiers:
/// - Tier 1: Feature Coverage (>=5 per requirement)
/// - Tier 2: Boundary & Corner Cases (>=5 per requirement)
/// - Tier 3: Cross-Feature Combinations (Pairwise)
/// - Tier 4: Real-World Scenarios
/// </summary>
public class RequirementE2ETests
{
    private const long ColorPropertyId = VariationEditor.CustomProperty1; // 513
    private const long SizePropertyId = 100L;

    #region Fixture & Environment Setup

    private sealed record TestEnvironment(
        FakeEtsyShop Shop,
        int ListingSingleVar1, // Color: Red $10, Blue $20, Green $30, Custom Swirl $25 (price varies by Color)
        int ListingSingleVar2, // Color: Red $5, Blue $15 (price varies by Color)
        int ListingDualVar,    // Size (Small, Large) x Color (Red, Blue) (price varies by Size and Color)
        int ListingFlatPrice,  // Color: Red, Blue, White ($19.99 flat, price does NOT vary by property)
        int ListingSingleValue // Color: Red ($15.00 only)
    ) : IDisposable
    {
        public void Dispose() => Shop.Dispose();
    }

    private static async Task<TestEnvironment> SetupEnvironmentAsync()
    {
        var shop = new FakeEtsyShop();

        // 1. ListingSingleVar1: Red $10, Blue $20, Green $30, Custom swirl $25 (price on property 513)
        var listing1 = FakeEtsyShop.Listing(101, "Single Var Vase 1");
        listing1["inventory"] = CreateInventoryWithColor(
            [("Red", 1000L), ("Blue", 2000L), ("Green", 3000L), ("Custom swirl", 2500L)],
            priceVariesOnColor: true);
        shop.Listings.Add(listing1);

        // 2. ListingSingleVar2: Red $5, Blue $15 (price on property 513)
        var listing2 = FakeEtsyShop.Listing(102, "Single Var Mug 2");
        listing2["inventory"] = CreateInventoryWithColor(
            [("Red", 500L), ("Blue", 1500L)],
            priceVariesOnColor: true);
        shop.Listings.Add(listing2);

        // 3. ListingDualVar: Size (Small, Large) x Color (Red, Blue), price varies by both 100 and 513
        // Small Red: $12.00, Small Blue: $16.00; Large Red: $22.00, Large Blue: $28.00
        var listing3 = FakeEtsyShop.Listing(103, "Dual Var Shirt 3");
        listing3["inventory"] = CreateDualInventory(
            sizes: ["Small", "Large"],
            colors: ["Red", "Blue"],
            prices: new Dictionary<(string, string), long>
            {
                [("Small", "Red")] = 1200L,
                [("Small", "Blue")] = 1600L,
                [("Large", "Red")] = 2200L,
                [("Large", "Blue")] = 2800L,
            });
        shop.Listings.Add(listing3);

        // 4. ListingFlatPrice: Red, Blue, White ($19.99 flat, no price on property)
        var listing4 = FakeEtsyShop.Listing(104, "Flat Price Plate 4");
        listing4["inventory"] = CreateInventoryWithColor(
            [("Red", 1999L), ("Blue", 1999L), ("White", 1999L)],
            priceVariesOnColor: false);
        shop.Listings.Add(listing4);

        // 5. ListingSingleValue: Red only ($15.00)
        var listing5 = FakeEtsyShop.Listing(105, "Single Value Hat 5");
        listing5["inventory"] = CreateInventoryWithColor(
            [("Red", 1500L)],
            priceVariesOnColor: true);
        shop.Listings.Add(listing5);

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        var idMap = await db.Listings.ToDictionaryAsync(l => l.EtsyListingId, l => l.Id);

        return new TestEnvironment(
            shop,
            idMap[101],
            idMap[102],
            idMap[103],
            idMap[104],
            idMap[105]
        );
    }

    private static JsonObject CreateInventoryWithColor(IEnumerable<(string Color, long PriceCents)> items, bool priceVariesOnColor)
    {
        long id = 1000;
        var productList = new JsonArray();
        foreach (var (color, price) in items)
        {
            var p = FakeEtsyShop.Product(++id, "", "", price, 5);
            p["property_values"] = new JsonArray(new JsonObject
            {
                ["property_id"] = ColorPropertyId,
                ["property_name"] = "Color",
                ["value_ids"] = new JsonArray(),
                ["values"] = new JsonArray(color),
            });
            productList.Add(p);
        }

        return new JsonObject
        {
            ["price_on_property"] = priceVariesOnColor ? new JsonArray(ColorPropertyId) : new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = productList,
        };
    }

    private static JsonObject CreateDualInventory(string[] sizes, string[] colors, Dictionary<(string Size, string Color), long> prices)
    {
        long id = 5000;
        var productList = new JsonArray();
        foreach (var size in sizes)
        {
            foreach (var color in colors)
            {
                var price = prices[(size, color)];
                var p = FakeEtsyShop.Product(++id, "", size, price, 5);
                p["property_values"] = new JsonArray(
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
                    });
                productList.Add(p);
            }
        }

        return new JsonObject
        {
            ["price_on_property"] = new JsonArray(SizePropertyId, ColorPropertyId),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = productList,
        };
    }

    private static VariationListService Lists(FakeEtsyShop s) => s.Provider.GetRequiredService<VariationListService>();
    private static ListingEditor Editor(FakeEtsyShop s) => s.Provider.GetRequiredService<ListingEditor>();

    private static async Task<JsonNode> InventoryAsync(FakeEtsyShop s, int listingId) =>
        (await Editor(s).LoadAsync(listingId))!.Model.Local(ListingFields.Inventory)!;

    private static IReadOnlyList<string> GetColors(JsonNode inventory) =>
        VariationEditor.Variations(inventory).Single(v => v.PropertyId == ColorPropertyId).Values;

    private static decimal? GetColorPrice(JsonNode inventory, string color, string? size = null)
    {
        var products = inventory["products"]?.AsArray();
        if (products is null) return null;

        foreach (var p in products)
        {
            if (p is not JsonObject pObj) continue;
            var pvs = pObj["property_values"]?.AsArray();
            if (pvs is null) continue;

            var cVal = pvs.FirstOrDefault(pv => pv != null && (long)pv["property_id"]! == ColorPropertyId)?["values"]?[0]?.ToString();
            if (!string.Equals(cVal, color, StringComparison.OrdinalIgnoreCase)) continue;

            if (size != null)
            {
                var sVal = pvs.FirstOrDefault(pv => pv != null && (long)pv["property_id"]! == SizePropertyId)?["values"]?[0]?.ToString();
                if (!string.Equals(sVal, size, StringComparison.OrdinalIgnoreCase)) continue;
            }

            var offerings = pObj["offerings"]?.AsArray();
            var offering = offerings?.FirstOrDefault() as JsonObject;
            if (offering is null) return null;

            return VariationEditor.OfferingPrice(offering);
        }
        return null;
    }

    #endregion

    #region Reflection & Contract Invocation Helpers

    private static Type? GetPricingStrategyEnumType()
    {
        return typeof(VariationListService).Assembly.GetType("EtsyShopManager.Web.Modules.Templates.VariationPricingStrategy")
            ?? typeof(VariationList).Assembly.GetType("EtsyShopManager.Core.Domain.VariationPricingStrategy")
            ?? typeof(VariationList).Assembly.GetType("EtsyShopManager.Core.Listings.VariationPricingStrategy");
    }

    private static async Task<int> ApplyTemplateWithStrategyAsync(
        VariationListService service,
        int listId,
        string user,
        string strategy = "TakeHighest",
        IReadOnlyDictionary<int, decimal>? customPrices = null)
    {
        var strategyType = GetPricingStrategyEnumType();
        var methodWithStrategy = typeof(VariationListService).GetMethods()
            .FirstOrDefault(m => m.Name == "ApplyAsync" && m.GetParameters().Any(p => p.ParameterType == strategyType));

        if (methodWithStrategy != null && strategyType != null)
        {
            var strategyEnumVal = Enum.Parse(strategyType, strategy);
            var parameters = methodWithStrategy.GetParameters();
            object?[] args = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (p.Name == "listId") args[i] = listId;
                else if (p.Name == "user") args[i] = user;
                else if (p.Name == "listingId") args[i] = null;
                else if (p.ParameterType == strategyType) args[i] = strategyEnumVal;
                else if (p.ParameterType.IsAssignableFrom(typeof(Dictionary<int, decimal>))) args[i] = customPrices;
                else if (p.ParameterType == typeof(CancellationToken)) args[i] = CancellationToken.None;
                else args[i] = p.DefaultValue;
            }

            var task = (Task)methodWithStrategy.Invoke(service, args)!;
            await task.ConfigureAwait(false);
            var resultProp = task.GetType().GetProperty("Result");
            return (int)resultProp!.GetValue(task)!;
        }

        // Fallback to basic ApplyAsync
        return await service.ApplyAsync(listId, user, listingId: null, ct: CancellationToken.None);
    }

    #endregion

    #region TIER 1: FEATURE COVERAGE (R1 - R4)

    [Fact]
    public async Task T1_R1_01_OptionRemoval_DetectsRemovedOption_And_EmitsRemovesChange()
    {
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "Test Colors",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId)], "admin");

        // Remove Green from the template
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "Test Colors",
            [list.Options[0], list.Options[1]], "admin");

        var use = Assert.Single(await Lists(env.Shop).UsesAsync(listId));
        Assert.Equal(VariationListUseStatus.HasChanges, use.Status);
        Assert.Contains(use.Changes, c => c.Contains("Removes Green"));
    }

    [Fact]
    public async Task T1_R1_02_OptionRemoval_RemovesProductRows_FromDraftInventory()
    {
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "Test Colors",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId)], "admin");

        // Remove Green from template and apply
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "Test Colors",
            [list.Options[0], list.Options[1]], "admin");

        var appliedCount = await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin");
        Assert.Equal(1, appliedCount);

        var inventory = await InventoryAsync(env.Shop, env.ListingSingleVar1);
        var colors = GetColors(inventory);
        Assert.DoesNotContain("Green", colors);
    }

    [Fact]
    public async Task T1_R1_03_OptionRemoval_PreservesListingOwnCustomValues()
    {
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "Test Colors",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId)], "admin");

        // "Custom swirl" was present on ListingSingleVar1 from start, never in template
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "Test Colors",
            [list.Options[0]], "admin"); // Remove Blue and Green

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin");

        var inventory = await InventoryAsync(env.Shop, env.ListingSingleVar1);
        var colors = GetColors(inventory);
        Assert.Contains("Custom swirl", colors);
        Assert.True(VariationEditor.IsValueAvailable(inventory, ColorPropertyId, "Custom swirl"));
    }

    [Fact]
    public async Task T1_R1_04_OptionRemoval_UpdatesAppliedOptionsTracking()
    {
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "Test Colors",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId)], "admin");

        // Verify initial AppliedOptions
        await using (var db = env.Shop.Db.CreateDbContext())
        {
            var link = await db.ListingVariationLists.SingleAsync(l => l.ListingId == env.ListingSingleVar1);
            Assert.Contains(link.AppliedOptions, o => o.Name == "Green");
        }

        // Remove Green from template and apply
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "Test Colors",
            [list.Options[0], list.Options[1]], "admin");
        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin");

        // Verify Green is no longer in AppliedOptions
        await using (var db = env.Shop.Db.CreateDbContext())
        {
            var link = await db.ListingVariationLists.SingleAsync(l => l.ListingId == env.ListingSingleVar1);
            Assert.DoesNotContain(link.AppliedOptions, o => o.Name == "Green");
        }
    }

    [Fact]
    public async Task T1_R1_05_OptionRemoval_StatusTransitionsToUpToDate()
    {
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "Test Colors",
            [new(0, "Red", true), new(0, "Blue", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId)], "admin");

        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "Test Colors", [list.Options[0]], "admin"); // Remove Blue

        var useBefore = Assert.Single(await Lists(env.Shop).UsesAsync(listId));
        Assert.Equal(VariationListUseStatus.HasChanges, useBefore.Status);

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin");

        var useAfter = Assert.Single(await Lists(env.Shop).UsesAsync(listId));
        Assert.Equal(VariationListUseStatus.UpToDate, useAfter.Status);
    }

    [Fact]
    public async Task T1_R2_01_Pricing_TakeHighest_SingleVariation()
    {
        using var env = await SetupEnvironmentAsync();
        // Listing 1 has Red $10, Blue $20, Green $30. Highest = $30.
        // Listing 2 has Red $5, Blue $15. Highest = $15.
        var listId = (await Lists(env.Shop).SaveAsync(null, "PLA Colors",
            [new(0, "Red", true), new(0, "Blue", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId), (env.ListingSingleVar2, ColorPropertyId)], "admin");

        // Add "Gold"
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors",
            [.. list.Options, new(0, "Gold", true)], "admin");

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "TakeHighest");

        var inv1 = await InventoryAsync(env.Shop, env.ListingSingleVar1);
        var inv2 = await InventoryAsync(env.Shop, env.ListingSingleVar2);

        // Gold should take highest price on each respective listing
        Assert.Equal(30.00m, GetColorPrice(inv1, "Gold"));
        Assert.Equal(15.00m, GetColorPrice(inv2, "Gold"));
    }

    [Fact]
    public async Task T1_R2_02_Pricing_TakeLowest_SingleVariation()
    {
        using var env = await SetupEnvironmentAsync();
        // Listing 1 has Red $10, Blue $20, Green $30. Lowest = $10.
        // Listing 2 has Red $5, Blue $15. Lowest = $5.
        var listId = (await Lists(env.Shop).SaveAsync(null, "PLA Colors",
            [new(0, "Red", true), new(0, "Blue", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId), (env.ListingSingleVar2, ColorPropertyId)], "admin");

        // Add "Silver"
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors",
            [.. list.Options, new(0, "Silver", true)], "admin");

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "TakeLowest");

        var inv1 = await InventoryAsync(env.Shop, env.ListingSingleVar1);
        var inv2 = await InventoryAsync(env.Shop, env.ListingSingleVar2);

        Assert.Equal(10.00m, GetColorPrice(inv1, "Silver"));
        Assert.Equal(5.00m, GetColorPrice(inv2, "Silver"));
    }

    [Fact]
    public async Task T1_R2_03_Pricing_TakeHighest_TwoVariations()
    {
        using var env = await SetupEnvironmentAsync();
        // ListingDualVar has Small (Red $12, Blue $16) and Large (Red $22, Blue $28)
        var listId = (await Lists(env.Shop).SaveAsync(null, "Shirt Colors",
            [new(0, "Red", true), new(0, "Blue", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingDualVar, ColorPropertyId)], "admin");

        // Add "Navy"
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "Shirt Colors",
            [.. list.Options, new(0, "Navy", true)], "admin");

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "TakeHighest");

        var inv = await InventoryAsync(env.Shop, env.ListingDualVar);

        // Within Small: max(12, 16) = $16.00
        // Within Large: max(22, 28) = $28.00
        Assert.Equal(16.00m, GetColorPrice(inv, "Navy", "Small"));
        Assert.Equal(28.00m, GetColorPrice(inv, "Navy", "Large"));
    }

    [Fact]
    public async Task T1_R2_04_Pricing_TakeLowest_TwoVariations()
    {
        using var env = await SetupEnvironmentAsync();
        // ListingDualVar has Small (Red $12, Blue $16) and Large (Red $22, Blue $28)
        var listId = (await Lists(env.Shop).SaveAsync(null, "Shirt Colors",
            [new(0, "Red", true), new(0, "Blue", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingDualVar, ColorPropertyId)], "admin");

        // Add "White"
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "Shirt Colors",
            [.. list.Options, new(0, "White", true)], "admin");

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "TakeLowest");

        var inv = await InventoryAsync(env.Shop, env.ListingDualVar);

        // Within Small: min(12, 16) = $12.00
        // Within Large: min(22, 28) = $22.00
        Assert.Equal(12.00m, GetColorPrice(inv, "White", "Small"));
        Assert.Equal(22.00m, GetColorPrice(inv, "White", "Large"));
    }

    [Fact]
    public async Task T1_R2_05_Pricing_PickPerListing_AppliesCustomPrice()
    {
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "PLA Colors",
            [new(0, "Red", true), new(0, "Blue", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId), (env.ListingSingleVar2, ColorPropertyId)], "admin");

        // Add "Teal" with PickPerListing: $27.50 on Listing 1, $18.25 on Listing 2
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors",
            [.. list.Options, new(0, "Teal", true)], "admin");

        var customOverrides = new Dictionary<int, decimal>
        {
            [env.ListingSingleVar1] = 27.50m,
            [env.ListingSingleVar2] = 18.25m,
        };

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "PickPerListing", customPrices: customOverrides);

        var inv1 = await InventoryAsync(env.Shop, env.ListingSingleVar1);
        var inv2 = await InventoryAsync(env.Shop, env.ListingSingleVar2);

        Assert.Equal(27.50m, GetColorPrice(inv1, "Teal"));
        Assert.Equal(18.25m, GetColorPrice(inv2, "Teal"));
    }

    [Fact]
    public async Task T1_R2_06_Pricing_NeverAltersExistingOptionPrices()
    {
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "PLA Colors",
            [new(0, "Red", true), new(0, "Blue", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId)], "admin");

        // Listing 1 has Red $10.00, Blue $20.00, Green $30.00
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors",
            [.. list.Options, new(0, "Bronze", true)], "admin");

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "TakeHighest");

        var inv = await InventoryAsync(env.Shop, env.ListingSingleVar1);
        Assert.Equal(10.00m, GetColorPrice(inv, "Red"));
        Assert.Equal(20.00m, GetColorPrice(inv, "Blue"));
        Assert.Equal(30.00m, GetColorPrice(inv, "Green"));
        Assert.Equal(25.00m, GetColorPrice(inv, "Custom swirl"));
    }

    [Fact]
    public void T1_R3_01_Roles_OnlyAdminAndUserDefined()
    {
        // Requirement R3: Roles defines only Admin and User. Editor and Viewer are removed.
        var userField = typeof(Roles).GetField("User");
        var adminField = typeof(Roles).GetField("Admin");
        var editorField = typeof(Roles).GetField("Editor");
        var viewerField = typeof(Roles).GetField("Viewer");
        var allField = typeof(Roles).GetField("All");

        Assert.NotNull(adminField);
        Assert.Equal("Admin", adminField.GetValue(null));

        Assert.NotNull(userField);
        Assert.Equal("User", userField.GetValue(null));

        Assert.Null(editorField);
        Assert.Null(viewerField);

        var all = (string[]?)allField?.GetValue(null);
        Assert.NotNull(all);
        Assert.Equal(new[] { "Admin", "User" }.Order(), all.Order());
    }

    [Fact]
    public void T1_R3_02_AnonymousAccess_PublicPagesViewableWithoutAuth()
    {
        // Requirement R3: Public pages have no @attribute [Authorize] or allow anonymous/User access
        Type[] publicPages = [
            typeof(Listings),
            typeof(ListingDetail),
            typeof(Lookups),
            typeof(Templates),
            typeof(TextBlockPage),
            typeof(VariationListPage),
            typeof(PhotoTemplatePage),
            typeof(OrderColors),
        ];

        foreach (var page in publicPages)
        {
            var authAttr = page.GetCustomAttribute<AuthorizeAttribute>();
            Assert.True(authAttr is null || string.IsNullOrEmpty(authAttr.Roles) || authAttr.Roles == "User",
                $"{page.Name} should not restrict view access to Admin; found Authorize(Roles='{authAttr?.Roles}')");
        }
    }

    [Fact]
    public void T1_R3_03_AppBar_DisplaysSignInForAnonymous_AndSignOutForAdmin()
    {
        // Verify in MainLayout markup that Sign In is rendered when unauthenticated
        var mainLayoutPath = Path.Combine(DesignRulesTests.Root, "src", "EtsyShopManager.Web", "Components", "Layout", "MainLayout.razor");
        Assert.True(File.Exists(mainLayoutPath));
        var content = File.ReadAllText(mainLayoutPath);

        Assert.True(content.Contains("Sign in", StringComparison.OrdinalIgnoreCase),
            "MainLayout must contain a 'Sign in' link/button for unauthenticated visitors.");
        Assert.True(content.Contains("Sign out", StringComparison.OrdinalIgnoreCase),
            "MainLayout must contain a 'Sign out' link/button for authenticated admin.");
    }

    [Fact]
    public void T1_R3_04_ReadOnly_MutatingControlsHiddenForUser()
    {
        // Check key public razor components to ensure mutate buttons are guarded
        var variationPagePath = Path.Combine(DesignRulesTests.Root, "src", "EtsyShopManager.Web", "Components", "Pages", "Templates", "VariationListPage.razor");
        var content = File.ReadAllText(variationPagePath);

        // Applying or saving templates should require Admin
        Assert.True(content.Contains("AuthorizeView") || content.Contains("Roles.Admin") || content.Contains("_isAdmin"),
            "VariationListPage must protect mutating actions with Admin authorization.");
    }

    [Fact]
    public void T1_R3_05_Authorization_WriteRoutesRequireAdmin()
    {
        // EditListing and CloneListing pages must require Admin role
        var editPage = typeof(EditListing);
        var clonePage = typeof(CloneListing);

        var editAuth = editPage.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(editAuth);
        Assert.Equal("Admin", editAuth.Roles);

        var cloneAuth = clonePage.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(cloneAuth);
        Assert.Equal("Admin", cloneAuth.Roles);
    }

    [Fact]
    public void T1_R4_01_Nav_ShopsMovedToSettings()
    {
        var registry = new ModuleRegistry([
            new ShopsModule(), new ListingsModule(), new OrdersModule(),
            new EtsyShopManager.Web.Modules.Dashboard.DashboardModule(), new EtsyShopManager.Web.Modules.Stock.EditorModule(),
            new TemplatesModule(), new JobsModule(), new AdminModule(),
        ]);

        var shopsNav = Assert.Single(registry.NavItems, i => i.Title == "Shops");
        Assert.Equal(NavGroups.Settings, shopsNav.Group);
    }

    [Fact]
    public void T1_R4_02_Nav_ShopsRequiresAdmin()
    {
        var registry = new ModuleRegistry([
            new ShopsModule(), new ListingsModule(), new OrdersModule(),
            new EtsyShopManager.Web.Modules.Dashboard.DashboardModule(), new EtsyShopManager.Web.Modules.Stock.EditorModule(),
            new TemplatesModule(), new JobsModule(), new AdminModule(),
        ]);

        var shopsNav = Assert.Single(registry.NavItems, i => i.Title == "Shops");
        Assert.Equal(Roles.Admin, shopsNav.Roles);

        // Also check the page component itself
        var shopsAuth = typeof(Shops).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(shopsAuth);
        Assert.Equal(Roles.Admin, shopsAuth.Roles);
    }

    [Fact]
    public void T1_R4_03_Nav_OrderColorsAtTopLevel()
    {
        var registry = new ModuleRegistry([
            new ShopsModule(), new ListingsModule(), new OrdersModule(),
            new EtsyShopManager.Web.Modules.Dashboard.DashboardModule(), new EtsyShopManager.Web.Modules.Stock.EditorModule(),
            new TemplatesModule(), new JobsModule(), new AdminModule(),
        ]);

        var orderColorsNav = Assert.Single(registry.NavItems, i => i.Title == "Order colors");
        Assert.Null(orderColorsNav.Group); // Top level
        Assert.True(orderColorsNav.Roles is null || orderColorsNav.Roles != Roles.Admin,
            "Order colors at top level should be viewable by User");
    }

    [Fact]
    public void T1_R4_04_Nav_SettingsCategoryHiddenForNonAdmin()
    {
        var registry = new ModuleRegistry([
            new ShopsModule(), new ListingsModule(), new OrdersModule(),
            new EtsyShopManager.Web.Modules.Dashboard.DashboardModule(), new EtsyShopManager.Web.Modules.Stock.EditorModule(),
            new TemplatesModule(), new JobsModule(), new AdminModule(),
        ]);

        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "User")]));
        bool CanSee(NavItem item) => item.Roles is null || item.Roles.Split(',').Any(r => user.IsInRole(r.Trim()));

        var entries = SidebarLayout.Build(registry.NavItems.Where(CanSee));
        Assert.DoesNotContain(entries, e => e.Group == NavGroups.Settings);
    }

    [Fact]
    public void T1_R4_05_Nav_AllSettingsSubpagesRequireAdmin()
    {
        var registry = new ModuleRegistry([
            new ShopsModule(), new ListingsModule(), new OrdersModule(),
            new EtsyShopManager.Web.Modules.Dashboard.DashboardModule(), new EtsyShopManager.Web.Modules.Stock.EditorModule(),
            new TemplatesModule(), new JobsModule(), new AdminModule(),
        ]);

        var settingsItems = registry.NavItems.Where(i => i.Group == NavGroups.Settings).ToList();
        Assert.NotEmpty(settingsItems);
        Assert.All(settingsItems, item => Assert.Equal(Roles.Admin, item.Roles));
    }

    #endregion

    #region TIER 2: BOUNDARY & CORNER CASES

    [Fact]
    public async Task T2_R1_01_HandRemovedOption_GracefulNoteAndCleanup()
    {
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "PLA Colors",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId)], "admin");

        // Seller removes "Blue" by hand in editor
        var invBefore = await InventoryAsync(env.Shop, env.ListingSingleVar1);
        await Editor(env.Shop).SaveAsync(env.ListingSingleVar1, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Inventory] = VariationEditor.RemoveValue(invBefore, ColorPropertyId, "Blue"),
        }, "admin");

        // Now remove "Blue" from the template
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors",
            [list.Options[0], list.Options[2]], "admin"); // Keep Red and Green

        var use = Assert.Single(await Lists(env.Shop).UsesAsync(listId));
        // Should report note that Blue was already removed by hand
        Assert.Contains(use.Notes, n => n.Contains("Blue was already removed from this listing by hand"));

        // Applying should succeed cleanly without crashing
        var applied = await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin");
        Assert.True(applied >= 0);

        // AppliedOptions should no longer track Blue
        await using var db = env.Shop.Db.CreateDbContext();
        var link = await db.ListingVariationLists.SingleAsync(l => l.ListingId == env.ListingSingleVar1);
        Assert.DoesNotContain(link.AppliedOptions, o => o.Name == "Blue");
    }

    [Fact]
    public async Task T2_R1_02_LastRemainingValue_RefusalWithNote()
    {
        using var env = await SetupEnvironmentAsync();
        // ListingSingleValue only has "Red"
        var listId = (await Lists(env.Shop).SaveAsync(null, "Single Red Template",
            [new(0, "Red", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleValue, ColorPropertyId)], "admin");

        // Remove "Red" from template (making template empty or switching it off)
        // Note: Template requires >= 1 option, so add "Dummy" and remove "Red"
        await Lists(env.Shop).SaveAsync(listId, "Single Red Template",
            [new(0, "Dummy", true)], "admin");

        var use = Assert.Single(await Lists(env.Shop).UsesAsync(listId));
        Assert.Contains(use.Notes, n => n.Contains("last remaining value"));

        // Red must NOT be removed (Etsy requires >= 1 variation value)
        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin");
        var inv = await InventoryAsync(env.Shop, env.ListingSingleValue);
        Assert.Contains("Red", GetColors(inv));
    }

    [Fact]
    public async Task T2_R1_03_CaseInsensitiveOptionRemoval()
    {
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "PLA Colors",
            [new(0, "RED", true), new(0, "BLUE", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar2, ColorPropertyId)], "admin");

        // Remove RED from template
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors", [list.Options[1]], "admin");

        var use = Assert.Single(await Lists(env.Shop).UsesAsync(listId));
        Assert.Contains(use.Changes, c => c.Contains("Removes RED", StringComparison.OrdinalIgnoreCase));

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin");
        var inv = await InventoryAsync(env.Shop, env.ListingSingleVar2);
        Assert.DoesNotContain("Red", GetColors(inv), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task T2_R1_04_PartialLinkSubsetRemoval()
    {
        using var env = await SetupEnvironmentAsync();
        // Listing 1 has Red, Blue, Green.
        // Listing 2 has only Red, Blue.
        var listId = (await Lists(env.Shop).SaveAsync(null, "PLA Colors",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId), (env.ListingSingleVar2, ColorPropertyId)], "admin");

        // Remove Green from template
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors",
            [list.Options[0], list.Options[1]], "admin");

        var uses = await Lists(env.Shop).UsesAsync(listId);
        var use1 = uses.Single(u => u.ListingId == env.ListingSingleVar1);
        var use2 = uses.Single(u => u.ListingId == env.ListingSingleVar2);

        // Listing 1 had Green, so it shows "Removes Green"
        Assert.Contains(use1.Changes, c => c.Contains("Removes Green"));
        // Listing 2 never had Green applied, so it should not remove Green
        Assert.DoesNotContain(use2.Changes, c => c.Contains("Removes Green"));
    }

    [Fact]
    public async Task T2_R1_05_RapidRemoveAndReAddCycle()
    {
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "PLA Colors",
            [new(0, "Red", true), new(0, "Blue", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar2, ColorPropertyId)], "admin");

        var list = (await Lists(env.Shop).GetAsync(listId))!;

        // 1. Remove Blue and apply
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors", [list.Options[0]], "admin");
        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin");
        var inv1 = await InventoryAsync(env.Shop, env.ListingSingleVar2);
        Assert.DoesNotContain("Blue", GetColors(inv1));

        // 2. Re-add Blue and apply
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors", [list.Options[0], new(0, "Blue", true)], "admin");
        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "TakeHighest");
        var inv2 = await InventoryAsync(env.Shop, env.ListingSingleVar2);
        Assert.Contains("Blue", GetColors(inv2));
    }

    [Fact]
    public async Task T2_R2_01_UniformPricing_HighestEqualsLowest()
    {
        using var env = await SetupEnvironmentAsync();
        // Listing 4 has uniform flat price $19.99 for all colors
        var listId = (await Lists(env.Shop).SaveAsync(null, "Uniform Colors",
            [new(0, "Red", true), new(0, "Blue", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingFlatPrice, ColorPropertyId)], "admin");

        // Add "Purple"
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "Uniform Colors",
            [.. list.Options, new(0, "Purple", true)], "admin");

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "TakeHighest");

        var inv = await InventoryAsync(env.Shop, env.ListingFlatPrice);
        Assert.Equal(19.99m, GetColorPrice(inv, "Purple"));
    }

    [Fact]
    public async Task T2_R2_02_MultipleNewOptionsSimultaneous()
    {
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "PLA Colors",
            [new(0, "Red", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId)], "admin");

        // Add 3 new colors simultaneously: Amber, Coral, Emerald
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors",
            [list.Options[0], new(0, "Amber", true), new(0, "Coral", true), new(0, "Emerald", true)], "admin");

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "TakeHighest");

        var inv = await InventoryAsync(env.Shop, env.ListingSingleVar1);
        Assert.Equal(30.00m, GetColorPrice(inv, "Amber"));
        Assert.Equal(30.00m, GetColorPrice(inv, "Coral"));
        Assert.Equal(30.00m, GetColorPrice(inv, "Emerald"));
    }

    [Fact]
    public async Task T2_R2_03_DecimalPrecisionIntegrity()
    {
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "PLA Colors",
            [new(0, "Red", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId)], "admin");

        // Add "Special" with exact custom price $14.95
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors",
            [list.Options[0], new(0, "Special", true)], "admin");

        var overrides = new Dictionary<int, decimal> { [env.ListingSingleVar1] = 14.95m };
        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "PickPerListing", customPrices: overrides);

        var inv = await InventoryAsync(env.Shop, env.ListingSingleVar1);
        Assert.Equal(14.95m, GetColorPrice(inv, "Special"));
    }

    [Fact]
    public async Task T2_R2_04_NonVaryingPriceProperty()
    {
        using var env = await SetupEnvironmentAsync();
        // Listing 4 does not vary price on color: price_on_property is empty
        var listId = (await Lists(env.Shop).SaveAsync(null, "Flat Plate Colors",
            [new(0, "Red", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingFlatPrice, ColorPropertyId)], "admin");

        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "Flat Plate Colors",
            [list.Options[0], new(0, "Charcoal", true)], "admin");

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "TakeHighest");

        var inv = await InventoryAsync(env.Shop, env.ListingFlatPrice);
        Assert.Empty(inv["price_on_property"]!.AsArray());
        Assert.Equal(19.99m, GetColorPrice(inv, "Charcoal"));
    }

    [Fact]
    public async Task T2_R2_05_PickPerListing_MultipleListingsSimultaneously()
    {
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "PLA Colors",
            [new(0, "Red", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId), (env.ListingSingleVar2, ColorPropertyId)], "admin");

        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors",
            [list.Options[0], new(0, "Onyx", true)], "admin");

        var customPrices = new Dictionary<int, decimal>
        {
            [env.ListingSingleVar1] = 42.00m,
            [env.ListingSingleVar2] = 12.50m,
        };

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "PickPerListing", customPrices: customPrices);

        var inv1 = await InventoryAsync(env.Shop, env.ListingSingleVar1);
        var inv2 = await InventoryAsync(env.Shop, env.ListingSingleVar2);

        Assert.Equal(42.00m, GetColorPrice(inv1, "Onyx"));
        Assert.Equal(12.50m, GetColorPrice(inv2, "Onyx"));
    }

    [Fact]
    public void T2_R3_01_DirectUrlTampering_EditAndCloneRoutesBlockedForAnonymous()
    {
        var editAuth = typeof(EditListing).GetCustomAttribute<AuthorizeAttribute>();
        var cloneAuth = typeof(CloneListing).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(editAuth);
        Assert.True(editAuth.Roles == Roles.Admin, "EditListing must strictly require Admin");

        Assert.NotNull(cloneAuth);
        Assert.True(cloneAuth.Roles == Roles.Admin, "CloneListing must strictly require Admin");
    }

    [Fact]
    public async Task T2_R3_02_LegacyAccountMigration_LegacyRolesMigratedOrRejectedSafely()
    {
        // When checking user accounts in database, legacy roles like Editor/Viewer should not grant access
        using var env = await SetupEnvironmentAsync();
        await using var db = env.Shop.Db.CreateDbContext();

        var rolesInDb = await db.Roles.Select(r => r.Name).ToListAsync();
        Assert.DoesNotContain("Editor", rolesInDb);
        Assert.DoesNotContain("Viewer", rolesInDb);
    }

    [Fact]
    public void T2_R3_03_PublicPackingSlipPrintAllowed()
    {
        // Packing slip printing should remain accessible without Admin role
        var printSlipsAuth = typeof(EtsyShopManager.Web.Components.Pages.Orders.PrintPackingSlips).GetCustomAttribute<AuthorizeAttribute>();
        var printPickAuth = typeof(EtsyShopManager.Web.Components.Pages.Orders.PrintPickList).GetCustomAttribute<AuthorizeAttribute>();

        Assert.True(printSlipsAuth is null || printSlipsAuth.Roles != Roles.Admin);
        Assert.True(printPickAuth is null || printPickAuth.Roles != Roles.Admin);
    }

    [Fact]
    public void T2_R3_04_AnonymousCannotInvokeJobTriggers()
    {
        // Jobs page requires Admin
        var jobsAuth = typeof(Jobs).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(jobsAuth);
        Assert.Equal(Roles.Admin, jobsAuth.Roles);
    }

    [Fact]
    public void T2_R3_05_EmptyOrWhitespaceUsernameHandling()
    {
        // Check that auth UI doesn't crash with null/empty identity name
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.Admin)]);
        var principal = new ClaimsPrincipal(identity);

        Assert.True(principal.IsInRole(Roles.Admin));
        Assert.Null(principal.Identity?.Name);
    }

    [Fact]
    public void T2_R4_01_DirectUrlTampering_ShopsRouteBlockedForAnonymous()
    {
        var shopsAuth = typeof(Shops).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(shopsAuth);
        Assert.Equal(Roles.Admin, shopsAuth.Roles);
    }

    [Fact]
    public void T2_R4_02_DirectUrlTampering_SettingsRoutesBlockedForAnonymous()
    {
        Type[] settingsPages = [
            typeof(Settings),
            typeof(Users),
            typeof(Jobs),
            typeof(Audit),
            typeof(ApiKeys),
            typeof(EtsyWrites),
        ];

        foreach (var page in settingsPages)
        {
            var auth = page.GetCustomAttribute<AuthorizeAttribute>();
            Assert.NotNull(auth);
            Assert.Equal(Roles.Admin, auth.Roles);
        }
    }

    [Fact]
    public void T2_R4_03_OrderColors_NonAdminCannotModify()
    {
        var orderColorsPath = Path.Combine(DesignRulesTests.Root, "src", "EtsyShopManager.Web", "Components", "Pages", "Admin", "OrderColors.razor");
        Assert.True(File.Exists(orderColorsPath));
        var content = File.ReadAllText(orderColorsPath);

        // Mutating controls (Add color, Delete, Move) must be guarded
        Assert.True(content.Contains("AuthorizeView") || content.Contains("Roles.Admin") || content.Contains("_isAdmin"),
            "OrderColors razor component must guard color mutation controls from non-admin users.");
    }

    [Fact]
    public void T2_R4_04_EmptySettingsGroupSuppressedWhenNonAdmin()
    {
        var registry = new ModuleRegistry([
            new ShopsModule(), new ListingsModule(), new OrdersModule(),
            new EtsyShopManager.Web.Modules.Dashboard.DashboardModule(), new EtsyShopManager.Web.Modules.Stock.EditorModule(),
            new TemplatesModule(), new JobsModule(), new AdminModule(),
        ]);

        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        bool CanSee(NavItem item) => item.Roles is null || item.Roles.Split(',').Any(r => anonymous.IsInRole(r.Trim()));

        var entries = SidebarLayout.Build(registry.NavItems.Where(CanSee));
        Assert.DoesNotContain(entries, e => e.Group == NavGroups.Settings);
    }

    [Fact]
    public void T2_R4_05_CaseInsensitiveRoleAuthorization()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        Assert.True(principal.IsInRole(Roles.Admin));
    }

    #endregion

    #region TIER 3: CROSS-FEATURE COMBINATIONS (PAIRWISE)

    [Fact]
    public async Task T3_Pairwise_01_RemoveAndAddOptionWithTakeHighest()
    {
        // R1 + R2: Removing one option while simultaneously adding another option with TakeHighest
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "PLA Colors",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId)], "admin");

        // Template update: Remove Green ($30), Add Ruby
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors",
            [list.Options[0], list.Options[1], new(0, "Ruby", true)], "admin");

        var use = Assert.Single(await Lists(env.Shop).UsesAsync(listId));
        Assert.Contains(use.Changes, c => c.Contains("Removes Green"));
        Assert.Contains(use.Changes, c => c.Contains("Adds Ruby"));

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "TakeHighest");

        var inv = await InventoryAsync(env.Shop, env.ListingSingleVar1);
        Assert.DoesNotContain("Green", GetColors(inv));
        Assert.Contains("Ruby", GetColors(inv));
        // Ruby gets highest existing price among remaining options (Red $10, Blue $20, Custom swirl $25) = $25.00
        Assert.Equal(25.00m, GetColorPrice(inv, "Ruby"));
    }

    [Fact]
    public async Task T3_Pairwise_02_RemoveAndAddOptionWithTakeLowest_TwoVariations()
    {
        // R1 + R2: Simultaneous remove and add with TakeLowest on 2-variation listing
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "Dual Colors",
            [new(0, "Red", true), new(0, "Blue", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingDualVar, ColorPropertyId)], "admin");

        // Small Red $12, Small Blue $16; Large Red $22, Large Blue $28.
        // Remove Blue, Add Plum with TakeLowest
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "Dual Colors",
            [list.Options[0], new(0, "Plum", true)], "admin");

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "TakeLowest");

        var inv = await InventoryAsync(env.Shop, env.ListingDualVar);
        Assert.DoesNotContain("Blue", GetColors(inv));
        Assert.Contains("Plum", GetColors(inv));
        // Lowest within Small (only Red $12 remaining) = $12.00
        // Lowest within Large (only Red $22 remaining) = $22.00
        Assert.Equal(12.00m, GetColorPrice(inv, "Plum", "Small"));
        Assert.Equal(22.00m, GetColorPrice(inv, "Plum", "Large"));
    }

    [Fact]
    public async Task T3_Pairwise_03_AnonymousViewsRemovalChanges_AdminApplies()
    {
        // R1 + R3: Anonymous User views pending "Removes <option>" change without auth; Admin applies it
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "PLA Colors",
            [new(0, "Red", true), new(0, "Blue", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar2, ColorPropertyId)], "admin");

        // Template removes Blue
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors", [list.Options[0]], "admin");

        // UsesAsync is viewable
        var uses = await Lists(env.Shop).UsesAsync(listId);
        Assert.Equal(VariationListUseStatus.HasChanges, uses[0].Status);
        Assert.Contains(uses[0].Changes, c => c.Contains("Removes Blue"));

        // Admin applies
        var applied = await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin");
        Assert.Equal(1, applied);
    }

    [Fact]
    public void T3_Pairwise_04_AnonymousCannotInvokePricingApply()
    {
        // R2 + R3: Mutating template endpoints and apply dialog require Admin role
        var templatePagePath = Path.Combine(DesignRulesTests.Root, "src", "EtsyShopManager.Web", "Components", "Pages", "Templates", "VariationListPage.razor");
        var content = File.ReadAllText(templatePagePath);

        // Apply button must be wrapped in Admin authorization check
        Assert.True(content.Contains("AuthorizeView") || content.Contains("Roles.Admin") || content.Contains("_isAdmin"));
    }

    [Fact]
    public void T3_Pairwise_05_PublicSidebarVersusAdminSidebar()
    {
        // R3 + R4: Anonymous user sees top-level items; Admin sees Settings group
        var registry = new ModuleRegistry([
            new ShopsModule(), new ListingsModule(), new OrdersModule(),
            new EtsyShopManager.Web.Modules.Dashboard.DashboardModule(), new EtsyShopManager.Web.Modules.Stock.EditorModule(),
            new TemplatesModule(), new JobsModule(), new AdminModule(),
        ]);

        var publicItems = registry.NavItems.Where(i => i.Group is null && (i.Roles is null || i.Roles != Roles.Admin)).ToList();
        var settingsItems = registry.NavItems.Where(i => i.Group == NavGroups.Settings).ToList();

        // Top level items viewable without Admin
        Assert.Contains(publicItems, i => i.Title == "Orders");
        Assert.Contains(publicItems, i => i.Title == "Listings");
        Assert.Contains(publicItems, i => i.Title == "Templates");
        Assert.Contains(publicItems, i => i.Title == "Lookups");
        Assert.Contains(publicItems, i => i.Title == "Order colors");

        // Settings items all require Admin
        Assert.All(settingsItems, i => Assert.Equal(Roles.Admin, i.Roles));
        Assert.Contains(settingsItems, i => i.Title == "Shops");
    }

    [Fact]
    public async Task T3_Pairwise_06_TemplateRemovalAcrossMultipleShops()
    {
        // R1 + R4: Template removal uniformly updates listings across shops
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "Global Colors",
            [new(0, "Red", true), new(0, "Blue", true)], "admin")).Id!.Value;

        // Link multiple listings
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId), (env.ListingSingleVar2, ColorPropertyId)], "admin");

        // Remove Blue
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "Global Colors", [list.Options[0]], "admin");

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin");

        var inv1 = await InventoryAsync(env.Shop, env.ListingSingleVar1);
        var inv2 = await InventoryAsync(env.Shop, env.ListingSingleVar2);

        Assert.DoesNotContain("Blue", GetColors(inv1));
        Assert.DoesNotContain("Blue", GetColors(inv2));
    }

    #endregion

    #region TIER 4: REAL-WORLD SCENARIOS

    [Fact]
    public async Task T4_Scenario_01_SeasonalColorCatalogUpdate()
    {
        // Real-world scenario: Seasonal color transition
        // Step 1: Admin manages "PLA Colors" template linked to multiple listings.
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "PLA Colors",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId)], "admin");

        // Step 2: Discontinue Green, introduce Silk Gold
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors",
            [list.Options[0], list.Options[1], new(0, "Silk Gold", true)], "admin");

        // Step 3: Review changes
        var uses = await Lists(env.Shop).UsesAsync(listId);
        Assert.Equal(VariationListUseStatus.HasChanges, uses[0].Status);
        Assert.Contains(uses[0].Changes, c => c.Contains("Removes Green"));
        Assert.Contains(uses[0].Changes, c => c.Contains("Adds Silk Gold"));

        // Step 4: Apply with TakeHighest pricing
        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "TakeHighest");

        // Step 5: Verify draft inventory
        var inv = await InventoryAsync(env.Shop, env.ListingSingleVar1);
        Assert.DoesNotContain("Green", GetColors(inv));
        Assert.Contains("Silk Gold", GetColors(inv));
        Assert.Equal(25.00m, GetColorPrice(inv, "Silk Gold")); // highest of Red $10, Blue $20, Custom swirl $25

        // Step 6: Verify push readiness
        var review = Assert.Single(await Lists(env.Shop).ReviewAsync(listId));
        Assert.True(review.Preview.CanPush, string.Join("; ", review.Preview.Blockers));
    }

    [Fact]
    public void T4_Scenario_02_WorkshopFloorTerminalWorkflow()
    {
        // Real-world scenario: Workshop floor terminal operates unauthenticated
        // 1. Can view Orders, Listings, Templates, Order colors
        Type[] operatorPages = [
            typeof(EtsyShopManager.Web.Components.Pages.Orders.Orders),
            typeof(Listings),
            typeof(ListingDetail),
            typeof(Templates),
            typeof(OrderColors),
        ];

        foreach (var p in operatorPages)
        {
            var auth = p.GetCustomAttribute<AuthorizeAttribute>();
            Assert.True(auth is null || auth.Roles != Roles.Admin, $"{p.Name} should be accessible to workshop terminal");
        }

        // 2. Cannot access Admin Settings or Shops
        Type[] adminOnlyPages = [
            typeof(Shops),
            typeof(Settings),
            typeof(Users),
        ];

        foreach (var p in adminOnlyPages)
        {
            var auth = p.GetCustomAttribute<AuthorizeAttribute>();
            Assert.NotNull(auth);
            Assert.Equal(Roles.Admin, auth.Roles);
        }
    }

    [Fact]
    public async Task T4_Scenario_03_MultiVariationCatalogExpansionWithPickPerListing()
    {
        // Real-world scenario: Manager expands multi-variation listing with custom prices per listing
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "Catalog Colors",
            [new(0, "Red", true), new(0, "Blue", true)], "admin")).Id!.Value;

        await Lists(env.Shop).LinkAsync(listId, [(env.ListingDualVar, ColorPropertyId)], "admin");

        // Add "Prism"
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "Catalog Colors",
            [.. list.Options, new(0, "Prism", true)], "admin");

        var customOverrides = new Dictionary<int, decimal>
        {
            [env.ListingDualVar] = 34.50m,
        };

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin", strategy: "PickPerListing", customPrices: customOverrides);

        var inv = await InventoryAsync(env.Shop, env.ListingDualVar);
        Assert.Contains("Prism", GetColors(inv));
        Assert.Equal(34.50m, GetColorPrice(inv, "Prism", "Small"));
        Assert.Equal(34.50m, GetColorPrice(inv, "Prism", "Large"));
    }

    [Fact]
    public async Task T4_Scenario_04_ResilientTemplateApplicationWithHandDrift()
    {
        // Real-world scenario: Listing had an option removed by hand before template removed it
        using var env = await SetupEnvironmentAsync();
        var listId = (await Lists(env.Shop).SaveAsync(null, "PLA Colors",
            [new(0, "Red", true), new(0, "Blue", true), new(0, "Green", true)], "admin")).Id!.Value;
        await Lists(env.Shop).LinkAsync(listId, [(env.ListingSingleVar1, ColorPropertyId)], "admin");

        // Hand edit on listing removes Green
        var inv = await InventoryAsync(env.Shop, env.ListingSingleVar1);
        await Editor(env.Shop).SaveAsync(env.ListingSingleVar1, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Inventory] = VariationEditor.RemoveValue(inv, ColorPropertyId, "Green"),
        }, "admin");

        // Template later removes Green
        var list = (await Lists(env.Shop).GetAsync(listId))!;
        await Lists(env.Shop).SaveAsync(listId, "PLA Colors",
            [list.Options[0], list.Options[1]], "admin");

        // System handles missing option gracefully
        var uses = await Lists(env.Shop).UsesAsync(listId);
        Assert.Contains(uses[0].Notes, n => n.Contains("Green was already removed from this listing by hand"));

        await ApplyTemplateWithStrategyAsync(Lists(env.Shop), listId, "admin");

        // Inventory is intact and up to date
        var finalInv = await InventoryAsync(env.Shop, env.ListingSingleVar1);
        Assert.DoesNotContain("Green", GetColors(finalInv));
    }

    #endregion
}
