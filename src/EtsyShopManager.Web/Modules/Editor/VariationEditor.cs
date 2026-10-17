using System.Text.Json.Nodes;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>A variation of a listing (e.g. Size) and its values, in the order the products list them.</summary>
public sealed record Variation(long PropertyId, string? Name, IReadOnlyList<string> Values)
{
    /// <summary>Etsy's custom variation slots; their name is the seller's (Etsy's own properties, like colors, have fixed names).</summary>
    public bool IsCustom => PropertyId is VariationEditor.CustomProperty1 or VariationEditor.CustomProperty2;
}

/// <summary>
/// Edits the variation structure of a listing's inventory in managed-state JSON (see ManagedInventory): adding,
/// removing and renaming values and variations, what the price and processing vary by, and which combinations are
/// available. Every combination of values always exists (Etsy's inventory is the full grid; a combination is switched
/// off, not left out), and products stay in managed-state order so drafts compare cleanly with Etsy's version.
/// New values carry no Etsy value ids; Etsy assigns them when the inventory is written.
/// <para>Stock is not edited here: new combinations get their stock when the inventory is written (see InventoryWriter).</para>
/// </summary>
public static class VariationEditor
{
    public const long CustomProperty1 = 513;
    public const long CustomProperty2 = 514;
    public const int MaxVariations = 2;
    public const string PriceOnProperty = "price_on_property";
    public const string ProcessingOnProperty = "readiness_state_on_property";

    /// <summary>
    /// The variations and their values. <paramref name="order"/> is Etsy's order of the variations (the order buyers pick
    /// them in, see <see cref="EtsyOrder"/>); without it they come by property id, as managed state stores them.
    /// </summary>
    public static IReadOnlyList<Variation> Variations(JsonNode inventory, IReadOnlyList<long>? order = null)
    {
        var result = new List<Variation>();
        foreach (var pv in Products(inventory).SelectMany(PropertyValues))
        {
            var id = (long)pv["property_id"]!;
            var index = result.FindIndex(v => v.PropertyId == id);
            if (index < 0)
            {
                result.Add(new Variation(id, (string?)pv["property_name"], [ValueText(pv)]));
                continue;
            }
            var v = result[index];
            if (!v.Values.Contains(ValueText(pv), StringComparer.Ordinal))
                result[index] = v with { Values = [.. v.Values, ValueText(pv)] };
        }
        return [.. result.OrderBy(v => Position(order, v.PropertyId)).ThenBy(v => v.PropertyId)];
    }

    /// <summary>Etsy's order of a listing's variations, from its imported products (managed state sorts them by id).</summary>
    public static IReadOnlyList<long> EtsyOrder(EtsyShopManager.Core.Domain.Listing listing) =>
        [.. listing.Products.Where(p => !p.IsDeleted).SelectMany(p => p.PropertyValues).Select(v => v.PropertyId).Distinct()];

