using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EtsyShopManager.Web.Components.Pages.Listings;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using Xunit;

namespace EtsyShopManager.Tests;

public class ChallengerM15VariationLockingTests
{
    private const long ColorPropId = VariationEditor.CustomProperty1;      // 513
    private const long MaterialPropId = VariationEditor.CustomProperty2;   // 514

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

    private static JsonObject CreateSingleVariationInventory() => VariationEditor.Canonical(new JsonObject
    {
        ["price_on_property"] = new JsonArray(ColorPropId),
        ["quantity_on_property"] = new JsonArray(),
        ["sku_on_property"] = new JsonArray(),
        ["readiness_state_on_property"] = new JsonArray(),
        ["products"] = new JsonArray(
            Product("SKU-RED", 1000, 5, true, Pv(ColorPropId, "Color", "Red", 1)),
            Product("SKU-GRN", 1200, 5, true, Pv(ColorPropId, "Color", "Green", 2)),
            Product("SKU-BLU", 1500, 5, true, Pv(ColorPropId, "Color", "Blue", 3))),
    });

    private static JsonObject CreateTwoVariationInventory() => VariationEditor.Canonical(new JsonObject
    {
        ["price_on_property"] = new JsonArray(ColorPropId),
        ["quantity_on_property"] = new JsonArray(),
        ["sku_on_property"] = new JsonArray(),
        ["readiness_state_on_property"] = new JsonArray(),
        ["products"] = new JsonArray(
            Product("SKU-RED-WOOD", 1000, 5, true, Pv(ColorPropId, "Color", "Red", 1), Pv(MaterialPropId, "Material", "Wood", 10)),
            Product("SKU-RED-METL", 1000, 5, true, Pv(ColorPropId, "Color", "Red", 1), Pv(MaterialPropId, "Material", "Metal", 20)),
            Product("SKU-BLU-WOOD", 1500, 5, true, Pv(ColorPropId, "Color", "Blue", 2), Pv(MaterialPropId, "Material", "Wood", 10)),
            Product("SKU-BLU-METL", 1500, 5, true, Pv(ColorPropId, "Color", "Blue", 2), Pv(MaterialPropId, "Material", "Metal", 20))),
    });

