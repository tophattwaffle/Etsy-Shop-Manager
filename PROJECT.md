# Project: Etsy Shop Manager Enhancement

## Architecture
- **Language / Framework**: C# .NET 10, Blazor Interactive Server, MudBlazor, EF Core SQLite.
- **Modules**:
  - `EtsyShopManager.Core`: Domain models (`VariationList`, `ListingVariationList`), Roles (`Roles.Admin`, `Roles.User`).
  - `EtsyShopManager.Data`: `AppDbContext`, JSON converters, migrations.
  - `EtsyShopManager.Web`:
    - `Modules/Templates`: `VariationListService`, text blocks, photo templates.
    - `Modules/Editor`: `VariationEditor`, `InventoryWriter`, `ListingEditModel`.
    - `Modules/Orders`: `OrderColors`, `OrdersModule`.
    - `Components/Layout`: `MainLayout`, `NavMenu`, `AppLayout`, `AppNav`.
    - `Components/Pages`: `Shops`, `Listings`, `Templates`, `Admin/Settings`, `Admin/OrderColors`.
  - `EtsyShopManager.Tests`: xUnit tests for services, navigation, design rules, and integration.

## Feature Inventory
Every feature from the Survey phase appears here with its assigned milestone.
| # | Feature | Description | Milestone | Source |
|---|---------|-------------|-----------|--------|
| F1 | Git Feature Branch Setup | Create feature branch from `origin/master` and verify baseline build | M1 | Survey baseline |
| F2 | Variation Template Option Removal (Logic) | Detecting removed template options via `AppliedOptions`, calling `VariationEditor.RemoveValue`, preserving custom values, handling zero-value and already-removed edge cases | M2 | R1 |
| F3 | Variation Template Option Removal (UI) | Update `VariationListPage.razor` UI copy and pending changes description ("Removes <option>") | M2 | R1 |
| F4 | Variation Template Pricing Strategies | "Take highest price", "Take lowest price", and "Pick per listing" pricing algorithms for 1- and 2-variation products | M2 | R2 |
| F5 | Variation Template Pricing Dialog & UI | UI dialog / picker for selecting pricing strategy or entering per-listing prices when adding new options to listings with per-variation pricing | M2 | R2 |
| F6 | Role System Simplification | Replace `Admin`/`Editor`/`Viewer` with `Admin` and `User` in `Roles.cs`, remove `Editor`/`Viewer`, migrate legacy accounts on startup | M3 | R3 |
| F7 | Anonymous User Access | Unauthenticated access lands directly into app with `User` role; remove `[Authorize]` from public pages | M3 | R3 |
| F8 | Top App Bar Sign In / Sign Out | Display "Sign in" button when unauthenticated; display username + "Sign out" button when signed in as Admin | M3 | R3 |
| F9 | Read-Only Enforcement for User | Hide/disable mutating controls on viewable pages for non-Admin; lock down edit/clone/write routes to `Admin` | M3 | R3 |
| F10 | Move Order Colors to Top-Level | Move `Order colors` from `NavGroups.Settings` to top-level sidebar; readable by `User`, editable by `Admin` | M3 | R3 |
| F11 | Move Shops to Settings | Move `Shops` into `NavGroups.Settings` and require `Admin` | M3 | R3 |
| F12 | Settings Category Lockdown | Hide entire `Settings` sidebar category unless signed in as `Admin`; all settings pages require `Admin` | M3 | R3, R4 |
| F13 | Navigation Tests Update | Update `NavigationTests.cs` to reflect new top-level and Settings navigation structure | M3 | R3, R4 |
| F14 | Comprehensive E2E & Unit Test Suite | Opaque-box E2E test suite covering Tiers 1-4 across all requirements, passing 100% | M4 | R1-R5 |
| F15 | Adversarial & Design Rules Verification | Verify `DesignRulesTests`, adversarial edge cases, and run Forensic Integrity Audit | M4 | R5 |
| F16 | Delivery: Git Push & Gitea Pull Request | Push feature branch to `origin` and open a Pull Request to `master` on Gitea | M5 | R5 |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| M1 | Branch Setup | Create feature branch `feature/variation-roles-enhancements` from `origin/master`, verify build baseline | none | DONE |
| M2 | Variation Templates (R1 + R2) | Option removal (F2, F3) and Pricing choices (F4, F5) in `VariationLists.cs`, `VariationEditor.cs`, UI dialog, and tests | M1 | DONE |
| M3 | Roles & Navigation (R3 + R4) | Role simplification (F6), anonymous access (F7, F8, F9), navigation & settings lockdown (F10, F11, F12, F13) | M1, M2 | DONE |
| M4 | E2E Integration & Audit | Pass 100% tests (F14), DesignRulesTests, Challenger & Forensic Auditor checks (F15) | M2, M3 | DONE |
| M5 | Delivery & PR Creation | Push feature branch to origin, open Gitea PR to `master`, report to Sentinel (F16) | M4 | READY |

