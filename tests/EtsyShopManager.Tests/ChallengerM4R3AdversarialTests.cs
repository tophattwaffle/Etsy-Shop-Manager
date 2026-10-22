using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EtsyShopManager.Tests;

public class ChallengerM4R3AdversarialTests
{
    private static async Task<(FakeEtsyShop Shop, Dictionary<long, int> Ids)> SetupAsync()
    {
        var shop = new FakeEtsyShop();
        foreach (var id in new long[] { 1, 2, 3 })
        {
            var listing = FakeEtsyShop.Listing(id, $"Item {id}");
            shop.Listings.Add(listing);
        }
        shop.Listings.Add(FakeEtsyShop.Listing(4, "Test Listing"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return (shop, await db.Listings.ToDictionaryAsync(l => l.EtsyListingId, l => l.Id));
    }

    private static TextBlockService Blocks(FakeEtsyShop s) => s.Provider.GetRequiredService<TextBlockService>();
    private static ListingEditor Editor(FakeEtsyShop s) => s.Provider.GetRequiredService<ListingEditor>();

    // ---------------------------------------------------------------------------------------------
    // 1. Circular Reference Adversarial Stress Tests
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Circular_references_self_reference_rejected_in_SaveAsync()
    {
        var (shop, _) = await SetupAsync();
        using var _ = shop;
        var blocks = Blocks(shop);

        var (id, err) = await blocks.SaveAsync(null, "Self Block", "self-block", "Self loop {{text.self-block}}", "tester");
        Assert.Null(id);
        Assert.Equal("Template contains circular template references.", err);
    }

    [Fact]
    public async Task Circular_references_two_step_cycle_rejected_in_SaveAsync()
    {
        var (shop, _) = await SetupAsync();
        using var _ = shop;
        var blocks = Blocks(shop);

        var (idA, errA) = await blocks.SaveAsync(null, "Block A", "block-a", "A calls {{text.block-b}}", "tester");
        Assert.Null(errA);
        Assert.NotNull(idA);

        var (idB, errB) = await blocks.SaveAsync(null, "Block B", "block-b", "B calls {{text.block-a}}", "tester");
        Assert.Null(idB);
        Assert.Equal("Template contains circular template references.", errB);
    }

    [Fact]
    public async Task Circular_references_three_step_cycle_rejected_in_SaveAsync()
    {
        var (shop, _) = await SetupAsync();
        using var _ = shop;
        var blocks = Blocks(shop);

        var (idA, errA) = await blocks.SaveAsync(null, "Block A", "block-a", "A calls {{text.block-b}}", "tester");
        Assert.Null(errA);
        var (idB, errB) = await blocks.SaveAsync(null, "Block B", "block-b", "B calls {{text.block-c}}", "tester");
        Assert.Null(errB);

        // C calls A -> forms A -> B -> C -> A
        var (idC, errC) = await blocks.SaveAsync(null, "Block C", "block-c", "C calls {{text.block-a}}", "tester");
        Assert.Null(idC);
        Assert.Equal("Template contains circular template references.", errC);
    }

    [Fact]
    public async Task Circular_references_four_step_cycle_rejected_in_SaveAsync()
    {
        var (shop, _) = await SetupAsync();
        using var _ = shop;
        var blocks = Blocks(shop);

        Assert.Null((await blocks.SaveAsync(null, "Block A", "block-a", "A calls {{text.block-b}}", "tester")).Error);
        Assert.Null((await blocks.SaveAsync(null, "Block B", "block-b", "B calls {{text.block-c}}", "tester")).Error);
        Assert.Null((await blocks.SaveAsync(null, "Block C", "block-c", "C calls {{text.block-d}}", "tester")).Error);

        var (idD, errD) = await blocks.SaveAsync(null, "Block D", "block-d", "D calls {{text.block-a}}", "tester");
        Assert.Null(idD);
        Assert.Equal("Template contains circular template references.", errD);
    }

    [Fact]
    public async Task Circular_references_cycle_created_on_updating_existing_block_rejected_in_SaveAsync()
    {
        var (shop, _) = await SetupAsync();
        using var _ = shop;
        var blocks = Blocks(shop);

        // Initially safe: A -> B, and B has no dependencies
        var (idA, _) = await blocks.SaveAsync(null, "Block A", "block-a", "A calls {{text.block-b}}", "tester");
        var (idB, _) = await blocks.SaveAsync(null, "Block B", "block-b", "Safe B content", "tester");

        // Now update B to call A -> would create cycle B -> A -> B!
        var (updatedIdB, errB) = await blocks.SaveAsync(idB, "Block B", "block-b", "B now calls {{text.block-a}}", "tester");
        Assert.Null(updatedIdB);
        Assert.Equal("Template contains circular template references.", errB);
    }

    [Fact]
    public async Task Circular_references_case_insensitive_and_whitespace_slugs_detected()
    {
        var (shop, _) = await SetupAsync();
        using var _ = shop;
        var blocks = Blocks(shop);

        var (idA, _) = await blocks.SaveAsync(null, "Block Alpha", "block-alpha", "Calls {{ text.BLOCK-BETA }}", "tester");

        var (idB, errB) = await blocks.SaveAsync(null, "Block Beta", "block-beta", "Calls {{   text.Block-Alpha   }}", "tester");
        Assert.Null(idB);
        Assert.Equal("Template contains circular template references.", errB);
    }

    [Fact]
    public async Task Circular_references_valid_diamond_DAG_is_allowed()
    {
        var (shop, _) = await SetupAsync();
        using var _ = shop;
        var blocks = Blocks(shop);

        // D is leaf
        var (idD, errD) = await blocks.SaveAsync(null, "Block D", "block-d", "Leaf D content", "tester");
        Assert.Null(errD);

        // B calls D, C calls D
        var (idB, errB) = await blocks.SaveAsync(null, "Block B", "block-b", "B calls {{text.block-d}}", "tester");
        Assert.Null(errB);

        var (idC, errC) = await blocks.SaveAsync(null, "Block C", "block-c", "C calls {{text.block-d}}", "tester");
        Assert.Null(errC);

        // A calls both B and C (diamond: A -> B -> D, A -> C -> D)
        var (idA, errA) = await blocks.SaveAsync(null, "Block A", "block-a", "A calls {{text.block-b}} and {{text.block-c}}", "tester");
        Assert.Null(errA);
        Assert.NotNull(idA);
    }

    // ---------------------------------------------------------------------------------------------
    // 2. Deep Nesting Multi-Pass Stress Tests
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Deep_nesting_exactly_five_levels_expands_completely_to_plain_text()
    {
        var shop = new Shop { Name = "Acme Shop" };
        var local = new JsonObject();
        var blocks = new Dictionary<string, string>
        {
            ["lvl1"] = "L1 -> {{text.lvl2}}",
            ["lvl2"] = "L2 -> {{text.lvl3}}",
            ["lvl3"] = "L3 -> {{text.lvl4}}",
            ["lvl4"] = "L4 -> {{text.lvl5}}",
            ["lvl5"] = "FINAL_CONTENT",
        };
        var context = new PlaceholderContext(shop, local, [], blocks);

        var (rendered, unresolved) = PlaceholderEngine.Render("Start: {{text.lvl1}} :End", context);

        Assert.Empty(unresolved);
        Assert.Equal("Start: L1 -> L2 -> L3 -> L4 -> FINAL_CONTENT :End", rendered);
        Assert.DoesNotContain("{{", rendered);
    }

    [Fact]
    public void Deep_nesting_six_levels_stops_at_five_and_flags_remainder_as_unresolved()
    {
        var shop = new Shop { Name = "Acme Shop" };
        var local = new JsonObject();
        var blocks = new Dictionary<string, string>
        {
            ["lvl1"] = "L1 -> {{text.lvl2}}",
            ["lvl2"] = "L2 -> {{text.lvl3}}",
            ["lvl3"] = "L3 -> {{text.lvl4}}",
            ["lvl4"] = "L4 -> {{text.lvl5}}",
            ["lvl5"] = "L5 -> {{text.lvl6}}",
            ["lvl6"] = "NEVER_REACHED",
        };
        var context = new PlaceholderContext(shop, local, [], blocks);

        var (rendered, unresolved) = PlaceholderEngine.Render("Start: {{text.lvl1}} :End", context);

        Assert.Equal("Start: L1 -> L2 -> L3 -> L4 -> L5 -> {{text.lvl6}} :End", rendered);
        Assert.Equal(["lvl6"], unresolved);
    }

    [Fact]
    public void Deep_nesting_seven_levels_stops_at_five_and_flags_remainder_as_unresolved()
    {
        var shop = new Shop { Name = "Acme Shop" };
        var local = new JsonObject();
        var blocks = new Dictionary<string, string>
        {
            ["lvl1"] = "{{text.lvl2}}",
            ["lvl2"] = "{{text.lvl3}}",
            ["lvl3"] = "{{text.lvl4}}",
            ["lvl4"] = "{{text.lvl5}}",
            ["lvl5"] = "{{text.lvl6}}",
            ["lvl6"] = "{{text.lvl7}}",
            ["lvl7"] = "DEEP_LEAF",
        };
        var context = new PlaceholderContext(shop, local, [], blocks);

        var (rendered, unresolved) = PlaceholderEngine.Render("{{text.lvl1}}", context);

        Assert.Equal("{{text.lvl6}}", rendered);
        Assert.Equal(["lvl6"], unresolved);
    }

    [Fact]
    public void Deep_nesting_five_levels_with_complex_builtins_at_level_five_resolves_all()
    {
        var shop = new Shop { Name = "Star Artisans", Url = "https://etsy.com/shop/StarArtisans" };
        var local = new JsonObject
        {
            ["title"] = "Constellation Pendant",
            ["item_length"] = 10.5m,
            ["item_width"] = 4.25m,
            ["item_height"] = 2m,
            ["item_dimensions_unit"] = "in",
            ["materials"] = new JsonArray("Silver", "Opal"),
        };
        var blocks = new Dictionary<string, string>
        {
            ["lvl1"] = "Header:\n{{text.lvl2}}",
            ["lvl2"] = "Details:\n{{text.lvl3}}",
            ["lvl3"] = "Specs:\n{{text.lvl4}}",
            ["lvl4"] = "Product:\n{{text.lvl5}}",
            ["lvl5"] = "Item: {{listing.title}} | Made by: {{shop.name}} | Dims: {{listing.dimensions}} | Material: {{listing.material}}",
        };
        var context = new PlaceholderContext(shop, local, [], blocks);

        var (rendered, unresolved) = PlaceholderEngine.Render("{{text.lvl1}}", context);

        Assert.Empty(unresolved);
        Assert.DoesNotContain("{{", rendered);
        Assert.Contains("Item: Constellation Pendant | Made by: Star Artisans | Dims: 10.5 \u00D7 4.25 \u00D7 2 in | Material: Silver, Opal", rendered);
    }

    // ---------------------------------------------------------------------------------------------
    // 3. Complex Built-ins Stress Tests
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Complex_builtins_dimensions_with_fractions_and_decimals()
    {
        var shop = new Shop { Name = "Crafts" };
        var local = new JsonObject
        {
            ["item_length"] = 10.5m,
            ["item_width"] = 4.25m,
            ["item_height"] = 2.0m,
            ["item_dimensions_unit"] = "in",
            ["item_weight"] = 3.125m,
            ["item_weight_unit"] = "lbs",
        };
        var context = new PlaceholderContext(shop, local, [], new Dictionary<string, string>());

        var (dims, unres1) = PlaceholderEngine.Render("{{listing.dimensions}}", context);
        Assert.Equal("10.5 \u00D7 4.25 \u00D7 2 in", dims);
        Assert.Empty(unres1);

        var (components, unres2) = PlaceholderEngine.Render(
            "L:{{listing.length}}, W:{{listing.width}}, H:{{listing.height}} {{listing.dimensions_unit}} (alias: {{listing.dimension_unit}})", context);
        Assert.Equal("L:10.5, W:4.25, H:2 in (alias: in)", components);
        Assert.Empty(unres2);

        var (weight, unres3) = PlaceholderEngine.Render("{{listing.weight}} {{listing.weight_unit}}", context);
        Assert.Equal("3.125 lbs", weight);
        Assert.Empty(unres3);
    }

    [Fact]
    public void Complex_builtins_dimensions_with_null_and_missing_units()
    {
        var shop = new Shop { Name = "Crafts" };
        // null unit
        var localNullUnit = new JsonObject
        {
            ["item_length"] = 10.5m,
            ["item_width"] = 4.25m,
            ["item_height"] = 2m,
            ["item_dimensions_unit"] = null,
        };
        var ctxNull = new PlaceholderContext(shop, localNullUnit, [], new Dictionary<string, string>());
        Assert.Equal("10.5 \u00D7 4.25 \u00D7 2", PlaceholderEngine.Render("{{listing.dimensions}}", ctxNull).Rendered);
        Assert.Equal("", PlaceholderEngine.Render("{{listing.dimensions_unit}}", ctxNull).Rendered);
        Assert.Equal("", PlaceholderEngine.Render("{{listing.dimension_unit}}", ctxNull).Rendered);

        // empty / whitespace unit
        var localWsUnit = new JsonObject
        {
            ["item_length"] = 10.5m,
            ["item_width"] = 4.25m,
            ["item_height"] = 2m,
            ["item_dimensions_unit"] = "   ",
        };
        var ctxWs = new PlaceholderContext(shop, localWsUnit, [], new Dictionary<string, string>());
        Assert.Equal("10.5 \u00D7 4.25 \u00D7 2", PlaceholderEngine.Render("{{listing.dimensions}}", ctxWs).Rendered);
    }

    [Fact]
    public void Complex_builtins_dimensions_with_partial_missing_values_resolves_to_empty_without_push_blocker()
    {
        var shop = new Shop { Name = "Crafts" };

        // Missing length
        var local1 = new JsonObject { ["item_length"] = null, ["item_width"] = 4.25m, ["item_height"] = 2m, ["item_dimensions_unit"] = "in" };
        var (r1, u1) = PlaceholderEngine.Render("Dims: '{{listing.dimensions}}'", new PlaceholderContext(shop, local1, [], new Dictionary<string, string>()));
        Assert.Equal("Dims: ''", r1);
        Assert.Empty(u1);

        // Missing width
        var local2 = new JsonObject { ["item_length"] = 10.5m, ["item_width"] = null, ["item_height"] = 2m, ["item_dimensions_unit"] = "in" };
        var (r2, u2) = PlaceholderEngine.Render("Dims: '{{listing.dimensions}}'", new PlaceholderContext(shop, local2, [], new Dictionary<string, string>()));
        Assert.Equal("Dims: ''", r2);
        Assert.Empty(u2);

        // Missing height
        var local3 = new JsonObject { ["item_length"] = 10.5m, ["item_width"] = 4.25m, ["item_height"] = null, ["item_dimensions_unit"] = "in" };
        var (r3, u3) = PlaceholderEngine.Render("Dims: '{{listing.dimensions}}'", new PlaceholderContext(shop, local3, [], new Dictionary<string, string>()));
        Assert.Equal("Dims: ''", r3);
        Assert.Empty(u3);
    }

    [Fact]
    public void Complex_builtins_singular_vs_plural_material_aliases()
    {
        var shop = new Shop { Name = "Crafts" };

        // Multiple materials
        var localMulti = new JsonObject
        {
            ["materials"] = new JsonArray("Walnut", "Epoxy Resin", "Brass")
        };
        var ctxMulti = new PlaceholderContext(shop, localMulti, [], new Dictionary<string, string>());
        Assert.Equal("Walnut, Epoxy Resin, Brass", PlaceholderEngine.Render("{{listing.materials}}", ctxMulti).Rendered);
        Assert.Equal("Walnut, Epoxy Resin, Brass", PlaceholderEngine.Render("{{listing.material}}", ctxMulti).Rendered);
        Assert.Equal("Walnut, Epoxy Resin, Brass", PlaceholderEngine.Render("{{  listing.material  }}", ctxMulti).Rendered);
        Assert.Equal("Walnut, Epoxy Resin, Brass", PlaceholderEngine.Render("{{   listing.materials   }}", ctxMulti).Rendered);

        // Single material
        var localSingle = new JsonObject
        {
            ["materials"] = new JsonArray("Ceramic")
        };
        var ctxSingle = new PlaceholderContext(shop, localSingle, [], new Dictionary<string, string>());
        Assert.Equal("Ceramic", PlaceholderEngine.Render("{{listing.materials}}", ctxSingle).Rendered);
        Assert.Equal("Ceramic", PlaceholderEngine.Render("{{listing.material}}", ctxSingle).Rendered);

        // Empty array
        var localEmpty = new JsonObject { ["materials"] = new JsonArray() };
        var ctxEmpty = new PlaceholderContext(shop, localEmpty, [], new Dictionary<string, string>());
        Assert.Equal("", PlaceholderEngine.Render("{{listing.materials}}", ctxEmpty).Rendered);
        Assert.Equal("", PlaceholderEngine.Render("{{listing.material}}", ctxEmpty).Rendered);

        // Null property
        var localNull = new JsonObject();
        var ctxNull = new PlaceholderContext(shop, localNull, [], new Dictionary<string, string>());
        Assert.Equal("", PlaceholderEngine.Render("{{listing.materials}}", ctxNull).Rendered);
        Assert.Equal("", PlaceholderEngine.Render("{{listing.material}}", ctxNull).Rendered);
    }

    // ---------------------------------------------------------------------------------------------
    // 4. Unresolved Token Push Blocker Stress Tests
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Push_blocker_invalid_text_template_blocks_push_in_ListingEditor()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;

        var unknown = await Blocks(shop).SaveDescriptionAsync(ids[4], "Description with {{text.nonexistent}} template", "tester");
        Assert.Equal(["nonexistent"], unknown);

        var preview = await Editor(shop).PreviewAsync(ids[4]);
        Assert.False(preview.CanPush);
        Assert.Contains(preview.Blockers, b => b.StartsWith("The description has {{text.nonexistent}}, which isn't a text template. Fix the placeholder or create the template."));

        var (pushId, blockers) = await Editor(shop).RequestPushAsync(ids[4], "tester");
        Assert.Null(pushId);
        Assert.Contains(blockers, b => b.Contains("{{text.nonexistent}}"));
    }

    [Fact]
    public async Task Push_blocker_invalid_shop_field_blocks_push_in_ListingEditor()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;

        var unknown = await Blocks(shop).SaveDescriptionAsync(ids[4], "Shop info: {{shop.nonexistent_field}}", "tester");
        Assert.Equal(["shop.nonexistent_field"], unknown);

        var preview = await Editor(shop).PreviewAsync(ids[4]);
        Assert.False(preview.CanPush);
        Assert.Contains(preview.Blockers, b => b.Contains("The description has unresolved placeholder(s): {{shop.nonexistent_field}}. Fix the placeholder or verify the field exists."));

        var (pushId, blockers) = await Editor(shop).RequestPushAsync(ids[4], "tester");
        Assert.Null(pushId);
        Assert.Contains(blockers, b => b.Contains("{{shop.nonexistent_field}}"));
    }

