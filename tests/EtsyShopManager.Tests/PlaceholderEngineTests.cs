using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Web.Modules.Templates;

namespace EtsyShopManager.Tests;

public class PlaceholderEngineTests
{
    private static Shop CreateShop(string name = "Acme Craft Shop", string? url = "https://etsy.com/shop/AcmeCraftShop") =>
        new() { Id = 1, EtsyShopId = 100, Name = name, Url = url };

    private static JsonObject CreateLocalState(
        string title = "Handmade Wooden Bowl",
        string[]? tags = null,
        string[]? materials = null,
        decimal? length = 10.5m,
        decimal? width = 5m,
        decimal? height = 2.25m,
        string? dimensionsUnit = "in",
        decimal? weight = 1.75m,
        string? weightUnit = "lb",
        string? inventorySku = "INV-001")
    {
        var json = new JsonObject
        {
            ["title"] = title,
            ["tags"] = new JsonArray([.. (tags ?? ["kitchen", "wooden bowl", "gift"]).Select(t => (JsonNode)t)]),
            ["materials"] = new JsonArray([.. (materials ?? ["Wood", "Beeswax"]).Select(m => (JsonNode)m)]),
            ["item_length"] = length.HasValue ? JsonValue.Create(length.Value) : null,
            ["item_width"] = width.HasValue ? JsonValue.Create(width.Value) : null,
            ["item_height"] = height.HasValue ? JsonValue.Create(height.Value) : null,
            ["item_dimensions_unit"] = dimensionsUnit,
            ["item_weight"] = weight.HasValue ? JsonValue.Create(weight.Value) : null,
            ["item_weight_unit"] = weightUnit,
        };

        if (inventorySku is not null)
        {
            json["inventory"] = new JsonObject
            {
                ["products"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["sku"] = inventorySku,
                        ["offerings"] = new JsonArray { new JsonObject { ["price_amount"] = 2500 } }
                    }
                }
            };
        }

