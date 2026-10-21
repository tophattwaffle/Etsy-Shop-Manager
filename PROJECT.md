# Project: Etsy Shop Manager — Feature Expansion (R1–R7)

## Architecture
- **Language & Runtime:** C# 13 / .NET 10
- **Frameworks:** Blazor Web App (Interactive Server), MudBlazor, ASP.NET Core
- **Database:** SQLite via EF Core with Write-Ahead Logging (WAL)
- **External API:** Etsy Open API v3 (OAuth 2.0 + PKCE, strictly isolated from production via `FakeEtsyShop` during test/dev)
- **Hosting / Staging:** Windows Service behind reverse proxy; dedicated staging slots managed by `scripts/deploy-staging.ps1`

## Feature Inventory
| # | Feature | Description | Milestone | Source |
|---|---------|-------------|-----------|--------|
| 1 | M1.1 | Add `ListingVideo` entity and `Listing.Videos` collection | M1 | Survey R5 |
| 2 | M1.2 | Add `Listing.SkuPattern` property | M1 | Survey R4 |
| 3 | M1.3 | Consolidated EF Core Migration `ListingVideosAndSkuPatterns` | M1 | Survey R7 |
| 4 | M1.4 | Bump `ManagedListingState.CurrentVersion` from 5 to 6 with `ManagedVideo` | M1 | Survey R5 |
| 5 | M1.5 | Add `StorageOptions.VideosDirectory` and directory initialization | M1 | Survey R2/R5 |
| 6 | R1.1 | `ShopDefaults` record, `ShopDefaultsService`, and `ISettingsStore` persistence | M2 | Survey R1 |
| 7 | R1.2 | `ShopDefaultsPanel.razor` UI for per-shop default profiles on Shops page | M2 | Survey R1 |
| 8 | R1.3 | `ListingCloner.DefaultsAsync` pre-fill from `ShopDefaults` | M2 | Survey R1 |
| 9 | R1.4 | Guided `NewListing.razor` wizard (`/listings/new`) with `ListingTextRules` validation | M2 | Survey R1 |
| 10 | R1.5 | Draft listing creation via `FakeEtsyShop`, local import, and redirect to editor | M2 | Survey R1 |
| 11 | R2.1 | Online SQLite database snapshot via `Microsoft.Data.Sqlite.SqliteConnection.BackupDatabase` | M3 | Survey R2 |
| 12 | R2.2 | Restorable backup `.zip` generator (DB snapshot, media files, DPAPI keys, manifest) | M3 | Survey R2 |
| 13 | R2.3 | Scheduled automatic backups via `CreateBackupJob : IJobHandler` and `RecurringJobDefinition` | M3 | Survey R2 |
| 14 | R2.4 | Backup retention pruner (retention days and max count, protecting newest and deploy zips) | M3 | Survey R2 |
| 15 | R2.5 | Admin/Settings UI (`BackupSettingsForm.razor`) for schedule, retention, trigger, and backup table | M3 | Survey R2 |
| 16 | R2.6 | Portable listing export `.zip` generator (structured JSON, rendered text, media by shop/listing) | M3 | Survey R2 |
| 17 | R3.1 | `PlaceholderEngine` multi-pass token resolution for direct and nested placeholders | M4 | Survey R3 |
| 18 | R3.2 | Built-in placeholders: `{{shop.name}}` and `{{shop.url}}` | M4 | Survey R3 |
| 19 | R3.3 | Built-in placeholders: `{{listing.title}}` and `{{listing.sku}}` | M4 | Survey R3 |
| 20 | R3.4 | Built-in placeholders: `{{listing.materials}}`/`{{listing.material}}` and `{{listing.tags}}` | M4 | Survey R3 |
| 21 | R3.5 | Built-in placeholders: `{{listing.dimensions}}`, size, and weight | M4 | Survey R3 |
| 22 | R3.6 | Cyclic reference detection for nested text templates | M4 | Survey R3 |
| 23 | R3.7 | Dynamic re-rendering of draft description in editor when referenced listing fields change | M4 | Survey R3 |
| 24 | R3.8 | Preview blocker for unresolved placeholders before push | M4 | Survey R3 |
| 25 | R4.1 | Explicit `value_order` in managed inventory JSON | M5 | Survey R4 |
| 26 | R4.2 | Product ordering in `InventoryWriter.FromLocal` preserving existing offering IDs, prices, stock | M5 | Survey R4 |
| 27 | R4.3 | UI Move Up / Move Down value reordering controls in `VariationsEditor.razor` | M5 | Survey R4 |
| 28 | R4.4 | Variation template option reordering in `VariationListService` | M5 | Survey R4 |
| 29 | R4.5 | `SkuPatternResolver` supporting `{listing.sku}-{size}-{color}` patterns | M5 | Survey R4 |
| 30 | R4.6 | Automatic SKU generation on value add and template application | M5 | Survey R4 |
| 31 | R5.1 | Etsy v3 API video endpoints (`getListingVideos`, `uploadListingVideo`, `deleteListingVideo`, `ListingIncludes`) | M6 | Survey R5 |
| 32 | R5.2 | `VideoStore` with byte-range partial content streaming (`enableRangeProcessing: true`) | M6 | Survey R5 |
| 33 | R5.3 | `ListingFields.Video` integration in `ListingEditModel` for drift and pending diff | M6 | Survey R5 |
| 34 | R5.4 | Video preview in listing details and `VideoEditor.razor` in listing editor | M6 | Survey R5 |
| 35 | R5.5 | Video upload, replace, delete push workflow in `PushListingJob` | M6 | Survey R5 |
| 36 | R5.6 | Video copying support in `ListingCloner` | M6 | Survey R5 |
| 37 | R5.7 | `FakeEtsyShop` video endpoint mocks and CDN streaming | M6 | Survey R5 |
| 38 | R6.1 | Augmented candidate/use records with `ShopId`, `EtsyListingId`, `State`, `IsMissingOnEtsy`, `Skus`, `Tags` | M7 | Survey R6 |
| 39 | R6.2 | Reusable in-memory filter matching helper (`TemplateFilterMatcher`) | M7 | Survey R6 |
| 40 | R6.3 | Search, Shop, and Status filter controls on `TextBlockPage.razor` | M7 | Survey R6 |
| 41 | R6.4 | Search, Shop, and Status filter controls on `VariationListPage.razor` | M7 | Survey R6 |
| 42 | R6.5 | Search, Shop, and Status filter controls on `PhotoTemplatePage.razor` | M7 | Survey R6 |
| 43 | R6.6 | Bulk selection ("Select all" / header checkbox) scoped strictly to visible/filtered rows | M7 | Survey R6 |
| 44 | R7.1 | Requirement-driven E2E test suite (Tiers 1–4) verified against `FakeEtsyShop` | M8 | Survey R7 |
| 45 | R7.2 | Tier 5 Adversarial Coverage Hardening | M8 | Survey R7 |
| 46 | R7.3 | Complete build, migration check, and test suite pass via `pwsh scripts/build.ps1` | M8 | Survey R7 |
| 47 | R7.4 | Zero `DesignRulesTests` violations across all Razor components | M8 | Survey R7 |
| 48 | R7.5 | Feature branch staging deployment via `pwsh scripts/deploy-staging.ps1` | M8 | Survey R7 |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| M1 | Schema & Domain Foundation | Video entity/collection, SkuPattern property, EF migration, ManagedListingState v6, StorageOptions | None | DONE |
| M2 | R1: New Listing & Shop Defaults | Per-shop defaults, NewListing wizard, validation, draft creation & import | M1 | DONE |
| M3 | R2: Scheduled Backups & Export | Online SQLite backup, restorable zip, portable listing export, retention pruning, Admin UI | M1 | DONE |
| M4 | R3: Field Placeholders | Built-in placeholders, multi-pass engine, nested templates, dynamic editor sync | M1 | PLANNED |
| M5 | R4: Variation Reordering & SKU Patterns | Value order in inventory, InventoryWriter order fix, UI/template reordering, SkuPatternResolver | M1 | PLANNED |
| M6 | R5: Listing Videos | Etsy client video endpoints, VideoStore, editor UI, push/diff, cloner, FakeEtsyShop mocks | M1 | PLANNED |
| M7 | R6: Template Table Listing Filters | Filter controls (search, shop, status) & scoped bulk selection in text/variation/photo templates | M1 | PLANNED |
| M8 | Final Integration & Staging Delivery | Pass 100% E2E tests, Tier 5 hardening, build.ps1 exit 0, deploy-staging.ps1 | M2, M3, M4, M5, M6, M7 | PLANNED |

