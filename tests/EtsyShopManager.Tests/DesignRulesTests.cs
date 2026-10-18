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

    /// <summary>
    /// Blazor shows our reconnect dialog instead of its own only if the ids it looks for are there (it falls back to
    /// its built-in, off-style box otherwise).
    /// </summary>
    [Fact]
    public void Reconnect_dialog_replaces_Blazors_default()
    {
        var modal = File.ReadAllText(Path.Combine(Components, "Layout", "ReconnectModal.razor"));
        foreach (var id in new[] { "components-reconnect-modal", "components-reconnect-button", "components-resume-button",
                     "components-seconds-to-next-attempt", "components-reconnect-current-attempt", "components-reconnect-max-retries" })
            Assert.Contains($"id=\"{id}\"", modal);
        Assert.Contains("<ReconnectModal />", File.ReadAllText(Path.Combine(Components, "App.razor")));
        var appJs = File.ReadAllText(Path.Combine(Root, "src", "EtsyShopManager.Web", "wwwroot", "app.js"));
        Assert.Contains("esm.initReconnect();", appJs);
        // Blazor is started by app.js (with the faster reconnect schedule and our dialog's id); without that call the
        // app would never start at all.
        Assert.Contains("autostart=\"false\"", File.ReadAllText(Path.Combine(Components, "App.razor")));
        Assert.Contains("esm.startBlazor();", appJs);
        Assert.Contains("dialogId: \"components-reconnect-modal\"", appJs);
    }

    [Fact]
    public void Order_cards_carry_their_shops_accent_color()
    {
        var orders = File.ReadAllText(Path.Combine(Components, "Pages", "Orders", "Orders.razor"));
        Assert.Contains("<div class=\"order-card @(due.Urgent ? \"urgent\" : \"\")\" style=\"@ShopStyle(o.ShopId)\">", orders);
        var css = File.ReadAllText(Path.Combine(Root, "src", "EtsyShopManager.Web", "wwwroot", "app.css"));
        Assert.Contains("border: 1px solid var(--shop, var(--esm-line));", css);
        Assert.Contains("box-shadow: inset 4px 0 0 var(--shop, transparent);", css);
    }

    /// <summary>
    /// Every tab bar is outlined and rounded, so the shared style in app.css applies: the bar clips the selected tab to
    /// its corners, and the selected tab gets a wash and an underline in the accent (the shop's, else ink).
    /// </summary>
    [Fact]
    public void Tabs_share_the_rounded_accent_style()
    {
        var offenders = RazorFiles()
            .SelectMany(f => MudTabsTag().Matches(f.Text).Cast<Match>()
                .Where(t => !(t.Value.Contains("Elevation=\"0\"") && t.Value.Contains("Outlined=\"true\"") && t.Value.Contains("Rounded=\"true\"")))
                .Select(_ => $"{f.Name}: <MudTabs> needs Elevation=\"0\" Outlined=\"true\" Rounded=\"true\" (see docs/DESIGN.md, Panels)"))
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));

        var css = File.ReadAllText(Path.Combine(Root, "src", "EtsyShopManager.Web", "wwwroot", "app.css"));
        Assert.Contains(".mud-tabs .mud-tabs-tabbar { border-radius: var(--esm-radius); overflow: hidden; }", css);
        Assert.Contains("background-color: color-mix(in srgb, var(--shop, var(--esm-ink)) 8%, transparent);", css);
        Assert.Contains(".mud-tabs .mud-tab-slider { background: var(--shop, var(--esm-ink)); }", css);
    }

    /// <summary>
    /// Data tables sort by clicking a column header: every labelled header cell is a <c>&lt;SortHeader&gt;</c> (or a
    /// <c>MudTableSortLabel</c> in a MudTable). Header cells without a label (checkboxes, photos, actions) are fine, and an
    /// editing grid whose rows must keep their order opts out with <c>&lt;thead class="esm-fixed-order"&gt;</c>.
    /// </summary>
    [Fact]
    public void Table_headers_are_sortable()
    {
        var offenders = RazorFiles()
            .SelectMany(f => TableHeader().Matches(f.Text).Cast<Match>()
                .Where(h => !h.Value.Contains("esm-fixed-order"))
                .SelectMany(h => PlainHeaderCell().Matches(h.Value).Cast<Match>())
                .Select(c => $"{f.Name}: \"{c.Value.Trim()}…\" — use <SortHeader> (see docs/DESIGN.md, Tables)"))
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Sort_rule_catches_plain_headers()
    {
        Assert.Matches(PlainHeaderCell(), "<th>Name</th>");
        Assert.Matches(PlainHeaderCell(), "<th class=\"text-right\">Qty</th>");
        Assert.Matches(PlainHeaderCell(), "<th>@p.Name</th>");
        Assert.Matches(PlainHeaderCell(), "<MudTh>Date</MudTh>");
        Assert.DoesNotMatch(PlainHeaderCell(), "<th></th>");
        Assert.DoesNotMatch(PlainHeaderCell(), "<th scope=\"col\"><span class=\"visually-hidden\">Photo</span></th>");
        Assert.DoesNotMatch(PlainHeaderCell(), "<MudTh><MudTableSortLabel T=\"Order\">Date</MudTableSortLabel></MudTh>");
        Assert.DoesNotMatch(PlainHeaderCell(), "<SortHeader Sort=\"_sort\" Column=\"name\">Name</SortHeader>");
    }

    [GeneratedRegex("<thead\\b.*?</thead>|<HeaderContent>.*?</HeaderContent>", RegexOptions.Singleline)]
    private static partial Regex TableHeader();

    // A header cell whose content starts with text (a label) rather than another element.
    [GeneratedRegex("<(?:th|MudTh)\\b[^>]*>\\s*[^\\s<]")]
    private static partial Regex PlainHeaderCell();

    [GeneratedRegex("<MudTabs\\b[^>]*>", RegexOptions.Singleline)]
    private static partial Regex MudTabsTag();

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
