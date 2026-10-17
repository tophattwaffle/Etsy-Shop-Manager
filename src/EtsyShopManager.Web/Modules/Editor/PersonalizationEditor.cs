using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Etsy.Models;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>
/// Personalization questions in managed-state JSON (a list of <see cref="ManagedPersonalizationQuestion"/>, Etsy's
/// order). A push replaces them all with one updateListingPersonalization call, or removes them with
/// deleteListingPersonalization when the local list is empty. Limits are the ones Etsy's API documents.
/// </summary>
public static class PersonalizationEditor
{
    public const int MaxQuestions = 5;
    public const int MaxQuestionText = 45;
    public const int MaxDropdownLabel = 20;
    public const int MaxUploadLabel = 45;
    public const int DefaultMaxCharacters = 256;

    public const string TextInput = "text_input";
    public const string Dropdown = "dropdown";
    public const string UnlabeledUpload = "unlabeled_upload";
    public const string LabeledUpload = "labeled_upload";
    public static readonly string[] Types = [TextInput, Dropdown, UnlabeledUpload, LabeledUpload];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string TypeLabel(string type) => type switch
    {
        TextInput => "Text box",
        Dropdown => "Drop-down",
        UnlabeledUpload => "File upload",
        LabeledUpload => "Labeled file uploads",
        _ => type,
    };

    public static List<ManagedPersonalizationQuestion> FromManaged(JsonNode? node) =>
        node is JsonArray array ? array.Deserialize<List<ManagedPersonalizationQuestion>>(Json) ?? [] : [];

    public static JsonArray ToManaged(IEnumerable<ManagedPersonalizationQuestion> questions) =>
        JsonSerializer.SerializeToNode(questions.Select(Normalize).ToList(), Json)!.AsArray();

    /// <summary>A new question as Etsy's form starts it: an optional text box.</summary>
    public static ManagedPersonalizationQuestion NewQuestion() =>
        new(TextInput, "Personalization", null, false, DefaultMaxCharacters, null, null, null);

    /// <summary>The question as the managed state stores it: fields that don't apply to its type are cleared.</summary>
    public static ManagedPersonalizationQuestion Normalize(ManagedPersonalizationQuestion q) => q with
    {
        Instructions = string.IsNullOrEmpty(q.Instructions) || q.QuestionType == Dropdown ? null : q.Instructions.Replace("\r\n", "\n"),
        MaxAllowedCharacters = q.QuestionType == TextInput ? q.MaxAllowedCharacters : null,
        MaxAllowedFiles = q.QuestionType is UnlabeledUpload or LabeledUpload ? q.MaxAllowedFiles : null,
        AddOnPrice = q.QuestionType == TextInput && !q.Required && q.AddOnPrice is > 0 ? Math.Round(q.AddOnPrice.Value, 2) : null,
        Options = q.QuestionType is Dropdown or LabeledUpload && q.Options is { Count: > 0 } o ? [.. o] : null,
    };

    /// <summary>The call a push makes, described for the review; null when Etsy already has the local questions.</summary>
    public static string? Call(JsonNode? etsy, JsonNode? local, long shopId, long listingId)
    {
        if (JsonNode.DeepEquals(etsy ?? new JsonArray(), local ?? new JsonArray())) return null;
        var path = $"/v3/application/shops/{shopId}/listings/{listingId}/personalization";
        var questions = FromManaged(local);
        return questions.Count == 0
            ? $"DELETE {path}  (no personalization)"
            : $"POST {path}?supports_multiple_personalization_questions=true  ({questions.Count} question{(questions.Count == 1 ? "" : "s")}, replacing Etsy's)";
    }

