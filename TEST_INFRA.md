# Test Infrastructure & Strategy: Etsy Shop Manager Enhancement

## 1. Test Philosophy
- **Opaque-Box & Requirement-Driven**: Tests are designed around behavioral specifications and interface contracts rather than internal implementation details. Assertions test observable outcomes: database state, returned DTOs, generated JSON drafts, HTTP status codes, and authorization policies.
- **Independence & Isolation**: Every test is self-contained. State is initialized per test using isolated in-memory databases (`TestDb` with actual EF Core SQLite migrations applied) and scoped dependency injection containers. Tests never rely on execution order or shared state.
- **Authoritative Expected Outputs**: Expected outputs are derived directly from the canonical requirements in `ORIGINAL_REQUEST.md` and the interface contracts in `PROJECT.md`.
- **Test Integrity**: Facade tests, tautological tests, and dummy assertions are strictly forbidden. All tests exercise real domain entities, real services, and real repository logic.
- **Adversarial & Edge Case Coverage**: Rigorous boundary stress testing includes zero-length collections, boundary limits (e.g. 70-option Etsy ceiling, 80-char name limits), missing options, hand-modified listings, role escalation attempts, and unauthenticated access checks.

---

## 2. Feature Inventory

The test suite provides end-to-end verification across all 16 features from `PROJECT.md` corresponding to requirements R1 through R5:

| Feature ID | Name | Requirement | Description |
|------------|------|-------------|-------------|
| **F1** | Feature Branch Setup | R5 | Baseline verification of project build, EF migrations, and git branch isolation. |
| **F2** | Variation Template Option Removal (Logic) | R1 | Pure planning and state transition logic: detecting removed template options via `AppliedOptions`, calling `VariationEditor.RemoveValue`, preserving custom values, and handling edge cases (hand-removed options, single remaining value). |
| **F3** | Variation Template Option Removal (UI & Notes) | R1 | Verification of pending change notes ("Removes <option>"), explanatory notes, and use status. |
| **F4** | Variation Template Pricing Strategies | R2 | Core pricing algorithms: `TakeHighest`, `TakeLowest`, and `PickPerListing` across 1-variation and 2-variation listings without altering existing prices. |
| **F5** | Variation Template Pricing Dialog & Workflow | R2 | Detection of listings needing pricing decisions and execution of `ApplyAsync` with specified strategies and overrides. |
| **F6** | Role System Simplification | R3 | Clean reduction of the role system to `Roles.Admin` and `Roles.User`; elimination of legacy `Editor` and `Viewer` roles; migration of existing accounts. |
| **F7** | Anonymous User Access | R3 | Unauthenticated visitors land in the application with `User` privileges without being redirected to `/account/login`. |
| **F8** | Top App Bar Sign In / Sign Out | R3 | Dynamic authentication state display: "Sign in" button for anonymous `User`, username and "Sign out" button for `Admin`. |
| **F9** | Read-Only Enforcement for User | R3 | Complete lockdown of mutating actions (create, edit, save, delete, apply, push, clone, import, sync) for non-Admin users; route protection on write endpoints. |
| **F10** | Move Order Colors to Top-Level | R3 | Elevation of Order Colors to the top-level navigation bar (readable by `User`, editable by `Admin`). |
| **F11** | Move Shops to Settings | R4 | Re-parenting of Shops page into `NavGroups.Settings`, requiring `Roles.Admin`. |
| **F12** | Settings Category Lockdown | R3, R4 | Complete hiding of `NavGroups.Settings` and all child pages (`General`, `Shops`, `Jobs`, `Users`, `Audit log`, `API keys`, `Etsy writes`, `Photo order test`) from non-Admin users. |
| **F13** | Navigation Tests Synchronization | R3, R4 | Validation of top-level and grouped navigation items and their role restrictions. |
| **F14** | Comprehensive E2E & Unit Suite | R1-R5 | 100% pass rate across the full xUnit test suite covering Tiers 1 through 4. |
| **F15** | Adversarial & Design Rules Verification | R5 | Conformance to `docs/DESIGN.md` (via `DesignRulesTests`) and adversarial robustness checks. |
| **F16** | Delivery & Pull Request | R5 | Verification of branch integrity and PR readiness. |

---

## 3. Test Architecture & Tooling

### Test Execution Commands
- **Full Build & Test**:
  ```powershell
  pwsh scripts/build.ps1
  ```
- **xUnit Test Execution via .NET CLI**:
  ```powershell
  dotnet test EtsyShopManager.slnx
  ```
- **Targeted Requirement E2E Suite**:
  ```powershell
  dotnet test EtsyShopManager.slnx --filter "FullyQualifiedName~RequirementE2ETests"
  ```
