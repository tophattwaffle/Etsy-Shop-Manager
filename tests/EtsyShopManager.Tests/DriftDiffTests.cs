using System.Text.Json.Nodes;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Web.Modules.Editor;

namespace EtsyShopManager.Tests;

public class TextDiffTests
{
    private static string Text(IEnumerable<DiffPiece> pieces) => string.Concat(pieces.Select(p => p.Text));

    private static string Marked(IEnumerable<DiffPiece> pieces) => string.Concat(pieces.Select(p => p.Kind switch
    {
        DiffKind.Removed => $"[-{p.Text}-]",
        DiffKind.Added => $"{{+{p.Text}+}}",
        _ => p.Text,
    }));

    [Fact]
    public void Equal_texts_are_one_unchanged_piece()
    {
        var diff = TextDiff.Words("Blue ceramic vase", "Blue ceramic vase");
        Assert.Equal([new DiffPiece(DiffKind.Same, "Blue ceramic vase")], diff);
    }

    [Fact]
    public void A_changed_word_is_removed_then_added_like_git_word_diff()
    {
        var diff = TextDiff.Words("Blue ceramic vase", "Green ceramic vase");
        Assert.Equal("[-Blue-]{+Green+} ceramic vase", Marked(diff));
    }

    [Fact]
    public void A_changed_phrase_reads_as_one_highlight()
    {
        var diff = TextDiff.Words("A small blue vase for flowers", "A large green vase for flowers");
        Assert.Equal("A [-small blue-]{+large green+} vase for flowers", Marked(diff));
    }

    [Fact]
    public void Insertions_and_deletions_keep_the_rest_unchanged()
    {
        Assert.Equal("Hand made{+, glazed+} vase", Marked(TextDiff.Words("Hand made vase", "Hand made, glazed vase")));
        Assert.Equal("Hand [-made -]vase", Marked(TextDiff.Words("Hand made vase", "Hand vase")));
    }

    [Fact]
    public void Each_side_rebuilds_its_own_text_exactly()
    {
        const string mine = "Line one.\nLine two has words.\n\nThird  line, with punctuation!";
        const string etsy = "Line one!\nLine two had more words.\nThird line, with punctuation";
        var diff = TextDiff.Words(mine, etsy);
        Assert.Equal(mine, Text(TextDiff.OldSide(diff)));
        Assert.Equal(etsy, Text(TextDiff.NewSide(diff)));
        Assert.DoesNotContain(TextDiff.OldSide(diff), p => p.Kind == DiffKind.Added);
        Assert.DoesNotContain(TextDiff.NewSide(diff), p => p.Kind == DiffKind.Removed);
    }

    [Fact]
    public void Empty_sides_are_all_added_or_all_removed()
    {
        Assert.Equal("{+New text+}", Marked(TextDiff.Words(null, "New text")));
        Assert.Equal("[-Old text-]", Marked(TextDiff.Words("Old text", "")));
        Assert.Empty(TextDiff.Words(null, null));
    }

    [Fact]
    public void Sequence_finds_the_shortest_edit_script()
    {
        // Myers' paper example: ABCABBA → CBABAC takes 5 edits.
        var ops = TextDiff.Sequence("ABCABBA".ToCharArray(), "CBABAC".ToCharArray());
        Assert.Equal(5, ops.Count(o => o.Kind != DiffKind.Same));
        Assert.Equal("ABCABBA", string.Concat(ops.Where(o => o.Kind != DiffKind.Added).Select(o => o.Item)));
        Assert.Equal("CBABAC", string.Concat(ops.Where(o => o.Kind != DiffKind.Removed).Select(o => o.Item)));
    }

    [Fact]
    public void Random_sequences_round_trip_and_are_never_longer_than_replacing_everything()
    {
        var random = new Random(42);
        for (var run = 0; run < 300; run++)
        {
            var a = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(0, 5)).ToList();
            var b = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(0, 5)).ToList();
            var ops = TextDiff.Sequence(a, b);
            Assert.Equal(a, ops.Where(o => o.Kind != DiffKind.Added).Select(o => o.Item));
            Assert.Equal(b, ops.Where(o => o.Kind != DiffKind.Removed).Select(o => o.Item));
            Assert.Equal(Lcs(a, b), ops.Count(o => o.Kind == DiffKind.Same));
        }
    }

    private static int Lcs(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        var t = new int[a.Count + 1, b.Count + 1];
        for (var i = 1; i <= a.Count; i++)
        for (var j = 1; j <= b.Count; j++)
            t[i, j] = a[i - 1] == b[j - 1] ? t[i - 1, j - 1] + 1 : Math.Max(t[i - 1, j], t[i, j - 1]);
        return t[a.Count, b.Count];
    }

    [Fact]
    public void A_complete_rewrite_of_a_long_text_stays_bounded()
    {
        var mine = string.Join(" ", Enumerable.Range(0, 3000).Select(i => $"a{i}"));
        var etsy = string.Join(" ", Enumerable.Range(0, 3000).Select(i => $"b{i}"));
        var diff = TextDiff.Words(mine, etsy);
        Assert.Equal(mine, Text(TextDiff.OldSide(diff)));
        Assert.Equal(etsy, Text(TextDiff.NewSide(diff)));
    }
}

public class DriftDiffTests
{
    private static readonly EditorLookups Lookups = new(
        new Dictionary<long, string> { [1] = "Standard", [2] = "Express" },
        new Dictionary<long, string> { [7] = "1-3 days" },
        new Dictionary<long, string>(), new Dictionary<long, string>(),
        new Dictionary<long, string> { [5] = "Kiln Co" },
        new Dictionary<long, string>(), "USD");