    /// <summary>
    /// The body for updateListingPersonalization. A question that is still there keeps Etsy's question id (matched by
    /// type and title; an edited one takes an unused id of its type, preferring its position), and its options keep
    /// theirs by label, so Etsy updates them rather than starting new ones.
    /// </summary>
    /// <param name="etsyQuestionsJson">Etsy's current personalization_questions (a fresh read; ids included).</param>
    public static EtsyPersonalizationUpdate Body(JsonNode local, string? etsyQuestionsJson)
    {
        var current = (string.IsNullOrWhiteSpace(etsyQuestionsJson) ? [] : JsonNode.Parse(etsyQuestionsJson) as JsonArray ?? [])
            .OfType<JsonObject>().ToList();
        var questions = FromManaged(local).Select(Normalize).ToList();
        var matched = new JsonObject?[questions.Count];
        bool Free(JsonObject e) => !matched.Contains(e);
        for (var i = 0; i < questions.Count; i++)
            matched[i] = current.FirstOrDefault(e => Free(e) && (string?)e["question_type"] == questions[i].QuestionType
                                                   && System.Net.WebUtility.HtmlDecode((string?)e["question_text"] ?? "") == questions[i].QuestionText);
        for (var i = 0; i < questions.Count; i++)
        {
            if (matched[i] is not null) continue;
            bool SameType(JsonObject e) => Free(e) && (string?)e["question_type"] == questions[i].QuestionType;
            matched[i] = i < current.Count && SameType(current[i]) ? current[i] : current.FirstOrDefault(SameType);
        }
        return new EtsyPersonalizationUpdate
        {
            PersonalizationQuestions = [.. questions.Select((q, i) =>
            {
                var existing = matched[i];
                var options = existing?["options"] as JsonArray ?? [];
                return new EtsyPersonalizationQuestionUpdate
                {
                    QuestionId = (long?)existing?["question_id"],
                    QuestionText = q.QuestionText,
                    Instructions = q.Instructions,
                    QuestionType = q.QuestionType,
                    Required = q.Required,
                    MaxAllowedCharacters = q.MaxAllowedCharacters,
                    MaxAllowedFiles = q.MaxAllowedFiles,
                    AddOnPrice = q.AddOnPrice,
                    Options = q.Options?.Select(label => new EtsyPersonalizationOptionUpdate
                    {
                        OptionId = (long?)options.FirstOrDefault(o => System.Net.WebUtility.HtmlDecode((string?)o?["label"] ?? "") == label)?["option_id"],
                        Label = label,
                    }).ToList(),
                };
            })],
        };
    }

    /// <summary>What Etsy's API would refuse.</summary>
    public static IEnumerable<string> Problems(JsonNode? local)
    {
        var questions = FromManaged(local);
        if (questions.Count > MaxQuestions) yield return $"{questions.Count} personalization questions; Etsy allows {MaxQuestions}.";
        foreach (var (q, n) in questions.Select((q, i) => (q, i + 1)))
        {
            var label = $"Personalization question {n}";
            if (!Types.Contains(q.QuestionType)) yield return $"{label} has an unknown type \"{q.QuestionType}\".";
            if (q.QuestionText.Trim().Length == 0) yield return $"{label} needs a title.";
            if (q.QuestionText.Length > MaxQuestionText) yield return $"{label}'s title is {q.QuestionText.Length} characters; Etsy allows {MaxQuestionText}.";
            switch (q.QuestionType)
            {
                case TextInput:
                    if (q.MaxAllowedCharacters is not > 0) yield return $"{label} needs a character limit.";
                    if (q.AddOnPrice is not null && q.Required) yield return $"{label}: an add-on price is only allowed on an optional question.";
                    break;
                case Dropdown:
                    if (!string.IsNullOrEmpty(q.Instructions)) yield return $"{label}: a drop-down can't have instructions.";
                    if (q.Options is not { Count: > 0 }) yield return $"{label} needs at least one choice.";
                    foreach (var o in q.Options ?? [])
                    {
                        if (o.Trim().Length == 0) yield return $"{label} has an empty choice.";
                        if (o.Length > MaxDropdownLabel) yield return $"{label}: the choice \"{o}\" is longer than {MaxDropdownLabel} characters.";
                    }
                    break;
                case UnlabeledUpload or LabeledUpload:
                    if (q.MaxAllowedFiles is not > 0) yield return $"{label} needs the number of files.";
                    if (q.QuestionType == LabeledUpload)
                    {
                        if ((q.Options?.Count ?? 0) != q.MaxAllowedFiles)
                            yield return $"{label} needs one label per file ({q.MaxAllowedFiles ?? 0}).";
                        foreach (var o in q.Options ?? [])
                        {
                            if (o.Trim().Length == 0) yield return $"{label} has an empty file label.";
                            if (o.Length > MaxUploadLabel) yield return $"{label}: the label \"{o}\" is longer than {MaxUploadLabel} characters.";
                        }
                    }
                    break;
            }
            if ((q.Options ?? []).GroupBy(o => o.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                yield return $"{label} has the same choice twice.";
        }
    }

    /// <summary>"1. Front Text (text box, required, up to 48 characters) · 2. …".</summary>
    public static string Describe(JsonNode? value, string? currency = null)
    {
        var questions = FromManaged(value);
        if (questions.Count == 0) return "None";
        return string.Join(" · ", questions.Select((q, i) =>
        {
            var parts = new List<string> { TypeLabel(q.QuestionType).ToLowerInvariant(), q.Required ? "required" : "optional" };
            if (q.MaxAllowedCharacters is { } c) parts.Add($"up to {c} characters");
            if (q.MaxAllowedFiles is { } f) parts.Add($"{f} file{(f == 1 ? "" : "s")}");
            if (q.Options is { Count: > 0 } o) parts.Add(string.Join(" / ", o));
            if (q.AddOnPrice is { } p) parts.Add($"+{p.ToString("0.00", CultureInfo.InvariantCulture)}{(currency is null ? "" : $" {currency}")}");
            return $"{i + 1}. {q.QuestionText} ({string.Join(", ", parts)})";
        }));
    }
}
