using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Web.Components.Pages.Listings;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using Xunit;

namespace EtsyShopManager.Tests;

public class VariationLockedStructureTests
{
    private const long SizePropId = 100L;
    private const long CustomColorPropId = VariationEditor.CustomProperty1;

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

    private static JsonObject SingleVariationInventory() => VariationEditor.Canonical(new JsonObject
    {
        ["price_on_property"] = new JsonArray(CustomColorPropId),
        ["quantity_on_property"] = new JsonArray(),
        ["sku_on_property"] = new JsonArray(),
        ["readiness_state_on_property"] = new JsonArray(),
        ["products"] = new JsonArray(
            Product("SKU-RED", 1000, 5, true, Pv(CustomColorPropId, "Color", "Red", 1)),
            Product("SKU-GRN", 1200, 5, true, Pv(CustomColorPropId, "Color", "Green", 2)),
            Product("SKU-BLU", 1500, 5, true, Pv(CustomColorPropId, "Color", "Blue", 3))),
    });

    private static JsonObject TwoVariationInventory() => VariationEditor.Canonical(new JsonObject
    {
        ["price_on_property"] = new JsonArray(SizePropId),
        ["quantity_on_property"] = new JsonArray(),
        ["sku_on_property"] = new JsonArray(),
        ["readiness_state_on_property"] = new JsonArray(),
        ["products"] = new JsonArray(
            Product("SKU-S-RED", 1000, 5, true, Pv(SizePropId, "Size", "Small", 1), Pv(CustomColorPropId, "Color", "Red", 11)),
            Product("SKU-S-BLU", 1000, 5, true, Pv(SizePropId, "Size", "Small", 1), Pv(CustomColorPropId, "Color", "Blue", 12)),
            Product("SKU-L-RED", 2000, 5, true, Pv(SizePropId, "Size", "Large", 2), Pv(CustomColorPropId, "Color", "Red", 11)),
            Product("SKU-L-BLU", 2000, 5, true, Pv(SizePropId, "Size", "Large", 2), Pv(CustomColorPropId, "Color", "Blue", 12))),
    });

    private static async Task<string> RenderVariationsEditorHtmlAsync(
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
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

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
    // 1. Helper Methods Unit Tests
    // =========================================================================

    [Fact]
    public void AssertNotLocked_Throws_InvalidOperationException_When_Locked()
    {
        long[] lockedIds = [CustomColorPropId];
        var ex = Assert.Throws<InvalidOperationException>(() => VariationEditor.AssertNotLocked(CustomColorPropId, lockedIds));
        Assert.Contains(CustomColorPropId.ToString(), ex.Message);
        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);

        var link = new VariationListLink(1, "Color Palette", CustomColorPropId);
        var exLink = Assert.Throws<InvalidOperationException>(() => VariationEditor.AssertNotLocked(CustomColorPropId, [link]));
        Assert.Contains(CustomColorPropId.ToString(), exLink.Message);
    }

    [Fact]
    public void AssertNotLocked_DoesNotThrow_When_NotLocked_Or_Null()
    {
        VariationEditor.AssertNotLocked(CustomColorPropId, (IEnumerable<long>?)null);
        VariationEditor.AssertNotLocked(CustomColorPropId, (IEnumerable<VariationListLink>?)null);
        VariationEditor.AssertNotLocked(CustomColorPropId, [SizePropId]);
        VariationEditor.AssertNotLocked(CustomColorPropId, [new VariationListLink(1, "Sizes", SizePropId)]);
    }

    [Fact]
    public void IsLocked_Returns_True_Only_When_PropertyId_Matches()
    {
        Assert.True(VariationEditor.IsLocked(CustomColorPropId, [CustomColorPropId]));
        Assert.False(VariationEditor.IsLocked(SizePropId, [CustomColorPropId]));
        Assert.False(VariationEditor.IsLocked(SizePropId, (IEnumerable<long>?)null));

        var links = new[] { new VariationListLink(1, "Color Palette", CustomColorPropId) };
        Assert.True(VariationEditor.IsLocked(CustomColorPropId, links));
        Assert.False(VariationEditor.IsLocked(SizePropId, links));
        Assert.False(VariationEditor.IsLocked(SizePropId, (IEnumerable<VariationListLink>?)null));
    }

