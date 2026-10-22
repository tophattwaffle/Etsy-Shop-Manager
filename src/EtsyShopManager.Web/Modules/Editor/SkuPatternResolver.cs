using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>
/// Resolves SKU pattern templates (e.g. "{listing.sku}-{size}-{color}") across variation combinations.
/// </summary>
public static class SkuPatternResolver
{
    private static readonly Regex TokenRegex = new(@"\{([^}]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Sanitizes a variation value for use in an SKU:
    /// replaces spaces and slashes with '-', removes quotes, commas, and other special punctuation,
    /// preserving letters, digits, '-', and '_'.
    /// </summary>
    public static string SanitizeForSku(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var sb = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || ch == '/' || ch == '\\')
            {
                sb.Append('-');
            }
            else if (ch is '"' or '\'' or '`' or ',' or '.' or '!' or '?' or ';' or ':' or '@' or '#' or '$' or '%' or '^' or '&' or '*' or '(' or ')' or '+' or '=' or '<' or '>' or '|' or '[' or ']' or '{' or '}')
            {
                // drop quotes, commas, punctuation and symbols
                continue;
            }
            else if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                sb.Append(ch);
            }
        }

        var result = sb.ToString();
        // Collapse multiple consecutive hyphens
        result = Regex.Replace(result, "-+", "-");
        // Trim hyphens from edges
        result = result.Trim('-');
        return result;
    }

    /// <summary>
    /// Normalizes a property name by trimming, removing whitespace/hyphens, and lowercasing.
    /// E.g. "Primary color" -> "primarycolor", "Size" -> "size".
    /// </summary>
    public static string NormalizePropertyName(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName)) return "";

        var sb = new StringBuilder();
        foreach (var ch in propertyName.Trim())
        {
            if (!char.IsWhiteSpace(ch) && ch != '-')
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Generates an SKU by replacing {listing.sku} and {sku} with baseSku,
    /// and {&lt;property_name&gt;} tokens with sanitized variation values.
    /// </summary>
    public static string GenerateSku(string pattern, string baseSku, IReadOnlyDictionary<string, string> variationValues)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "";

        return TokenRegex.Replace(pattern, m =>
        {
            var token = m.Groups[1].Value.Trim();
            if (string.Equals(token, "listing.sku", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "sku", StringComparison.OrdinalIgnoreCase))
            {
                return baseSku ?? "";
            }

            var normToken = NormalizePropertyName(token);

            // 1. Exact match (case-insensitive)
            var match = variationValues.FirstOrDefault(kv => string.Equals(kv.Key, token, StringComparison.OrdinalIgnoreCase));
            if (match.Key is not null) return SanitizeForSku(match.Value);

            // 2. Normalized match
            match = variationValues.FirstOrDefault(kv => NormalizePropertyName(kv.Key) == normToken);
            if (match.Key is not null) return SanitizeForSku(match.Value);

            // 3. Suffix match (e.g. "color" matches "primarycolor")
            match = variationValues.FirstOrDefault(kv => NormalizePropertyName(kv.Key).EndsWith(normToken, StringComparison.OrdinalIgnoreCase));
            if (match.Key is not null) return SanitizeForSku(match.Value);

            // If not found, keep token unchanged
            return m.Value;
        });
    }

    /// <summary>
    /// Applies an SKU pattern to an inventory JSON object by iterating products in inventory["products"]
    /// and updating product["sku"] with the computed SKU for each product's combination of property values.
    /// Also records the pattern in inventory["sku_pattern"].
    /// </summary>
    public static JsonObject ApplyPattern(JsonObject inventory, string pattern, string baseSku)
    {
        inventory["sku_pattern"] = pattern;

        var products = inventory["products"]?.AsArray();
        if (products is null || products.Count == 0) return inventory;

        var variations = VariationEditor.Variations(inventory);

        foreach (var productNode in products)
        {
            if (productNode is not JsonObject product) continue;

            var pvs = product["property_values"]?.AsArray();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (pvs is not null)
            {
                foreach (var pvNode in pvs)
                {
                    if (pvNode is not JsonObject pv) continue;
                    var pId = (long)pv["property_id"]!;
                    var pName = (string?)pv["property_name"]
                        ?? variations.FirstOrDefault(v => v.PropertyId == pId)?.Name
                        ?? pId.ToString();
                    var valText = string.Join("|", (pv["values"]?.AsArray() ?? []).Select(x => (string?)x));
                    values[pName] = valText;
                }
            }

            product["sku"] = GenerateSku(pattern, baseSku, values);
        }

        return inventory;
    }
}