    public static JsonObject AddValue(JsonNode inventory, long propertyId, string value)
    {
        var inv = Clone(inventory);
        var text = Clean(value, "The value");
        var variation = Find(inv, propertyId);
        if (variation.Values.Contains(text, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"{Label(variation)} already has \"{text}\".");

        // One new product per combination of the other variations' values, copied from an existing one (so the price
        // and processing follow whatever they vary by).
        var templates = Products(inv)
            .GroupBy(p => OtherKey(p, propertyId))
            .Select(g => g.First())
            .ToList();
        foreach (var template in templates)
        {
            var product = (JsonObject)template.DeepClone();
            var pv = PropertyValues(product).Single(v => (long)v["property_id"]! == propertyId);
            pv["value_ids"] = new JsonArray();
            pv["values"] = new JsonArray(text);
            foreach (var offering in Offerings(product)) offering["is_enabled"] = true;
            ProductArray(inv).Add(product);
        }
        return Canonical(inv);
    }

    public static JsonObject RemoveValue(JsonNode inventory, long propertyId, string value)
    {
        var inv = Clone(inventory);
        var variation = Find(inv, propertyId);
        if (!variation.Values.Contains(value, StringComparer.Ordinal))
            throw new ArgumentException($"{Label(variation)} has no \"{value}\".");
        if (variation.Values.Count == 1)
            throw new ArgumentException($"\"{value}\" is the only value of {Label(variation)}; remove the variation instead.");
        RemoveProducts(inv, p => ValueOf(p, propertyId) == value);
        return Canonical(inv);
    }

    public static JsonObject RenameValue(JsonNode inventory, long propertyId, string value, string newValue)
    {
        var inv = Clone(inventory);
        var variation = Find(inv, propertyId);
        var text = Clean(newValue, "The value");
        if (text == value) return Canonical(inv);
        if (variation.Values.Any(v => v != value && string.Equals(v, text, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"{Label(variation)} already has \"{text}\".");
        foreach (var pv in Products(inv).SelectMany(PropertyValues)
                     .Where(v => (long)v["property_id"]! == propertyId && ValueText(v) == value))
        {
            pv["value_ids"] = new JsonArray(); // a new value for Etsy
            pv["values"] = new JsonArray(text);
        }
        return Canonical(inv);
    }

    /// <summary>Adds a variation (an Etsy custom one) with its values; every existing combination gets each value.</summary>
    public static JsonObject AddVariation(JsonNode inventory, string name, IEnumerable<string> values)
    {
        var inv = Clone(inventory);
        var variations = Variations(inv);
        if (variations.Count >= MaxVariations) throw new ArgumentException($"Etsy allows at most {MaxVariations} variations.");
        var label = Clean(name, "The variation name");
        if (variations.Any(v => string.Equals(v.Name, label, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"There is already a variation called \"{label}\".");
        var texts = values.Select(v => Clean(v, "A value")).ToList();
        if (texts.Count == 0) throw new ArgumentException("Give the new variation at least one value.");
        if (texts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != texts.Count)
            throw new ArgumentException("The new variation has the same value twice.");
        var propertyId = new[] { CustomProperty1, CustomProperty2 }.First(id => variations.All(v => v.PropertyId != id));

        var existing = Products(inv).ToList();
        ProductArray(inv).Clear();
        foreach (var product in existing)
        {
            foreach (var text in texts)
            {
                var copy = (JsonObject)product.DeepClone();
                PropertyValueArray(copy).Add(new JsonObject
                {
                    ["property_id"] = propertyId,
                    ["property_name"] = label,
                    ["scale_id"] = null,
                    ["value_ids"] = new JsonArray(),
                    ["values"] = new JsonArray(text),
                });
                ProductArray(inv).Add(copy);
            }
        }
        return Canonical(inv);
    }

    /// <summary>
    /// Removes a variation: each remaining combination keeps the first of the products it merges (its price, processing,
    /// availability and SKU), and nothing varies by the removed variation any more.
    /// </summary>
    public static JsonObject RemoveVariation(JsonNode inventory, long propertyId)
    {
        var inv = Clone(inventory);
        Find(inv, propertyId);
        var kept = Products(inv).GroupBy(p => OtherKey(p, propertyId)).Select(g => g.First()).ToList();
        ProductArray(inv).Clear();
        foreach (var product in kept)
        {
            var pvs = PropertyValueArray(product);
            foreach (var pv in pvs.Where(v => (long)v!["property_id"]! == propertyId).ToList()) pvs.Remove(pv);
            ProductArray(inv).Add(product.DeepClone());
        }
        foreach (var key in new[] { PriceOnProperty, "quantity_on_property", "sku_on_property", ProcessingOnProperty })
            if (inv[key] is JsonArray on)
                foreach (var item in on.Where(x => (long)x! == propertyId).ToList()) on.Remove(item);
        return Canonical(inv);
    }

    public static JsonObject RenameVariation(JsonNode inventory, long propertyId, string name)
    {
        var inv = Clone(inventory);
        var variation = Find(inv, propertyId);
        if (!variation.IsCustom) throw new ArgumentException($"{Label(variation)} is one of Etsy's own variations; its name can't be changed.");
        var label = Clean(name, "The variation name");
        if (Variations(inv).Any(v => v.PropertyId != propertyId && string.Equals(v.Name, label, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"There is already a variation called \"{label}\".");
        foreach (var pv in Products(inv).SelectMany(PropertyValues).Where(v => (long)v["property_id"]! == propertyId))
            pv["property_name"] = label;
        return Canonical(inv);
    }

    /// <summary>
    /// Sets whether the price (<see cref="PriceOnProperty"/>) or processing (<see cref="ProcessingOnProperty"/>) varies
    /// by a variation. When it stops varying, variations that now share one take the first one's value.
    /// </summary>
    public static JsonObject SetVaries(JsonNode inventory, string onPropertyKey, long propertyId, bool varies)
    {
        if (onPropertyKey is not (PriceOnProperty or ProcessingOnProperty)) throw new ArgumentException($"Unknown setting {onPropertyKey}.");
        var inv = Clone(inventory);
        Find(inv, propertyId);
        var on = (inv[onPropertyKey] as JsonArray) ?? [];
        var ids = on.Select(x => (long)x!).Where(id => id != propertyId).ToList();
        if (varies) ids.Add(propertyId);
        inv[onPropertyKey] = new JsonArray([.. ids.Order().Select(id => (JsonNode)id)]);

        if (!varies)
        {
            foreach (var (_, offerings) in InventoryRules.Groups(inv, onPropertyKey))
            {
                var first = offerings[0];
                foreach (var o in offerings.Skip(1))
                {
                    if (onPropertyKey == PriceOnProperty)
                    {
                        o["price_amount"] = first["price_amount"]?.DeepClone();
                        o["price_divisor"] = first["price_divisor"]?.DeepClone();
                    }
                    else o["readiness_state_id"] = first["readiness_state_id"]?.DeepClone();
                }
            }
        }
        return Canonical(inv);
    }

    /// <summary>Makes a combination available to buyers or not (Etsy keeps every combination; unavailable ones are hidden).</summary>
    public static JsonObject SetAvailable(JsonNode inventory, int productIndex, bool available)
    {
        var inv = Clone(inventory);
        foreach (var offering in Offerings(ProductArray(inv)[productIndex]!.AsObject())) offering["is_enabled"] = available;
        return Canonical(inv);
    }

    /// <summary>What changed between two inventories' structure, in words, for the push preview.</summary>
    public static IReadOnlyList<string> Describe(JsonNode before, JsonNode after)
    {
        var lines = new List<string>();
        var old = Variations(before);
        var now = Variations(after);
        foreach (var v in now.Where(v => old.All(o => o.PropertyId != v.PropertyId)))
            lines.Add($"New variation {Label(v)}: {string.Join(", ", v.Values)}.");
        foreach (var o in old.Where(o => now.All(v => v.PropertyId != o.PropertyId)))
            lines.Add($"Variation {Label(o)} is removed; buyers no longer choose it.");
        foreach (var v in now)
        {
            if (old.FirstOrDefault(o => o.PropertyId == v.PropertyId) is not { } o) continue;
            if (v.Name != o.Name) lines.Add($"Variation {Label(o)} is renamed to {Label(v)}.");
            var added = v.Values.Except(o.Values).ToList();
            var removed = o.Values.Except(v.Values).ToList();
            if (added.Count > 0) lines.Add($"{Label(v)}: adds {string.Join(", ", added)}.");
            if (removed.Count > 0) lines.Add($"{Label(v)}: removes {string.Join(", ", removed)} (buyers can no longer choose {(removed.Count == 1 ? "it" : "them")}).");
        }
        foreach (var (key, what) in new[] { (PriceOnProperty, "Price"), (ProcessingOnProperty, "Processing") })
        {
            var a = OnNames(before, key, old);
            var b = OnNames(after, key, now);
            if (a != b) lines.Add($"{what} now {(b.Length == 0 ? "is the same for every variation" : $"varies by {b}")}.");
        }
        // Price changes of combinations on both sides (matched by their values, so new value ids don't matter).
        var oldPrices = Products(before).GroupBy(TextKey).ToDictionary(g => g.Key, g => PriceText(g.First()));
        var priceChanges = Products(after)
            .Where(p => oldPrices.TryGetValue(TextKey(p), out var was) && was != PriceText(p))
            .Select(p => $"{Name(p)}: {oldPrices[TextKey(p)]} → {PriceText(p)}")
            .ToList();
        lines.AddRange(priceChanges.Take(10).Select(c => $"Price of {c}."));
        if (priceChanges.Count > 10) lines.Add($"…and {priceChanges.Count - 10} more price changes.");

        var unavailableBefore = Products(before).Count(p => !Available(p));
        var unavailableAfter = Products(after).Count(p => !Available(p));
        if (old.Count == now.Count && old.Zip(now).All(x => x.First.Values.SequenceEqual(x.Second.Values)) && unavailableBefore != unavailableAfter)
            lines.Add($"{unavailableAfter} combination{(unavailableAfter == 1 ? " is" : "s are")} unavailable to buyers (was {unavailableBefore}).");
        return lines;
    }

    /// <summary>
    /// After a write, Etsy has given new values their ids. Copies them into <paramref name="local"/> wherever a value with
    /// no ids matches Etsy's by variation and text, so the pushed draft compares equal to what Etsy now holds.
    /// </summary>
    public static JsonObject AdoptValueIds(JsonNode local, JsonNode etsy)
    {
        var inv = Clone(local);
        var known = Products(etsy).SelectMany(PropertyValues)
            .Where(v => (v["value_ids"] as JsonArray)?.Count > 0)
            .GroupBy(v => ((long)v["property_id"]!, ValueText(v)))
            .ToDictionary(g => g.Key, g => g.First()["value_ids"]!);
        foreach (var pv in Products(inv).SelectMany(PropertyValues).Where(v => (v["value_ids"] as JsonArray)?.Count is null or 0))
            if (known.TryGetValue(((long)pv["property_id"]!, ValueText(pv)), out var ids))
                pv["value_ids"] = ids.DeepClone();
        return Canonical(inv);
    }

    /// <summary>Managed-state order: property values by id, products by their values (ordinal), then SKU (see ManagedInventory).</summary>
    public static JsonObject Canonical(JsonObject inventory)
    {
        var products = Products(inventory).ToList();
        foreach (var product in products)
        {
            var pvs = PropertyValues(product).OrderBy(v => (long)v["property_id"]!).Select(v => v.DeepClone()).ToArray();
            product["property_values"] = new JsonArray(pvs);
        }
        var sorted = products
            .OrderBy(p => string.Join(";", PropertyValues(p).Select(Key)), StringComparer.Ordinal)
            .ThenBy(p => (string?)p["sku"] ?? "", StringComparer.Ordinal)
            .Select(p => p.DeepClone())
            .ToArray();
        inventory["products"] = new JsonArray(sorted);
        return inventory;
    }

    // ---- JSON helpers ----

    private static JsonObject Clone(JsonNode inventory) => (JsonObject)inventory.DeepClone();
    private static JsonArray ProductArray(JsonNode inventory) => (JsonArray)(inventory["products"] ??= new JsonArray());
    private static IEnumerable<JsonObject> Products(JsonNode inventory) => (inventory["products"]?.AsArray() ?? []).Select(p => p!.AsObject());
    private static JsonArray PropertyValueArray(JsonObject product) => (JsonArray)(product["property_values"] ??= new JsonArray());
    private static IEnumerable<JsonObject> PropertyValues(JsonObject product) => PropertyValueArray(product).Select(v => v!.AsObject());
    private static IEnumerable<JsonObject> Offerings(JsonObject product) => (product["offerings"]?.AsArray() ?? []).Select(o => o!.AsObject());
    private static bool Available(JsonObject product) => Offerings(product).Any(o => (bool?)o["is_enabled"] == true);
    private static string ValueText(JsonObject pv) => string.Join("|", (pv["values"]?.AsArray() ?? []).Select(x => (string?)x));
    private static string? ValueOf(JsonObject product, long propertyId) =>
        PropertyValues(product).FirstOrDefault(v => (long)v["property_id"]! == propertyId) is { } pv ? ValueText(pv) : null;

    private static string TextKey(JsonObject product) =>
        string.Join(";", PropertyValues(product).OrderBy(v => (long)v["property_id"]!).Select(v => $"{(long)v["property_id"]!}={ValueText(v)}"));

    private static string Name(JsonObject product) => PropertyValues(product).Any()
        ? string.Join(" / ", PropertyValues(product).Select(ValueText))
        : "the listing";

    private static string PriceText(JsonObject product) =>
        Offerings(product).FirstOrDefault() is { } o
            ? ((decimal)((long?)o["price_amount"] ?? 0) / ((int?)o["price_divisor"] is int d && d > 0 ? d : 100)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            : "";

    private static string OtherKey(JsonObject product, long propertyId) =>
        string.Join(";", PropertyValues(product).Where(v => (long)v["property_id"]! != propertyId).Select(Key));

    // Same as ManagedProperty.Key.
    private static string Key(JsonObject pv) =>
        $"{(long)pv["property_id"]!}:{(long?)pv["scale_id"]}:{string.Join(",", (pv["value_ids"]?.AsArray() ?? []).Select(x => (long)x!))}:{ValueText(pv)}";

    private static void RemoveProducts(JsonObject inventory, Func<JsonObject, bool> remove)
    {
        var array = ProductArray(inventory);
        foreach (var product in array.Where(p => remove(p!.AsObject())).ToList()) array.Remove(product);
    }

    private static Variation Find(JsonNode inventory, long propertyId) =>
        Variations(inventory).FirstOrDefault(v => v.PropertyId == propertyId)
        ?? throw new ArgumentException($"This listing has no variation {propertyId}.");

    /// <summary>Where a property comes in Etsy's order (unknown ones last).</summary>
    public static int Position(IReadOnlyList<long>? order, long propertyId)
    {
        for (var i = 0; i < (order?.Count ?? 0); i++)
            if (order![i] == propertyId) return i;
        return int.MaxValue;
    }

    private static string Label(Variation v) => v.Name ?? $"variation {v.PropertyId}";

    private static string OnNames(JsonNode inventory, string key, IReadOnlyList<Variation> variations) =>
        string.Join(" and ", (inventory[key]?.AsArray() ?? []).Select(x => (long)x!)
            .Select(id => variations.FirstOrDefault(v => v.PropertyId == id) is { } v ? Label(v) : $"variation {id}"));

    private static string Clean(string? text, string what)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) throw new ArgumentException($"{what} can't be empty.");
        return t;
    }
}
