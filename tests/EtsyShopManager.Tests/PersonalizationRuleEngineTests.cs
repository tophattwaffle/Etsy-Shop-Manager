using EtsyShopManager.Core.Domain;
using EtsyShopManager.Web.Modules.Orders;

namespace EtsyShopManager.Tests;

public class PersonalizationRuleEngineTests
{
    private static OrderColorEngine Engine() => new(OrderColorService.Default);

    [Fact]
    public void Color_count_exact_match_passes_when_matching()
    {
        var rule = new PersonalizationRule
        {
            Name = "Two colors",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 2,
            MaxColors = 2,
        };

        var result = PersonalizationEngine.Evaluate(rule, "Copper and Blue", quantity: 1, Engine());

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Color_count_fails_when_below_minimum()
    {
        var rule = new PersonalizationRule
        {
            Name = "Two colors",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 2,
            MaxColors = 2,
        };

        var result = PersonalizationEngine.Evaluate(rule, "Only Copper", quantity: 1, Engine());

        Assert.False(result.IsValid);
        Assert.Contains("Expected 2 colors, found 1", result.ErrorMessage);
    }

    [Fact]
    public void Color_count_fails_when_above_maximum()
    {
        var rule = new PersonalizationRule
        {
            Name = "Up to two colors",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 1,
            MaxColors = 2,
        };

        var result = PersonalizationEngine.Evaluate(rule, "Red, Blue and Green", quantity: 1, Engine());

        Assert.False(result.IsValid);
        Assert.Contains("Expected between 1 and 2 colors, found 3", result.ErrorMessage);
    }

    [Fact]
    public void Color_count_scales_linearly_with_item_quantity()
    {
        var rule = new PersonalizationRule
        {
            Name = "Two colors per item",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 2,
            MaxColors = 2,
        };

        // Quantity 2 with rule of 2 colors expects 4 colors
        var failResult = PersonalizationEngine.Evaluate(rule, "Red, Blue, Green", quantity: 2, Engine());
        Assert.False(failResult.IsValid);
        Assert.Contains("Expected 4 colors, found 3", failResult.ErrorMessage);

        var passResult = PersonalizationEngine.Evaluate(rule, "Red, Blue, Green, Copper", quantity: 2, Engine());
        Assert.True(passResult.IsValid);
    }

    [Fact]
    public void Multi_color_filament_counts_as_one_color()
    {
        var rule = new PersonalizationRule
        {
            Name = "One color",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 1,
            MaxColors = 1,
        };

        // "Neon Rider" is a 2-color filament (#4FC3F7, #FF1FD1), but should count as 1 color
        var result = PersonalizationEngine.Evaluate(rule, "Neon Rider", quantity: 1, Engine());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Custom_alert_message_overrides_default_message()
    {
        var rule = new PersonalizationRule
        {
            Name = "Two colors",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 2,
            MaxColors = 2,
            CustomAlertMessage = "Please pick exactly two filaments from the swatch chart.",
        };

        var result = PersonalizationEngine.Evaluate(rule, "Only Red", quantity: 1, Engine());

        Assert.False(result.IsValid);
        Assert.Equal("Please pick exactly two filaments from the swatch chart.", result.ErrorMessage);
    }

    [Fact]
    public void Word_list_matches_primary_term_and_synonyms()
    {
        var rule = new PersonalizationRule
        {
            Name = "Switch Type",
            RuleType = PersonalizationRuleType.WordList,
            WordList = "linear / red / yellow\ntactile / brown\nclicky / blue",
        };

        // Primary word
        Assert.True(PersonalizationEngine.Evaluate(rule, "I want linear switches", 1, Engine()).IsValid);

        // Synonym on same line
        Assert.True(PersonalizationEngine.Evaluate(rule, "Please use yellow switches", 1, Engine()).IsValid);

        // Case insensitivity
        Assert.True(PersonalizationEngine.Evaluate(rule, "TACTILE please", 1, Engine()).IsValid);

        // Another synonym
        Assert.True(PersonalizationEngine.Evaluate(rule, "give me blue switches", 1, Engine()).IsValid);
    }

    [Fact]
    public void Word_list_respects_word_boundaries_preventing_false_positives()
    {
        var rule = new PersonalizationRule
        {
            Name = "Switch Type",
            RuleType = PersonalizationRuleType.WordList,
            WordList = "red\nbrown\nblue",
        };

        // "ordered", "bored", "predator" all contain "red" as a substring, but must NOT match
        Assert.False(PersonalizationEngine.Evaluate(rule, "ordered yesterday", 1, Engine()).IsValid);
        Assert.False(PersonalizationEngine.Evaluate(rule, "feeling bored", 1, Engine()).IsValid);
        Assert.False(PersonalizationEngine.Evaluate(rule, "predator mask", 1, Engine()).IsValid);

        // Exact word matches
        Assert.True(PersonalizationEngine.Evaluate(rule, "red switch", 1, Engine()).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "bright red!", 1, Engine()).IsValid);
    }

    [Fact]
    public void Word_list_fails_when_no_choice_mentioned()
    {
        var rule = new PersonalizationRule
        {
            Name = "Switch Type",
            RuleType = PersonalizationRuleType.WordList,
            WordList = "linear / red\ntactile / brown\nclicky / blue",
        };

        var result = PersonalizationEngine.Evaluate(rule, "standard mechanical keyboard", 1, Engine());

        Assert.False(result.IsValid);
        Assert.Contains("Must mention one of: linear, tactile, clicky", result.ErrorMessage);
    }

    [Fact]
    public void Not_empty_fails_on_empty_and_whitespace()
    {
        var rule = new PersonalizationRule
        {
            Name = "Custom Text Required",
            RuleType = PersonalizationRuleType.NotEmpty,
        };

        Assert.False(PersonalizationEngine.Evaluate(rule, "", 1, Engine()).IsValid);
        Assert.False(PersonalizationEngine.Evaluate(rule, "   \t\n  ", 1, Engine()).IsValid);
        Assert.False(PersonalizationEngine.Evaluate(rule, null, 1, Engine()).IsValid);

        Assert.True(PersonalizationEngine.Evaluate(rule, "Hello", 1, Engine()).IsValid);
    }

    [Fact]
    public void Regex_rule_validates_pattern_and_rejects_non_matches()
    {
        var rule = new PersonalizationRule
        {
            Name = "Serial Format",
            RuleType = PersonalizationRuleType.Regex,
            RegexPattern = @"^[A-Z]{3}-\d{4}$",
        };

        Assert.True(PersonalizationEngine.Evaluate(rule, "ABC-1234", 1, Engine()).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(rule, "xyz-9876", 1, Engine()).IsValid); // case-insensitive

        var failResult = PersonalizationEngine.Evaluate(rule, "ABC-12", 1, Engine());
        Assert.False(failResult.IsValid);
        Assert.Contains("Personalization text did not match required pattern", failResult.ErrorMessage);
    }

    [Fact]
    public void Random_bypass_passes_all_rule_types_unconditionally()
    {
        var engine = Engine();

        // 1. Failing color count rule
        var colorRule = new PersonalizationRule
        {
            Name = "Exact 5 colors",
            RuleType = PersonalizationRuleType.ColorCount,
            MinColors = 5,
            MaxColors = 5,
        };
        Assert.True(PersonalizationEngine.Evaluate(colorRule, "random please", 1, engine).IsValid);
        Assert.True(PersonalizationEngine.Evaluate(colorRule, "any random colors", 1, engine).IsValid);

        // Custom random words
        var customEngine = new OrderColorEngine(new OrderColorConfig([], ["random", "surprise me"]));
        Assert.True(PersonalizationEngine.Evaluate(colorRule, "surprise me with colors", 1, customEngine).IsValid);

        // 2. Failing word list rule
        var wordRule = new PersonalizationRule
        {
            Name = "Must mention clicky",
            RuleType = PersonalizationRuleType.WordList,
            WordList = "clicky\ntactile",
        };
        Assert.True(PersonalizationEngine.Evaluate(wordRule, "choose random for me", 1, engine).IsValid);

        // 3. Failing regex rule
        var regexRule = new PersonalizationRule
        {
            Name = "Format",
            RuleType = PersonalizationRuleType.Regex,
            RegexPattern = @"^\d{10}$",
        };
        Assert.True(PersonalizationEngine.Evaluate(regexRule, "random", 1, engine).IsValid);
    }
}
