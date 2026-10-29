using System.Diagnostics;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Web.Modules.Orders;

namespace EtsyShopManager.Tests;

public class ChallengerRulesEngineTests
{
    private static OrderColorEngine DefaultEngine() => new(OrderColorService.Default);

    private static OrderColorEngine CustomEngine(IEnumerable<string>? randomWords = null) =>
        new(new OrderColorConfig(
            [
                new OrderColor("Copper", ["#B87333"], true),
                new OrderColor("Blue", ["#0000FF"], false),
                new OrderColor("Red", ["#FF0000"], false),
                new OrderColor("Green", ["#00FF00"], false),
                new OrderColor("Neon Rider", ["#4FC3F7", "#FF1FD1"], false), // Multi-color filament (2 colors)
                new OrderColor("Sunrise", ["#FF5722", "#FFC107"], false),    // Multi-color filament (2 colors)
            ],
            (randomWords ?? ["random", "surprise me", "dealer's choice"]).ToList()));

    #region 1. Color Count Rule & Quantity Scaling

    [Theory]
    [InlineData(1, 2, 2, "Copper, Blue", true)]                    // Exact 2 (1 unit)
    [InlineData(1, 2, 2, "Copper", false)]                          // Under min
    [InlineData(1, 2, 2, "Copper, Blue, Red", false)]               // Over max
    [InlineData(2, 2, 2, "Copper, Blue, Red, Green", true)]         // Exact 4 (2 units)
    [InlineData(2, 2, 2, "Copper, Blue, Red", false)]               // Found 3, expected 4
    [InlineData(5, 1, 1, "Copper, Blue, Red, Green, Neon Rider", true)] // Exact 5 (5 units * 1)
    [InlineData(5, 1, 1, "Copper, Blue, Red, Green", false)]        // Found 4, expected 5
    [InlineData(10, 1, 1, "Copper, Blue, Red, Green", false)]       // Found 4, expected 10
    public void Color_count_scales_linearly_with_item_quantity(
        int quantity, int min, int max, string text, bool expectedValid)
    {
        var rule = new PersonalizationRule
        {
            Name = "Dynamic Color Count",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = min,
            MaxColors = max,
        };

        var result = PersonalizationEngine.Evaluate(rule, text, quantity, CustomEngine());
        Assert.Equal(expectedValid, result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    public void Color_count_zero_or_negative_quantity_clamps_to_single_unit(int invalidQuantity)
    {
        // PROJECT.md requirement: Math.Max(1, itemQuantity)
        var rule = new PersonalizationRule
        {
            Name = "Min 2 Colors",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 2,
            MaxColors = 2,
        };

        // If clamped to 1 unit, 2 colors pass, 1 color fails
        var pass = PersonalizationEngine.Evaluate(rule, "Copper and Blue", invalidQuantity, CustomEngine());
        Assert.True(pass.IsValid);

        var fail = PersonalizationEngine.Evaluate(rule, "Copper only", invalidQuantity, CustomEngine());
        Assert.False(fail.IsValid);
        Assert.Contains("Expected 2 colors, found 1", fail.ErrorMessage);
    }

    [Fact]
    public void Color_count_multi_color_filaments_count_as_single_color_group()
    {
        var engine = CustomEngine();

        // "Neon Rider" has 2 hex values, "Sunrise" has 2 hex values
        var ruleSingle = new PersonalizationRule
        {
            Name = "Exactly One Color",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 1,
            MaxColors = 1,
        };

        var res1 = PersonalizationEngine.Evaluate(ruleSingle, "Neon Rider", 1, engine);
        Assert.True(res1.IsValid);

        var res2 = PersonalizationEngine.Evaluate(ruleSingle, "Sunrise", 1, engine);
        Assert.True(res2.IsValid);

        var rulePair = new PersonalizationRule
        {
            Name = "Exactly Two Colors",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 2,
            MaxColors = 2,
        };

        var resPair = PersonalizationEngine.Evaluate(rulePair, "Neon Rider and Sunrise", 1, engine);
        Assert.True(resPair.IsValid);

        // Neon Rider alone fails the rule of 2 colors
        var resFail = PersonalizationEngine.Evaluate(rulePair, "Neon Rider", 1, engine);
        Assert.False(resFail.IsValid);
        Assert.Contains("Expected 2 colors, found 1", resFail.ErrorMessage);
    }

    [Fact]
    public void Color_count_unbounded_minimum_only()
    {
        var rule = new PersonalizationRule
        {
            Name = "At least 2 colors",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 2,
            MaxColors = null,
        };

        var engine = CustomEngine();

        // 1 color fails
        var f1 = PersonalizationEngine.Evaluate(rule, "Copper", 1, engine);
        Assert.False(f1.IsValid);
        Assert.Contains("Expected at least 2 colors, found 1", f1.ErrorMessage);

        // 2, 3, 4 colors pass
        Assert.True(PersonalizationEngine.Evaluate(rule, "Copper, Blue", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "Copper, Blue, Red", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "Copper, Blue, Red, Green", 1, engine).IsValid);

        // Scales with quantity: quantity 2 requires at least 4 colors
        var f2 = PersonalizationEngine.Evaluate(rule, "Copper, Blue, Red", 2, engine);
        Assert.False(f2.IsValid);
        Assert.Contains("Expected at least 4 colors, found 3", f2.ErrorMessage);

        Assert.True(PersonalizationEngine.Evaluate(rule, "Copper, Blue, Red, Green", 2, engine).IsValid);
    }

    [Fact]
    public void Color_count_unbounded_maximum_only()
    {
        var rule = new PersonalizationRule
        {
            Name = "At most 2 colors",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = null,
            MaxColors = 2,
        };

        var engine = CustomEngine();

        // 0, 1, 2 colors pass
        Assert.True(PersonalizationEngine.Evaluate(rule, "No colors specified", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "Copper", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "Copper, Blue", 1, engine).IsValid);

        // 3 colors fail
        var f1 = PersonalizationEngine.Evaluate(rule, "Copper, Blue, Red", 1, engine);
        Assert.False(f1.IsValid);
        Assert.Contains("Expected between 0 and 2 colors, found 3", f1.ErrorMessage);

        // Scales with quantity: quantity 2 allows up to 4 colors
        Assert.True(PersonalizationEngine.Evaluate(rule, "Copper, Blue, Red", 2, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "Copper, Blue, Red, Green", 2, engine).IsValid);

        // 5 colors fail with quantity 2
        var f2 = PersonalizationEngine.Evaluate(rule, "Copper, Blue, Red, Green, Neon Rider", 2, engine);
        Assert.False(f2.IsValid);
        Assert.Contains("Expected between 0 and 4 colors, found 5", f2.ErrorMessage);
    }

    [Fact]
    public void Color_count_both_bounds_null_always_passes()
    {
        var rule = new PersonalizationRule
        {
            Name = "Unrestricted colors",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = null,
            MaxColors = null,
        };

        var engine = CustomEngine();
        Assert.True(PersonalizationEngine.Evaluate(rule, "", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "Copper, Blue, Red, Green", 1, engine).IsValid);
    }

    [Fact]
    public void Color_count_zero_bounds_expects_zero_colors()
    {
        var rule = new PersonalizationRule
        {
            Name = "Zero colors allowed",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 0,
            MaxColors = 0,
        };

        var engine = CustomEngine();
        Assert.True(PersonalizationEngine.Evaluate(rule, "Please no color tint", 1, engine).IsValid);

        var fail = PersonalizationEngine.Evaluate(rule, "Copper", 1, engine);
        Assert.False(fail.IsValid);
        Assert.Contains("Expected 0 colors, found 1", fail.ErrorMessage);
    }

    [Theory]
    [InlineData("copper and blue")]
    [InlineData("COPPER AND BLUE")]
    [InlineData("CoPpEr AnD bLuE")]
    [InlineData("  Copper,   &   Blue!  ")]
    [InlineData("Copper/Blue")]
    public void Color_count_handles_case_and_punctuation_variations(string input)
    {
        var rule = new PersonalizationRule
        {
            Name = "Exact 2 Colors",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 2,
            MaxColors = 2,
        };

        var result = PersonalizationEngine.Evaluate(rule, input, 1, CustomEngine());
        Assert.True(result.IsValid);
    }

    #endregion

    #region 2. Must Mention One Of (Word List)

    [Theory]
    [InlineData("ordered yesterday")]       // Contains "red"
    [InlineData("feeling bored")]           // Contains "red"
    [InlineData("predator mask")]           // Contains "red"
    [InlineData("pure hatred")]             // Contains "red"
    [InlineData("kindred spirits")]         // Contains "red"
    [InlineData("shredded paper")]          // Contains "red"
    [InlineData("feathered wings")]         // Contains "red"
    public void Word_list_strictly_enforces_word_boundaries_against_subwords(string input)
    {
        var rule = new PersonalizationRule
        {
            Name = "Red Switch Check",
            RuleType = PersonalizationRuleType.WordList,
            WordList = "red",
        };

        var result = PersonalizationEngine.Evaluate(rule, input, 1, DefaultEngine());
        Assert.False(result.IsValid);
        Assert.Contains("Must mention one of: red", result.ErrorMessage);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("RED")]
    [InlineData("Red")]
    [InlineData("red switch please")]
    [InlineData("I want bright red!")]
    [InlineData("(red)")]
    [InlineData("[red]")]
    [InlineData("switch: red, keycaps: blue")]
    [InlineData("finish: 'red'")]
    public void Word_list_matches_exact_word_boundaries_with_surrounding_punctuation(string input)
    {
        var rule = new PersonalizationRule
        {
            Name = "Red Switch Check",
            RuleType = PersonalizationRuleType.WordList,
            WordList = "red",
        };

        var result = PersonalizationEngine.Evaluate(rule, input, 1, DefaultEngine());
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Word_list_supports_synonyms_with_slash_syntax_and_spaces()
    {
        var rule = new PersonalizationRule
        {
            Name = "Switch Type",
            RuleType = PersonalizationRuleType.WordList,
            WordList = "linear / red / yellow\ntactile / brown\nclicky / blue / green",
        };

        var engine = DefaultEngine();

        // Any synonym from any line passes
        Assert.True(PersonalizationEngine.Evaluate(rule, "linear switch", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "RED SWITCH", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "yellow variant", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "tactile feedback", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "brown stem", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "clicky sound", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "blue switch", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "green switch", 1, engine).IsValid);

        // Completely unrelated text fails and lists the primary options (first word of each line)
        var fail = PersonalizationEngine.Evaluate(rule, "silent membrane keyboard", 1, engine);
        Assert.False(fail.IsValid);
        Assert.Contains("Must mention one of: linear, tactile, clicky", fail.ErrorMessage);
    }

    [Fact]
    public void Word_list_handles_empty_or_whitespace_lines_gracefully()
    {
        var rule = new PersonalizationRule
        {
            Name = "Switch Type",
            RuleType = PersonalizationRuleType.WordList,
            WordList = "  \n\nlinear / red\n   \n\ntactile / brown\n  ",
        };

        var engine = DefaultEngine();
        Assert.True(PersonalizationEngine.Evaluate(rule, "linear", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "brown", 1, engine).IsValid);

        var fail = PersonalizationEngine.Evaluate(rule, "none", 1, engine);
        Assert.False(fail.IsValid);
        Assert.Contains("Must mention one of: linear, tactile", fail.ErrorMessage);
    }

    [Fact]
    public void Word_list_with_empty_definition_always_passes()
    {
        var rule = new PersonalizationRule
        {
            Name = "Empty Word List",
            RuleType = PersonalizationRuleType.WordList,
            WordList = "   \n\n   ",
        };

        var result = PersonalizationEngine.Evaluate(rule, "Anything at all", 1, DefaultEngine());
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Word_list_custom_alert_message_overrides_default_error()
    {
        var rule = new PersonalizationRule
        {
            Name = "Switch Option",
            RuleType = PersonalizationRuleType.WordList,
            WordList = "linear\ntactile\nclicky",
            CustomAlertMessage = "Please select one of the three supported switch types in your notes.",
        };

        var result = PersonalizationEngine.Evaluate(rule, "optical only", 1, DefaultEngine());
        Assert.False(result.IsValid);
        Assert.Equal("Please select one of the three supported switch types in your notes.", result.ErrorMessage);
    }

    #endregion

    #region 3. Not Empty Rule

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("    ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    [InlineData("  \t \r\n  \t  ")]
    [InlineData(null)]
    public void Not_empty_fails_on_empty_null_and_standard_whitespace(string? input)
    {
        var rule = new PersonalizationRule
        {
            Name = "Personalization Mandatory",
            RuleType = PersonalizationRuleType.NotEmpty,
        };

        var result = PersonalizationEngine.Evaluate(rule, input, 1, DefaultEngine());
        Assert.False(result.IsValid);
        Assert.Contains("Personalization text is required", result.ErrorMessage);
    }

    [Fact]
    public void Not_empty_zero_width_space_behavior()
    {
        // Unicode U+200B (Zero-width space) is categorized as Format (Cf), not SpaceSeparator.
        // C#'s string.IsNullOrWhiteSpace("\u200B") returns false.
        // We verify the exact empirical behavior of the engine here.
        var rule = new PersonalizationRule
        {
            Name = "Personalization Mandatory",
            RuleType = PersonalizationRuleType.NotEmpty,
        };

        var result = PersonalizationEngine.Evaluate(rule, "\u200B", 1, DefaultEngine());
        // Document empirical result:
        // char.IsWhiteSpace('\u200B') is false in .NET runtime, so string.IsNullOrWhiteSpace returns false.
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("Hello")]
    [InlineData(" a ")]
    [InlineData("12345")]
    [InlineData("!@#$%^&*()")]
    [InlineData("Custom Text: Engrave 'Jane Doe'")]
    [InlineData("🚀🔥")]
    public void Not_empty_passes_on_any_meaningful_content(string input)
    {
        var rule = new PersonalizationRule
        {
            Name = "Personalization Mandatory",
            RuleType = PersonalizationRuleType.NotEmpty,
        };

        var result = PersonalizationEngine.Evaluate(rule, input, 1, DefaultEngine());
        Assert.True(result.IsValid);
    }

    #endregion

    #region 4. Regular Expression Rule & Timeout Guards

    [Fact]
    public void Regex_rule_validates_anchored_serial_code()
    {
        var rule = new PersonalizationRule
        {
            Name = "Serial Code",
            RuleType = PersonalizationRuleType.Regex,
            RegexPattern = @"^[A-Z]{3}-\d{4}$",
        };

        var engine = DefaultEngine();

        // Exact match (case insensitive by default in engine)
        Assert.True(PersonalizationEngine.Evaluate(rule, "ABC-1234", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "xyz-5678", 1, engine).IsValid);

        // Substring / extra characters should fail because of ^ and $
        Assert.False(PersonalizationEngine.Evaluate(rule, "ABC-1234 extra", 1, engine).IsValid);
        Assert.False(PersonalizationEngine.Evaluate(rule, "prefix ABC-1234", 1, engine).IsValid);
        Assert.False(PersonalizationEngine.Evaluate(rule, "ABC-123", 1, engine).IsValid);
        Assert.False(PersonalizationEngine.Evaluate(rule, "ABCD-1234", 1, engine).IsValid);
    }

    [Fact]
    public void Regex_rule_validates_email_pattern()
    {
        var rule = new PersonalizationRule
        {
            Name = "Customer Email",
            RuleType = PersonalizationRuleType.Regex,
            RegexPattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        };

        var engine = DefaultEngine();
        Assert.True(PersonalizationEngine.Evaluate(rule, "buyer@example.com", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "JOHN.DOE+ETSY@GMAIL.COM", 1, engine).IsValid);

        Assert.False(PersonalizationEngine.Evaluate(rule, "not an email", 1, engine).IsValid);
        Assert.False(PersonalizationEngine.Evaluate(rule, "missing@domain", 1, engine).IsValid);
    }

    [Fact]
    public void Regex_rule_empty_pattern_succeeds_without_checking()
    {
        var rule = new PersonalizationRule
        {
            Name = "Empty Regex",
            RuleType = PersonalizationRuleType.Regex,
            RegexPattern = "",
        };

        Assert.True(PersonalizationEngine.Evaluate(rule, "anything", 1, DefaultEngine()).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, null, 1, DefaultEngine()).IsValid);
    }

    [Theory]
    [InlineData("[unclosed-bracket")]
    [InlineData("(")]
    [InlineData("*")]
    [InlineData("(?P<invalid>")]
    [InlineData(@"a{5,2}")]
    public void Regex_rule_malformed_patterns_handled_gracefully_without_unhandled_exception(string badPattern)
    {
        var rule = new PersonalizationRule
        {
            Name = "Broken Regex",
            RuleType = PersonalizationRuleType.Regex,
            RegexPattern = badPattern,
        };

        // Engine must catch ArgumentException and return IsValid: false without crashing
        var result = PersonalizationEngine.Evaluate(rule, "test input", 1, DefaultEngine());

        Assert.False(result.IsValid);
        Assert.Contains("Invalid pattern", result.ErrorMessage);
    }

    [Fact]
    public void Regex_rule_redos_catastrophic_backtracking_triggers_timeout_guard()
    {
        // Classic ReDoS pattern with nested quantifiers
        var rule = new PersonalizationRule
        {
            Name = "ReDoS Shield",
            RuleType = PersonalizationRuleType.Regex,
            RegexPattern = @"^((a+)+)+$",
        };

        // 30 repetitions of 'a' followed by '!' causes catastrophic backtracking without timeout
        var maliciousInput = new string('a', 30) + "!";

        var sw = Stopwatch.StartNew();
        var result = PersonalizationEngine.Evaluate(rule, maliciousInput, 1, DefaultEngine());
        sw.Stop();

        // Must fail with timeout message
        Assert.False(result.IsValid);
        Assert.Contains("Evaluation timed out", result.ErrorMessage);

        // Verification: Must have terminated around the 1-second timeout guard, NOT hung indefinitely
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.5, 4.0);
    }

    #endregion

    #region 5. Random Keyword Bypass Invariant

    [Theory]
    [InlineData("random")]
    [InlineData("RANDOM")]
    [InlineData("Random")]
    [InlineData("rAnDoM")]
    [InlineData("please choose random")]
    [InlineData("random colors!")]
    [InlineData("(random)")]
    [InlineData("any random")]
    [InlineData("surprise me")]
    [InlineData("SURPRISE ME")]
    [InlineData("surprise me please!")]
    [InlineData("dealer's choice")]
    public void Random_bypass_passes_failing_color_count_rule(string randomText)
    {
        var engine = CustomEngine();

        // Impossible rule: requires 50 colors
        var impossibleRule = new PersonalizationRule
        {
            Name = "50 Colors",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 50,
            MaxColors = 50,
        };

        var result = PersonalizationEngine.Evaluate(impossibleRule, randomText, 1, engine);
        Assert.True(result.IsValid, $"Failed for input '{randomText}'");
        Assert.Null(result.ErrorMessage);
    }

    [Theory]
    [InlineData("random")]
    [InlineData("surprise me")]
    [InlineData("dealer's choice")]
    public void Random_bypass_passes_failing_word_list_rule(string randomText)
    {
        var engine = CustomEngine();

        var wordRule = new PersonalizationRule
        {
            Name = "Strict Word Check",
            RuleType = PersonalizationRuleType.WordList,
            WordList = "cherry-red\nroyal-blue\nemerald-green",
        };

        var result = PersonalizationEngine.Evaluate(wordRule, randomText, 1, engine);
        Assert.True(result.IsValid, $"Word list rule failed for random input '{randomText}'");
        Assert.Null(result.ErrorMessage);
    }

    [Theory]
    [InlineData("random")]
    [InlineData("surprise me")]
    public void Random_bypass_passes_failing_regex_rule(string randomText)
    {
        var engine = CustomEngine();

        var regexRule = new PersonalizationRule
        {
            Name = "Numeric Only",
            RuleType = PersonalizationRuleType.Regex,
            RegexPattern = @"^\d{10}$", // Text "random" does not match digits
        };

        var result = PersonalizationEngine.Evaluate(regexRule, randomText, 1, engine);
        Assert.True(result.IsValid, $"Regex rule failed for random input '{randomText}'");
        Assert.Null(result.ErrorMessage);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void Random_bypass_scales_with_high_quantity_and_still_bypasses(int quantity)
    {
        var engine = CustomEngine();

        var rule = new PersonalizationRule
        {
            Name = "Colors per item",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 5,
            MaxColors = 5,
        };

        var result = PersonalizationEngine.Evaluate(rule, "random", quantity, engine);
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("randomize")]
    [InlineData("randomly")]
    public void Non_exact_random_words_do_not_falsely_bypass(string nonRandomText)
    {
        // "randomize" and "randomly" are distinct words and should not match "random"
        var engine = CustomEngine();

        var rule = new PersonalizationRule
        {
            Name = "Exact 2 Colors",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 2,
            MaxColors = 2,
        };

        var result = PersonalizationEngine.Evaluate(rule, nonRandomText, 1, engine);
        Assert.False(result.IsValid);
    }

    #endregion

    #region 6. Deep Adversarial Edge Cases & Stress Testing

    [Fact]
    public void Word_list_synonyms_separated_by_commas_in_rule_definition()
    {
        // PROJECT.md specification states: "Word list matching: case-insensitive, word-boundary matching for words and /-separated synonyms"
        // If an administrator separates synonyms with commas instead of slashes, verify the engine's exact empirical behavior:
        var rule = new PersonalizationRule
        {
            Name = "Switch Type",
            RuleType = PersonalizationRuleType.WordList,
            WordList = "linear, red, yellow",
        };

        var engine = DefaultEngine();

        // Since the delimiter is '/', "linear, red, yellow" is treated as one single option requiring all 3 words in that order.
        var singleResult = PersonalizationEngine.Evaluate(rule, "linear", 1, engine);
        Assert.False(singleResult.IsValid);

        // However, if the full comma-separated phrase is in the text, it matches:
        var fullResult = PersonalizationEngine.Evaluate(rule, "linear, red, yellow", 1, engine);
        Assert.True(fullResult.IsValid);
    }

    [Fact]
    public void Word_list_with_hyphenated_and_compound_terms()
    {
        var rule = new PersonalizationRule
        {
            Name = "Connector Type",
            RuleType = PersonalizationRuleType.WordList,
            WordList = "usb-c / type-c\nmicro-usb\nlightning",
        };

        var engine = DefaultEngine();

        Assert.True(PersonalizationEngine.Evaluate(rule, "Please provide usb-c cable", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "need TYPE-C connector", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "micro-usb only", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "apple lightning adapter", 1, engine).IsValid);

        Assert.False(PersonalizationEngine.Evaluate(rule, "standard usb cable", 1, engine).IsValid);
    }

    [Fact]
    public void Word_list_terms_ending_with_symbols_require_word_boundary_or_end_of_string()
    {
        var rule = new PersonalizationRule
        {
            Name = "Programming Language",
            RuleType = PersonalizationRuleType.WordList,
            WordList = "c++ / c#\npython",
        };

        var engine = DefaultEngine();

        // At end of string, $ matches
        Assert.True(PersonalizationEngine.Evaluate(rule, "I write C++", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "Expert in Python", 1, engine).IsValid);
    }

    [Fact]
    public void Color_count_consecutive_vs_nonconsecutive_duplicate_colors()
    {
        var engine = CustomEngine();
        var rule = new PersonalizationRule
        {
            Name = "Two Colors",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 2,
            MaxColors = 2,
        };

        // Consecutive duplicate colors in OrderColorEngine.ColorsIn are collapsed into 1 group (matching pill renderer)
        var consecutive = PersonalizationEngine.Evaluate(rule, "Copper and Copper", 1, engine);
        Assert.False(consecutive.IsValid); // Found 1, expected 2

        // Non-consecutive duplicate colors are preserved as distinct groups (e.g. Copper, Blue, Copper)
        var nonConsecutive = PersonalizationEngine.Evaluate(rule, "Copper, Blue, Copper", 1, engine);
        Assert.False(nonConsecutive.IsValid); // Found 3, expected 2
    }

    [Fact]
    public void Color_count_multi_color_filament_mixed_with_quantity_scaling()
    {
        var engine = CustomEngine();
        var rule = new PersonalizationRule
        {
            Name = "Two Colors Per Unit",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 2,
            MaxColors = 2,
        };

        // Quantity 2 -> expects 4 color groups.
        // "Neon Rider" (1 multi-color group), "Sunrise" (1 multi-color group), "Copper" (1 group), "Blue" (1 group) = 4 groups
        var passResult = PersonalizationEngine.Evaluate(rule, "Neon Rider, Sunrise, Copper, Blue", 2, engine);
        Assert.True(passResult.IsValid);

        // 3 groups -> fails with quantity 2
        var failResult = PersonalizationEngine.Evaluate(rule, "Neon Rider, Sunrise, Copper", 2, engine);
        Assert.False(failResult.IsValid);
        Assert.Contains("Expected 4 colors, found 3", failResult.ErrorMessage);
    }

    [Theory]
    [InlineData(@"^([a-zA-Z0-9]+)*$")]
    [InlineData(@"^(a|a?)+$")]
    [InlineData(@"^(a+)+$")]
    public void Regex_rule_multiple_redos_patterns_safely_time_out(string redosPattern)
    {
        var rule = new PersonalizationRule
        {
            Name = "ReDoS Probe",
            RuleType = PersonalizationRuleType.Regex,
            RegexPattern = redosPattern,
        };

        var attackPayload = new string('a', 28) + "!";
        var sw = Stopwatch.StartNew();
        var result = PersonalizationEngine.Evaluate(rule, attackPayload, 1, DefaultEngine());
        sw.Stop();

        Assert.False(result.IsValid);
        Assert.Contains("Evaluation timed out", result.ErrorMessage);
        Assert.True(sw.Elapsed.TotalSeconds < 5.0, $"ReDoS took too long: {sw.Elapsed.TotalSeconds}s");
    }

    [Theory]
    [InlineData(@"[")]
    [InlineData(@"(?<=")]
    [InlineData(@"(?!")]
    [InlineData(@"\x")]
    [InlineData(@"\p{InvalidCategory}")]
    public void Regex_rule_additional_malformed_syntax_errors_caught_cleanly(string invalidRegex)
    {
        var rule = new PersonalizationRule
        {
            Name = "Bad Regex",
            RuleType = PersonalizationRuleType.Regex,
            RegexPattern = invalidRegex,
        };

        var result = PersonalizationEngine.Evaluate(rule, "some text", 1, DefaultEngine());
        Assert.False(result.IsValid);
        Assert.Contains("Invalid pattern", result.ErrorMessage);
    }

    [Fact]
    public void All_rule_types_handle_null_personalization_text_gracefully()
    {
        var engine = DefaultEngine();

        // 1. ColorCount: null text -> 0 colors detected
        var colorRule = new PersonalizationRule
        {
            Name = "Min 1 Color",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 1,
        };
        Assert.False(PersonalizationEngine.Evaluate(colorRule, null, 1, engine).IsValid);

        // 2. WordList: null text -> 0 words matched
        var wordRule = new PersonalizationRule
        {
            Name = "Word List",
            RuleType = PersonalizationRuleType.WordList,
            WordList = "red\nblue",
        };
        Assert.False(PersonalizationEngine.Evaluate(wordRule, null, 1, engine).IsValid);

        // 3. NotEmpty: null text -> invalid
        var notEmptyRule = new PersonalizationRule
        {
            Name = "NotEmpty",
            RuleType = PersonalizationRuleType.NotEmpty,
        };
        Assert.False(PersonalizationEngine.Evaluate(notEmptyRule, null, 1, engine).IsValid);

        // 4. Regex: null text -> checks pattern against ""
        var regexRule = new PersonalizationRule
        {
            Name = "Regex",
            RuleType = PersonalizationRuleType.Regex,
            RegexPattern = @"^.+$",
        };
        Assert.False(PersonalizationEngine.Evaluate(regexRule, null, 1, engine).IsValid);
    }

    #endregion
}
