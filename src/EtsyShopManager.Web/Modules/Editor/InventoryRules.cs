using System.Text.Json.Nodes;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>
/// Etsy's rules for what varies by which variation, on managed-state inventory JSON. A price (or processing profile)
/// "varies by" the properties in <c>price_on_property</c> (<c>readiness_state_on_property</c>): variations with the
/// same values for those properties must hold the same price. With an empty list one price applies to all of them.
/// </summary>
public static class InventoryRules
{
    /// <summary>The products (by index) that must share the value at <paramref name="index"/>, itself included.</summary>
    public static IReadOnlyList<int> Linked(JsonNode inventory, int index, string onPropertyKey)
    {
        var products = inventory["products"]?.AsArray() ?? [];
        var on = OnProperty(inventory, onPropertyKey);
        var key = GroupKey(products[index], on);
        return [.. products.Select((p, i) => (p, i)).Where(x => GroupKey(x.p, on) == key).Select(x => x.i)];
    }

    /// <summary>Groups of variations that must share one value, as (description, offerings) for checks and messages.</summary>
    public static IEnumerable<(string Group, IReadOnlyList<JsonNode> Offerings)> Groups(JsonNode inventory, string onPropertyKey)
    {
        var on = OnProperty(inventory, onPropertyKey);
        return (inventory["products"]?.AsArray() ?? [])
            .GroupBy(p => GroupKey(p, on))
            .Select(g => (g.Key, (IReadOnlyList<JsonNode>)[.. g.SelectMany(p => p!["offerings"]?.AsArray() ?? []).Select(o => o!)]));
    }

    /// <summary>Names of the properties a value varies by, e.g. "Size", for messages (managed state keeps only ids).</summary>
    public static string PropertyNames(JsonNode inventory, string onPropertyKey, IReadOnlyDictionary<long, string> names)
    {
        var on = OnProperty(inventory, onPropertyKey);
        var labels = (inventory["products"]?.AsArray() ?? [])
            .SelectMany(p => p!["property_values"]?.AsArray() ?? [])
            .Where(v => on.Contains((long)v!["property_id"]!))
            .Select(v => (long)v!["property_id"]!)
            .Distinct()
            .Select(id => names.GetValueOrDefault(id, $"property {id}"))
            .Distinct();
        return string.Join(" and ", labels);
    }

    /// <summary>Property id → name, from a listing's products (e.g. 513 → "Bulk Pricing").</summary>
    public static IReadOnlyDictionary<long, string> Names(EtsyShopManager.Core.Domain.Listing listing) =>
        listing.Products.SelectMany(p => p.PropertyValues).Where(v => v.PropertyName is not null)
            .GroupBy(v => v.PropertyId).ToDictionary(g => g.Key, g => g.First().PropertyName!);

    private static HashSet<long> OnProperty(JsonNode inventory, string key) =>
        [.. (inventory[key]?.AsArray() ?? []).Select(v => (long)v!)];

    private static string GroupKey(JsonNode? product, HashSet<long> on) =>
        string.Join(";", (product?["property_values"]?.AsArray() ?? [])
            .Where(v => on.Contains((long)v!["property_id"]!))
            .OrderBy(v => (long)v!["property_id"]!)
            .Select(v => $"{(long)v!["property_id"]!}={string.Join("|", v!["values"]!.AsArray().Select(x => (string?)x))}"));
}