- **Individual Requirement Areas**:
  ```powershell
  dotnet test EtsyShopManager.slnx --filter "FullyQualifiedName~VariationTemplateOptionRemoval"
  dotnet test EtsyShopManager.slnx --filter "FullyQualifiedName~VariationTemplatePricing"
  dotnet test EtsyShopManager.slnx --filter "FullyQualifiedName~RoleSimplificationAndAccess"
  dotnet test EtsyShopManager.slnx --filter "FullyQualifiedName~NavigationAndSettingsLockdown"
  ```

### Fixtures and Test Primitives
- **`TestDb`**: Lightweight in-memory SQLite context factory initialized with EF Core migrations (`EnsureCreatedAsync` / migrations pipeline). Guarantees schema consistency with production SQLite database.
- **`FakeEtsyShop`**: High-fidelity in-memory HTTP and Etsy API simulator capturing all write payloads (`w.Path`, `w.Method`, `w.Body`) and simulating realistic listings, receipts, and inventory schemas.
- **`ImmediateTimeProvider`**: Deterministic virtual time provider eliminating race conditions and delays in background tasks and audit timestamps.
- **`TestAuth` / `ClaimsPrincipal` Helpers**: Fixtures providing simulated `Anonymous` (unauthenticated), `User` role, and `Admin` role security contexts.

---

## 4. 4-Tier Test Strategy

### Tier 1: Feature Coverage (>= 5 Tests per Requirement Area)

#### Area R1: Variation Template Option Removal
1. `T1_R1_01_OptionRemoval_EmitsRemovesChangeDescription`: Removing an option from `VariationList` produces `"Removes <option>"` in `VariationListPlan.Changes` for linked listings that had it in `AppliedOptions`.
2. `T1_R1_02_OptionRemoval_RemovesProductFromDraftInventory`: Applying the template deletes the corresponding product entries from the listing's draft inventory JSON.
3. `T1_R1_03_OptionRemoval_PreservesListingSpecificCustomValues`: Values present on the listing that were never part of the template's `AppliedOptions` remain untouched in the inventory.
4. `T1_R1_04_OptionRemoval_UpdatesAppliedOptionsState`: After applying the template, `ListingVariationList.AppliedOptions` no longer contains the removed option.
5. `T1_R1_05_OptionRemoval_StatusTransitionsToUpToDate`: After applying the removal, `UsesAsync` reports `VariationListUseStatus.UpToDate` with no remaining pending changes for that option.

#### Area R2: Variation Template Pricing Strategies
1. `T1_R2_01_Pricing_TakeHighest_SingleVariation`: On a single-variation listing, newly added options adopt the highest existing option price.
2. `T1_R2_02_Pricing_TakeLowest_SingleVariation`: On a single-variation listing, newly added options adopt the lowest existing option price.
3. `T1_R2_03_Pricing_TakeHighest_TwoVariations`: On a two-variation listing where price varies by both, newly added options adopt the highest existing price within each specific secondary variation combination.
4. `T1_R2_04_Pricing_TakeLowest_TwoVariations`: On a two-variation listing where price varies by both, newly added options adopt the lowest existing price within each secondary variation combination.
5. `T1_R2_05_Pricing_PickPerListing_AppliesCustomPrice`: When `PickPerListing` is chosen, the user-supplied custom price for each listing is assigned to newly added options.
6. `T1_R2_06_Pricing_NeverAltersExistingOptionPrices`: Applying new options with any strategy strictly preserves existing product prices on the listing.

#### Area R3: Role System Simplification & Read-Only Views
1. `T1_R3_01_Roles_OnlyAdminAndUserDefined`: `Roles.All` contains exactly `["Admin", "User"]`; `Roles.Editor` and `Roles.Viewer` are eradicated.
2. `T1_R3_02_AnonymousAccess_PublicPagesViewableWithoutAuth`: Unauthenticated visitor can access Dashboard, Orders, Listings, Templates, and Order Colors without redirect to `/account/login`.
3. `T1_R3_03_AppBar_DisplaysSignInForAnonymous`: Unauthenticated requests render a "Sign in" button in the top app bar; authenticated Admin renders username and "Sign out".
4. `T1_R3_04_ReadOnly_MutatingControlsHiddenForUser`: Non-Admin users do not see create/edit/save/delete/apply/push/clone buttons on Listings, Templates, and Order Colors.
5. `T1_R3_05_Authorization_WriteRoutesRequireAdmin`: All write routes (e.g. `/listings/{id}/edit`, `/listings/{id}/clone`, `/templates/*/new`, and mutating API endpoints) require `Roles.Admin`.