    [Fact]
    public async Task Push_blocker_invalid_listing_field_blocks_push_in_ListingEditor()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;

        var unknown = await Blocks(shop).SaveDescriptionAsync(ids[4], "Item: {{listing.secret_code}}", "tester");
        Assert.Equal(["listing.secret_code"], unknown);

        var preview = await Editor(shop).PreviewAsync(ids[4]);
        Assert.False(preview.CanPush);
        Assert.Contains(preview.Blockers, b => b.Contains("The description has unresolved placeholder(s): {{listing.secret_code}}. Fix the placeholder or verify the field exists."));

        var (pushId, blockers) = await Editor(shop).RequestPushAsync(ids[4], "tester");
        Assert.Null(pushId);
        Assert.Contains(blockers, b => b.Contains("{{listing.secret_code}}"));
    }

    [Fact]
    public async Task Push_blocker_arbitrary_unmapped_token_blocks_push_in_ListingEditor()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;

        var unknown = await Blocks(shop).SaveDescriptionAsync(ids[4], "Text with {{arbitrary_token}} embedded", "tester");
        Assert.Equal(["arbitrary_token"], unknown);

        var preview = await Editor(shop).PreviewAsync(ids[4]);
        Assert.False(preview.CanPush);
        Assert.Contains(preview.Blockers, b => b.Contains("The description has unresolved placeholder(s): {{arbitrary_token}}. Fix the placeholder or verify the field exists."));

        var (pushId, blockers) = await Editor(shop).RequestPushAsync(ids[4], "tester");
        Assert.Null(pushId);
        Assert.Contains(blockers, b => b.Contains("{{arbitrary_token}}"));
    }

    [Fact]
    public async Task Push_blocker_multiple_distinct_invalid_tokens_all_reported()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;

        var unknown = await Blocks(shop).SaveDescriptionAsync(ids[4],
            "Check {{text.missing_block}} and {{shop.fake_data}} with {{listing.mystery}}", "tester");
        Assert.Equal(["missing_block", "shop.fake_data", "listing.mystery"], unknown);

        var preview = await Editor(shop).PreviewAsync(ids[4]);
        Assert.False(preview.CanPush);
        // Both template blocker and field blocker should be present
        Assert.Contains(preview.Blockers, b => b.Contains("{{text.missing_block}}"));
        Assert.Contains(preview.Blockers, b => b.Contains("{{shop.fake_data}}") && b.Contains("{{listing.mystery}}"));

        var (pushId, blockers) = await Editor(shop).RequestPushAsync(ids[4], "tester");
        Assert.Null(pushId);
        Assert.Equal(2, blockers.Count(b => b.Contains("description")));
    }

    [Fact]
    public async Task Push_allowed_when_all_placeholders_resolve_cleanly()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;

        // Populate valid fields on listing 4
        await Editor(shop).SaveAsync(ids[4], new Dictionary<string, JsonNode?>
        {
            ["item_length"] = 10.5m,
            ["item_width"] = 4.25m,
            ["item_height"] = 2.0m,
            ["item_dimensions_unit"] = "in",
            ["materials"] = new JsonArray("Glass", "Brass"),
        }, "tester");

        var source = "Welcome to {{shop.name}}!\nTitle: {{listing.title}}\nSize: {{listing.dimensions}}\nMaterials: {{listing.material}}";
        var unknown = await Blocks(shop).SaveDescriptionAsync(ids[4], source, "tester");
        Assert.Empty(unknown);

        var preview = await Editor(shop).PreviewAsync(ids[4]);
        Assert.DoesNotContain(preview.Blockers, b => b.Contains("description"));
        Assert.True(preview.CanPush);

        var (pushId, blockers) = await Editor(shop).RequestPushAsync(ids[4], "tester");
        Assert.NotNull(pushId);
        Assert.Empty(blockers);
    }
}
