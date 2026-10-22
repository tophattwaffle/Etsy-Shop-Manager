using System.Diagnostics;
using EtsyShopManager.Web.Modules.Templates;
using Xunit;

namespace EtsyShopManager.Tests;

/// <summary>
/// Empirical challenger stress tests for Milestone 7 (R6: Template Table Listing Filters).
/// Stress-tests TemplateFilterMatcher.Matches across edge-case queries, unicode, numeric boundaries,
/// multi-word and substring matches, null/corrupted fields, and exhaustive combinatorial filter matrices.
/// </summary>
public class ChallengerM7FilterStressTests
{
    private sealed record StressItem : ITemplateListingItem
    {
        public int ListingId { get; init; } = 1;
        public string Title { get; init; } = "";
        public int ShopId { get; init; } = 1;
        public string ShopName { get; init; } = "Shop One";
        public long EtsyListingId { get; init; } = 123456789;
        public string State { get; init; } = "active";
        public bool IsMissingOnEtsy { get; init; }
        public IReadOnlyList<string> Skus { get; init; } = [];
        public IReadOnlyList<string> Tags { get; init; } = [];
    }

    #region 1. Edge Case Search Queries: Special Characters, Wildcards, Quotes, Escapes

    [Theory]
    [InlineData("%")]
    [InlineData("100%")]
    [InlineData("% Silk")]
    [InlineData("_")]
    [InlineData("SKU_01")]
    [InlineData(@"\")]
    [InlineData(@"ITEM\01")]
    [InlineData("\"")]
    [InlineData("12\"")]
    [InlineData("'")]
    [InlineData("O'Connor")]
    [InlineData("`")]
    [InlineData("`backtick`")]
    [InlineData("!@#$^&*()+=")]
    [InlineData("[]{}|:;<>?,./")]
    public void SpecialCharacters_InSearchText_MatchesCorrectlyWithoutCrashing(string specialQuery)
    {
        var itemMatching = new StressItem
        {
            Title = $"Special Item with {specialQuery} inside Title",
            Skus = [$"SKU-{specialQuery}-123"],
            Tags = [$"tag-{specialQuery}"]
        };

        var itemNonMatching = new StressItem
        {
            Title = "Plain Standard Item",
            Skus = ["PLAIN-SKU-999"],
            Tags = ["plain", "standard"]
        };

        var filter = new TemplateListingFilter { Text = specialQuery };

        Assert.True(TemplateFilterMatcher.Matches(itemMatching, filter));
        Assert.False(TemplateFilterMatcher.Matches(itemNonMatching, filter));
    }

    [Fact]
    public void SpecialCharacters_SqlLikeWildcards_AreTreatedAsLiteralCharacters()
    {
        // In SQL LIKE, '%' matches anything and '_' matches any single char.
        // In-memory TemplateFilterMatcher must treat '%' and '_' as literal characters.
        var itemWithPercent = new StressItem { Title = "100% Organic Linen", Skus = ["LINEN%100"], Tags = ["100%"] };
        var itemWithoutPercent = new StressItem { Title = "100 Percent Organic Linen", Skus = ["LINEN100"], Tags = ["100"] };

        var filterPercent = new TemplateListingFilter { Text = "%" };
        Assert.True(TemplateFilterMatcher.Matches(itemWithPercent, filterPercent));
        Assert.False(TemplateFilterMatcher.Matches(itemWithoutPercent, filterPercent));

        var itemWithUnderscore = new StressItem { Title = "Custom_Product_Variant", Skus = ["VAR_A_01"], Tags = ["tag_name"] };
        var itemWithDash = new StressItem { Title = "Custom-Product-Variant", Skus = ["VAR-A-01"], Tags = ["tag-name"] };

        var filterUnderscore = new TemplateListingFilter { Text = "_" };
        Assert.True(TemplateFilterMatcher.Matches(itemWithUnderscore, filterUnderscore));
        Assert.False(TemplateFilterMatcher.Matches(itemWithDash, filterUnderscore));
    }

    [Fact]
    public void SpecialCharacters_BackslashesAndEscapes_AreTreatedLiterally()
    {
        var itemWithBackslash = new StressItem
        {
            Title = @"Folder\Subfolder\Listing",
            Skus = [@"SKU\V1"],
            Tags = [@"tag\v1"]
        };
        var itemWithSlash = new StressItem
        {
            Title = "Folder/Subfolder/Listing",
            Skus = ["SKU/V1"],
            Tags = ["tag/v1"]
        };

        var filterBackslash = new TemplateListingFilter { Text = @"\" };
        Assert.True(TemplateFilterMatcher.Matches(itemWithBackslash, filterBackslash));
        Assert.False(TemplateFilterMatcher.Matches(itemWithSlash, filterBackslash));

        var filterDoubleBackslash = new TemplateListingFilter { Text = @"\\" };
        Assert.False(TemplateFilterMatcher.Matches(itemWithBackslash, filterDoubleBackslash));
    }

    #endregion

    #region 2. Unicode Characters: Scripts, Diacritics, Casing, and Emojis

    [Fact]
    public void Unicode_CyrillicScript_MatchesCaseInsensitively()
    {
        var item = new StressItem
        {
            Title = "Кружка ручной работы из глины",
            Skus = ["КРУЖКА-01-СИНЯЯ"],
            Tags = ["глина", "ручная работа", "подарок"]
        };

        // Exact match
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "Кружка" }));
        // Lowercase query
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "кружка" }));
        // Uppercase query
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "РУЧНОЙ" }));
        // SKU match
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "синяя" }));
        // Tag match
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "ПОДАРОК" }));
        // Non-matching
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "тарелка" }));
    }

    [Fact]
    public void Unicode_GreekScript_MatchesWithExactDiacriticsAndCasing()
    {
        var item = new StressItem
        {
            Title = "Χειροποίητο κεραμικό βάζο",
            Skus = ["SKU-01"],
            Tags = ["κεραμικά", "τέχνη"]
        };

        // Lowercase query matching title
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "χειροποίητο" }));
        // Uppercase query with matching tonos accent (ό -> Ό)
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "ΚΕΡΑΜΙΚΌ" }));
        // Exact lowercase match with accent
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "βάζο" }));
        // Uppercase with accent (ά -> Ά)
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "ΒΆΖΟ" }));

        // Diacritic mismatch: "βαζο" without accent does not match "βάζο" with accent
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "βαζο" }));
        // Completely different word
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "πιάτο" }));

        // Now test SKU with unaccented all-caps Greek (standard practice for Greek SKUs)
        var itemWithGreekSku = new StressItem
        {
            Title = "Modern Pottery",
            Skus = ["ΒΑΖΟ-01"],
            Tags = ["decor"]
        };
        // Lowercase unaccented "βαζο" matches uppercase unaccented SKU "ΒΑΖΟ-01"
        Assert.True(TemplateFilterMatcher.Matches(itemWithGreekSku, new TemplateListingFilter { Text = "βαζο" }));
        Assert.True(TemplateFilterMatcher.Matches(itemWithGreekSku, new TemplateListingFilter { Text = "ΒΑΖΟ" }));
    }

    [Fact]
    public void Unicode_CJKAndArabicScripts_MatchCorrectly()
    {
        var itemJapanese = new StressItem
        {
            Title = "手作りの美濃焼陶器マグカップ",
            Skus = ["MINO-MUG-01"],
            Tags = ["陶器", "マグカップ", "美濃焼"]
        };

        Assert.True(TemplateFilterMatcher.Matches(itemJapanese, new TemplateListingFilter { Text = "陶器" }));
        Assert.True(TemplateFilterMatcher.Matches(itemJapanese, new TemplateListingFilter { Text = "マグカップ" }));
        Assert.True(TemplateFilterMatcher.Matches(itemJapanese, new TemplateListingFilter { Text = "手作り" }));
        Assert.False(TemplateFilterMatcher.Matches(itemJapanese, new TemplateListingFilter { Text = "急須" }));

        var itemArabic = new StressItem
        {
            Title = "فنجان قهوة سيراميك يدوي الصنع",
            Skus = ["AR-CUP-01"],
            Tags = ["فنجان", "قهوة", "سيراميك"]
        };

        Assert.True(TemplateFilterMatcher.Matches(itemArabic, new TemplateListingFilter { Text = "فنجان" }));
        Assert.True(TemplateFilterMatcher.Matches(itemArabic, new TemplateListingFilter { Text = "قهوة" }));
        Assert.True(TemplateFilterMatcher.Matches(itemArabic, new TemplateListingFilter { Text = "سيراميك" }));
        Assert.False(TemplateFilterMatcher.Matches(itemArabic, new TemplateListingFilter { Text = "شاي" }));
    }

    [Fact]
    public void Unicode_AccentsAndUmlauts_MatchCorrectly()
    {
        var item = new StressItem
        {
            Title = "Café Crème Ceramic Mug — Über München",
            Tags = ["naïve art", "décor français"]
        };

        // Exact diacritic matching
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "Café" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "München" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "naïve" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "français" }));

        // Cased versions
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "café" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "über" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "ÜBER" }));

        // Unaccented "Cafe" does not match accented "Café" (matching SQLite ASCII LIKE behavior)
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "Cafe" }));
    }

    [Fact]
    public void Unicode_Emojis_InAnyField_MatchAccurately()
    {
        var item = new StressItem
        {
            Title = "✨ Silk Scarf 🧣 🌟",
            Skus = ["SKU-🧣-01"],
            Tags = ["silk ✨", "scarf 🧣", "gift 🎁"]
        };

        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "✨" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "🧣" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "🌟" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "🎁" }));
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "🔥" }));
    }

    #endregion

    #region 3. Numeric IDs: Leading Zeros, Overflow, Boundary, and Number Formats

    [Fact]
    public void NumericId_LeadingZeros_ParsesAndMatchesEtsyListingId()
    {
        // "0123" parses as numeric long 123.
        var item123 = new StressItem { EtsyListingId = 123, Title = "Standard Mug" };
        var item456 = new StressItem { EtsyListingId = 456, Title = "Standard Plate" };

        Assert.True(TemplateFilterMatcher.Matches(item123, new TemplateListingFilter { Text = "0123" }));
        Assert.False(TemplateFilterMatcher.Matches(item456, new TemplateListingFilter { Text = "0123" }));

        // But "0123" can also match as a substring in SKU or Title
        var itemWithSkuLeadingZero = new StressItem { EtsyListingId = 999, Title = "Plate", Skus = ["SKU-0123-A"] };
        Assert.True(TemplateFilterMatcher.Matches(itemWithSkuLeadingZero, new TemplateListingFilter { Text = "0123" }));
    }

    [Fact]
    public void NumericId_ExceedingLongBounds_DoesNotThrowAndSafelyEvaluatesSubstring()
    {
        // Number with 80 digits far exceeds long.MaxValue (9223372036854775807)
        var hugeNumber = new string('9', 80);
        var item = new StressItem
        {
            EtsyListingId = 123456789,
            Title = "Standard Item"
        };

        var filter = new TemplateListingFilter { Text = hugeNumber };

        // Should return false cleanly without OverflowException
        Assert.False(TemplateFilterMatcher.Matches(item, filter));

        // If the huge number actually appears in the title or SKU, it should match via substring
        var itemWithHugeNumberTitle = new StressItem
        {
            EtsyListingId = 123456789,
            Title = $"Item with {hugeNumber} inside title"
        };
        Assert.True(TemplateFilterMatcher.Matches(itemWithHugeNumberTitle, filter));
    }

    [Fact]
    public void NumericId_BoundaryValues_LongMaxAndMin()
    {
        var itemMax = new StressItem { EtsyListingId = long.MaxValue, Title = "Item at Max Long" };
        var itemMin = new StressItem { EtsyListingId = long.MinValue, Title = "Item at Min Long" };

        Assert.True(TemplateFilterMatcher.Matches(itemMax, new TemplateListingFilter { Text = long.MaxValue.ToString() }));
        Assert.False(TemplateFilterMatcher.Matches(itemMin, new TemplateListingFilter { Text = long.MaxValue.ToString() }));

        Assert.True(TemplateFilterMatcher.Matches(itemMin, new TemplateListingFilter { Text = long.MinValue.ToString() }));
        Assert.False(TemplateFilterMatcher.Matches(itemMax, new TemplateListingFilter { Text = long.MinValue.ToString() }));
    }

    [Theory]
    [InlineData("123.45")]
    [InlineData("1,000")]
    [InlineData("1e6")]
    [InlineData("0x1A")]
    [InlineData("12345L")]
    public void NumericId_NonStandardNumberFormats_HandledSafely(string query)
    {
        // These do not parse as standard long IDs via long.TryParse(query, out ...)
        var item = new StressItem { EtsyListingId = 12345, Title = "Item Standard" };

        var filter = new TemplateListingFilter { Text = query };
        // Unless query matches Title/SKU/Tag substring, it returns false
        Assert.False(TemplateFilterMatcher.Matches(item, filter));

        var itemWithLiteralText = new StressItem { Title = $"Item with {query} in title" };
        Assert.True(TemplateFilterMatcher.Matches(itemWithLiteralText, filter));
    }

    [Fact]
    public void NumericId_SignedNumbers_Behavior()
    {
        var item = new StressItem { EtsyListingId = 12345, Title = "Standard Item" };

        // In .NET, long.TryParse("+12345", out var id) parses as 12345 (AllowLeadingSign).
        // Therefore "+12345" matches an item whose EtsyListingId is 12345!
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "+12345" }));

        // "-12345" parses as -12345, which does NOT equal 12345.
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "-12345" }));

        // If title contains "-12345", it matches via Title substring
        var itemWithNegative = new StressItem { EtsyListingId = 99999, Title = "Temperature probe -12345" };
        Assert.True(TemplateFilterMatcher.Matches(itemWithNegative, new TemplateListingFilter { Text = "-12345" }));
    }

    #endregion

    #region 4. Multi-Word and Substring Matches Across Fields

    [Fact]
    public void MultiWord_ExactSubstring_MatchesInTitleSkuAndTags()
    {
        var item = new StressItem
        {
            Title = "Handmade Ceramic Mug with Blue Glaze",
            Skus = ["MUG-BLU-CER-01", "GIFT-BOX-SET-LG"],
            Tags = ["ceramic mug", "blue glaze pottery", "kitchen decor"]
        };

        // Multi-word in Title
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "Ceramic Mug" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "Blue Glaze" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "Mug with Blue" }));

        // Multi-word in Tags
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "blue glaze" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "kitchen decor" }));

        // Multi-word in SKU
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "BOX-SET" }));
    }

    [Fact]
    public void MultiWord_DisjointWords_DoNotMatchSubstring()
    {
        // TemplateFilterMatcher and ListingQueries.Filter both implement substring search (LIKE %query%),
        // not tokenized AND search. Disjoint words should return false.
        var item = new StressItem
        {
            Title = "Handmade Ceramic Mug with Blue Glaze",
            Skus = ["MUG-01"],
            Tags = ["ceramic", "pottery"]
        };

        // "Handmade Glaze" is not a contiguous substring
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "Handmade Glaze" }));
    }

    [Fact]
    public void MultiWord_WordsAcrossDifferentFields_DoNotCrossFieldBoundaries()
    {
        var item = new StressItem
        {
            Title = "Ceramic Bowl",
            Skus = ["BOWL-01"],
            Tags = ["blue glaze"]
        };

        // "Bowl blue" has "Bowl" in title and "blue" in tag. Substring matching does not cross fields.
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "Bowl blue" }));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData(" \t \r\n ")]
    public void WhitespaceOnlyQuery_IsTreatedAsNullFilter_MatchesAll(string whitespaceQuery)
    {
        var item = new StressItem { Title = "Any Item" };
        var filter = new TemplateListingFilter { Text = whitespaceQuery };

        Assert.True(TemplateFilterMatcher.Matches(item, filter));
    }

    [Fact]
    public void SurroundingWhitespace_InQuery_IsTrimmedBeforeMatching()
    {
        var item = new StressItem { Title = "Handmade Ceramic Mug" };

        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "  Ceramic  " }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "\tMug\n" }));
    }

    #endregion

    #region 5. Defensive Hardening: Nulls, Null Elements, and Empty Collections

    [Fact]
    public void NullItemFields_DoNotThrowExceptions()
    {
        var corruptedItem = new StressItem
        {
            Title = null!,
            Skus = null!,
            Tags = null!,
            State = null!,
            ShopName = null!
        };

        // Status matching with null state
        Assert.True(TemplateFilterMatcher.Matches(corruptedItem, new TemplateListingFilter { Status = "all" }));
        Assert.False(TemplateFilterMatcher.Matches(corruptedItem, new TemplateListingFilter { Status = "active" }));

        // Text matching with null fields
        Assert.False(TemplateFilterMatcher.Matches(corruptedItem, new TemplateListingFilter { Text = "test" }));
        Assert.True(TemplateFilterMatcher.Matches(corruptedItem, new TemplateListingFilter { Text = null }));
    }

    [Fact]
    public void Collections_ContainingNullOrEmptyElements_AreHandledSafely()
    {
        var itemWithNullElements = new StressItem
        {
            Title = "Solid Item",
            Skus = [null!, "", "VALID-SKU-1", null!, "   "],
            Tags = [null!, "", "valid-tag", null!, "   "]
        };

        Assert.True(TemplateFilterMatcher.Matches(itemWithNullElements, new TemplateListingFilter { Text = "VALID-SKU" }));
        Assert.True(TemplateFilterMatcher.Matches(itemWithNullElements, new TemplateListingFilter { Text = "valid-tag" }));
        Assert.False(TemplateFilterMatcher.Matches(itemWithNullElements, new TemplateListingFilter { Text = "nonexistent" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("UNKNOWN_STATUS")]
    [InlineData("  Active  ")]
    [InlineData("ACTIVE")]
    [InlineData("DrAfT")]
    [InlineData("  sOlD_oUt  ")]
    public void StatusFilter_HandlesMalformedOrVariedCasing(string? status)
    {
        var activeItem = new StressItem { State = "active", IsMissingOnEtsy = false };
        var draftItem = new StressItem { State = "draft", IsMissingOnEtsy = false };
        var soldOutItem = new StressItem { State = "sold_out", IsMissingOnEtsy = false };
        var missingItem = new StressItem { State = "active", IsMissingOnEtsy = true };

        var filter = new TemplateListingFilter { Status = status! };

        var normalized = (status ?? "all").Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "active":
                Assert.True(TemplateFilterMatcher.Matches(activeItem, filter));
                Assert.False(TemplateFilterMatcher.Matches(draftItem, filter));
                break;
            case "draft":
                Assert.True(TemplateFilterMatcher.Matches(draftItem, filter));
                Assert.False(TemplateFilterMatcher.Matches(activeItem, filter));
                break;
            case "sold_out":
                Assert.True(TemplateFilterMatcher.Matches(soldOutItem, filter));
                Assert.False(TemplateFilterMatcher.Matches(activeItem, filter));
                break;
            case "all":
            default:
                // Unknown/default behaves like "all": matches non-missing items
                Assert.True(TemplateFilterMatcher.Matches(activeItem, filter));
                Assert.True(TemplateFilterMatcher.Matches(draftItem, filter));
                Assert.False(TemplateFilterMatcher.Matches(missingItem, filter));
                break;
        }
    }

    #endregion

    #region 6. Combinatorial Filter Matrix: (Text x ShopId x Status) vs Ground Truth Oracle

    private static bool OracleMatches(ITemplateListingItem item, TemplateListingFilter filter)
    {
        // 1. Shop filter
        if (filter.ShopId.HasValue && item.ShopId != filter.ShopId.Value)
            return false;

        // 2. Status filter
        var status = (filter.Status ?? "all").Trim().ToLowerInvariant();
        var statusMatches = status switch
        {
            "all" => !item.IsMissingOnEtsy,
            "active" => !item.IsMissingOnEtsy && string.Equals(item.State, "active", StringComparison.OrdinalIgnoreCase),
            "draft" => !item.IsMissingOnEtsy && string.Equals(item.State, "draft", StringComparison.OrdinalIgnoreCase),
            "expired" => !item.IsMissingOnEtsy && string.Equals(item.State, "expired", StringComparison.OrdinalIgnoreCase),
            "sold_out" => !item.IsMissingOnEtsy && string.Equals(item.State, "sold_out", StringComparison.OrdinalIgnoreCase),
            "inactive" => !item.IsMissingOnEtsy && string.Equals(item.State, "inactive", StringComparison.OrdinalIgnoreCase),
            "missing" => item.IsMissingOnEtsy,
            _ => !item.IsMissingOnEtsy
        };

        if (!statusMatches)
            return false;

        // 3. Text filter
        var text = filter.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            var isNumeric = long.TryParse(text, out var etsyId);
            var matchesEtsyId = isNumeric && item.EtsyListingId == etsyId;
            var matchesTitle = !string.IsNullOrEmpty(item.Title) && item.Title.Contains(text, StringComparison.OrdinalIgnoreCase);
            var matchesSku = item.Skus is not null && item.Skus.Any(s => !string.IsNullOrEmpty(s) && s.Contains(text, StringComparison.OrdinalIgnoreCase));
            var matchesTag = item.Tags is not null && item.Tags.Any(t => !string.IsNullOrEmpty(t) && t.Contains(text, StringComparison.OrdinalIgnoreCase));

            if (!matchesEtsyId && !matchesTitle && !matchesSku && !matchesTag)
                return false;
        }

        return true;
    }

    [Fact]
    public void CombinatorialMatrix_ExhaustiveCartesianProduct_MatchesOracle()
    {
        // Build a diverse catalog of test items
        var catalog = new List<StressItem>
        {
            new() { ListingId = 1, Title = "Blue Cotton Shirt", ShopId = 1, State = "active", IsMissingOnEtsy = false, Skus = ["SHIRT-BLU-S"], Tags = ["clothing", "summer"], EtsyListingId = 1001 },
            new() { ListingId = 2, Title = "Red Wool Sweater", ShopId = 1, State = "draft", IsMissingOnEtsy = false, Skus = ["SWEATER-RED-M"], Tags = ["winter", "apparel"], EtsyListingId = 1002 },
            new() { ListingId = 3, Title = "Leather Wallet", ShopId = 1, State = "expired", IsMissingOnEtsy = false, Skus = ["WALLET-BRN"], Tags = ["accessories", "leather"], EtsyListingId = 1003 },
            new() { ListingId = 4, Title = "Silk Scarf", ShopId = 1, State = "inactive", IsMissingOnEtsy = false, Skus = ["SCARF-SLK"], Tags = ["accessories", "silk"], EtsyListingId = 1004 },
            new() { ListingId = 5, Title = "Ceramic Coffee Mug", ShopId = 2, State = "active", IsMissingOnEtsy = false, Skus = ["MUG-WHT-01"], Tags = ["kitchen", "ceramic"], EtsyListingId = 2001 },
            new() { ListingId = 6, Title = "Handmade Wooden Spoon", ShopId = 2, State = "sold_out", IsMissingOnEtsy = false, Skus = ["SPOON-OAK"], Tags = ["kitchen", "wood"], EtsyListingId = 2002 },
            new() { ListingId = 7, Title = "Vintage Silver Ring", ShopId = 2, State = "active", IsMissingOnEtsy = true, Skus = ["RING-SLV-7"], Tags = ["jewelry", "vintage"], EtsyListingId = 2003 },
            new() { ListingId = 8, Title = "Deleted Antique Clock", ShopId = 2, State = "draft", IsMissingOnEtsy = true, Skus = ["CLOCK-ANT"], Tags = ["decor", "vintage"], EtsyListingId = 2004 },
        };

        // Text query options
        string?[] textOptions =
        [
            null,
            "",
            "   ",
            "shirt",              // matches Item 1 Title
            "SWEATER-RED",        // matches Item 2 SKU
            "kitchen",            // matches Item 5 & 6 Tags
            "accessories",        // matches Item 3 & 4 Tags
            "2001",               // matches Item 5 EtsyListingId
            "02001",              // numeric with leading zero -> parses to 2001 -> matches Item 5
            "vintage",            // matches Item 7 & 8 Tags
            "nonexistent_xyz_123" // matches nothing
        ];

        // ShopId options
        int?[] shopOptions = [null, 1, 2, 999];

        // Status options
        string[] statusOptions =
        [
            "all",
            "active",
            "draft",
            "expired",
            "sold_out",
            "inactive",
            "missing",
            "  ACTIVE  ",
            "unknown_status"
        ];

        var totalEvaluations = 0;

        foreach (var text in textOptions)
        {
            foreach (var shopId in shopOptions)
            {
                foreach (var status in statusOptions)
                {
                    var filter = new TemplateListingFilter
                    {
                        Text = text,
                        ShopId = shopId,
                        Status = status
                    };

                    foreach (var item in catalog)
                    {
                        var expected = OracleMatches(item, filter);
                        var actual = TemplateFilterMatcher.Matches(item, filter);

                        Assert.True(actual == expected,
                            $"Mismatch for Item {item.ListingId} ({item.Title}) with Filter: Text='{text}', ShopId={shopId}, Status='{status}'. Expected {expected}, got {actual}.");

                        totalEvaluations++;
                    }
                }
            }
        }

        // 11 text options * 4 shop options * 9 status options * 8 catalog items = 3,168 evaluations
        Assert.Equal(3168, totalEvaluations);
    }

    #endregion

    #region 7. Stress Testing & Performance Benchmark

    [Fact]
    public void StressPerformance_TenThousandItemsFilteredRapidly()
    {
        // Generate 10,000 items
        const int itemCount = 10_000;
        var items = new List<StressItem>(itemCount);
        var states = new[] { "active", "draft", "expired", "sold_out", "inactive" };

        for (var i = 0; i < itemCount; i++)
        {
            items.Add(new StressItem
            {
                ListingId = i + 1,
                ShopId = (i % 3) + 1,
                Title = $"Handmade Product Number {i} with Ceramic Glaze and Wooden Base",
                EtsyListingId = 100_000_000L + i,
                State = states[i % states.Length],
                IsMissingOnEtsy = i % 20 == 0, // 5% missing
                Skus = [$"SKU-{i:D6}-A", $"SKU-{i:D6}-B"],
                Tags = [$"tag{i % 50}", "pottery", "decor", "gift"]
            });
        }

        var filter = new TemplateListingFilter
        {
            ShopId = 1,
            Status = "active",
            Text = "Ceramic Glaze"
        };

        var sw = Stopwatch.StartNew();

        var matched = 0;
        for (var run = 0; run < 10; run++)
        {
            matched = 0;
            foreach (var item in items)
            {
                if (TemplateFilterMatcher.Matches(item, filter))
                    matched++;
            }
        }

        sw.Stop();

        // 100,000 in-memory evaluations should easily complete in under 1.5 seconds.
        Assert.True(matched > 0);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Filtering was too slow: took {sw.ElapsedMilliseconds} ms for 100,000 evaluations");
    }

    #endregion

    #region 8. Tri-State Bulk Selection Stress Under Rapid Filter Transitions

    [Fact]
    public void TriStateBulkSelection_SurvivesComplexFilterMutations()
    {
        var items = new List<StressItem>
        {
            new() { ListingId = 10, ShopId = 1, Title = "Blue Shirt", State = "active" },
            new() { ListingId = 20, ShopId = 1, Title = "Blue Pants", State = "active" },
            new() { ListingId = 30, ShopId = 1, Title = "Red Hat", State = "draft" },
            new() { ListingId = 40, ShopId = 2, Title = "Blue Mug", State = "active" },
            new() { ListingId = 50, ShopId = 2, Title = "Green Mug", State = "active" },
            new() { ListingId = 60, ShopId = 2, Title = "Yellow Bowl", State = "sold_out" },
        };

        var picked = new HashSet<int>();

        bool? GetCandidateHeaderChecked(IReadOnlyList<StressItem> visible)
        {
            var visibleIds = visible.Select(c => c.ListingId).ToList();
            if (visibleIds.Count == 0) return false;
            var pickedCount = visibleIds.Count(picked.Contains);
            if (pickedCount == 0) return false;
            if (pickedCount == visibleIds.Count) return true;
            return null;
        }

        void TogglePickAllVisible(IReadOnlyList<StressItem> visible)
        {
            var visibleIds = visible.Select(c => c.ListingId).ToList();
            if (visibleIds.Count == 0) return;
            var allPicked = visibleIds.All(picked.Contains);
            if (allPicked)
                picked.ExceptWith(visibleIds);
            else
                picked.UnionWith(visibleIds);
        }

        // View 1: Text = "Blue" -> Items 10, 20, 40 visible
        var filterBlue = new TemplateListingFilter { Text = "Blue" };
        var visibleBlue = items.Where(i => TemplateFilterMatcher.Matches(i, filterBlue)).ToList();
        Assert.Equal(3, visibleBlue.Count);
        Assert.False(GetCandidateHeaderChecked(visibleBlue));

        // Select all Blue
        TogglePickAllVisible(visibleBlue);
        Assert.True(GetCandidateHeaderChecked(visibleBlue));
        Assert.Equal(3, picked.Count);

        // View 2: Shop 1, Status "draft" -> Item 30 only visible
        var filterShop1Draft = new TemplateListingFilter { ShopId = 1, Status = "draft" };
        var visibleShop1Draft = items.Where(i => TemplateFilterMatcher.Matches(i, filterShop1Draft)).ToList();
        Assert.Single(visibleShop1Draft);
        Assert.Equal(30, visibleShop1Draft[0].ListingId);

        // Item 30 is not picked yet
        Assert.False(GetCandidateHeaderChecked(visibleShop1Draft));

        // Pick Item 30
        TogglePickAllVisible(visibleShop1Draft);
        Assert.True(GetCandidateHeaderChecked(visibleShop1Draft));
        Assert.Equal(4, picked.Count); // 10, 20, 40, 30

        // View 3: All active in Shop 1 -> Items 10 and 20 visible. Both are already picked!
        var filterShop1Active = new TemplateListingFilter { ShopId = 1, Status = "active" };
        var visibleShop1Active = items.Where(i => TemplateFilterMatcher.Matches(i, filterShop1Active)).ToList();
        Assert.Equal(2, visibleShop1Active.Count);
        Assert.True(GetCandidateHeaderChecked(visibleShop1Active));

        // Uncheck all in View 3
        TogglePickAllVisible(visibleShop1Active);
        Assert.False(GetCandidateHeaderChecked(visibleShop1Active));
        // Items 10 and 20 removed, but 40 (Shop 2 Blue Mug) and 30 (Shop 1 Red Hat) remain!
        Assert.Equal(2, picked.Count);
        Assert.Contains(40, picked);
        Assert.Contains(30, picked);
        Assert.DoesNotContain(10, picked);
        Assert.DoesNotContain(20, picked);

        // View 4: Back to Blue -> Items 10, 20, 40 visible.
        // 40 is picked, 10 and 20 are NOT picked -> Header should be null (indeterminate)
        Assert.Null(GetCandidateHeaderChecked(visibleBlue));
    }

    #endregion
}