    private static string Marked(DiffLine line) => string.Concat(line.Pieces.Select(p => p.Kind switch
    {
        DiffKind.Removed => $"[-{p.Text}-]",
        DiffKind.Added => $"{{+{p.Text}+}}",
        _ => p.Text,
    }));

    [Fact]
    public void Title_is_shown_as_text_with_word_highlights_per_side()
    {
        var d = DriftDiff.For("title", JsonValue.Create("Blue vase"), JsonValue.Create("Green vase"), Lookups);
        Assert.Equal(DiffLayout.Text, d.Layout);
        Assert.Equal("[-Blue-] vase", Marked(Assert.Single(d.Mine)));
        Assert.Equal("{+Green+} vase", Marked(Assert.Single(d.Etsy)));
    }

    [Fact]
    public void Tags_mark_the_ones_missing_from_the_other_side()
    {
        var d = DriftDiff.For("tags", new JsonArray("vase", "blue", "gift"), new JsonArray("gift", "vase", "green"), Lookups);
        Assert.Equal(DiffLayout.Chips, d.Layout);
        Assert.Equal(["vase", "[-blue-]", "gift"], d.Mine.Select(Marked));
        Assert.Equal(["gift", "vase", "{+green+}"], d.Etsy.Select(Marked));
        Assert.Null(d.Note);
    }

    [Fact]
    public void Reordered_tags_get_a_note_instead_of_highlights()
    {
        var d = DriftDiff.For("materials", new JsonArray("clay", "glaze"), new JsonArray("glaze", "clay"), Lookups);
        Assert.All(d.Mine.Concat(d.Etsy), l => Assert.Equal(DiffKind.Same, l.Kind));
        Assert.Equal("Same items in a different order", d.Note);
    }

    [Fact]
    public void Duplicate_tags_count()
    {
        var d = DriftDiff.Chips("tags", ["a", "a"], ["a"]);
        Assert.Equal([DiffKind.Same, DiffKind.Removed], d.Mine.Select(l => l.Kind));
    }

    [Fact]
    public void Dropdowns_show_old_and_new_names()
    {
        var d = DriftDiff.For("shipping_profile_id", JsonValue.Create(1L), JsonValue.Create(2L), Lookups);
        Assert.Equal(DiffLayout.Value, d.Layout);
        Assert.Equal("[-Standard-]", Marked(Assert.Single(d.Mine)));
        Assert.Equal("{+Express+}", Marked(Assert.Single(d.Etsy)));
    }

    [Fact]
    public void Production_partners_are_named_chips()
    {
        var d = DriftDiff.For("production_partner_ids", new JsonArray(5L), new JsonArray(), Lookups);
        Assert.Equal("[-Kiln Co-]", Marked(Assert.Single(d.Mine)));
        Assert.Empty(d.Etsy);
    }

    private static JsonObject Product(string value, long price, bool enabled = true) => new()
    {
        ["sku"] = "",
        ["property_values"] = new JsonArray(new JsonObject { ["property_id"] = 200L, ["values"] = new JsonArray(value) }),
        ["offerings"] = new JsonArray(new JsonObject
        {
            ["price_amount"] = price, ["price_divisor"] = 100, ["is_enabled"] = enabled, ["readiness_state_id"] = 7L,
        }),
    };

    [Fact]
    public void Variations_are_rows_with_the_changed_price_highlighted()
    {
        var mine = new JsonObject { ["products"] = new JsonArray(Product("Small", 1000), Product("Large", 2000)) };
        var etsy = new JsonObject { ["products"] = new JsonArray(Product("Small", 1000), Product("Large", 2500), Product("Huge", 3000)) };
        var d = DriftDiff.For(ListingFields.Inventory, mine, etsy, Lookups);
        Assert.Equal(DiffLayout.Rows, d.Layout);
        Assert.Equal(2, d.Mine.Count);
        Assert.Equal(DiffKind.Same, d.Mine[0].Kind);
        Assert.Equal(DiffKind.Removed, d.Mine[1].Kind);
        Assert.Contains(d.Mine[1].Pieces, p => p.Kind == DiffKind.Removed && p.Text.Contains("20"));
        Assert.Equal(3, d.Etsy.Count);
        Assert.Contains(d.Etsy[1].Pieces, p => p.Kind == DiffKind.Added && p.Text.Contains("25"));
        Assert.Equal(DiffKind.Added, d.Etsy[2].Kind);
        Assert.StartsWith("Huge:", string.Concat(d.Etsy[2].Pieces.Select(p => p.Text)));
    }

    [Fact]
    public void Photos_are_rows_in_order()
    {
        JsonObject Photo(long id, string alt) => new() { ["etsy_image_id"] = id, ["rank"] = 1, ["alt_text"] = alt };
        var d = DriftDiff.For(ListingFields.Images, new JsonArray(Photo(1, "front"), Photo(2, "side")),
            new JsonArray(Photo(2, "side")), Lookups);
        Assert.Equal(["1. Photo #1 (front)", "2. Photo #2 (side)"], d.Mine.Select(l => string.Concat(l.Pieces.Select(p => p.Text))));
        Assert.Equal(["1. Photo #2 (side)"], d.Etsy.Select(l => string.Concat(l.Pieces.Select(p => p.Text))));
    }

    [Fact]
    public void Values_that_read_the_same_get_a_note()
    {
        var d = DriftDiff.Value("shop_section_id", "Vases", "Vases");
        Assert.All(d.Mine.Concat(d.Etsy), l => Assert.Equal(DiffKind.Same, l.Kind));
        Assert.NotNull(d.Note);
    }

    [Fact]
    public void Every_tracked_field_can_be_diffed()
    {
        foreach (var f in ListingFields.All.Where(f => f.Tracked))
        {
            var d = DriftDiff.For(f.Key, null, null, Lookups);
            Assert.Equal(f.Key, d.Key);
        }
    }
}