## Interface Contracts

### M1 (Domain & Schema) ↔ All Modules
- `ListingVideo`: `int Id, int ListingId, long EtsyVideoId, int? Height, int? Width, string? VideoState, string SourceUrl, string? SourceThumbUrl, string? FileName, string? ThumbFileName, long? ByteSize, DateTime? DownloadedAtUtc, string? DownloadError`
- `Listing.Videos`: `List<ListingVideo>` on `Listing` entity
- `Listing.SkuPattern`: `string?` on `Listing` entity
- `ManagedListingState.CurrentVersion = 6`: `ManagedVideo? Video` where `ManagedVideo(long EtsyVideoId, string VideoState, int? Width, int? Height)`
- `StorageOptions.VideosDirectory`: `Path.Combine(ResolvedRoot, "videos")`

### M2 (New Listing & Defaults) ↔ Modules / UI
- `ShopDefaults`: `record ShopDefaults(long? ShippingProfileId, long? ReadinessStateId, long? ReturnPolicyId, long? ShopSectionId, string? WhoMade = "i_did", string? WhenMade = "made_to_order", long? TaxonomyId = null)`
- `ShopDefaultsService`: `Task<ShopDefaults?> GetAsync(int shopId, CancellationToken ct)`, `Task SaveAsync(int shopId, ShopDefaults defaults, string? user, CancellationToken ct)`
- `ListingCreationService`: `(bool Valid, IReadOnlyList<string> Errors) Validate(NewListingModel model, EditorLookups lookups)`, `Task<int> CreateDraftAsync(NewListingModel model, string? user, CancellationToken ct)`

