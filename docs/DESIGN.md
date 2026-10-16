# Design system

The UI follows **Etsy's Shop Manager**, so it feels familiar to anyone who sells on Etsy. Dark mode is the default; the
app-bar button switches to light mode for the session. Every new page or component follows this guide, and
`tests/EtsyShopManager.Tests/DesignRulesTests.cs` fails the build when a page breaks the checkable rules.

## Where the style lives

| What | Where |
|---|---|
| Palette, typography, corner radius (MudBlazor theme) | `src/EtsyShopManager.Web/Components/Layout/AppTheme.cs` |
| CSS tokens, MudBlazor overrides, page styles | `src/EtsyShopManager.Web/wwwroot/app.css` |
| Page title block | `Components/Shared/PageHeader.razor` |
| Reference page (gallery, list, filter panel, shop colours) | `Components/Pages/Listings/Listings.razor` |

Colours exist only in `AppTheme.cs` and in the token block at the top of `app.css`. Everything else uses the tokens.

## Tokens

| Token | Use |
|---|---|
| `--esm-ink` | Text, primary buttons, focus rings, the selected state |
| `--esm-muted` | Secondary text, counts, table headings |
| `--esm-paper` | Panels and cards |
| `--esm-page` | Page background |
| `--esm-line` | Hairline borders and dividers |
| `--esm-sunk` | Selected filter or nav item, placeholders, code |
| `--esm-hover` | Hover background |
| `--esm-brand` | Etsy orange, for the brand mark only |
| `--esm-success`, `--esm-info`, `--esm-warning`, `--esm-error` | Status dots and status text |
| `--esm-radius` (12px), `--esm-radius-sm` (8px) | Panels and cards; thumbnails and inputs |
| `--shop` | A shop's accent colour, set on the element that belongs to the shop |

## Rules

**Type.** Segoe UI everywhere (Etsy's Graphik can't be licensed). Weights are 400 and 600 only. Sentence case for
everything: titles, buttons, tabs, labels. No all-caps labels and no overlines.

**Page structure.** Every page starts with `<PageHeader Title="…">`, with an optional `<Summary>` (one line: what the page
holds, or a count) and `<Actions>` (the page's main buttons). Pages never style their own `h1`. Inside the page, `h6`
(`Typo.h6`) titles a panel and `h5` titles a larger section. *(checked)*

**Panels.** Bordered, not shadowed: `<MudPaper Elevation="0" Outlined="true">`, 12px corners. Tabs use
`Elevation="0" Outlined="true"`. No drop shadows anywhere in page content. *(checked)*

**Buttons.** Pill shaped. The main action on a page is filled `Color.Primary`, which is black in light mode and white in
dark mode. Secondary actions are `Variant.Outlined` (2px border) or `Variant.Text`. Destructive actions use
`Color.Error` and confirm in a dialog. Labels say exactly what happens ("Connect a shop", not "Submit").

**Status.** Prefer a small dot in the status colour next to plain text, or the `lp-badge` pill used on the listing cards.
MudChips with `Color.Success`/`Error` are acceptable in tables. Never put status in colour alone: the text says it too.

**Shops.** Each shop has an accent colour read from its profile image (`esm.dominantColor` in `app.js`, via the
same-origin `/media/shop-icons/{id}`), with `--esm-shop-fallback-0..3` as fallbacks. Show it as `--shop` on whatever
belongs to the shop: a ring around `<ShopMark>`, a 3px edge on a card or row, the tint of a selected shop filter.
Never use it for text.

**Filtering lists.** Follow the listings page: pill search box, sort select and view toggle in one row, and filters in a
right-hand panel with counts. On phones the panel becomes swipeable chip rows above the results.

**No inline styles.** Add a class to `app.css` instead. The only exception is setting a custom property, such as
`style="--shop: …"`, or a value computed in code (`style="@ShopStyle(id)"`). *(checked)*

**No colour literals in Razor.** No hex, `rgb()` or `rgba()` values in `.razor` files. *(checked)*

**Accessibility.** Keyboard focus is always visible (2px ink outline). Icon-only buttons have an `aria-label` or
`title`. Images that are decoration get `alt=""`. Motion respects `prefers-reduced-motion`.

**Writing.** Name things by what the user sees, not how the system works. Empty states say what to do next. Errors say
what went wrong and how to fix it, without apologising.

**Sign-in and setup pages** are static (no MudBlazor). They use the same tokens, set directly on `.account-shell`, and are
dark unless the device asks for light.