## Interface Contracts
### `VariationListService` ↔ `ListingEditor` / `VariationEditor`
- `VariationPricingStrategy`: Enum `TakeHighest`, `TakeLowest`, `PickPerListing`.
- `ListingOptionPriceOverride`: DTO `{ int ListingId, string ListingTitle, decimal Price }`.
- `Plan(VariationList list, ListingVariationList link, ManagedListingState managed, ...)`:
  - Detects options in `link.AppliedOptions` missing from `list.Options`.
  - Emits `"Removes <option>"` in `use.Changes`.
  - Removes products via `VariationEditor.RemoveValue`.
  - Preserves custom unlinked options.
  - Handles edge cases: option already removed by hand (note added, unlinked from AppliedOptions), last remaining value (note added, retained in AppliedOptions).
- `ApplyAsync(int listId, string user, VariationPricingStrategy strategy = VariationPricingStrategy.TakeHighest, IReadOnlyDictionary<int, decimal>? customPrices = null)`:
  - Applies template changes and updates draft inventories and `AppliedOptions`.
- `ListingsNeedingPricingAsync(int listId)`:
  - Returns listings linked to `listId` that have per-variation pricing on the linked property and are adding new options.

### Auth & Roles
- `Roles.Admin = "Admin"`, `Roles.User = "User"`, `Roles.All = [Admin, User]`.
- Public Pages: `@attribute [Authorize]` removed. Unauthenticated visitors are treated as `User`.
- Admin-Only Pages: `@attribute [Authorize(Roles = Roles.Admin)]`.
- Mutating UI components: wrapped in `<AuthorizeView Roles="@Roles.Admin">` or guarded with `_isAdmin`.

### Navigation Items
- Top-level: `["Orders", "Listings", "Templates", "Lookups", "Order colors"]`.
- Settings (`NavGroups.Settings`): `["General", "Shops", "Jobs", "Users", "Audit log", "API keys", "Etsy writes", "Photo order test"]` — all require `Roles.Admin`.

## Code Layout
- `src/EtsyShopManager.Core/Domain/VariationList.cs`: Entities and JSON converters.
- `src/EtsyShopManager.Core/Modules/IModule.cs`: `Roles` static class.
- `src/EtsyShopManager.Web/Modules/Templates/VariationLists.cs`: `VariationListService`, `ApplyVariationTemplateDialog.razor`.
- `src/EtsyShopManager.Web/Modules/Editor/VariationEditor.cs`: Inventory variation editing.
- `src/EtsyShopManager.Web/Components/Layout/MainLayout.razor`, `NavMenu.razor`: App bar & navigation.
- `src/EtsyShopManager.Web/Components/Pages/Shops.razor`: Shops page.
- `src/EtsyShopManager.Web/Components/Pages/Admin/OrderColors.razor`: Order colors page.
- `tests/EtsyShopManager.Tests/`: Unit tests, `NavigationTests.cs`, `DesignRulesTests.cs`, and E2E tests.
