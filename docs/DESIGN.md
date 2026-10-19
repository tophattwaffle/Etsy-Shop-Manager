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
| `--esm-brand` | Etsy orange: the brand mark, and the selected tab where no shop colour applies |
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
`Elevation="0" Outlined="true" Rounded="true"`; the tab bar is a rounded box that clips the selected tab to its corners,
and the selected tab gets a light wash and an underline in the accent: the shop's (`--shop` on the `MudTabs`) on a page
about one shop, such as a listing, otherwise Etsy orange (`--esm-brand`). No drop shadows anywhere in page content. *(checked)*

**Toggles.** A `MudSwitch` that is on shows the same accent as the selected tab: knob and track in the shop's colour
(`--shop` on an element around it) where the switch belongs to one shop, otherwise Etsy orange. Off is grey. The shared
style in `app.css` does this, so switches take `Color="Color.Primary"` and never set `UncheckedColor`. *(checked)*

**Buttons.** Pill shaped. The main action on a page is filled `Color.Primary`, which is black in light mode and white in
dark mode. Secondary actions are `Variant.Outlined` (2px border) or `Variant.Text`. Destructive actions use
`Color.Error` and confirm in a dialog. Labels say exactly what happens ("Connect a shop", not "Submit").

**Unsaved changes.** A form saves through `<SaveButtons>`: Save is an outlined pill (disabled) while nothing has changed,
and turns into the filled main action as soon as a field differs from what was last saved, with a Cancel button beside
it that puts the saved values back. Cancel shows only while there are changes. The form says whether it has changes,
usually with `SavedValues<T>` (a record of its fields, compared by value), so typing an edit back to what it was clears
it. Text fields use `Immediate="true"` so Save lights up while typing. Something new that has never been saved passes
`New`, so Save works before any change. Dialogs keep their own Save and Cancel (Cancel closes them). *(checked)*

**Status.** Prefer a small dot in the status colour next to plain text, or the `lp-badge` pill used on the listing cards.
MudChips with `Color.Success`/`Error` are acceptable in tables. Never put status in colour alone: the text says it too.
Ship-by times use the `esm-due` pill (`OrderDue.For` sets its text and hue), which copies the Home Assistant card: "2d 5h"
or "3h 10m late", a see-through fill and outline whose hue runs from green at 72 h to red at 12 h (the one colour not
taken from a token, since it is a hue scale).

**Shops.** Each shop has an accent colour read from its profile image (`esm.dominantColor` in `app.js`, via the
same-origin `/media/shop-icons/{id}`), with `--esm-shop-fallback-0..3` as fallbacks. Show it as `--shop` on whatever
belongs to the shop: a ring around `<ShopMark>`, a 3px edge on a card or row, the tint of a selected shop filter.
Never use it for text.

**Filtering lists.** Follow the listings page: pill search box, sort select and view toggle in one row, and filters in a
right-hand panel with counts. On phones the panel becomes swipeable chip rows above the results.

**Tables.** Every data table sorts by clicking a column header; clicking again flips the direction, and an arrow shows
which column and which way. Use `<SortHeader>` with a `TableSort<T>` (rows already loaded: numbers, dates and money sort
by value, text ignores case) or a `ColumnSort` that the page turns into its query when the table is paged, so the order
covers every page (the listings list view, the audit log; a `MudTable` with server data uses `MudTableSortLabel`).
Header cells without a label (checkboxes, photos, row actions) stay plain. An editing grid whose rows must keep their
order while you type marks its header `<thead class="esm-fixed-order">`. Key/value detail tables have no header row.
*(checked)*

**No inline styles.** Add a class to `app.css` instead. The only exception is setting a custom property, such as
`style="--shop: …"`, or a value computed in code (`style="@ShopStyle(id)"`). *(checked)*

**No colour literals in Razor.** No hex, `rgb()` or `rgba()` values in `.razor` files. *(checked)*

**Accessibility.** Keyboard focus is always visible (2px ink outline). Icon-only buttons have an `aria-label` or
`title`. Images that are decoration get `alt=""`. Motion respects `prefers-reduced-motion`.

**Writing.** Name things by what the user sees, not how the system works. Empty states say what to do next. Errors say
what went wrong and how to fix it, without apologising.

**Sign-in and setup pages** are static (no MudBlazor). They use the same tokens, set directly on `.account-shell`, and are
dark unless the device asks for light.
