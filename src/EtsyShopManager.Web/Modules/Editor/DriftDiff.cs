using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Web.Modules.Listings;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>How a field's diff is shown: running text, chips (tags), one row per item (variations, photos), or a value.</summary>
public enum DiffLayout { Text, Chips, Rows, Value }

/// <summary>One line (or chip, or the value) on one side of a field diff, with its changed words highlighted.</summary>
public sealed record DiffLine(DiffKind Kind, IReadOnlyList<DiffPiece> Pieces)
{
    public static DiffLine Of(DiffKind kind, string text) => new(kind, [new DiffPiece(kind, text)]);
}

/// <summary>
/// A field changed both here and on etsy.com, side by side like a git diff: <see cref="Mine"/> is this app's value with
/// what only it has marked Removed, <see cref="Etsy"/> is Etsy's with what only Etsy has marked Added.
/// </summary>
public sealed record FieldDiff(string Key, DiffLayout Layout, IReadOnlyList<DiffLine> Mine, IReadOnlyList<DiffLine> Etsy, string? Note = null);

public static class DriftDiff
{
    public static FieldDiff For(string key, JsonNode? mine, JsonNode? etsy, EditorLookups lookups) => key switch
    {
        "title" or "description" => Text(key, (string?)mine, (string?)etsy),
        "tags" or "materials" => Chips(key, Strings(mine), Strings(etsy)),
        "production_partner_ids" => Chips(key, Ids(mine).Select(i => lookups.ProductionPartners.GetValueOrDefault(i, $"#{i}")).ToList(),
            Ids(etsy).Select(i => lookups.ProductionPartners.GetValueOrDefault(i, $"#{i}")).ToList()),
        ListingFields.Inventory or ListingFields.Images or ListingFields.Video or ListingFields.Attributes or ListingFields.Personalization =>
            Rows(key, Lines(key, mine, lookups), Lines(key, etsy, lookups)),
        _ => Value(key, lookups.Describe(key, mine), lookups.Describe(key, etsy)),
    };

    /// <summary>Running text with the changed words highlighted on each side.</summary>
    public static FieldDiff Text(string key, string? mine, string? etsy)
    {
        var diff = TextDiff.Words(mine, etsy);
        return new FieldDiff(key, DiffLayout.Text,
            [new DiffLine(DiffKind.Same, TextDiff.OldSide(diff))], [new DiffLine(DiffKind.Same, TextDiff.NewSide(diff))],
            string.IsNullOrEmpty(mine) ? "Empty in this app" : string.IsNullOrEmpty(etsy) ? "Empty on Etsy" : null);
    }

    /// <summary>
    /// Items as chips: an item missing from the other side is highlighted (duplicates count). Order doesn't matter for
    /// tags, so a reorder only gets a note.
    /// </summary>
    public static FieldDiff Chips(string key, IReadOnlyList<string> mine, IReadOnlyList<string> etsy)
    {
        var onlyMine = Mark(mine, etsy, DiffKind.Removed);
        var onlyEtsy = Mark(etsy, mine, DiffKind.Added);
        var note = onlyMine.All(l => l.Kind == DiffKind.Same) && onlyEtsy.All(l => l.Kind == DiffKind.Same)
            ? "Same items in a different order" : null;
        return new FieldDiff(key, DiffLayout.Chips, onlyMine, onlyEtsy, note);
    }

    private static List<DiffLine> Mark(IReadOnlyList<string> items, IReadOnlyList<string> other, DiffKind changed)
    {
        var left = other.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return items.Select(item =>
        {
            if (left.TryGetValue(item, out var n) && n > 0) { left[item] = n - 1; return DiffLine.Of(DiffKind.Same, item); }
            return DiffLine.Of(changed, item);
        }).ToList();
    }

    /// <summary>
    /// One row per item, diffed in order like lines of a file; a row changed on both sides (paired up within a hunk)
    /// gets its changed words highlighted, the way git highlights inside a changed line.
    /// </summary>
    public static FieldDiff Rows(string key, IReadOnlyList<string> mine, IReadOnlyList<string> etsy)
    {
        var ops = TextDiff.Sequence(mine, etsy, StringComparer.Ordinal);
        var left = new List<DiffLine>();
        var right = new List<DiffLine>();
        var removed = new List<string>();
        var added = new List<string>();

        void Flush()
        {
            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                if (i < removed.Count && i < added.Count)
                {
                    var words = TextDiff.Words(removed[i], added[i]);
                    left.Add(new DiffLine(DiffKind.Removed, TextDiff.OldSide(words)));
                    right.Add(new DiffLine(DiffKind.Added, TextDiff.NewSide(words)));
                }
                else if (i < removed.Count) left.Add(DiffLine.Of(DiffKind.Removed, removed[i]));
                else right.Add(DiffLine.Of(DiffKind.Added, added[i]));
            }
            removed.Clear();
            added.Clear();
        }

