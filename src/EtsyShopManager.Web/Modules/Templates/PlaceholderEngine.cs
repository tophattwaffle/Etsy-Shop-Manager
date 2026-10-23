using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;

namespace EtsyShopManager.Web.Modules.Templates;

public sealed record PlaceholderContext(
    Shop Shop,
    JsonObject LocalState,
    IReadOnlyCollection<string> Skus,
    IReadOnlyDictionary<string, string> TextBlocks);

public static partial class PlaceholderEngine
{
    public const int MaxRecursionDepth = 5;

    [GeneratedRegex(@"\{\{\s*text\.([A-Za-z0-9-]+)\s*\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex TextBlockRegex();

    [GeneratedRegex(@"\{\{\s*(shop|listing)\.([A-Za-z0-9_]+)\s*\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex BuiltInRegex();

    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_.-]+)\s*\}\}")]
    private static partial Regex AnyPlaceholderRegex();

    /// <summary>
    /// Renders template and built-in placeholders across multiple passes up to <see cref="MaxRecursionDepth"/>.
    /// Returns the rendered text and any unresolved/unknown placeholder tokens.
    /// </summary>
    public static (string Rendered, IReadOnlyList<string> Unresolved) Render(string source, PlaceholderContext context)
    {
        if (string.IsNullOrEmpty(source)) return ("", []);

        var text = source;
        var unknownSlugs = new List<string>();

        // Ensure text blocks can be looked up case-insensitively
        var blocks = context.TextBlocks is Dictionary<string, string> dict && dict.Comparer == StringComparer.OrdinalIgnoreCase
            ? dict
            : new Dictionary<string, string>(context.TextBlocks, StringComparer.OrdinalIgnoreCase);

        // Pass 1: Multi-pass Text Template Expansion (max 5 passes)
        for (var pass = 0; pass < MaxRecursionDepth; pass++)
        {
            var matches = TextBlockRegex().Matches(text);
            if (matches.Count == 0) break;

            var replacedAny = false;
            text = TextBlockRegex().Replace(text, m =>
            {
                var slug = m.Groups[1].Value.ToLowerInvariant();
                if (blocks.TryGetValue(slug, out var blockContent))
                {
                    replacedAny = true;
                    return blockContent;
                }
                if (!unknownSlugs.Contains(slug)) unknownSlugs.Add(slug);
                return m.Value;
            });

            if (!replacedAny) break;
        }

        // Pass 2: Substitute built-in placeholders (case-insensitive inside braces)
        text = BuiltInRegex().Replace(text, m =>
        {
            var prefix = m.Groups[1].Value.ToLowerInvariant();
            var field = m.Groups[2].Value.ToLowerInvariant();
            return ResolveBuiltIn(prefix, field, context, out var resolved) ? resolved : m.Value;
        });

        // Pass 3: Residual Unresolved Placeholders Collection
        var unresolved = new List<string>();
        foreach (var slug in unknownSlugs)
        {
            if (!unresolved.Contains(slug)) unresolved.Add(slug);
        }

        var residualMatches = AnyPlaceholderRegex().Matches(text);
        foreach (Match match in residualMatches)
        {
            var token = match.Groups[1].Value.Trim().ToLowerInvariant();
            if (token.StartsWith("text.", StringComparison.OrdinalIgnoreCase))
            {
                var slug = token[5..];
                if (!unresolved.Contains(slug)) unresolved.Add(slug);
            }
            else
            {
                if (!unresolved.Contains(token)) unresolved.Add(token);
            }
        }

        return (text, unresolved);
    }

    /// <summary>
    /// Checks whether any placeholder token (template or built-in) exists in the given text.
    /// </summary>
    public static bool HasPlaceholders(string? text) =>
        !string.IsNullOrWhiteSpace(text) && AnyPlaceholderRegex().IsMatch(text);

