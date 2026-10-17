using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EtsyShopManager.Etsy;

/// <summary>
/// Turns an Etsy API error reply into a sentence for people: what Etsy refused, why (Etsy's reason, reworded where it's
/// known) and what to do. The raw reply stays on the exception and in the Etsy write log (Admin → Etsy writes).
/// <para>
/// Etsy answers with either <c>{"error": "…"}</c> or, for field checks, a list of
/// <c>{"path": "/title/0", "type": "all_caps", "message": "…"}</c>.
/// </para>
/// </summary>
public static partial class EtsyErrorText
{
    /// <summary>Etsy's checks with a clearer wording, by the type Etsy gives them.</summary>
    private static readonly Dictionary<string, string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["all_caps"] = "more than 3 words start with two capital letters",
    };

    /// <summary>Etsy's error messages with a clearer wording and what to do about them.</summary>
    private static readonly Dictionary<string, string> KnownErrors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["The ListingImages for this Listing are in an inconsistent order."] =
            "Etsy has two of this listing's photos at the same position. Push the photos again: the app gives every photo its own position",
    };

    /// <summary>The fields Etsy names in its checks, as the editor calls them.</summary>
    private static readonly Dictionary<string, string> FieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["taxonomy_id"] = "category",
        ["readiness_state_id"] = "processing profile",
        ["shipping_profile_id"] = "shipping profile",
        ["return_policy_id"] = "return policy",
        ["shop_section_id"] = "shop section",
        ["who_made"] = "who made it",
        ["when_made"] = "when it was made",
        ["image"] = "photo",
        ["alt_text"] = "photo's alt text",
        ["value_ids"] = "attribute values",
        ["values"] = "attribute values",
        ["personalization_questions"] = "personalization",
    };

    public static string Describe(HttpStatusCode status, string? body, HttpMethod? method, string? path)
    {
        var what = What(method, path);
        var (reasons, fields) = Reasons(body);
        var code = (int)status;
        if (code >= 500)
            return $"Etsy had a problem on its side with {what} (error {code}). Try again in a few minutes." +
                   (reasons.Count > 0 ? $" Etsy said: {string.Join("; ", reasons)}." : "");
        var reason = reasons.Count > 0 ? string.Join("; ", reasons) : status switch
        {
            HttpStatusCode.Unauthorized => "the shop's sign-in wasn't accepted",
            HttpStatusCode.Forbidden => "the app isn't allowed to do this for the shop",
            HttpStatusCode.NotFound => "Etsy couldn't find it (it may have been deleted on Etsy)",
            HttpStatusCode.Conflict => "it clashed with another change made at the same moment",
            _ => $"no reason given (error {code})",
        };
        var advice = fields.Count > 0 ? $" Change the {Join(fields)} and try again."
            : status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => " Reconnect the shop on the Shops page, then try again.",
                HttpStatusCode.Conflict => " Try again.",
                _ => "",
            };
        var verb = method == HttpMethod.Get ? "Etsy couldn't provide" : "Etsy rejected";
        return $"{verb} {what}: {reason.TrimEnd('.')}.{advice}";
    }

    /// <summary>What the call was about, from its method and path (ids left out).</summary>
    public static string What(HttpMethod? method, string? path)
    {
        var parts = (path ?? "").Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var last = parts.LastOrDefault(p => !long.TryParse(p, out _)) ?? "";
        var write = method is not null && method != HttpMethod.Get;
        return last switch
        {
            "listings" when method == HttpMethod.Post => "the new draft listing",
            "listings" when parts.Length > 0 && long.TryParse(parts[^1], out _) => write ? "the listing changes" : "the listing",
            "listings" => "the listings",
            "inventory" => write ? "the variations, prices and stock" : "the listing's variations and stock",
            "images" when method == HttpMethod.Delete => "removing a photo",
            "images" => write ? "the photo change" : "the listing's photos",
            "properties" when method == HttpMethod.Delete => "removing a category attribute",
            "properties" => write ? "a category attribute" : "the category attributes",
            "personalization" when method == HttpMethod.Delete => "removing the personalization",
            "personalization" => "the personalization questions",
            "receipts" => "the orders",
            _ => write ? "the change" : "the request",
        };
    }

    /// <summary>Etsy's reasons in plain words, and the fields they're about.</summary>
    private static (List<string> Reasons, List<string> Fields) Reasons(string? body)
    {
        var reasons = new List<string>();
        var fields = new List<string>();
        if (string.IsNullOrWhiteSpace(body)) return (reasons, fields);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList()
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array
                    ? e.EnumerateArray().ToList() : null;
            if (items is not null)
            {
                foreach (var item in items.Where(i => i.ValueKind == JsonValueKind.Object))
                {
                    var field = Field(Str(item, "path"));
                    var type = Str(item, "type");
                    var message = type is not null && KnownTypes.TryGetValue(type, out var known) ? known : Clean(Str(item, "message") ?? type ?? "");
                    if (message.Length == 0) continue;
                    if (field is not null && !fields.Contains(field)) fields.Add(field);
                    reasons.Add(field is null ? message : $"the {field}: {message}");
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                var error = Str(root, "error") ?? Str(root, "message");
                var description = Str(root, "error_description");
                foreach (var text in new[] { error, description }.Where(t => !string.IsNullOrWhiteSpace(t)))
                    reasons.Add(KnownErrors.TryGetValue(text!.Trim(), out var known) ? known : Clean(text!));
            }
        }
        catch (JsonException)
        {
            // Not JSON (an HTML error page, plain text): keep a short plain-text reply, drop anything else.
            var text = body.Trim();
            if (text.Length <= 300 && !text.StartsWith('<')) reasons.Add(KnownErrors.TryGetValue(text, out var known) ? known : Clean(text));
        }
        return (reasons, fields);
    }

    /// <summary>"/title/0" → "title"; "/tags/2" → "tags"; "/shipping_profile_id" → "shipping profile".</summary>
    private static string? Field(string? path)
    {
        var name = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(p => !int.TryParse(p, out _));
        if (string.IsNullOrEmpty(name)) return null;
        return FieldNames.TryGetValue(name, out var known) ? known : name.Replace("_id", "").Replace('_', ' ');
    }

    private static string Clean(string text) => Whitespace().Replace(text.Trim(), " ").TrimEnd('.');

    private static string Join(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