        foreach (var (kind, line) in ops)
        {
            if (kind == DiffKind.Removed) { removed.Add(line); continue; }
            if (kind == DiffKind.Added) { added.Add(line); continue; }
            Flush();
            left.Add(DiffLine.Of(DiffKind.Same, line));
            right.Add(DiffLine.Of(DiffKind.Same, line));
        }
        Flush();
        if (left.Count == 0) left.Add(DiffLine.Of(DiffKind.Same, "None"));
        if (right.Count == 0) right.Add(DiffLine.Of(DiffKind.Same, "None"));
        var note = left.All(l => l.Kind == DiffKind.Same) && right.All(l => l.Kind == DiffKind.Same)
            ? "Shown the same here; the difference is in details the app doesn't display (such as Etsy's ids)" : null;
        return new FieldDiff(key, DiffLayout.Rows, left, right, note);
    }

    /// <summary>A single value: old and new, each highlighted whole when they differ.</summary>
    public static FieldDiff Value(string key, string mine, string etsy)
    {
        if (mine == etsy)
            return new FieldDiff(key, DiffLayout.Value, [DiffLine.Of(DiffKind.Same, mine)], [DiffLine.Of(DiffKind.Same, etsy)],
                "Shown the same here; the difference is in details the app doesn't display (such as Etsy's ids)");
        return new FieldDiff(key, DiffLayout.Value, [DiffLine.Of(DiffKind.Removed, mine)], [DiffLine.Of(DiffKind.Added, etsy)]);
    }

    /// <summary>A structured field as readable lines, one per variation combination, photo, attribute or question.</summary>
    public static IReadOnlyList<string> Lines(string key, JsonNode? value, EditorLookups lookups)
    {
        if (value is null) return [];
        switch (key)
        {
            case ListingFields.Inventory:
            {
                return (value["products"]?.AsArray() ?? []).Select(p =>
                {
                    var name = EditorLookups.VariationName(p!);
                    var sku = (string?)p!["sku"] is { Length: > 0 } s ? $" (SKU {s})" : "";
                    var o = p["offerings"]?.AsArray().FirstOrDefault();
                    if (o is null) return name + sku;
                    var price = ListingFormat.Price((long?)o["price_amount"] ?? 0, (int?)o["price_divisor"] ?? 100, lookups.CurrencyCode);
                    var processing = (long?)o["readiness_state_id"] is { } r ? $", {lookups.ProcessingProfiles.GetValueOrDefault(r, $"#{r}")}" : "";
                    var off = (bool?)o["is_enabled"] == false ? " (unavailable)" : "";
                    return $"{name}: {price}{processing}{off}{sku}";
                }).ToList();
            }
            case ListingFields.Images:
                return value.AsArray().Select((p, i) =>
                    $"{i + 1}. {((long?)p!["etsy_image_id"] is > 0 and long id ? $"Photo #{id}" : "New photo")}" +
                    ((string?)p["alt_text"] is { Length: > 0 } alt ? $" ({alt})" : "")).ToList();
            case ListingFields.Video:
                return value.GetValueKind() == JsonValueKind.Object
                    ? [$"Video #{((long?)value["etsy_video_id"])?.ToString(CultureInfo.InvariantCulture) ?? "?"}" +
                       ((string?)value["video_state"] is { Length: > 0 } s ? $" ({ListingFormat.Humanize(s)})" : "")]
                    : [];
            default:
                var text = lookups.Describe(key, value);
                return text == "None" ? [] : text.Split(" · ");
        }
    }

    private static IReadOnlyList<string> Strings(JsonNode? value) =>
        value is JsonArray a ? a.Select(v => (string?)v ?? "").ToList() : [];

    private static IReadOnlyList<long> Ids(JsonNode? value) =>
        value is JsonArray a ? a.Select(v => (long)v!).ToList() : [];
}
