using MudBlazor;

namespace EtsyShopManager.Web.Components.Layout;

/// <summary>
/// The app's MudBlazor theme, modelled on Etsy's Shop Manager: ink-black primary actions (white in dark mode), neutral
/// greys, hairline borders, 12px corners, Segoe UI. Colours live here and in the token block of <c>wwwroot/app.css</c>
/// only; see <c>docs/DESIGN.md</c>.
/// </summary>
public static class AppTheme
{
    public static readonly string[] Font =
        ["Segoe UI Variable Text", "Segoe UI", "system-ui", "-apple-system", "Helvetica Neue", "Arial", "sans-serif"];

    public static MudTheme Create()
    {
        var theme = new MudTheme
        {
            PaletteLight = new PaletteLight
            {
                Primary = "#222222",
                PrimaryContrastText = "#ffffff",
                Secondary = "#d5641c", // Etsy orange: the brand mark only
                Background = "#ffffff",
                BackgroundGray = "#f2f2f2",
                Surface = "#ffffff",
                AppbarBackground = "#ffffff",
                AppbarText = "#222222",
                DrawerBackground = "#ffffff",
                DrawerText = "#222222",
                DrawerIcon = "#595959",
                LinesDefault = "rgba(34,34,34,0.15)",
                TableLines = "rgba(34,34,34,0.12)",
                Divider = "rgba(34,34,34,0.12)",
                TextPrimary = "#222222",
                TextSecondary = "#595959",
                ActionDefault = "#595959",
            },
            PaletteDark = new PaletteDark
            {
                Primary = "#f2f2f2",
                PrimaryContrastText = "#222222",
                Secondary = "#f1823a",
                Background = "#222222",
                BackgroundGray = "#333333",
                Surface = "#2a2a2a",
                AppbarBackground = "#1c1c1c",
                AppbarText = "rgba(255,255,255,0.92)",
                DrawerBackground = "#1c1c1c",
                DrawerText = "rgba(255,255,255,0.92)",
                DrawerIcon = "rgba(255,255,255,0.64)",
                LinesDefault = "rgba(255,255,255,0.14)",
                TableLines = "rgba(255,255,255,0.10)",
                Divider = "rgba(255,255,255,0.10)",
                TextPrimary = "rgba(255,255,255,0.92)",
                TextSecondary = "rgba(255,255,255,0.64)",
                ActionDefault = "rgba(255,255,255,0.64)",
            },
            LayoutProperties = new LayoutProperties { DefaultBorderRadius = "12px" },
        };

        var t = theme.Typography;
        foreach (var variant in new BaseTypography[]
                 {
                     t.Default, t.H1, t.H2, t.H3, t.H4, t.H5, t.H6, t.Subtitle1, t.Subtitle2, t.Body1, t.Body2,
                     t.Button, t.Caption, t.Overline,
                 })
            variant.FontFamily = Font;

        // Page titles are PageHeader's job; h5/h6 are section and panel headings.
        t.H4.FontSize = "2rem";
        t.H4.FontWeight = "600";
        t.H5.FontSize = "1.4rem";
        t.H5.FontWeight = "600";
        t.H6.FontSize = "1.1rem";
        t.H6.FontWeight = "600";
        t.Button.FontWeight = "600";
        t.Button.TextTransform = "none";
        t.Button.LetterSpacing = "normal";
        return theme;
    }
}