#### Area R4: Navigation & Settings Restructuring
1. `T1_R4_01_Nav_ShopsMovedToSettings`: `Shops` nav item is categorized under `NavGroups.Settings` with `Roles.Admin` requirement.
2. `T1_R4_02_Nav_OrderColorsAtTopLevel`: `Order colors` nav item has `Group = null` (top-level) and allows viewing by `User`.
3. `T1_R4_03_Nav_SettingsCategoryHiddenForNonAdmin`: Entire `Settings` sidebar category is hidden when user is not signed in as `Admin`.
4. `T1_R4_04_Nav_AllSettingsPagesRequireAdmin`: Every item within `NavGroups.Settings` (`General`, `Shops`, `Jobs`, `Users`, `Audit log`, `API keys`, `Etsy writes`, `Photo order test`) requires `Roles.Admin`.
5. `T1_R4_05_Nav_TopLevelItemsMatchContract`: Top-level sidebar items strictly match `["Orders", "Listings", "Templates", "Lookups", "Order colors"]` (and Dashboard where applicable).

---

### Tier 2: Boundary & Corner Cases (>= 5 Tests per Requirement Area)

#### Area R1: Boundary Cases
1. `T2_R1_01_HandRemovedOption_GracefulNoteAndCleanup`: When an option was already removed from the listing by hand prior to template removal, the plan adds an explanatory note and cleanly unlinks it without error.
2. `T2_R1_02_LastRemainingValue_RefusalWithNote`: Attempting to remove the last remaining value of a variation is prevented (Etsy requires >= 1 value); a clear note is added and the option is retained in `AppliedOptions`.
3. `T2_R1_03_CaseInsensitiveOptionRemoval`: Option name matching for removal is case-insensitive ("red" matches "Red").
4. `T2_R1_04_PartialLinkSubsetRemoval`: Multiple listings linked to the same template with differing historical subsets of `AppliedOptions` correctly remove options only where previously applied.
5. `T2_R1_05_RapidRemoveAndReAddCycle`: Removing an option, applying, then re-adding the option with a new ID cleanly transitions the listing inventory back.

#### Area R2: Boundary Cases
1. `T2_R2_01_UniformPricing_HighestEqualsLowest`: On a listing where all options have the same price, `TakeHighest` and `TakeLowest` produce identical correct prices.
2. `T2_R2_02_MultipleNewOptionsSimultaneous`: Adding multiple new options at once with `TakeHighest` correctly assigns the top price to all new options across combinations.
3. `T2_R2_03_DecimalPrecisionIntegrity`: Pricing strategies maintain exact cent values (e.g. $19.99, $0.50) without floating-point truncation or rounding drift.
4. `T2_R2_04_NonVaryingPriceProperty`: When a listing has variations but price does NOT vary by that property (`price_on_property` empty), new options adopt the single shared listing price.
5. `T2_R2_05_PickPerListing_MissingListingFallback`: `PickPerListing` with partial listing dictionary throws informative error or falls back gracefully without corrupting state.

#### Area R3: Boundary Cases
1. `T2_R3_01_DirectUrlTampering_EditPageBlocked`: Unauthenticated visitor attempting direct HTTP GET navigation to `/listings/123/edit` or `/listings/123/clone` receives 401/403 or redirect to login.
2. `T2_R3_02_LegacyAccountMigrationOnStartup`: Database accounts with legacy roles `Editor` or `Viewer` are migrated to `User` without application crash.
3. `T2_R3_03_AnonymousPostEndpointRejection`: Publicly readable endpoints immediately reject POST/PUT/DELETE requests from unauthenticated clients.
4. `T2_R3_04_PublicPackingSlipPrintAllowed`: Anonymous users can access print packing slips and gift receipts while blocked from modifying order data.
5. `T2_R3_05_EmptyOrWhitespaceUsernameHandling`: Auth display handles null or empty identity names safely without rendering blank pills.

#### Area R4: Boundary Cases
1. `T2_R4_01_DirectUrlTampering_ShopsPageBlocked`: Non-admin navigating directly to `/shops` is blocked.
2. `T2_R4_02_DirectUrlTampering_SettingsSubpagesBlocked`: Non-admin navigating directly to `/admin/settings`, `/jobs`, `/admin/users` is blocked.
3. `T2_R4_03_OrderColors_NonAdminCannotModify`: Non-admin viewing `/admin/order-colors` (or `/order-colors`) cannot trigger color edit or re-order commands.
4. `T2_R4_04_EmptySettingsGroupSuppression`: When non-admin views the sidebar, the "Settings" group header itself is completely suppressed rather than showing an empty accordion.
5. `T2_R4_05_CaseInsensitiveRoleAuthorization`: Authorization checks handle role name comparisons consistently (`Admin` vs `admin`).

---

### Tier 3: Cross-Feature Combinations (Pairwise Verification)

