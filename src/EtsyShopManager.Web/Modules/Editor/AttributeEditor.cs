using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Editor;

public enum AttributeCallKind { Set, Delete }

/// <summary>One category attribute call: updateListingProperty (Set) or deleteListingProperty (Delete).</summary>
public sealed record AttributeCall(AttributeCallKind Kind, long PropertyId, long? ScaleId, IReadOnlyList<long> ValueIds, IReadOnlyList<string> Values)
{
    public string Describe(long shopId, long listingId, string name)
    {
        var path = $"/v3/application/shops/{shopId}/listings/{listingId}/properties/{PropertyId}";
        return Kind == AttributeCallKind.Delete
            ? $"DELETE {path}  (remove {name})"
            : $"PUT {path}  value_ids={string.Join(",", ValueIds)}, values={string.Join(",", Values)}" +
              (ScaleId is { } s ? $", scale_id={s}" : "") + $"  ({name})";
    }
}

public sealed record AttributeValue(long ValueId, string Name, long? ScaleId);

public sealed record AttributeScale(long ScaleId, string Name);

/// <summary>A property a listing's category offers as an attribute (from Etsy's taxonomy, imported per category).</summary>
public sealed record AttributeProperty(long PropertyId, string Name, string Label, bool IsRequired, int? MaxValues,
    IReadOnlyList<AttributeScale> Scales, IReadOnlyList<AttributeValue> Values)
{
    /// <summary>
    /// Properties with a list of values can be set here. The others (Height, Width…) take a typed number that Etsy gives
    /// its own value id; how Etsy wants a new one sent can't be checked without writing to a real listing, so the app
    /// leaves them to Etsy (they're kept as they are, and can be removed).
    /// </summary>
    public bool CanEdit => Values.Count > 0;

    public static AttributeProperty From(TaxonomyProperty p)
    {
        using var scales = JsonDocument.Parse(string.IsNullOrWhiteSpace(p.ScalesJson) ? "[]" : p.ScalesJson);
        using var values = JsonDocument.Parse(string.IsNullOrWhiteSpace(p.PossibleValuesJson) ? "[]" : p.PossibleValuesJson);
        return new AttributeProperty(p.PropertyId, p.Name, string.IsNullOrWhiteSpace(p.DisplayName) ? p.Name : p.DisplayName,
            p.IsRequired, p.IsMultivalued ? p.MaxValuesAllowed : 1,
            [.. scales.RootElement.EnumerateArray().Select(s => new AttributeScale(s.GetProperty("scale_id").GetInt64(),
                s.TryGetProperty("display_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : $"Scale {s.GetProperty("scale_id")}"))],
            [.. values.RootElement.EnumerateArray().Select(v => new AttributeValue(v.GetProperty("value_id").GetInt64(),
                v.GetProperty("name").GetString() ?? "",
                v.TryGetProperty("scale_id", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt64() : null))]);
    }
}

/// <summary>
/// Category attributes (Etsy's listing properties such as primary color or occasion) in managed-state JSON:
/// <c>[{property_id, scale_id, value_ids, values}]</c> in property order. A push sends one updateListingProperty per
/// attribute that differs from Etsy and one deleteListingProperty per attribute removed; untouched ones are not sent.
/// </summary>
public static class AttributeEditor
{
    /// <summary>The attribute properties of a category, or null when the app hasn't imported that category's properties.</summary>
    public static async Task<IReadOnlyList<AttributeProperty>?> LoadAsync(AppDbContext db, long? taxonomyId, CancellationToken ct = default)
    {
        if (taxonomyId is not { } id) return null;
        var rows = await db.TaxonomyProperties.AsNoTracking().Where(p => p.TaxonomyId == id).ToListAsync(ct);
        if (rows.Count == 0) return null;
        return [.. rows.Where(p => p.SupportsAttributes).OrderByDescending(p => p.IsRequired).ThenBy(p => p.DisplayName ?? p.Name)
            .Select(AttributeProperty.From)];
    }

    public static IReadOnlyList<JsonObject> Items(JsonNode? attributes) =>
        [.. (attributes as JsonArray ?? []).OfType<JsonObject>()];

    public static long PropertyId(JsonNode attribute) => (long)attribute["property_id"]!;

    /// <summary>The calls that turn Etsy's attributes into the local ones (none when they match).</summary>
    public static IReadOnlyList<AttributeCall> Plan(JsonNode? etsy, JsonNode? local)
    {
        var before = Items(etsy).ToDictionary(PropertyId);
        var after = Items(local).ToDictionary(PropertyId);
        var calls = new List<AttributeCall>();
        foreach (var (id, a) in after.OrderBy(p => p.Key))
        {
            if (before.TryGetValue(id, out var old) && JsonNode.DeepEquals(old, a)) continue;
            var ids = (a["value_ids"] as JsonArray ?? []).Select(v => (long)v!).ToList();
            calls.Add(ids.Count == 0
                ? new AttributeCall(AttributeCallKind.Delete, id, null, [], [])
                : new AttributeCall(AttributeCallKind.Set, id, (long?)a["scale_id"], ids,
                    [.. (a["values"] as JsonArray ?? []).Select(v => (string?)v ?? "")]));
        }
        foreach (var id in before.Keys.Where(id => !after.ContainsKey(id)).Order())
            calls.Add(new AttributeCall(AttributeCallKind.Delete, id, null, [], []));
        // An attribute set to no values is a removal; drop a removal of something Etsy doesn't have.
        return [.. calls.Where(c => c.Kind == AttributeCallKind.Set || before.ContainsKey(c.PropertyId)).DistinctBy(c => (c.Kind, c.PropertyId))];
    }

    /// <summary>The attributes with one property set to the chosen values (none removes it). Values keep Etsy's names.</summary>
    public static JsonArray Set(JsonNode? attributes, AttributeProperty property, long? scaleId, IEnumerable<long> valueIds)
    {
        var chosen = valueIds.Distinct().Select(id => property.Values.FirstOrDefault(v => v.ValueId == id)).OfType<AttributeValue>()
            .OrderBy(v => v.ValueId).ToList();
        var result = new JsonArray([.. Items(attributes).Where(a => PropertyId(a) != property.PropertyId).Select(a => (JsonNode)a.DeepClone())]);
        if (chosen.Count > 0)
            result.Add(new JsonObject
            {
                ["property_id"] = property.PropertyId,
                ["scale_id"] = property.Scales.Count > 0 ? scaleId : null,
                ["value_ids"] = new JsonArray([.. chosen.Select(v => (JsonNode)v.ValueId)]),
                ["values"] = new JsonArray([.. chosen.Select(v => (JsonNode)v.Name)]),
            });
        return new JsonArray([.. result.OrderBy(a => PropertyId(a!)).Select(a => a!.DeepClone())]);
    }

    public static JsonArray Remove(JsonNode? attributes, long propertyId) =>
        new([.. Items(attributes).Where(a => PropertyId(a) != propertyId).Select(a => (JsonNode)a.DeepClone())]);

    /// <summary>What stops the attribute calls: values Etsy wouldn't take for the listing's category.</summary>
    /// <param name="properties">The local category's attribute properties; null when they aren't in the app.</param>
    public static IEnumerable<string> Problems(JsonNode? etsy, JsonNode? local, IReadOnlyList<AttributeProperty>? properties,
        Func<long, string> name)
    {
        var sets = Plan(etsy, local).Where(c => c.Kind == AttributeCallKind.Set).ToList();
        if (sets.Count == 0) yield break;
        if (properties is null)
        {
            yield return "The category's attributes aren't in the app yet (they're read for the categories the shop's listings use). " +
                         "Push the category change on its own first, then set the attributes after the next import.";
            yield break;
        }
        foreach (var call in sets)
        {
            var property = properties.FirstOrDefault(p => p.PropertyId == call.PropertyId);
            if (property is null)
            {
                yield return $"{name(call.PropertyId)} isn't an attribute of this category; remove it.";
                continue;
            }
            if (!property.CanEdit)
            {
                yield return $"{property.Label} takes a typed value that only Etsy can set; change it on Etsy, or discard the change here.";
                continue;
            }
            if (property.MaxValues is { } max && call.ValueIds.Count > max)
                yield return $"{property.Label} takes at most {max} value{(max == 1 ? "" : "s")}.";
            if (property.Scales.Count > 0 && (call.ScaleId is not { } scale || property.Scales.All(s => s.ScaleId != scale)))
                yield return $"Choose a scale for {property.Label}.";
            foreach (var (id, value) in call.ValueIds.Zip(call.Values))
            {
                var known = property.Values.FirstOrDefault(v => v.ValueId == id);
                if (known is null || known.Name != value)
                    yield return $"{property.Label}: \"{value}\" isn't one of Etsy's values for it.";
                else if (known.ScaleId is { } s && s != call.ScaleId)
                    yield return $"{property.Label}: \"{value}\" belongs to another scale.";
                if (value.Contains(','))
                    yield return $"{property.Label}: \"{value}\" contains a comma, which Etsy's form can't carry.";
            }
        }
    }

    /// <summary>Worth knowing, not blocking: required attributes left empty, attributes the new category doesn't have.</summary>
    public static IEnumerable<string> Warnings(JsonNode? local, IReadOnlyList<AttributeProperty>? properties, bool categoryChanged,
        Func<long, string> name)
    {
        if (properties is null) yield break;
        var set = Items(local).Select(PropertyId).ToHashSet();
        foreach (var p in properties.Where(p => p.IsRequired && !set.Contains(p.PropertyId)))
            yield return $"{p.Label} is a required attribute in this category and isn't set.";
        if (categoryChanged)
            foreach (var id in set.Where(id => properties.All(p => p.PropertyId != id)))
                yield return $"{name(id)} isn't an attribute of the new category; Etsy may drop it.";
    }

    /// <summary>"Primary color: Red · Height: 0.75 Inches" for previews and drift.</summary>
    public static string Describe(JsonNode? attributes, Func<long, string> name, Func<long, long, string?> scaleName)
    {
        var items = Items(attributes);
        if (items.Count == 0) return "None";
        return string.Join(" · ", items.Select(a =>
        {
            var id = PropertyId(a);
            var values = string.Join(", ", (a["values"] as JsonArray ?? []).Select(v => (string?)v));
            var scale = (long?)a["scale_id"] is { } s ? scaleName(id, s) ?? $"scale {s.ToString(CultureInfo.InvariantCulture)}" : null;
            return $"{name(id)}: {values}{(scale is null ? "" : $" {scale}")}";
        }));
    }
}
