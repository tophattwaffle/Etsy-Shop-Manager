using System.Text.RegularExpressions;
using EtsyShopManager.Core.Domain;

namespace EtsyShopManager.Web.Modules.Orders;

public readonly record struct PersonalizationEvaluationResult(bool IsValid, string? ErrorMessage)
{
    public static PersonalizationEvaluationResult Success() => new(true, null);
    public static PersonalizationEvaluationResult Fail(string error) => new(false, error);
}

public static class PersonalizationEngine
{
    public static PersonalizationEvaluationResult Evaluate(
        PersonalizationRule rule,
        string? personalizationText,
        int quantity,
        OrderColorEngine colorEngine,
        IReadOnlyList<OrderVariation>? variations = null)
    {
        var nonPersonalization = variations?
            .Where(v => v.PropertyId != HaPayloadBuilder.PersonalizationPropertyId && !string.IsNullOrWhiteSpace(v.Value))
            .ToList() ?? [];

        // If the rule specifies a variation filter, skip evaluation unless a chosen variation matches.
        if (!string.IsNullOrWhiteSpace(rule.VariationFilter) && !MatchesVariationFilter(rule.VariationFilter, nonPersonalization))
            return PersonalizationEvaluationResult.Success();

        var text = personalizationText ?? "";

        // Invariant: Any personalization text or chosen variation recognized as "Random" by OrderColorEngine.IsRandom immediately passes all rules.
        if (colorEngine.IsRandom(text) || nonPersonalization.Any(v => colorEngine.IsRandom(v.Value)))
            return PersonalizationEvaluationResult.Success();

        return rule.RuleType switch
        {
            PersonalizationRuleType.ColorCount => EvaluateColorCount(rule, text, quantity, colorEngine),
            PersonalizationRuleType.WordList => EvaluateWordList(rule, text),
            PersonalizationRuleType.NotEmpty => EvaluateNotEmpty(rule, text),
            PersonalizationRuleType.Regex => EvaluateRegex(rule, text),
            _ => PersonalizationEvaluationResult.Success()
        };
    }

    public static bool MatchesVariationFilter(string filter, IReadOnlyList<OrderVariation> variations)
    {
        var terms = filter.Split(['\r', '\n', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .ToList();
        if (terms.Count == 0) return true;
        if (variations.Count == 0) return false;

        foreach (var term in terms)
        {
            var sepIndex = term.IndexOfAny(['=', ':']);
            if (sepIndex > 0 && sepIndex < term.Length - 1)
            {
                var propTerm = term[..sepIndex].Trim();
                var valTerm = term[(sepIndex + 1)..].Trim();
                if (propTerm.Length > 0 && valTerm.Length > 0)
                {
                    if (variations.Any(v => MatchesTerm(v.Name, propTerm) && MatchesTerm(v.Value, valTerm)))
                        return true;
                    continue;
                }
            }

            if (variations.Any(v => MatchesTerm(v.Value, term)))
                return true;
        }

        return false;
    }

    private static bool MatchesTerm(string input, string term)
    {
        if (string.Equals(input.Trim(), term, StringComparison.OrdinalIgnoreCase))
            return true;
        var pattern = $@"(?:\b|^){Regex.Escape(term)}(?:\b|$)";
        return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase);
    }

    private static PersonalizationEvaluationResult EvaluateColorCount(
        PersonalizationRule rule,
        string text,
        int quantity,
        OrderColorEngine colorEngine)
    {
        var units = Math.Max(1, quantity);
        var expectedMin = (rule.MinColors ?? 0) * units;
        var expectedMax = rule.MaxColors.HasValue ? rule.MaxColors.Value * units : (int?)null;

        var detectedColors = colorEngine.ColorsIn(text);
        var count = detectedColors.Count;

        if (count < expectedMin || (expectedMax.HasValue && count > expectedMax.Value))
        {
            if (!string.IsNullOrWhiteSpace(rule.CustomAlertMessage))
                return PersonalizationEvaluationResult.Fail(rule.CustomAlertMessage);

            string msg;
            if (rule.MinColors.HasValue && rule.MaxColors.HasValue && rule.MinColors == rule.MaxColors)
            {
                msg = $"Rule '{rule.Name}': Expected {expectedMin} colors, found {count}.";
            }
            else if (expectedMax.HasValue)
            {
                msg = $"Rule '{rule.Name}': Expected between {expectedMin} and {expectedMax.Value} colors, found {count}.";
            }
            else
            {
                msg = $"Rule '{rule.Name}': Expected at least {expectedMin} colors, found {count}.";
            }
            return PersonalizationEvaluationResult.Fail(msg);
        }

        return PersonalizationEvaluationResult.Success();
    }

    private static PersonalizationEvaluationResult EvaluateWordList(
        PersonalizationRule rule,
        string text)
    {
        var rawLines = (rule.WordList ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var options = new List<List<string>>();

        foreach (var line in rawLines)
        {
            var synonyms = line.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0)
                .ToList();
            if (synonyms.Count > 0)
                options.Add(synonyms);
        }

        if (options.Count == 0)
            return PersonalizationEvaluationResult.Success();

        // Must mention at least one option (i.e. at least one line has one of its synonyms present)
        var anyMatched = false;
        foreach (var synonyms in options)
        {
            foreach (var term in synonyms)
            {
                var pattern = $@"(?:\b|^){Regex.Escape(term)}(?:\b|$)";
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                {
                    anyMatched = true;
                    break;
                }
            }
            if (anyMatched) break;
        }

        if (!anyMatched)
        {
            if (!string.IsNullOrWhiteSpace(rule.CustomAlertMessage))
                return PersonalizationEvaluationResult.Fail(rule.CustomAlertMessage);

            var summary = string.Join(", ", options.Select(o => o[0]));
            return PersonalizationEvaluationResult.Fail($"Rule '{rule.Name}': Must mention one of: {summary}.");
        }

        return PersonalizationEvaluationResult.Success();
    }

    private static PersonalizationEvaluationResult EvaluateNotEmpty(
        PersonalizationRule rule,
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return PersonalizationEvaluationResult.Fail(
                !string.IsNullOrWhiteSpace(rule.CustomAlertMessage)
                    ? rule.CustomAlertMessage
                    : $"Rule '{rule.Name}': Personalization text is required.");
        }

        return PersonalizationEvaluationResult.Success();
    }

    private static PersonalizationEvaluationResult EvaluateRegex(
        PersonalizationRule rule,
        string text)
    {
        if (string.IsNullOrWhiteSpace(rule.RegexPattern))
            return PersonalizationEvaluationResult.Success();

        try
        {
            if (!Regex.IsMatch(text, rule.RegexPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            {
                return PersonalizationEvaluationResult.Fail(
                    !string.IsNullOrWhiteSpace(rule.CustomAlertMessage)
                        ? rule.CustomAlertMessage
                        : $"Rule '{rule.Name}': Personalization text did not match required pattern.");
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return PersonalizationEvaluationResult.Fail($"Rule '{rule.Name}': Evaluation timed out.");
        }
        catch (ArgumentException ex)
        {
            return PersonalizationEvaluationResult.Fail($"Rule '{rule.Name}': Invalid pattern ({ex.Message}).");
        }

        return PersonalizationEvaluationResult.Success();
    }
}
