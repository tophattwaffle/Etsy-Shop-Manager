using System.Text.RegularExpressions;

namespace EtsyShopManager.Core.Listings;

public enum DiffKind { Same, Removed, Added }

/// <summary>A run of text in a diff: unchanged, only in the old version (removed) or only in the new one (added).</summary>
public sealed record DiffPiece(DiffKind Kind, string Text);

/// <summary>
/// Git-style diffs for the drift view: Myers' shortest edit script over words (text) or whole items (lines).
/// "Old" and "new" are just the two sides; the drift view passes this app's value as old and Etsy's as new.
/// </summary>
public static partial class TextDiff
{
    /// <summary>Above this many edits the two sides are shown as wholly replaced (keeps memory bounded for rewrites).</summary>
    public const int MaxEdits = 2000;

    // Words (letters, digits, apostrophes), runs of whitespace, and any other single character.
    [GeneratedRegex(@"\s+|[\p{L}\p{N}_'’]+|.", RegexOptions.Singleline)]
    private static partial Regex Token();

    public static IReadOnlyList<string> Tokenize(string? text) =>
        string.IsNullOrEmpty(text) ? [] : Token().Matches(text).Select(m => m.Value).ToList();

    /// <summary>
    /// A word-level diff of two texts, in order: Same pieces belong to both, Removed only to the old text, Added only to
    /// the new one. Whitespace between two changes counts as changed, so a changed phrase reads as one highlight.
    /// </summary>
    public static IReadOnlyList<DiffPiece> Words(string? oldText, string? newText)
    {
        var ops = Sequence(Tokenize(oldText), Tokenize(newText), StringComparer.Ordinal);
        var pieces = new List<DiffPiece>(ops.Count);
        for (var i = 0; i < ops.Count; i++)
        {
            var (kind, token) = ops[i];
            if (kind == DiffKind.Same && string.IsNullOrWhiteSpace(token)
                && i > 0 && ops[i - 1].Kind != DiffKind.Same && i < ops.Count - 1 && ops[i + 1].Kind != DiffKind.Same)
            {
                pieces.Add(new DiffPiece(DiffKind.Removed, token));
                pieces.Add(new DiffPiece(DiffKind.Added, token));
            }
            else pieces.Add(new DiffPiece(kind, token));
        }
        return Merge(Reorder(pieces));
    }

    /// <summary>The old side of a diff: Same and Removed pieces.</summary>
    public static IReadOnlyList<DiffPiece> OldSide(IEnumerable<DiffPiece> diff) => Merge(diff.Where(p => p.Kind != DiffKind.Added));

    /// <summary>The new side of a diff: Same and Added pieces.</summary>
    public static IReadOnlyList<DiffPiece> NewSide(IEnumerable<DiffPiece> diff) => Merge(diff.Where(p => p.Kind != DiffKind.Removed));

    /// <summary>Joins neighbouring pieces of the same kind.</summary>
    public static IReadOnlyList<DiffPiece> Merge(IEnumerable<DiffPiece> pieces)
    {
        var result = new List<DiffPiece>();
        foreach (var p in pieces)
        {
            if (p.Text.Length == 0) continue;
            if (result.Count > 0 && result[^1].Kind == p.Kind) result[^1] = p with { Text = result[^1].Text + p.Text };
            else result.Add(p);
        }
        return result;
    }

    // Within each run of changes, removals first and then additions (as git prints a hunk), so merging joins them.
    private static List<DiffPiece> Reorder(List<DiffPiece> pieces)
    {
        var result = new List<DiffPiece>(pieces.Count);
        var added = new List<DiffPiece>();
        foreach (var p in pieces)
        {
            if (p.Kind == DiffKind.Added) { added.Add(p); continue; }
            if (p.Kind == DiffKind.Same) { result.AddRange(added); added.Clear(); }
            result.Add(p);
        }
        result.AddRange(added);
        return result;
    }

    /// <summary>
    /// The shortest edit script turning <paramref name="a"/> into <paramref name="b"/> (Myers 1986), as the items of both
    /// in order, each marked Same, Removed (only in a) or Added (only in b). Beyond <see cref="MaxEdits"/> edits the
    /// differing middle is returned as all removed, then all added.
    /// </summary>
    public static IReadOnlyList<(DiffKind Kind, T Item)> Sequence<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        // Common prefix and suffix need no search.
        var start = 0;
        while (start < a.Count && start < b.Count && comparer.Equals(a[start], b[start])) start++;
        var endA = a.Count;
        var endB = b.Count;
        while (endA > start && endB > start && comparer.Equals(a[endA - 1], b[endB - 1])) { endA--; endB--; }

        var result = new List<(DiffKind, T)>(Math.Max(a.Count, b.Count));
        for (var i = 0; i < start; i++) result.Add((DiffKind.Same, a[i]));
        result.AddRange(Middle(a, start, endA, b, start, endB, comparer));
        for (var i = endA; i < a.Count; i++) result.Add((DiffKind.Same, a[i]));
        return result;
    }

    private static List<(DiffKind, T)> Middle<T>(IReadOnlyList<T> a, int a0, int a1, IReadOnlyList<T> b, int b0, int b1, IEqualityComparer<T> comparer)
    {
        int n = a1 - a0, m = b1 - b0;
        var replaced = new List<(DiffKind, T)>(n + m);
        for (var i = a0; i < a1; i++) replaced.Add((DiffKind.Removed, a[i]));
        for (var i = b0; i < b1; i++) replaced.Add((DiffKind.Added, b[i]));
        if (n == 0 || m == 0) return replaced;

        // v[k] = furthest x on diagonal k (k = x - y); trace[d] keeps v for diagonals -d..d before step d.
        var max = Math.Min(n + m, MaxEdits);
        var offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        var found = -1;
        for (var d = 0; d <= max && found < 0; d++)
        {
            var snapshot = new int[2 * d + 1];
            Array.Copy(v, offset - d, snapshot, 0, 2 * d + 1);
            trace.Add(snapshot);
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && comparer.Equals(a[a0 + x], b[b0 + y])) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
        }
        if (found < 0) return replaced;

        // Walk back from the end through the saved steps.
        var reversed = new List<(DiffKind, T)>(n + m);
        int cx = n, cy = m;
        for (var d = found; d >= 0; d--)
        {
            var saved = trace[d];
            int At(int k) => saved[k + d];
            var k = cx - cy;
            int prevK, prevX, prevY;
            if (d == 0) { prevK = 0; prevX = 0; prevY = 0; }
            else
            {
                prevK = k == -d || (k != d && At(k - 1) < At(k + 1)) ? k + 1 : k - 1;
                prevX = At(prevK);
                prevY = prevX - prevK;
            }
            while (cx > prevX && cy > prevY) { reversed.Add((DiffKind.Same, a[a0 + cx - 1])); cx--; cy--; }
            if (d > 0)
            {
                if (cx == prevX) reversed.Add((DiffKind.Added, b[b0 + cy - 1]));
                else reversed.Add((DiffKind.Removed, a[a0 + cx - 1]));
            }
            cx = prevX;
            cy = prevY;
        }
        reversed.Reverse();
        return reversed;
    }
}