    /// <summary>
    /// Returns all recognized built-in tokens present in the text (e.g. "{{shop.name}}").
    /// </summary>
    public static IReadOnlyList<string> BuiltInTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return [.. BuiltInRegex().Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct()];
    }

    /// <summary>
    /// Checks whether saving <paramref name="text"/> for <paramref name="slug"/> would create a circular dependency
    /// among existing text blocks using depth-first graph traversal.
    /// </summary>
    public static bool HasCircularReference(string slug, string text, IReadOnlyDictionary<string, string> existingBlocks)
    {
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in existingBlocks)
        {
            graph[k.Trim().ToLowerInvariant()] = [.. TextBlockMarkup.Slugs(v)];
        }
        graph[normalizedSlug] = [.. TextBlockMarkup.Slugs(text)];

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Dfs(string current)
        {
            visited.Add(current);
            inStack.Add(current);

            if (graph.TryGetValue(current, out var neighbors))
            {
                foreach (var next in neighbors)
                {
                    if (inStack.Contains(next)) return true;
                    if (!visited.Contains(next) && Dfs(next)) return true;
                }
            }

            inStack.Remove(current);
            return false;
        }

        return Dfs(normalizedSlug);
    }

    /// <summary>
    /// Creates a <see cref="PlaceholderContext"/> from a listing, edit model, and text blocks dictionary.
    /// </summary>
    public static PlaceholderContext CreateContext(Listing listing, ListingEditModel model, IReadOnlyDictionary<string, string> textBlocks)
    {
        var shop = listing.Shop ?? new Shop { Name = "" };
        var skus = listing.Skus ?? [];
        return new PlaceholderContext(shop, model.LocalState(), skus, textBlocks);
    }

    private static bool ResolveBuiltIn(string prefix, string field, PlaceholderContext context, out string resolved)
    {
        resolved = "";
        if (prefix == "shop")
        {
            switch (field)
            {
                case "name":
                    resolved = context.Shop?.Name ?? "";
                    return true;
                case "url":
                    resolved = context.Shop?.Url
                        ?? (!string.IsNullOrWhiteSpace(context.Shop?.Name) ? $"https://www.etsy.com/shop/{context.Shop.Name}" : "");
                    return true;
                default:
                    return false;
            }
        }
        else if (prefix == "listing")
        {
            switch (field)
            {
                case "title":
                    resolved = (string?)context.LocalState["title"] ?? "";
                    return true;
                case "sku":
                    resolved = ResolveSku(context);
                    return true;
                case "materials":
                case "material":
                    resolved = ResolveMaterials(context);
                    return true;
                case "tags":
                    resolved = ResolveTags(context);
                    return true;
                case "dimensions":
                    resolved = ResolveDimensions(context);
                    return true;
                case "length":
                    resolved = FormatDecimal(GetDecimal(context.LocalState, "item_length"));
                    return true;
                case "width":
                    resolved = FormatDecimal(GetDecimal(context.LocalState, "item_width"));
                    return true;
                case "height":
                    resolved = FormatDecimal(GetDecimal(context.LocalState, "item_height"));
                    return true;
                case "dimensions_unit":
                case "dimension_unit":
                    resolved = (string?)context.LocalState["item_dimensions_unit"] ?? "";
                    return true;
                case "weight":
                    resolved = FormatDecimal(GetDecimal(context.LocalState, "item_weight"));
                    return true;
                case "weight_unit":
                    resolved = (string?)context.LocalState["item_weight_unit"] ?? "";
                    return true;
                default:
                    return false;
            }
        }
        return false;
    }

    private static string ResolveSku(PlaceholderContext context)
    {
        if (context.Skus is not null)
        {
            foreach (var s in context.Skus)
            {
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }

        if (context.LocalState.TryGetPropertyValue("inventory", out var inv) && inv is JsonObject invObj)
        {
            if (invObj.TryGetPropertyValue("products", out var prods) && prods is JsonArray prodArr)
            {
                foreach (var p in prodArr)
                {
                    if (p is JsonObject pObj && pObj.TryGetPropertyValue("sku", out var skuNode) && skuNode is not null)
                    {
                        var sku = (string?)skuNode;
                        if (!string.IsNullOrWhiteSpace(sku)) return sku.Trim();
                    }
                }
            }
        }

        return "";
    }

    private static string ResolveMaterials(PlaceholderContext context)
    {
        if (context.LocalState.TryGetPropertyValue("materials", out var mats) && mats is JsonArray matArr)
        {
            return string.Join(", ", matArr.Select(m => (string?)m).Where(s => !string.IsNullOrEmpty(s)));
        }
        return "";
    }

    private static string ResolveTags(PlaceholderContext context)
    {
        if (context.LocalState.TryGetPropertyValue("tags", out var tags) && tags is JsonArray tagArr)
        {
            return string.Join(", ", tagArr.Select(t => (string?)t).Where(s => !string.IsNullOrEmpty(s)));
        }
        return "";
    }

    private static string ResolveDimensions(PlaceholderContext context)
    {
        var length = GetDecimal(context.LocalState, "item_length");
        var width = GetDecimal(context.LocalState, "item_width");
        var height = GetDecimal(context.LocalState, "item_height");
        if (length is null || width is null || height is null)
        {
            return "";
        }
        var lStr = FormatDecimal(length);
        var wStr = FormatDecimal(width);
        var hStr = FormatDecimal(height);
        var unit = (string?)context.LocalState["item_dimensions_unit"];
        if (!string.IsNullOrWhiteSpace(unit))
        {
            return $"{lStr} \u00D7 {wStr} \u00D7 {hStr} {unit.Trim()}";
        }
        return $"{lStr} \u00D7 {wStr} \u00D7 {hStr}";
    }

    private static string FormatDecimal(decimal? d) =>
        d?.ToString("G29", CultureInfo.InvariantCulture) ?? "";

    private static decimal? GetDecimal(JsonObject local, string key)
    {
        if (local.TryGetPropertyValue(key, out var node) && node is not null)
        {
            if (node is JsonValue val)
            {
                if (val.TryGetValue<decimal>(out var d)) return d;
                if (val.TryGetValue<double>(out var dbl)) return (decimal)dbl;
                if (val.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            }
        }
        return null;
    }
}
