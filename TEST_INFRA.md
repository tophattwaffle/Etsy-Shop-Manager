# E2E Test Infra: Etsy Shop Manager

> **Historical.** Working notes from the R1–R7 expansion (merged in PR #104 and #105), kept for reference and not
> updated. Current design and status: [docs/PLAN.md](docs/PLAN.md); what is left to do: [docs/BACKLOG.md](docs/BACKLOG.md).

## Test Philosophy
- Opaque-box, requirement-driven. Derived strictly from `ORIGINAL_REQUEST.md` and user-facing specifications without coupling to internal class implementations.
- Verification exclusively against `FakeEtsyShop` and in-memory test databases (`TestDb`). Zero writes to live Etsy accounts.
- Methodology: Category-Partition + Boundary Value Analysis (BVA) + Pairwise Combinatorial Testing + Real-World Workload Testing across Tiers 1–4.

## Feature Inventory
| # | Feature | Source (requirement) | Tier 1 | Tier 2 | Tier 3 |
|---|---------|---------------------|:------:|:------:|:------:|
| 1 | R1: Per-Shop Defaults | ORIGINAL_REQUEST §R1 | 5 | 5 | ✓ |
| 2 | R1: New Listing Draft Creation | ORIGINAL_REQUEST §R1 | 5 | 5 | ✓ |
| 3 | R2: Database Snapshot & Restorable Backup | ORIGINAL_REQUEST §R2 | 5 | 5 | ✓ |
| 4 | R2: Scheduled Backup & Retention Pruning | ORIGINAL_REQUEST §R2 | 5 | 5 | ✓ |
| 5 | R2: Portable Listing Export .zip | ORIGINAL_REQUEST §R2 | 5 | 5 | ✓ |
| 6 | R3: Built-in Placeholders (Shop & Listing) | ORIGINAL_REQUEST §R3 | 5 | 5 | ✓ |
| 7 | R3: Nested Placeholders & Template Blocks | ORIGINAL_REQUEST §R3 | 5 | 5 | ✓ |
| 8 | R4: Variation Value Reordering | ORIGINAL_REQUEST §R4 | 5 | 5 | ✓ |
| 9 | R4: SKU Pattern Generation | ORIGINAL_REQUEST §R4 | 5 | 5 | ✓ |
| 10 | R5: Listing Video Import & Local Media Store | ORIGINAL_REQUEST §R5 | 5 | 5 | ✓ |
| 11 | R5: Video Editor, Diff Preview & Push | ORIGINAL_REQUEST §R5 | 5 | 5 | ✓ |
| 12 | R6: Search & Filter on Template Tables | ORIGINAL_REQUEST §R6 | 5 | 5 | ✓ |
| 13 | R6: Filter-Scoped Bulk Selection | ORIGINAL_REQUEST §R6 | 5 | 5 | ✓ |

## Test Architecture
- **Test Runner:** xUnit via `dotnet test EtsyShopManager.slnx` or `pwsh scripts/build.ps1`.
- **Pass/Fail Semantics:** Exit code 0, all assertions pass, zero test skips or failures.
- **Directory Layout:**
  - `tests/EtsyShopManager.Tests/`
    - `E2E/` (Requirement-driven test suites)
      - `NewListingE2ETests.cs`
      - `BackupAndExportE2ETests.cs`
      - `PlaceholderE2ETests.cs`
      - `VariationAndSkuPatternE2ETests.cs`
      - `ListingVideoE2ETests.cs`
      - `TemplateFilterE2ETests.cs`

## Real-World Application Scenarios (Tier 4)
| # | Scenario | Features Exercised | Complexity |
|---|----------|--------------------|------------|
| 1 | Complete Seller Onboarding: Connect shop, set defaults, create draft with placeholders & video, verify draft & preview | R1, R3, R5 | High |
| 2 | Catalog Rebranding: Update template text with shop placeholders, apply to filtered listings, verify rendered outputs | R3, R6 | Medium |
| 3 | Seasonal Product Line Update: Reorder variation options, apply new SKU pattern `{sku}-{size}-{color}`, push inventory | R4 | High |
| 4 | Full Disaster Recovery & Migration: Scheduled backup -> extract DB + media + keys -> restore on clean instance -> verify all data | R2 | High |
| 5 | Cross-Shop Listing Migration with Full Media: Clone listing with variations, video, and template text to second shop using target defaults | R1, R3, R4, R5 | High |
| 6 | Bulk Template Management with Active Filters: Filter candidate listings by status and shop, select filtered only, link template | R6 | Medium |

## Coverage Thresholds
- Tier 1: ≥5 per feature (Total: 65 tests)
- Tier 2: ≥5 per feature boundary/corner (Total: 65 tests)
- Tier 3: Pairwise coverage across feature interactions (Total: ≥13 tests)
- Tier 4: ≥6 realistic application scenarios
- Total Planned: ≥149 automated test cases