### M3 (Backups & Export) ↔ Infrastructure
- `BackupService`: `Task<string> CreateBackupAsync(string? label, CancellationToken ct)`, `Task<string> CreateExportAsync(CancellationToken ct)`, `Task<int> PruneBackupsAsync(int retentionDays, int maxCount, CancellationToken ct)`
- `CreateBackupJob`: `Type = "backups.create"`, recurring registration with `IntervalSettingKey = SettingKeys.BackupsScheduleIntervalHours`

### M4 (Placeholders) ↔ TextBlocks / Editor
- `PlaceholderEngine`: `(string Rendered, IReadOnlyList<string> Unresolved) Render(string source, PlaceholderContext context)`
- `PlaceholderContext`: `Shop Shop, JsonObject LocalState, IReadOnlyCollection<string> Skus, IReadOnlyDictionary<string, string> TextBlocks`

### M5 (Variation Reorder & SKUs) ↔ Variations / Cloner
- `InventoryWriter.FromLocal`: preserves product grid order matching `value_order`
- `SkuPatternResolver`: `string GenerateSku(string pattern, string baseSku, IReadOnlyDictionary<string, string> variationValues)`, `JsonObject ApplyPattern(JsonObject inventory, string pattern, string baseSku)`

### M6 (Videos) ↔ Etsy / Editor / Push
- `VideoStore`: `string Save(Stream content, string extension)`, `string PathFor(string fileName)`, `bool Exists(string fileName)`, `string ContentTypeFor(string fileName)`
- `EtsyApiClient`: `GetListingVideosAsync`, `UploadListingVideoAsync`, `DeleteListingVideoAsync`

### M7 (Filters) ↔ Template Pages
- `TemplateListingFilter`: `record TemplateListingFilter(int? ShopId, string? Text, string? Status)`
- `TemplateFilterMatcher`: `bool Matches(ITemplateListingRow row, TemplateListingFilter filter)`

## Code Layout
```
src/EtsyShopManager.Core/
  Domain/Listing.cs (ListingVideo, SkuPattern)
  Configuration/StorageOptions.cs (VideosDirectory)
  Listings/ManagedListingState.cs (v6, ManagedVideo)
src/EtsyShopManager.Etsy/
  EtsyApiClient.Listings.cs (ListingIncludes, GetListingVideosAsync)
  EtsyApiClient.Edit.cs (UploadListingVideoAsync, DeleteListingVideoAsync)
  Models/ListingModels.cs (EtsyListingVideo)
src/EtsyShopManager.Data/
  AppDbContext.cs (ListingVideos configuration)
  Migrations/ (Consolidated ListingVideosAndSkuPatterns migration)
src/EtsyShopManager.Web/
  Infrastructure/
    Storage/VideoStore.cs
    Backups/BackupService.cs
    Backups/CreateBackupJob.cs
  Modules/
    Shops/ShopDefaultsService.cs
    Listings/ListingCreationService.cs
    Templates/PlaceholderEngine.cs
    Editor/SkuPatternResolver.cs
    Editor/VideoEditor.razor
    Admin/BackupSettingsForm.razor
    Templates/TemplateFilterMatcher.cs
  Components/
    Pages/Listings/NewListing.razor
tests/EtsyShopManager.Tests/
  ListingTestSupport.cs (FakeEtsyShop video & draft support)
  NewListingTests.cs
  BackupServiceTests.cs
  PlaceholderEngineTests.cs
  VariationReorderAndSkuPatternTests.cs
  ListingVideoTests.cs
  TemplateFilterTests.cs
```