- **R1 + R2 (Simultaneous Removal & Addition with Pricing)**:
  - `T3_Pairwise_01_RemoveAndAddOptionWithTakeHighest`: Template updates to retire/remove "Blue" and add "Gold". Plan generates `"Removes Blue"` and `"Adds Gold"`. Applying with `TakeHighest` assigns highest existing price to "Gold" while deleting "Blue" products and preserving "Red".
  - `T3_Pairwise_02_RemoveAndAddOptionWithTakeLowest_TwoVariations`: On a Size x Color listing, "Small" is removed and "XXL" is added with `TakeLowest`. Every color combination for "XXL" gets the lowest size price for that color; "Small" products are deleted.
- **R1 + R3 (Option Removal Viewed as Anonymous vs Applied as Admin)**:
  - `T3_Pairwise_03_AnonymousViewsRemovalChanges_AdminApplies`: Unauthenticated visitor sees template with pending `"Removes <option>"` changes in read-only mode (no "Apply" button). Admin user sees "Apply" button, executes apply, and state updates.
- **R2 + R3 (Pricing Dialog Workflow Across Role Boundaries)**:
  - `T3_Pairwise_04_AnonymousCannotInvokePricingApply`: Anonymous client cannot execute `ApplyAsync` or submit pricing dialog parameters. Admin client successfully passes `VariationPricingStrategy.PickPerListing`.
- **R3 + R4 (Complete Public vs Admin Navigation and Sidebar Inspection)**:
  - `T3_Pairwise_05_PublicSidebarVersusAdminSidebar`: Render navigation for anonymous user: only top-level items appear (`Orders`, `Listings`, `Templates`, `Lookups`, `Order colors`); Settings and Shops are completely omitted. Render navigation for Admin: Settings group expands to show `Shops`, `General`, `Jobs`, `Users`, etc.
- **R1 + R4 (Template Option Removal on Shop-Restricted Listings)**:
  - `T3_Pairwise_06_TemplateRemovalAcrossMultipleShops`: Template linked to listings across Shop 1 and Shop 2 (managed under Settings -> Shops) executes option removal uniformly across all linked listings regardless of shop ownership.

---

### Tier 4: Real-World Scenarios (End-to-End User Journeys)

1. `T4_Scenario_01_SeasonalColorCatalogUpdate`:
   - Step 1: Admin manages "PLA Colors" template linked to 3 active listings (Single-variation and Multi-variation).
   - Step 2: Admin removes discontinued "Summer Coral" and introduces "Autumn Amber".
   - Step 3: Admin selects `TakeHighest` pricing strategy.
   - Step 4: System creates listing drafts removing all "Summer Coral" products and pricing "Autumn Amber" at top existing rates ($24.99 and $34.99 respectively).
   - Step 5: Listing-specific custom swirl colors remain untouched.
   - Step 6: Review preview confirms `CanPush = true`.

2. `T4_Scenario_02_WorkshopFloorTerminalWorkflow`:
   - Step 1: Workshop terminal accesses app without login.
   - Step 2: Operator views open orders and packing slips.
   - Step 3: Operator views Listing details and Variation Templates to check available options.
   - Step 4: Operator navigates to Order Colors to check packaging tags.
   - Step 5: Operator attempts to modify a variation template or delete a color; action is forbidden.
   - Step 6: Store Manager logs in as Admin; Settings and Shops become available, edit controls activate, and modifications can be performed.

3. `T4_Scenario_03_MultiVariationCatalogExpansionWithPickPerListing`:
   - Step 1: Shop has listings with Size (S, M, L) and Finish (Matte, Glossy) where prices vary across both dimensions.
   - Step 2: "Finish" template adds "Satin".
   - Step 3: Manager uses `PickPerListing` to set specific promotional prices on key listings.
   - Step 4: Matrix expansion creates all S/M/L combinations for "Satin" with the exact specified pricing.
   - Step 5: Drafts validate without drift blockers.

4. `T4_Scenario_04_ResilientTemplateApplicationWithHandDrift`:
   - Step 1: Listing linked to "Sizes" template has "XXS" removed directly on the listing by hand.
   - Step 2: Template manager later removes "XXS" from the global template.
   - Step 3: System detects hand-removal, emits non-fatal note: `"XXS was removed from this listing by hand, so the template leaves it out."`.
   - Step 4: AppliedOptions is cleanly synchronized without unhandled exceptions or data loss.

---

## 5. Verification Protocol & Quality Gates
- **Zero Test Failures**: `dotnet test EtsyShopManager.slnx` must pass 100%.
- **Zero Design Rule Violations**: `DesignRulesTests` must pass without regressions.
- **Database Schema Validation**: `scripts/build.ps1` confirms `dotnet ef migrations has-pending-model-changes` returns zero pending model changes.
- **Forensic Audit**: The Challenger and Forensic Auditor independently verify test authenticity, confirming zero mocked shortcuts or facade assertions.
