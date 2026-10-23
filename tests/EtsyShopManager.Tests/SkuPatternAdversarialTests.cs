using System.Text.Json.Nodes;
using EtsyShopManager.Web.Modules.Editor;
using Xunit;

namespace EtsyShopManager.Tests;

public class SkuPatternAdversarialTests
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

    // ---------------------------------------------------------------------------------------------
    // 1. SanitizeForSku Stress Tests
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1/2\"", "1-2")]
    [InlineData("1\\4\"", "1-4")]
    [InlineData("Red / White", "Red-White")]
    [InlineData("Galaxy Purple (Glitter)", "Galaxy-Purple-Glitter")]
    [InlineData("6\" Bowl", "6-Bowl")]
    [InlineData("'Single' Quote", "Single-Quote")]
    [InlineData("Sale! 20% Off", "Sale-20-Off")]
    [InlineData("Size: XL #1", "Size-XL-1")]
    [InlineData("Cats & Dogs", "Cats-Dogs")]
    [InlineData("[Limited] Edition", "Limited-Edition")]
    [InlineData("{Special}", "Special")]
    [InlineData("foo+bar=baz", "foobarbaz")]
    [InlineData("Café au Lait", "Café-au-Lait")]
    [InlineData("Größe L", "Größe-L")]
    [InlineData("Niño", "Niño")]
    [InlineData("Alpha -- Beta", "Alpha-Beta")]
    [InlineData("  Leading and Trailing Spaces  ", "Leading-and-Trailing-Spaces")]
    [InlineData("---hyphens---", "hyphens")]
    [InlineData("///slashes\\\\\\", "slashes")]
    [InlineData("   ", "")]
    [InlineData("", "")]
    [InlineData("!@#$%^&*()", "")]
    public void SanitizeForSku_EdgeCases_AreHandledSafely(string input, string expected)
    {
        var actual = SkuPatternResolver.SanitizeForSku(input);
        Assert.Equal(expected, actual);
    }

    // ---------------------------------------------------------------------------------------------
    // 2. GenerateSku Case-Insensitivity & Token Variations Stress Tests
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("{listing.sku}", "BASE", "BASE")]
    [InlineData("{LISTING.SKU}", "BASE", "BASE")]
    [InlineData("{Listing.Sku}", "BASE", "BASE")]
    [InlineData("{sku}", "BASE", "BASE")]
    [InlineData("{SKU}", "BASE", "BASE")]
    [InlineData("{Sku}", "BASE", "BASE")]
    [InlineData("{ listing.sku }", "BASE", "BASE")]
    [InlineData("{ sku }", "BASE", "BASE")]
    public void GenerateSku_CaseInsensitiveBaseSkuTokens(string pattern, string baseSku, string expected)
    {
        var values = new Dictionary<string, string>();
        var result = SkuPatternResolver.GenerateSku(pattern, baseSku, values);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GenerateSku_NullOrEmptyBaseSku_BecomesEmptyString()
    {
        var values = new Dictionary<string, string> { ["Size"] = "Large" };
        var result = SkuPatternResolver.GenerateSku("{listing.sku}-{size}", null!, values);
        Assert.Equal("-Large", result);

        var resultEmpty = SkuPatternResolver.GenerateSku("{sku}-{size}", "", values);
        Assert.Equal("-Large", resultEmpty);
    }

    // ---------------------------------------------------------------------------------------------
    // 3. Complex Property Names (Accents, Spaces, Punctuation, Suffixes)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void GenerateSku_ComplexPropertyNames_MatchTokensRobustly()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Primary Color"] = "Deep Blue",
            ["Size - Adult"] = "XL",
            ["Couleur"] = "Noir",
            ["Material / Finish"] = "Matte Gloss",
            ["Dimensions (cm)"] = "10x20"
        };

        // 1. Exact case-insensitive match
        Assert.Equal("Deep-Blue", SkuPatternResolver.GenerateSku("{Primary Color}", "SKU", values));

        // 2. Normalized match (whitespace and hyphens stripped)
        Assert.Equal("Deep-Blue", SkuPatternResolver.GenerateSku("{primarycolor}", "SKU", values));
        Assert.Equal("XL", SkuPatternResolver.GenerateSku("{sizeadult}", "SKU", values));
        Assert.Equal("XL", SkuPatternResolver.GenerateSku("{Size-Adult}", "SKU", values));

        // 3. Suffix match ("color" matches "primary color")
        Assert.Equal("Deep-Blue", SkuPatternResolver.GenerateSku("{color}", "SKU", values));
        Assert.Equal("Noir", SkuPatternResolver.GenerateSku("{couleur}", "SKU", values));
        Assert.Equal("Matte-Gloss", SkuPatternResolver.GenerateSku("{finish}", "SKU", values));

        // 4. Multiple variations in one pattern
        var multi = SkuPatternResolver.GenerateSku("{listing.sku}-{sizeadult}-{color}", "SHIRT", values);
        Assert.Equal("SHIRT-XL-Deep-Blue", multi);
    }

    // ---------------------------------------------------------------------------------------------
    // 4. Special Characters in Variation Values
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void GenerateSku_SpecialCharactersInValues_AreSanitizedAndFormCleanSkus()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Size"] = "1/2\"",
            ["Color"] = "Red / White",
            ["Style"] = "Galaxy Purple (Glitter)"
        };

        var sku = SkuPatternResolver.GenerateSku("{listing.sku}-{size}-{color}", "STICKER", values);
        Assert.Equal("STICKER-1-2-Red-White", sku);

        var sku3 = SkuPatternResolver.GenerateSku("{sku}-{size}-{color}-{style}", "STICKER", values);
        Assert.Equal("STICKER-1-2-Red-White-Galaxy-Purple-Glitter", sku3);
    }

    [Fact]
    public void GenerateSku_UnknownTokens_RemainUnchangedInOutput()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Size"] = "Small"
        };

        var sku = SkuPatternResolver.GenerateSku("{listing.sku}-{size}-{nonexistent}", "MUG", values);
        Assert.Equal("MUG-Small-{nonexistent}", sku);
    }

    // ---------------------------------------------------------------------------------------------
    // 5. Complete Inventory Grids ApplyPattern Stress Tests
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ApplyPattern_SingleVariationGrid_ResolvesAllProductSkus()
    {
        var inv = VariationEditor.Canonical(new JsonObject
        {
            ["price_on_property"] = new JsonArray(SizePropId),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(SizePropId),
            ["readiness_state_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                Product("OLD-1", 1000, true, Pv(SizePropId, "Size", "Small", 1)),
                Product("OLD-2", 1500, true, Pv(SizePropId, "Size", "Medium", 2)),
                Product("OLD-3", 2000, true, Pv(SizePropId, "Size", "Large", 3)),
                Product("OLD-4", 2500, true, Pv(SizePropId, "Size", "1/2\"", 4))
            ),
        });

        var pattern = "{listing.sku}-{size}";
        var applied = SkuPatternResolver.ApplyPattern(inv, pattern, "RING");

        Assert.Equal(pattern, (string?)applied["sku_pattern"]);
        var products = applied["products"]!.AsArray();
        Assert.Equal("RING-Small", (string?)products[0]!["sku"]);
        Assert.Equal("RING-Medium", (string?)products[1]!["sku"]);
        Assert.Equal("RING-Large", (string?)products[2]!["sku"]);
        Assert.Equal("RING-1-2", (string?)products[3]!["sku"]);
    }

    [Fact]
    public void ApplyPattern_TwoVariationGrid_WithComplexValues_ResolvesAllCombinations()
    {
        var inv = VariationEditor.Canonical(new JsonObject
        {
            ["price_on_property"] = new JsonArray(SizePropId),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(SizePropId),
            ["readiness_state_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                Product("", 1000, true, Pv(SizePropId, "Size", "Small", 1), Pv(ColorPropId, "Color", "Red / White", 10)),
                Product("", 1000, true, Pv(SizePropId, "Size", "Small", 1), Pv(ColorPropId, "Color", "Galaxy Purple (Glitter)", 11)),
                Product("", 2000, true, Pv(SizePropId, "Size", "1/2\"", 2), Pv(ColorPropId, "Color", "Red / White", 10)),
                Product("", 2000, true, Pv(SizePropId, "Size", "1/2\"", 2), Pv(ColorPropId, "Color", "Galaxy Purple (Glitter)", 11))
            ),
        });

        var pattern = "{listing.sku}_{size}_{color}";
        var applied = SkuPatternResolver.ApplyPattern(inv, pattern, "DEC-01");

        Assert.Equal(pattern, (string?)applied["sku_pattern"]);
        var products = applied["products"]!.AsArray();
        Assert.Equal(4, products.Count);

        Assert.Equal("DEC-01_Small_Red-White", (string?)products[0]!["sku"]);
        Assert.Equal("DEC-01_Small_Galaxy-Purple-Glitter", (string?)products[1]!["sku"]);
        Assert.Equal("DEC-01_1-2_Red-White", (string?)products[2]!["sku"]);
        Assert.Equal("DEC-01_1-2_Galaxy-Purple-Glitter", (string?)products[3]!["sku"]);
    }

    [Fact]
    public void ApplyPattern_FallbackWhenPropertyNameIsMissingInProductPropertyValues()
    {
        // 1. Partial missing property_name: product 1 has null property_name, but product 0 has "Size".
        // ApplyPattern must resolve property name from the inventory variation definition.
        var invPartial = VariationEditor.Canonical(new JsonObject
        {
            ["price_on_property"] = new JsonArray(SizePropId),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(SizePropId),
            ["readiness_state_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                Product("", 1000, true, Pv(SizePropId, "Size", "Small", 1)),
                Product("", 1000, true, Pv(SizePropId, null, "Large", 2))
            ),
        });

        var pattern = "{sku}-{size}";
        var appliedPartial = SkuPatternResolver.ApplyPattern(invPartial, pattern, "BASE");

        var productsPartial = appliedPartial["products"]!.AsArray();
        Assert.Equal("BASE-Small", (string?)productsPartial[0]!["sku"]);
        Assert.Equal("BASE-Large", (string?)productsPartial[1]!["sku"]);

        // 2. Completely missing property_name on all products: falls back to property ID token {100}
        var invAllNull = VariationEditor.Canonical(new JsonObject
        {
            ["price_on_property"] = new JsonArray(SizePropId),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(SizePropId),
            ["readiness_state_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                Product("", 1000, true, Pv(SizePropId, null, "Small", 1)),
                Product("", 1000, true, Pv(SizePropId, null, "Large", 2))
            ),
        });

        var patternId = "{sku}-{100}";
        var appliedId = SkuPatternResolver.ApplyPattern(invAllNull, patternId, "BASE");
        var productsId = appliedId["products"]!.AsArray();
        Assert.Equal("BASE-Small", (string?)productsId[0]!["sku"]);
        Assert.Equal("BASE-Large", (string?)productsId[1]!["sku"]);
    }

    [Fact]
    public void ApplyPattern_EmptyOrNullProducts_ReturnsInventorySafely()
    {
        var invEmpty = new JsonObject { ["products"] = new JsonArray() };
        var res1 = SkuPatternResolver.ApplyPattern(invEmpty, "{sku}-{size}", "BASE");
        Assert.Equal("{sku}-{size}", (string?)res1["sku_pattern"]);

        var invNull = new JsonObject();
        var res2 = SkuPatternResolver.ApplyPattern(invNull, "{sku}-{size}", "BASE");
        Assert.Equal("{sku}-{size}", (string?)res2["sku_pattern"]);
    }

    [Fact]
    public void ApplyPattern_LargeInventoryGrid_PerformsFastAndAccurately()
    {
        // Stress test a 10 x 10 = 100 product grid
        var sizes = Enumerable.Range(1, 10).Select(i => $"Size-{i}").ToArray();
        var colors = Enumerable.Range(1, 10).Select(i => $"Color #{i} / Variant").ToArray();

        var products = new JsonArray();
        for (var s = 0; s < sizes.Length; s++)
        {
            for (var c = 0; c < colors.Length; c++)
            {
                products.Add(Product("", 1000, true,
                    Pv(SizePropId, "Size", sizes[s], s + 1),
                    Pv(ColorPropId, "Color", colors[c], c + 1)));
            }
        }

        var inv = VariationEditor.Canonical(new JsonObject
        {
            ["price_on_property"] = new JsonArray(SizePropId),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(SizePropId),
            ["readiness_state_on_property"] = new JsonArray(),
            ["products"] = products
        });

        var pattern = "{listing.sku}-{size}-{color}";
        var applied = SkuPatternResolver.ApplyPattern(inv, pattern, "BIGGRID");

        var appliedProducts = applied["products"]!.AsArray();
        Assert.Equal(100, appliedProducts.Count);

        var allSkus = appliedProducts.Select(p => (string?)p!["sku"]).ToList();
        // All 100 SKUs must be unique and non-empty
        Assert.Equal(100, allSkus.Distinct().Count());
        Assert.All(allSkus, sku =>
        {
            Assert.False(string.IsNullOrWhiteSpace(sku));
            Assert.StartsWith("BIGGRID-Size-", sku!);
            Assert.Contains("-Color-", sku!);
            Assert.EndsWith("-Variant", sku!);
        });
    }

    // ---------------------------------------------------------------------------------------------
    // 6. VariationEditor.AddValue with SKU Pattern Integration
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void VariationEditor_AddValue_AutoGeneratesSku_WhenSkuPatternIsConfigured()
    {
        var inv = VariationEditor.Canonical(new JsonObject
        {
            ["price_on_property"] = new JsonArray(SizePropId),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(SizePropId),
            ["readiness_state_on_property"] = new JsonArray(),
            ["sku_pattern"] = "{listing.sku}-{size}-{color}",
            ["base_sku"] = "SHIRT",
            ["products"] = new JsonArray(
                Product("SHIRT-Small-Black", 1000, true, Pv(SizePropId, "Size", "Small", 1), Pv(ColorPropId, "Color", "Black", 10)),
                Product("SHIRT-Small-White", 1000, true, Pv(SizePropId, "Size", "Small", 1), Pv(ColorPropId, "Color", "White", 11))
            ),
        });

        // Add a new value "Red / White" to Color
        var updated = VariationEditor.AddValue(inv, ColorPropId, "Red / White");

        var products = updated["products"]!.AsArray();
        Assert.Equal(3, products.Count);

        var newProduct = products.FirstOrDefault(p =>
            (string?)p!["property_values"]![1]!["values"]![0] == "Red / White");
        Assert.NotNull(newProduct);
        Assert.Equal("SHIRT-Small-Red-White", (string?)newProduct["sku"]);
    }
}
