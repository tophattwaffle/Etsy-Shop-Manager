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
        OrderColorEngine colorEngine)
    {
        var text = personalizationText ?? "";

        // Invariant: Any personalization text recognized as "Random" by OrderColorEngine.IsRandom immediately passes all rules.
        if (colorEngine.IsRandom(text))
            return PersonalizationEvaluationResult.Success();

        return rule.RuleType switch
        {
            PersonalizationRuleType.ColourCount => EvaluateColourCount(rule, text, quantity, colorEngine),
            PersonalizationRuleType.WordList => EvaluateWordList(rule, text),
            PersonalizationRuleType.NotEmpty => EvaluateNotEmpty(rule, text),
            PersonalizationRuleType.Regex => EvaluateRegex(rule, text),
            _ => PersonalizationEvaluationResult.Success()
        };
    }

    private static PersonalizationEvaluationResult EvaluateColourCount(
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
                msg = $"Rule '{rule.Name}': Expected {expectedMin} colours, found {count}.";
            }
            else if (expectedMax.HasValue)
            {
                msg = $"Rule '{rule.Name}': Expected between {expectedMin} and {expectedMax.Value} colours, found {count}.";
            }
            else
            {
                msg = $"Rule '{rule.Name}': Expected at least {expectedMin} colours, found {count}.";
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