        return json;
    }

    [Fact]
    public void All_individual_builtin_placeholders_resolve_correctly()
    {
        var shop = CreateShop();
        var local = CreateLocalState();
        var context = new PlaceholderContext(shop, local, ["SKU-001"], new Dictionary<string, string>());

        // Shop placeholders
        var (shopName, unres1) = PlaceholderEngine.Render("{{shop.name}}", context);
        Assert.Equal("Acme Craft Shop", shopName);
        Assert.Empty(unres1);

        var (shopUrl, unres2) = PlaceholderEngine.Render("{{shop.url}}", context);
        Assert.Equal("https://etsy.com/shop/AcmeCraftShop", shopUrl);
        Assert.Empty(unres2);

        // Fallback shop url when null
        var fallbackShop = CreateShop(url: null);
        var fallbackContext = new PlaceholderContext(fallbackShop, local, ["SKU-001"], new Dictionary<string, string>());
        var (shopUrlFallback, _) = PlaceholderEngine.Render("{{shop.url}}", fallbackContext);
        Assert.Equal("https://www.etsy.com/shop/Acme Craft Shop", shopUrlFallback);

        // Listing title & SKU
        var (title, unres3) = PlaceholderEngine.Render("{{listing.title}}", context);
        Assert.Equal("Handmade Wooden Bowl", title);
        Assert.Empty(unres3);

        var (sku, unres4) = PlaceholderEngine.Render("{{listing.sku}}", context);
        Assert.Equal("SKU-001", sku);
        Assert.Empty(unres4);

        // Materials and alias
        var (materials, unres5) = PlaceholderEngine.Render("{{listing.materials}}", context);
        Assert.Equal("Wood, Beeswax", materials);
        Assert.Empty(unres5);

        var (materialAlias, unres6) = PlaceholderEngine.Render("{{listing.material}}", context);
        Assert.Equal("Wood, Beeswax", materialAlias);
        Assert.Empty(unres6);

        // Tags
        var (tags, unres7) = PlaceholderEngine.Render("{{listing.tags}}", context);
        Assert.Equal("kitchen, wooden bowl, gift", tags);
        Assert.Empty(unres7);

        // Dimensions (uses \u00D7 Unicode multiplication sign)
        var (dims, unres8) = PlaceholderEngine.Render("{{listing.dimensions}}", context);
        Assert.Equal("10.5 \u00D7 5 \u00D7 2.25 in", dims);
        Assert.Empty(unres8);

        // Dimensions without unit
        var noUnitLocal = CreateLocalState(dimensionsUnit: null);
        var noUnitContext = new PlaceholderContext(shop, noUnitLocal, ["SKU-001"], new Dictionary<string, string>());
        var (dimsNoUnit, _) = PlaceholderEngine.Render("{{listing.dimensions}}", noUnitContext);
        Assert.Equal("10.5 \u00D7 5 \u00D7 2.25", dimsNoUnit);

        // Individual dimension and weight fields
        Assert.Equal("10.5", PlaceholderEngine.Render("{{listing.length}}", context).Rendered);
        Assert.Equal("5", PlaceholderEngine.Render("{{listing.width}}", context).Rendered);
        Assert.Equal("2.25", PlaceholderEngine.Render("{{listing.height}}", context).Rendered);
        Assert.Equal("in", PlaceholderEngine.Render("{{listing.dimensions_unit}}", context).Rendered);
        Assert.Equal("in", PlaceholderEngine.Render("{{listing.dimension_unit}}", context).Rendered);
        Assert.Equal("1.75", PlaceholderEngine.Render("{{listing.weight}}", context).Rendered);
        Assert.Equal("lb", PlaceholderEngine.Render("{{listing.weight_unit}}", context).Rendered);
    }

    [Fact]
    public void Builtin_placeholders_are_case_insensitive_and_permit_spacing()
    {
        var shop = CreateShop();
        var local = CreateLocalState();
        var context = new PlaceholderContext(shop, local, ["SKU-001"], new Dictionary<string, string>());

        var template = "{{ SHOP.NAME }} - {{ Listing.Title }} - {{ listing.DIMENSIONS }}";
        var (rendered, unresolved) = PlaceholderEngine.Render(template, context);

        Assert.Equal("Acme Craft Shop - Handmade Wooden Bowl - 10.5 \u00D7 5 \u00D7 2.25 in", rendered);
        Assert.Empty(unresolved);
    }

    [Fact]
    public void Missing_and_empty_values_resolve_to_empty_string_without_error()
    {
        var shop = new Shop { Name = "" };
        var emptyLocal = new JsonObject
        {
            ["title"] = null,
            ["tags"] = new JsonArray(),
            ["materials"] = new JsonArray(),
            ["item_length"] = null,
            ["item_width"] = null,
            ["item_height"] = null,
            ["item_dimensions_unit"] = null,
            ["item_weight"] = null,
            ["item_weight_unit"] = null,
        };
        var context = new PlaceholderContext(shop, emptyLocal, [], new Dictionary<string, string>());

        var template = "Title: '{{listing.title}}', SKU: '{{listing.sku}}', Mats: '{{listing.materials}}', Tags: '{{listing.tags}}', Dims: '{{listing.dimensions}}', W: '{{listing.weight}}'";
        var (rendered, unresolved) = PlaceholderEngine.Render(template, context);

        Assert.Equal("Title: '', SKU: '', Mats: '', Tags: '', Dims: '', W: ''", rendered);
        Assert.Empty(unresolved);
    }

    [Fact]
    public void Sku_resolves_from_inventory_when_Skus_collection_is_empty()
    {
        var shop = CreateShop();
        var local = CreateLocalState(inventorySku: "INVENTORY-SKU-42");
        var context = new PlaceholderContext(shop, local, [], new Dictionary<string, string>());

        var (sku, unresolved) = PlaceholderEngine.Render("{{listing.sku}}", context);
        Assert.Equal("INVENTORY-SKU-42", sku);
        Assert.Empty(unresolved);
    }

    [Fact]
    public void Partial_dimensions_resolve_to_empty_string()
    {
        var shop = CreateShop();
        var local = CreateLocalState(length: 10m, width: null, height: 5m);
        var context = new PlaceholderContext(shop, local, [], new Dictionary<string, string>());

        var (dims, unresolved) = PlaceholderEngine.Render("{{listing.dimensions}}", context);
        Assert.Equal("", dims);
        Assert.Empty(unresolved);
    }

    [Fact]
    public void Multi_pass_nested_template_expansion_resolves_all_levels()
    {
        var shop = CreateShop();
        var local = CreateLocalState(title: "Handmade Mug");
        var textBlocks = new Dictionary<string, string>
        {
            ["footer"] = "Thanks for shopping at {{shop.name}}!\n{{text.care}}",
            ["care"] = "Care instructions: Hand-wash only.",
            ["header"] = "Welcome to {{shop.name}} - featuring {{listing.title}}",
        };
        var context = new PlaceholderContext(shop, local, ["MUG-01"], textBlocks);

        var template = "{{text.header}}\n\nBody content\n\n{{text.footer}}";
        var (rendered, unresolved) = PlaceholderEngine.Render(template, context);

        var expected = "Welcome to Acme Craft Shop - featuring Handmade Mug\n\nBody content\n\nThanks for shopping at Acme Craft Shop!\nCare instructions: Hand-wash only.";
        Assert.Equal(expected, rendered);
        Assert.Empty(unresolved);
    }

    [Fact]
    public void Recursion_depth_stops_at_5_passes_and_residual_unresolved_are_reported()
    {
        var shop = CreateShop();
        var local = CreateLocalState();
        // 6 chained templates: A -> B -> C -> D -> E -> F -> G ("Final")
        var textBlocks = new Dictionary<string, string>
        {
            ["a"] = "1 {{text.b}}",
            ["b"] = "2 {{text.c}}",
            ["c"] = "3 {{text.d}}",
            ["d"] = "4 {{text.e}}",
            ["e"] = "5 {{text.f}}",
            ["f"] = "6 {{text.g}}",
            ["g"] = "Final",
        };
        var context = new PlaceholderContext(shop, local, [], textBlocks);

        var (rendered, unresolved) = PlaceholderEngine.Render("{{text.a}}", context);
        // After 5 passes:
        // Pass 0: 1 {{text.b}}
        // Pass 1: 1 2 {{text.c}}
        // Pass 2: 1 2 3 {{text.d}}
        // Pass 3: 1 2 3 4 {{text.e}}
        // Pass 4: 1 2 3 4 5 {{text.f}}
        // Pass limit (5 passes) reached. {{text.f}} remains or {{text.g}} remains depending on index.
        Assert.Contains("{{text.", rendered);
        Assert.NotEmpty(unresolved);
    }

    [Fact]
    public void Unresolved_and_unknown_placeholders_are_collected_in_Unresolved_list()
    {
        var shop = CreateShop();
        var local = CreateLocalState();
        var context = new PlaceholderContext(shop, local, [], new Dictionary<string, string>());

        var template = "Hello {{text.missing_template}} from {{shop.invalid_field}} and {{listing.fake_attr}} with {{mystery_token}}";
        var (rendered, unresolved) = PlaceholderEngine.Render(template, context);

        Assert.Equal(["missing_template", "shop.invalid_field", "listing.fake_attr", "mystery_token"], unresolved);
        Assert.Equal(template, rendered);
    }

    [Fact]
    public void HasCircularReference_detects_self_references_and_cycles()
    {
        // 1. Direct self-reference
        Assert.True(PlaceholderEngine.HasCircularReference("care", "Care: {{text.care}}", new Dictionary<string, string>()));

        // 2. Direct mutual cycle: A -> B -> A
        var blocks1 = new Dictionary<string, string>
        {
            ["b"] = "B content {{text.a}}"
        };
        Assert.True(PlaceholderEngine.HasCircularReference("a", "A content {{text.b}}", blocks1));

        // 3. Indirect cycle: A -> B -> C -> A
        var blocks2 = new Dictionary<string, string>
        {
            ["b"] = "B content {{text.c}}",
            ["c"] = "C content {{text.a}}",
        };
        Assert.True(PlaceholderEngine.HasCircularReference("a", "A content {{text.b}}", blocks2));

        // 4. Non-circular DAG (diamond): A -> B -> D, A -> C -> D
        var blocks3 = new Dictionary<string, string>
        {
            ["b"] = "B content {{text.d}}",
            ["c"] = "C content {{text.d}}",
            ["d"] = "D content leaf",
        };
        Assert.False(PlaceholderEngine.HasCircularReference("a", "A content {{text.b}} and {{text.c}}", blocks3));

        // 5. Normal text with built-in placeholders (no circular reference)
        Assert.False(PlaceholderEngine.HasCircularReference("header", "Welcome to {{shop.name}}!", new Dictionary<string, string>()));
    }

    [Fact]
    public void HasPlaceholders_identifies_placeholder_presence()
    {
        Assert.False(PlaceholderEngine.HasPlaceholders(null));
        Assert.False(PlaceholderEngine.HasPlaceholders(""));
        Assert.False(PlaceholderEngine.HasPlaceholders("   "));
        Assert.False(PlaceholderEngine.HasPlaceholders("Plain description with no braces."));
        Assert.False(PlaceholderEngine.HasPlaceholders("Single {brace} or {other}"));

        Assert.True(PlaceholderEngine.HasPlaceholders("{{text.slug}}"));
        Assert.True(PlaceholderEngine.HasPlaceholders("{{shop.name}}"));
        Assert.True(PlaceholderEngine.HasPlaceholders("{{listing.title}}"));
        Assert.True(PlaceholderEngine.HasPlaceholders("{{ listing.dimensions }}"));
        Assert.True(PlaceholderEngine.HasPlaceholders("Text before {{shop.url}} text after"));
        Assert.True(PlaceholderEngine.HasPlaceholders("{{arbitrary_token}}"));
    }

    [Fact]
    public void BuiltInTokens_returns_recognized_tokens()
    {
        Assert.Empty(PlaceholderEngine.BuiltInTokens(null));
        Assert.Empty(PlaceholderEngine.BuiltInTokens("No tokens"));
        Assert.Empty(PlaceholderEngine.BuiltInTokens("{{text.slug}}"));

        var tokens = PlaceholderEngine.BuiltInTokens("{{shop.name}} and {{listing.title}} and {{shop.name}}");
        Assert.Equal(["{{shop.name}}", "{{listing.title}}"], tokens);
    }

    [Fact]
    public void CreateContext_builds_valid_context_from_listing_and_model()
    {
        var shop = CreateShop();
        var listing = new Listing
        {
            Id = 1,
            Shop = shop,
            Skus = ["SKU-A", "SKU-B"],
            EtsyState = new ListingEtsyState
            {
                ManagedJson = "{\"title\":\"Test\"}",
                BaseJson = "{\"title\":\"Test\"}",
            },
            DraftJson = "{\"title\":\"Draft Test\"}"
        };
        var model = ListingEditModel.Parse(listing.EtsyState.ManagedJson, listing.EtsyState.BaseJson, listing.DraftJson);
        var blocks = new Dictionary<string, string> { ["care"] = "Care text" };

        var context = PlaceholderEngine.CreateContext(listing, model, blocks);

        Assert.Equal(shop, context.Shop);
        Assert.Equal(["SKU-A", "SKU-B"], context.Skus);
        Assert.Equal("Draft Test", (string?)context.LocalState["title"]);
        Assert.Equal("Care text", context.TextBlocks["care"]);
    }
}