    // =========================================================================
    // 2. Structural Modification Rejection Tests in VariationEditor
    // =========================================================================

    [Fact]
    public void AddValue_Throws_InvalidOperationException_When_Variation_Locked()
    {
        var inv = SingleVariationInventory();
        var links = new[] { new VariationListLink(10, "Colors", CustomColorPropId) };

        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.AddValue(inv, CustomColorPropId, "Yellow", lockedPropertyIds: [CustomColorPropId]));

        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.AddValue(inv, CustomColorPropId, "Yellow", links));
    }

    [Fact]
    public void RemoveValue_Throws_InvalidOperationException_When_Variation_Locked()
    {
        var inv = SingleVariationInventory();
        var links = new[] { new VariationListLink(10, "Colors", CustomColorPropId) };

        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RemoveValue(inv, CustomColorPropId, "Red", lockedPropertyIds: [CustomColorPropId]));

        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RemoveValue(inv, CustomColorPropId, "Red", links));
    }

    [Fact]
    public void RenameValue_Throws_InvalidOperationException_When_Variation_Locked()
    {
        var inv = SingleVariationInventory();
        var links = new[] { new VariationListLink(10, "Colors", CustomColorPropId) };

        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RenameValue(inv, CustomColorPropId, "Red", "Crimson", lockedPropertyIds: [CustomColorPropId]));

        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RenameValue(inv, CustomColorPropId, "Red", "Crimson", links));
    }

    [Fact]
    public void RemoveVariation_Throws_InvalidOperationException_When_Variation_Locked()
    {
        var inv = TwoVariationInventory();
        var links = new[] { new VariationListLink(10, "Colors", CustomColorPropId) };

        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RemoveVariation(inv, CustomColorPropId, lockedPropertyIds: [CustomColorPropId]));

        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RemoveVariation(inv, CustomColorPropId, links));
    }

    [Fact]
    public void ReorderValues_Throws_InvalidOperationException_When_Variation_Locked()
    {
        var inv = SingleVariationInventory();
        var links = new[] { new VariationListLink(10, "Colors", CustomColorPropId) };
        string[] newOrder = ["Blue", "Green", "Red"];

        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.ReorderValues(inv, CustomColorPropId, newOrder, lockedPropertyIds: [CustomColorPropId]));

        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.ReorderValues(inv, CustomColorPropId, newOrder, links));
    }

    [Fact]
    public void RenameVariation_Throws_InvalidOperationException_When_Variation_Locked()
    {
        var inv = SingleVariationInventory();
        var links = new[] { new VariationListLink(10, "Colors", CustomColorPropId) };

        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RenameVariation(inv, CustomColorPropId, "Shade", lockedPropertyIds: [CustomColorPropId]));

        Assert.Throws<InvalidOperationException>(() =>
            VariationEditor.RenameVariation(inv, CustomColorPropId, "Shade", links));
    }

    [Fact]
    public void Structural_Mutations_Succeed_When_Variation_Is_Not_Locked()
    {
        var inv = TwoVariationInventory();
        // Size is locked, Color is NOT locked
        long[] lockedIds = [SizePropId];

        // Structural mutations on unlinked Color variation succeed
        var withAdded = VariationEditor.AddValue(inv, CustomColorPropId, "Yellow", lockedPropertyIds: lockedIds);
        Assert.Contains("Yellow", VariationEditor.Variations(withAdded).First(v => v.PropertyId == CustomColorPropId).Values);

        var renamed = VariationEditor.RenameValue(inv, CustomColorPropId, "Red", "Ruby", lockedPropertyIds: lockedIds);
        Assert.Contains("Ruby", VariationEditor.Variations(renamed).First(v => v.PropertyId == CustomColorPropId).Values);

        var reordered = VariationEditor.ReorderValues(inv, CustomColorPropId, ["Blue", "Red"], lockedPropertyIds: lockedIds);
        Assert.Equal(["Blue", "Red"], VariationEditor.Variations(reordered).First(v => v.PropertyId == CustomColorPropId).Values);

        var removedVal = VariationEditor.RemoveValue(inv, CustomColorPropId, "Red", lockedPropertyIds: lockedIds);
        Assert.DoesNotContain("Red", VariationEditor.Variations(removedVal).First(v => v.PropertyId == CustomColorPropId).Values);

        var removedVar = VariationEditor.RemoveVariation(inv, CustomColorPropId, lockedPropertyIds: lockedIds);
        Assert.DoesNotContain(VariationEditor.Variations(removedVar), v => v.PropertyId == CustomColorPropId);
    }

    // =========================================================================
    // 3. UI Component Rendering Tests for VariationsEditor.razor
    // =========================================================================

    [Fact]
    public async Task VariationsEditor_UI_Locked_Variation_Suppresses_Delete_Rename_Add_And_Remove()
    {
        var inv = SingleVariationInventory();
        var links = new[] { new VariationListLink(42, "Standard Colors", CustomColorPropId) };

        var html = await RenderVariationsEditorHtmlAsync(inv, links);

        // 1. MudChip delete/close button MUST NOT be rendered
        Assert.DoesNotContain("mud-chip-close-button", html);

        // 2. Chip click-to-rename title MUST NOT be rendered on locked variation
        Assert.DoesNotContain("Select to rename", html);

        // 3. Add to variation text field MUST be disabled and have template placeholder
        Assert.Contains("placeholder=\"Values controlled by template\"", html);
        Assert.Contains("aria-label=\"New value for Color\"", html);

        // 4. Add button MUST be disabled
        // MudBlazor renders disabled buttons with disabled="" attribute and nested spans
        Assert.Matches(new System.Text.RegularExpressions.Regex(@"<button[^>]*disabled[^>]*>.*?Add.*?</button>", System.Text.RegularExpressions.RegexOptions.Singleline), html);

        // 5. Remove variation button MUST be disabled
        Assert.Matches(new System.Text.RegularExpressions.Regex(@"<button[^>]*disabled[^>]*>.*?Remove variation.*?</button>", System.Text.RegularExpressions.RegexOptions.Singleline), html);

        // 6. Custom variation name rename input MUST be disabled
        // For custom variation, "Variation name" text field is rendered and must be disabled
        Assert.Matches(@"<input[^>]*disabled[^>]*value=""Color""", html);

        // 7. Value reordering buttons (ArrowBack, ArrowForward) MUST NOT be rendered
        Assert.DoesNotContain("Move Red earlier", html);
        Assert.DoesNotContain("Move Red later", html);

        // 8. Template indicator caption is rendered
        Assert.Contains("Value order is controlled by template Standard Colors", html);
    }

    [Fact]
    public async Task VariationsEditor_UI_Unlinked_Variation_Has_All_Editing_Controls_Enabled()
    {
        var inv = SingleVariationInventory();
        // No links provided
        var html = await RenderVariationsEditorHtmlAsync(inv, links: null);

        // 1. MudChip delete/close button IS rendered for unlocked variation with >1 values
        Assert.Contains("mud-chip-close-button", html);

        // 2. Chip title "Select to rename" IS rendered
        Assert.Contains("Select to rename", html);

        // 3. Add text field has normal placeholder and is NOT disabled
        Assert.Contains("placeholder=\"Add to Color\"", html);
        Assert.DoesNotMatch(@"<input[^>]*disabled[^>]*placeholder=""Add to Color""", html);

        // 4. Remove variation button is NOT disabled
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"<button[^>]*disabled[^>]*>.*?Remove variation.*?</button>", System.Text.RegularExpressions.RegexOptions.Singleline), html);
        Assert.Contains("Remove variation", html);

        // 5. Custom variation name input is NOT disabled
        Assert.DoesNotMatch(@"<input[^>]*disabled[^>]*value=""Color""", html);

        // 6. Value reorder buttons ARE rendered
        Assert.Contains("Move Green earlier", html);
        Assert.Contains("Move Green later", html);

        // 7. Template indicator is NOT rendered
        Assert.DoesNotContain("Value order is controlled by template", html);
    }

    [Fact]
    public async Task VariationsEditor_UI_Mixed_Linked_And_Unlinked_Variations_On_Same_Listing()
    {
        var inv = TwoVariationInventory();
        // Size (100) is Etsy property and NOT linked; Color (513) is custom and IS linked
        var links = new[] { new VariationListLink(99, "Global Colors", CustomColorPropId) };

        var html = await RenderVariationsEditorHtmlAsync(inv, links);

        // Size (unlocked):
        Assert.Contains("placeholder=\"Add to Size\"", html);
        Assert.Contains("Move Small later", html);

        // Color (locked):
        Assert.Contains("placeholder=\"Values controlled by template\"", html);
        Assert.Contains("Value order is controlled by template Global Colors", html);
        Assert.DoesNotContain("Move Red later", html);
    }

    [Fact]
    public async Task VariationsEditor_UI_Unlinking_Restores_Full_Editing_Capabilities()
    {
        var inv = SingleVariationInventory();
        var links = new[] { new VariationListLink(42, "Standard Colors", CustomColorPropId) };

        // 1. Render linked
        var lockedHtml = await RenderVariationsEditorHtmlAsync(inv, links);
        Assert.DoesNotContain("mud-chip-close-button", lockedHtml);
        Assert.DoesNotContain("Select to rename", lockedHtml);
        Assert.Contains("placeholder=\"Values controlled by template\"", lockedHtml);

        // 2. Unlink by passing empty links list
        var unlinkedHtml = await RenderVariationsEditorHtmlAsync(inv, links: []);
        Assert.Contains("mud-chip-close-button", unlinkedHtml);
        Assert.Contains("Select to rename", unlinkedHtml);
        Assert.Contains("placeholder=\"Add to Color\"", unlinkedHtml);
        Assert.Contains("Move Green earlier", unlinkedHtml);
        Assert.Contains("Move Green later", unlinkedHtml);
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"<button[^>]*disabled[^>]*>.*?Remove variation.*?</button>", System.Text.RegularExpressions.RegexOptions.Singleline), unlinkedHtml);
    }

    // =========================================================================
    // 4. Offerings and Varies Checkboxes Editability Tests
    // =========================================================================

    [Fact]
    public async Task VariationsEditor_UI_Price_And_Processing_Varies_Checkboxes_Remain_Always_Enabled()
    {
        var inv = SingleVariationInventory();
        var links = new[] { new VariationListLink(42, "Standard Colors", CustomColorPropId) };

        var html = await RenderVariationsEditorHtmlAsync(inv, links);

        // Verify that the checkboxes for "Price varies" and "Processing varies" exist and are NOT disabled
        Assert.Contains("Price varies", html);
        Assert.Contains("Processing varies", html);
        Assert.DoesNotMatch(@"<input[^>]*disabled[^>]*aria-label=""Price varies""", html);
        Assert.DoesNotMatch(@"<input[^>]*disabled[^>]*aria-label=""Processing varies""", html);
    }

    [Fact]
    public void Offerings_And_Varies_Settings_Remain_Fully_Editable_When_Variation_Locked()
    {
        var inv = SingleVariationInventory();
        var links = new[] { new VariationListLink(42, "Standard Colors", CustomColorPropId) };

        // 1. SetVaries (Price) succeeds on locked variation
        var priceVaries = VariationEditor.SetVaries(inv, VariationEditor.PriceOnProperty, CustomColorPropId, true);
        var priceArray = priceVaries[VariationEditor.PriceOnProperty]!.AsArray();
        Assert.Contains(priceArray, x => (long)x! == CustomColorPropId);

        // 2. SetVaries (Processing) succeeds on locked variation
        var procVaries = VariationEditor.SetVaries(inv, VariationEditor.ProcessingOnProperty, CustomColorPropId, true);
        var procArray = procVaries[VariationEditor.ProcessingOnProperty]!.AsArray();
        Assert.Contains(procArray, x => (long)x! == CustomColorPropId);

        // 3. SetAvailable (combination offering toggle) succeeds
        var disabledComb = VariationEditor.SetAvailable(inv, 0, false);
        var firstProductOfferings = disabledComb["products"]![0]!["offerings"]!.AsArray();
        Assert.False((bool)firstProductOfferings[0]!["is_enabled"]!);

        var reenabledComb = VariationEditor.SetAvailable(disabledComb, 0, true);
        Assert.True((bool)reenabledComb["products"]![0]!["offerings"]![0]!["is_enabled"]!);

        // 4. SetValueAvailable succeeds
        var disabledValue = VariationEditor.SetValueAvailable(inv, CustomColorPropId, "Red", false);
        Assert.False(VariationEditor.IsValueAvailable(disabledValue, CustomColorPropId, "Red"));
        Assert.True(VariationEditor.IsValueAvailable(disabledValue, CustomColorPropId, "Green"));
    }
}