    private static async Task<string> RenderEditorHtmlAsync(
        JsonObject inventory,
        IReadOnlyList<VariationListLink>? links = null,
        IReadOnlyList<long>? order = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, TestJsRuntime>();
        services.AddSingleton<NavigationManager>(new TestNavigation("http://localhost/"));
        services.AddMudServices();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Inventory"] = inventory,
                ["Links"] = links,
                ["Order"] = order,
            });
            var output = await renderer.RenderComponentAsync<VariationsEditor>(parameters);
            return output.ToHtmlString();
        });
    }

    // =========================================================================
    // Part 1: Attempting mutations on linked variations
    // Confirm all 6 operations throw InvalidOperationException
    // =========================================================================

    [Theory]
    [InlineData(true)]   // test via lockedPropertyIds
    [InlineData(false)]  // test via links overload
    public void RemoveValue_OnLinkedVariation_Throws_InvalidOperationException(bool useLockedPropertyIds)
    {
        var inv = CreateSingleVariationInventory();
        var links = new[] { new VariationListLink(1, "Palette", ColorPropId) };
        var lockedIds = new[] { ColorPropId };

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            if (useLockedPropertyIds)
                VariationEditor.RemoveValue(inv, ColorPropId, "Red", lockedPropertyIds: lockedIds);
            else
                VariationEditor.RemoveValue(inv, ColorPropId, "Red", links);
        });

        Assert.Contains(ColorPropId.ToString(), ex.Message);
        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddValue_OnLinkedVariation_Throws_InvalidOperationException(bool useLockedPropertyIds)
    {
        var inv = CreateSingleVariationInventory();
        var links = new[] { new VariationListLink(1, "Palette", ColorPropId) };
        var lockedIds = new[] { ColorPropId };

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            if (useLockedPropertyIds)
                VariationEditor.AddValue(inv, ColorPropId, "Yellow", lockedPropertyIds: lockedIds);
            else
                VariationEditor.AddValue(inv, ColorPropId, "Yellow", links);
        });

        Assert.Contains(ColorPropId.ToString(), ex.Message);
        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RenameValue_OnLinkedVariation_Throws_InvalidOperationException(bool useLockedPropertyIds)
    {
        var inv = CreateSingleVariationInventory();
        var links = new[] { new VariationListLink(1, "Palette", ColorPropId) };
        var lockedIds = new[] { ColorPropId };

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            if (useLockedPropertyIds)
                VariationEditor.RenameValue(inv, ColorPropId, "Red", "Crimson", lockedPropertyIds: lockedIds);
            else
                VariationEditor.RenameValue(inv, ColorPropId, "Red", "Crimson", links);
        });

        Assert.Contains(ColorPropId.ToString(), ex.Message);
        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RemoveVariation_OnLinkedVariation_Throws_InvalidOperationException(bool useLockedPropertyIds)
    {
        var inv = CreateTwoVariationInventory();
        var links = new[] { new VariationListLink(1, "Palette", ColorPropId) };
        var lockedIds = new[] { ColorPropId };

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            if (useLockedPropertyIds)
                VariationEditor.RemoveVariation(inv, ColorPropId, lockedPropertyIds: lockedIds);
            else
                VariationEditor.RemoveVariation(inv, ColorPropId, links);
        });

        Assert.Contains(ColorPropId.ToString(), ex.Message);
        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReorderValues_OnLinkedVariation_Throws_InvalidOperationException(bool useLockedPropertyIds)
    {
        var inv = CreateSingleVariationInventory();
        var links = new[] { new VariationListLink(1, "Palette", ColorPropId) };
        var lockedIds = new[] { ColorPropId };
        string[] newOrder = ["Blue", "Green", "Red"];

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            if (useLockedPropertyIds)
                VariationEditor.ReorderValues(inv, ColorPropId, newOrder, lockedPropertyIds: lockedIds);
            else
                VariationEditor.ReorderValues(inv, ColorPropId, newOrder, links);
        });

        Assert.Contains(ColorPropId.ToString(), ex.Message);
        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RenameVariation_OnLinkedVariation_Throws_InvalidOperationException(bool useLockedPropertyIds)
    {
        var inv = CreateSingleVariationInventory();
        var links = new[] { new VariationListLink(1, "Palette", ColorPropId) };
        var lockedIds = new[] { ColorPropId };

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            if (useLockedPropertyIds)
                VariationEditor.RenameVariation(inv, ColorPropId, "Hue", lockedPropertyIds: lockedIds);
            else
                VariationEditor.RenameVariation(inv, ColorPropId, "Hue", links);
        });

        Assert.Contains(ColorPropId.ToString(), ex.Message);
        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structural_Mutations_Throw_InvalidOperationException_Even_If_Input_Would_Otherwise_Be_Invalid()
    {
        // Adversarial challenge: locking assertion must precede input validation so callers cannot
        // probe or leak validation errors on locked variations.
        var inv = CreateSingleVariationInventory();
        var links = new[] { new VariationListLink(1, "Palette", ColorPropId) };

        // 1. Remove non-existent value -> still throws InvalidOperationException, NOT ArgumentException
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RemoveValue(inv, ColorPropId, "NonExistentColor", links));

        // 2. Add existing duplicate value -> throws InvalidOperationException, NOT ArgumentException
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.AddValue(inv, ColorPropId, "Red", links));

        // 3. Rename to existing duplicate value -> throws InvalidOperationException, NOT ArgumentException
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RenameValue(inv, ColorPropId, "Red", "Blue", links));

        // 4. Reorder with bogus value list -> throws InvalidOperationException, NOT ArgumentException
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.ReorderValues(inv, ColorPropId, ["Bogus1", "Bogus2"], links));

        // 5. Rename variation to empty string -> throws InvalidOperationException, NOT ArgumentException
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RenameVariation(inv, ColorPropId, "", links));
    }

    // =========================================================================
    // Part 2: Unlinking
    // Remove link, confirm all structural mutations now succeed without error
    // =========================================================================

    [Fact]
    public void Unlinking_Allows_All_Six_Structural_Mutations_To_Succeed_Sequentially()
    {
        var inv = CreateTwoVariationInventory();
        var links = new[] { new VariationListLink(1, "Color Palette", ColorPropId) };

        // Precondition check: while linked, all mutations fail
        Assert.Throws<InvalidOperationException>(() => VariationEditor.AddValue(inv, ColorPropId, "Yellow", links));
        Assert.Throws<InvalidOperationException>(() => VariationEditor.RenameValue(inv, ColorPropId, "Red", "Ruby", links));
        Assert.Throws<InvalidOperationException>(() => VariationEditor.ReorderValues(inv, ColorPropId, ["Blue", "Red"], links));
        Assert.Throws<InvalidOperationException>(() => VariationEditor.RemoveValue(inv, ColorPropId, "Red", links));
        Assert.Throws<InvalidOperationException>(() => VariationEditor.RenameVariation(inv, ColorPropId, "Colorway", links));
        Assert.Throws<InvalidOperationException>(() => VariationEditor.RemoveVariation(inv, ColorPropId, links));

        // --- UNLINK (links = null or empty) ---
        IReadOnlyList<VariationListLink> unlinked = [];

        // 1. AddValue succeeds
        var step1 = VariationEditor.AddValue(inv, ColorPropId, "Yellow", unlinked);
        var colorValues1 = VariationEditor.Variations(step1).First(v => v.PropertyId == ColorPropId).Values;
        Assert.Equal(["Red", "Blue", "Yellow"], colorValues1);

        // 2. RenameValue succeeds
        var step2 = VariationEditor.RenameValue(step1, ColorPropId, "Red", "Ruby", unlinked);
        var colorValues2 = VariationEditor.Variations(step2).First(v => v.PropertyId == ColorPropId).Values;
        Assert.Contains("Ruby", colorValues2);
        Assert.DoesNotContain("Red", colorValues2);
        Assert.Equal(3, colorValues2.Count);

        // 3. ReorderValues succeeds
        var step3 = VariationEditor.ReorderValues(step2, ColorPropId, ["Yellow", "Ruby", "Blue"], unlinked);
        var colorValues3 = VariationEditor.Variations(step3).First(v => v.PropertyId == ColorPropId).Values;
        Assert.Equal(["Yellow", "Ruby", "Blue"], colorValues3);

        // 4. RemoveValue succeeds
        var step4 = VariationEditor.RemoveValue(step3, ColorPropId, "Yellow", unlinked);
        var colorValues4 = VariationEditor.Variations(step4).First(v => v.PropertyId == ColorPropId).Values;
        Assert.Equal(["Ruby", "Blue"], colorValues4);

        // 5. RenameVariation succeeds
        var step5 = VariationEditor.RenameVariation(step4, ColorPropId, "Shade", unlinked);
        Assert.Equal("Shade", VariationEditor.Variations(step5).First(v => v.PropertyId == ColorPropId).Name);

        // 6. RemoveVariation succeeds
        var step6 = VariationEditor.RemoveVariation(step5, ColorPropId, unlinked);
        Assert.Single(VariationEditor.Variations(step6));
        Assert.Equal(MaterialPropId, VariationEditor.Variations(step6)[0].PropertyId);
    }

    // =========================================================================
    // Part 3: Mixed variations on a 2-variation listing
    // Variation 1 linked, Variation 2 unlinked
    // Confirm Variation 1 is locked while Variation 2 allows full editing
    // =========================================================================

    [Fact]
    public void TwoVariationListing_Variation1_Linked_Variation2_Unlinked_LocksV1_AllowsFullEditingOfV2()
    {
        var inv = CreateTwoVariationInventory();

        // Variation 1 (ColorPropId = 513) is LINKED
        // Variation 2 (MaterialPropId = 514) is UNLINKED
        var links = new[] { new VariationListLink(101, "Managed Colors", ColorPropId) };

        // -------------------------------------------------------------
        // A. Confirm Variation 1 (Color) is strictly locked
        // -------------------------------------------------------------
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.AddValue(inv, ColorPropId, "Green", links));
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RemoveValue(inv, ColorPropId, "Red", links));
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RenameValue(inv, ColorPropId, "Red", "Ruby", links));
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.ReorderValues(inv, ColorPropId, ["Blue", "Red"], links));
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RenameVariation(inv, ColorPropId, "Hue", links));
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RemoveVariation(inv, ColorPropId, links));

        // -------------------------------------------------------------
        // B. Confirm Variation 2 (Material) allows full editing
        // -------------------------------------------------------------

        // 1. AddValue on Variation 2 succeeds and expands product matrix (2 colors x 3 materials = 6 products)
        var v2Added = VariationEditor.AddValue(inv, MaterialPropId, "Glass", links);
        var varsAfterAdd = VariationEditor.Variations(v2Added);
        Assert.Equal(["Red", "Blue"], varsAfterAdd.First(v => v.PropertyId == ColorPropId).Values);
        Assert.Equal(["Wood", "Metal", "Glass"], varsAfterAdd.First(v => v.PropertyId == MaterialPropId).Values);
        var productsAfterAdd = v2Added["products"]!.AsArray();
        Assert.Equal(6, productsAfterAdd.Count);

        // 2. RenameValue on Variation 2 succeeds and updates all affected combination products
        var v2Renamed = VariationEditor.RenameValue(v2Added, MaterialPropId, "Wood", "Timber", links);
        var matValuesAfterRename = VariationEditor.Variations(v2Renamed).First(v => v.PropertyId == MaterialPropId).Values;
        Assert.Contains("Timber", matValuesAfterRename);
        Assert.DoesNotContain("Wood", matValuesAfterRename);
        Assert.Equal(3, matValuesAfterRename.Count);

        // 3. ReorderValues on Variation 2 succeeds
        var v2Reordered = VariationEditor.ReorderValues(v2Renamed, MaterialPropId, ["Glass", "Timber", "Metal"], links);
        var matValuesAfterReorder = VariationEditor.Variations(v2Reordered).First(v => v.PropertyId == MaterialPropId).Values;
        Assert.Equal(["Glass", "Timber", "Metal"], matValuesAfterReorder);

        // 4. RemoveValue on Variation 2 succeeds and contracts matrix back to 4 products
        var v2RemovedVal = VariationEditor.RemoveValue(v2Reordered, MaterialPropId, "Glass", links);
        var matValuesAfterRemove = VariationEditor.Variations(v2RemovedVal).First(v => v.PropertyId == MaterialPropId).Values;
        Assert.Equal(["Timber", "Metal"], matValuesAfterRemove);
        Assert.Equal(4, v2RemovedVal["products"]!.AsArray().Count);

        // 5. RenameVariation on Variation 2 succeeds
        var v2RenamedVar = VariationEditor.RenameVariation(v2RemovedVal, MaterialPropId, "Texture", links);
        Assert.Equal("Texture", VariationEditor.Variations(v2RenamedVar).First(v => v.PropertyId == MaterialPropId).Name);

        // 6. RemoveVariation on Variation 2 succeeds, leaving only the locked Variation 1 intact
        var v2RemovedVar = VariationEditor.RemoveVariation(v2RenamedVar, MaterialPropId, links);
        var remainingVars = VariationEditor.Variations(v2RemovedVar);
        Assert.Single(remainingVars);
        Assert.Equal(ColorPropId, remainingVars[0].PropertyId);
        Assert.Equal(["Red", "Blue"], remainingVars[0].Values);

        // Variation 1 is STILL locked in the single-variation state
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.AddValue(v2RemovedVar, ColorPropId, "Yellow", links));
    }

    [Fact]
    public void TwoVariationListing_SymmetricCheck_Variation1_Unlinked_Variation2_Linked()
    {
        var inv = CreateTwoVariationInventory();

        // Inverse test: Variation 1 (Color) is UNLINKED, Variation 2 (Material) is LINKED
        var links = new[] { new VariationListLink(202, "Standard Materials", MaterialPropId) };

        // -------------------------------------------------------------
        // A. Confirm Variation 2 (Material) is strictly locked
        // -------------------------------------------------------------
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.AddValue(inv, MaterialPropId, "Plastic", links));
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RemoveValue(inv, MaterialPropId, "Wood", links));
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RenameValue(inv, MaterialPropId, "Wood", "Oak", links));
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.ReorderValues(inv, MaterialPropId, ["Metal", "Wood"], links));
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RenameVariation(inv, MaterialPropId, "Substance", links));
        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RemoveVariation(inv, MaterialPropId, links));

        // -------------------------------------------------------------
        // B. Confirm Variation 1 (Color) allows full editing
        // -------------------------------------------------------------
        var v1Added = VariationEditor.AddValue(inv, ColorPropId, "Emerald", links);
        Assert.Equal(["Red", "Blue", "Emerald"], VariationEditor.Variations(v1Added).First(v => v.PropertyId == ColorPropId).Values);

        var v1Renamed = VariationEditor.RenameValue(v1Added, ColorPropId, "Red", "Crimson", links);
        var v1ColorValues = VariationEditor.Variations(v1Renamed).First(v => v.PropertyId == ColorPropId).Values;
        Assert.Contains("Crimson", v1ColorValues);
        Assert.DoesNotContain("Red", v1ColorValues);
        Assert.Equal(3, v1ColorValues.Count);

        var v1Reordered = VariationEditor.ReorderValues(v1Renamed, ColorPropId, ["Emerald", "Blue", "Crimson"], links);
        Assert.Equal(["Emerald", "Blue", "Crimson"], VariationEditor.Variations(v1Reordered).First(v => v.PropertyId == ColorPropId).Values);

        var v1RemovedVal = VariationEditor.RemoveValue(v1Reordered, ColorPropId, "Emerald", links);
        Assert.Equal(["Blue", "Crimson"], VariationEditor.Variations(v1RemovedVal).First(v => v.PropertyId == ColorPropId).Values);

        var v1RenamedVar = VariationEditor.RenameVariation(v1RemovedVal, ColorPropId, "Finish", links);
        Assert.Equal("Finish", VariationEditor.Variations(v1RenamedVar).First(v => v.PropertyId == ColorPropId).Name);

        var v1RemovedVar = VariationEditor.RemoveVariation(v1RenamedVar, ColorPropId, links);
        Assert.Single(VariationEditor.Variations(v1RemovedVar));
        Assert.Equal(MaterialPropId, VariationEditor.Variations(v1RemovedVar)[0].PropertyId);
    }

    // =========================================================================
    // Part 4: Razor UI Component Verification (VariationsEditor.razor)
    // Verify UI reflects locking on linked variation and freedom on unlinked
    // =========================================================================

    [Fact]
    public async Task VariationsEditor_UI_Mixed_Shows_Lock_Visuals_On_V1_And_Active_Controls_On_V2()
    {
        var inv = CreateTwoVariationInventory();
        var links = new[] { new VariationListLink(42, "Palette Template", ColorPropId) };

        var html = await RenderEditorHtmlAsync(inv, links);

        // Color (locked):
        // 1. Placeholder explains template control
        Assert.Contains("placeholder=\"Values controlled by template\"", html);
        // 2. Explanatory caption displayed
        Assert.Contains("Value order is controlled by template Palette Template", html);
        // 3. Arrow buttons for Color values are omitted
        Assert.DoesNotContain("Move Red earlier", html);
        Assert.DoesNotContain("Move Red later", html);
        Assert.DoesNotContain("Move Blue earlier", html);
        Assert.DoesNotContain("Move Blue later", html);

        // Material (unlocked):
        // 1. Add input is enabled with default placeholder
        Assert.Contains("placeholder=\"Add to Material\"", html);
        Assert.DoesNotMatch(@"<input[^>]*disabled[^>]*placeholder=""Add to Material""", html);
        // 2. Arrow buttons for Material values are present
        Assert.Contains("Move Wood later", html);
        Assert.Contains("Move Metal earlier", html);
        // 3. Chip close buttons and title="Select to rename" are present for Material
        Assert.Contains("mud-chip-close-button", html);
        Assert.Contains("Select to rename", html);
    }

    [Fact]
    public async Task VariationsEditor_UI_Dynamic_Unlinking_Restores_Controls_Immediately()
    {
        var inv = CreateTwoVariationInventory();
        var links = new[] { new VariationListLink(42, "Palette Template", ColorPropId) };

        // 1. Render linked
        var lockedHtml = await RenderEditorHtmlAsync(inv, links);
        Assert.Contains("placeholder=\"Values controlled by template\"", lockedHtml);
        Assert.DoesNotContain("Move Red later", lockedHtml);

        // 2. Render unlinked (links empty)
        var unlinkedHtml = await RenderEditorHtmlAsync(inv, links: []);
        Assert.Contains("placeholder=\"Add to Color\"", unlinkedHtml);
        Assert.Contains("placeholder=\"Add to Material\"", unlinkedHtml);
        Assert.Contains("Move Red later", unlinkedHtml);
        Assert.Contains("Move Wood later", unlinkedHtml);
        Assert.DoesNotContain("Values controlled by template", unlinkedHtml);
    }

    // =========================================================================
    // Part 5: Non-structural mutations (Offering prices, availability, varies)
    // Confirm these remain 100% editable even when variation is locked
    // =========================================================================

    [Fact]
    public void NonStructural_Mutations_Remain_Fully_Functional_On_Locked_Variations()
    {
        var inv = CreateTwoVariationInventory();
        var links = new[] { new VariationListLink(1, "Color Palette", ColorPropId) };

        // 1. Toggling price_on_property succeeds on locked variation
        var toggledPrice = VariationEditor.SetVaries(inv, VariationEditor.PriceOnProperty, ColorPropId, false);
        var priceProps = (toggledPrice[VariationEditor.PriceOnProperty] as JsonArray)!.Select(x => (long)x!).ToList();
        Assert.DoesNotContain(ColorPropId, priceProps);

        // Re-enabling price varies succeeds
        var reenabledPrice = VariationEditor.SetVaries(toggledPrice, VariationEditor.PriceOnProperty, ColorPropId, true);
        var reenabledProps = (reenabledPrice[VariationEditor.PriceOnProperty] as JsonArray)!.Select(x => (long)x!).ToList();
        Assert.Contains(ColorPropId, reenabledProps);

        // 2. Toggling readiness_state_on_property succeeds on locked variation
        var toggledProc = VariationEditor.SetVaries(inv, VariationEditor.ProcessingOnProperty, ColorPropId, true);
        var procProps = (toggledProc[VariationEditor.ProcessingOnProperty] as JsonArray)!.Select(x => (long)x!).ToList();
        Assert.Contains(ColorPropId, procProps);

        // 3. Enabling/disabling combination product offerings succeeds
        var disabledProduct = VariationEditor.SetAvailable(inv, 0, false);
        Assert.False((bool)disabledProduct["products"]![0]!["offerings"]![0]!["is_enabled"]!);

        // 4. SetValueAvailable succeeds on locked variation's values
        var disabledColorVal = VariationEditor.SetValueAvailable(inv, ColorPropId, "Red", false);
        Assert.False(VariationEditor.IsValueAvailable(disabledColorVal, ColorPropId, "Red"));
        Assert.True(VariationEditor.IsValueAvailable(disabledColorVal, ColorPropId, "Blue"));
    }
}
