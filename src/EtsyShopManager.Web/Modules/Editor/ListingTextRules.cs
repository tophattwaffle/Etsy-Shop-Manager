using System.Globalization;
using System.Text;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>
/// Etsy's rules for titles, tags and materials, checked in the review so a push or clone isn't sent only to be rejected.
/// From Etsy's API documentation (updateListing/createDraftListing), plus the capitals check Etsy applied to a real clone
/// (2026-10-17: "more than 3 start with 2 sequential capital letters").
/// </summary>
public static class ListingTextRules
{
    public const int MaxCapitalizedWords = 3;

    /// <summary>Problems with a title, in plain words (empty when Etsy would take it).</summary>
    public static IEnumerable<string> TitleProblems(string title)
    {
        title = title.Trim();
        if (title.Length == 0) { yield return "The title can't be empty."; yield break; }
        if (title.Length > ListingEditor.MaxTitleLength) yield return $"The title is {title.Length} characters; Etsy allows {ListingEditor.MaxTitleLength}.";
        if (Invalid(title, TitleAllowed) is { Length: > 0 } bad)
            yield return $"The title can't contain {bad}. Etsy allows letters, numbers, punctuation, spaces and ™ © ®.";
        foreach (var c in "%:&+")
        {
            var count = title.Count(x => x == c);
            if (count > 1) yield return $"The title uses \"{c}\" {count} times; Etsy allows it once.";
        }
        var capitalized = Capitalized(title);
        if (capitalized.Count > MaxCapitalizedWords)
            yield return $"The title has {capitalized.Count} words starting with two capital letters ({string.Join(", ", capitalized)}); " +
                         $"Etsy allows {MaxCapitalizedWords}. Write some of them in normal case.";
    }

    public static IEnumerable<string> TagProblems(IEnumerable<string> tags) =>
        tags.Where(t => Invalid(t, TagAllowed).Length > 0)
            .Select(t => $"The tag \"{t}\" can't contain {Invalid(t, TagAllowed)}. Etsy allows letters, numbers, spaces, - and ' in tags.");

    public static IEnumerable<string> MaterialProblems(IEnumerable<string> materials) =>
        materials.Where(m => Invalid(m, MaterialAllowed).Length > 0)
            .Select(m => $"The material \"{m}\" can't contain {Invalid(m, MaterialAllowed)}. Etsy allows only letters, numbers and spaces in materials.");

    /// <summary>Words whose first two characters are capital letters ("TEST", "SSBM").</summary>
    public static IReadOnlyList<string> Capitalized(string title) =>
        [.. title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 2 && char.IsUpper(w[0]) && char.IsUpper(w[1]))];

    /// <summary>The characters Etsy doesn't allow, quoted ("\"|\", \"#\""), or empty. Checked per character (emoji included).</summary>
    private static string Invalid(string text, Func<Rune, bool> allowed) =>
        string.Join(", ", text.EnumerateRunes().Where(r => !allowed(r)).Select(r => r.ToString()).Distinct().Select(c => $"\"{c}\""));

    // Etsy's own patterns: title /[^\p{L}\p{Nd}\p{P}\p{Sm}\p{Zs}™©®]/u, tags /[^\p{L}\p{Nd}\p{Zs}\-'™©®]/u,
    // materials /[^\p{L}\p{Nd}\p{Zs}]/u (characters outside them are rejected).
    private static bool Basic(Rune r) =>
        Rune.GetUnicodeCategory(r) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.SpaceSeparator;

    private static bool Marks(Rune r) => r.Value is '™' or '©' or '®';

    private static bool TitleAllowed(Rune r) => Basic(r) || Marks(r) || Rune.GetUnicodeCategory(r) is UnicodeCategory.ConnectorPunctuation
        or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
        or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation
        or UnicodeCategory.MathSymbol;

    private static bool TagAllowed(Rune r) => Basic(r) || Marks(r) || r.Value is '-' or '\'';

    private static bool MaterialAllowed(Rune r) => Basic(r);
}
