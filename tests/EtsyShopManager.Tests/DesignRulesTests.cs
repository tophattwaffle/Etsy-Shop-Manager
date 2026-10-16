using System.Text.RegularExpressions;

namespace EtsyShopManager.Tests;

/// <summary>
/// Keeps the UI on the design system in docs/DESIGN.md by checking the Razor sources. A failure names the file and
/// the rule; fix the markup (or, if the rule is wrong, change docs/DESIGN.md and this test together).
/// </summary>
public partial class DesignRulesTests
{
    private static readonly string Root = FindRepoRoot();
    private static readonly string Components = Path.Combine(Root, "src", "EtsyShopManager.Web", "Components");

    [Fact]
    public void Every_interactive_page_starts_with_PageHeader()
    {
        var pages = RazorFiles()
            .Where(f => f.Text.Contains("@page \"") && !f.Text.Contains("ExcludeFromInteractiveRouting"))
            .ToList();
        Assert.True(pages.Count >= 10, $"Only {pages.Count} interactive pages found under {Components}");
        var missing = pages
            .Where(f => !f.Text.Contains("<PageHeader "))
            .Select(f => f.Name)
            .ToList();
        Assert.True(missing.Count == 0, "Pages without <PageHeader Title=\"…\">: " + string.Join(", ", missing));
    }

    [Fact]
    public void Page_titles_are_not_hand_rolled()
    {
        var offenders = Find(HeadingTypo(), "use <PageHeader> for the page title; h5/h6 are for sections");
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Panels_have_no_drop_shadows()
    {
        var offenders = Find(RaisedElevation(), "use Elevation=\"0\" Outlined=\"true\": panels are bordered, not shadowed");
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Colours_come_from_the_theme_and_tokens()
    {
        var offenders = Find(ColourLiteral(), "colour literal: use a token from app.css or the theme in AppTheme.cs");
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void No_inline_styles_except_custom_properties()
    {
        var offenders = Find(InlineStyle(), "inline style: add a class in app.css (only --custom-property values may be inline)");
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Rules_catch_violations()
    {
        Assert.Matches(HeadingTypo(), "<MudText Typo=\"Typo.h4\">Orders</MudText>");
        Assert.Matches(RaisedElevation(), "<MudPaper Class=\"pa-4\" Elevation=\"1\">");
        Assert.Matches(ColourLiteral(), "Color = \"#D5641C\"");
        Assert.Matches(ColourLiteral(), "background: rgba(0, 0, 0, .1)");
        Assert.Matches(InlineStyle(), "<div style=\"flex: 1\">");
        Assert.Matches(InlineStyle(), "<MudSelect Style=\"min-width: 200px\">");
        Assert.DoesNotMatch(InlineStyle(), "<a style=\"@ShopStyle(row.ShopId)\">");
        Assert.DoesNotMatch(InlineStyle(), "<a style=\"--shop: red\">");
        Assert.DoesNotMatch(ColourLiteral(), "#@context.EtsyListingId");
    }

    [GeneratedRegex("Typo=\"Typo\\.h[1-4]\"")]
    private static partial Regex HeadingTypo();

    [GeneratedRegex("Elevation=\"[1-9]")]
    private static partial Regex RaisedElevation();

    [GeneratedRegex("#[0-9a-fA-F]{6}\\b|#[0-9a-fA-F]{3}\\b(?!-)|\\brgba?\\(", RegexOptions.IgnoreCase)]
    private static partial Regex ColourLiteral();

    // style="…" / Style="…" whose value is neither a custom property (--x) nor a computed value (@…).
    [GeneratedRegex("\\b[Ss]tyle=\"(?!--|@)")]
    private static partial Regex InlineStyle();

    private static List<string> Find(Regex rule, string message) =>
        RazorFiles()
            .SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Name, Line: i + 1, Text: line)))
            .Where(l => rule.IsMatch(l.Text))
            .Select(l => $"{l.Name}:{l.Line}: {message}")
            .ToList();

    private static IEnumerable<(string Name, string Text)> RazorFiles() =>
        Directory.EnumerateFiles(Components, "*.razor", SearchOption.AllDirectories)
            .Select(p => (Path.GetRelativePath(Components, p).Replace('\\', '/'), File.ReadAllText(p)));

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "EtsyShopManager.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Repository root (EtsyShopManager.slnx) not found.");
    }
}
