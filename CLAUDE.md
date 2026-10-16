# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Self-hosted Etsy manager for two shops: .NET 10, Blazor Web App (Interactive Server, MudBlazor), SQLite via EF Core,
running as a Windows Service behind HAProxy (which terminates HTTPS). Everything must build and run on a Windows host;
code is C# wherever possible and the UI is web-based. Full design and phase plan: `docs/PLAN.md`. Install/first run:
`docs/SETUP.md`. CI/CD: `docs/DEPLOY.md`.

**This app is the system of record for listings.** Edits happen here; changes made on Etsy.com are detected and flagged
(drift), never silently overwritten. Phases 0–2 (foundation, import, orders + Home Assistant) are done; Phase 3 is the
editor and render → diff → push path.

## Commands

All scripts need PowerShell 7 (`pwsh`) and the .NET 10 SDK.

```powershell
pwsh scripts/build.ps1                    # tool restore, restore, build, EF model/migration check, tests (Release)
pwsh scripts/build.ps1 -SkipTests
dotnet test EtsyShopManager.slnx --filter "FullyQualifiedName~OrderTests"          # one test class
dotnet test EtsyShopManager.slnx --filter "FullyQualifiedName~Numeric_search_also_matches_SKUs"   # one test
pwsh scripts/add-migration.ps1 -Name <Change>   # after any EF model change
dotnet run --project src/EtsyShopManager.Web    # local run (http://0.0.0.0:5080 by default)
```

Home Assistant integration (Python, its own test suite), run from `homeassistant/`:

```bash
pip install pytest-homeassistant-custom-component
pytest
```

- `build.ps1` fails if the EF model changed without a migration (`dotnet ef migrations has-pending-model-changes`).
  Migrations are committed in `src/EtsyShopManager.Data/Migrations` and applied automatically on startup.
- `dotnet-ef` is a local tool (`.config/dotnet-tools.json`); package versions are central in `Directory.Packages.props`
  (bump there, never in a `.csproj`).
- Merges to `master` deploy automatically: `.gitea/workflows/deploy.yml` runs on a self-hosted runner on the Windows
  host (build.ps1 → self-contained win-x64 publish → `scripts/deploy.ps1`, which backs up DB + binaries, swaps,
  waits for `/health`, and rolls back on failure). `master` is protected; work goes on branches and is merged by PR.

## Architecture

```
src/EtsyShopManager.Core   Domain entities, job/event/module contracts, StorageOptions, ManagedListingState
src/EtsyShopManager.Etsy   Etsy v3 client: OAuth+PKCE, token service, rate limiter, retry handlers, API client (partial classes per area)
src/EtsyShopManager.Data   AppDbContext (SQLite + Identity), migrations
src/EtsyShopManager.Web    Host (Program.cs), Blazor UI, setup wizard, job processor, feature modules
tests/EtsyShopManager.Tests  xUnit
homeassistant/             HA custom integration `etsy_shop_manager` (HACS layout) + Lovelace card (www/)
ExistingHASS/              The old `etsyapp` integration and card, reference only
```

### Modules and events
Every feature is an `IModule` (`Core/Modules/IModule.cs`) that registers its own services, job handlers
(`services.AddJobHandler<T>()`), recurring jobs (`services.AddRecurringJob(...)`), endpoints and nav items. Modules are
listed in `builder.AddModules(...)` in `Program.cs` (Shops, Listings, Orders, Jobs, Admin; see
`Web/Modules/BuiltInModules.cs` for small examples). Cross-module reactions go through in-process domain events
(`Core/Events/DomainEvents.cs`: `ShopConnected`, `ShopNeedsReauth`, `OrderReceived`, …) handled by
`IDomainEventHandler<TEvent>`, rather than one module calling into another.

### Background jobs
All Etsy work runs through a persistent SQLite job queue (`Core/Jobs/JobContracts.cs`, `Web/Infrastructure/Jobs/`).
`JobProcessor` runs **one job at a time** (Etsy's per-key rate limit and SQLite's single writer are the bottlenecks)
and resets jobs left `Running` after a restart. Handlers implement `IJobHandler`, read `JobExecutionContext.GetPayload<T>()`,
and set `context.Result` to a short outcome ("2 new, 5 updated") shown on the Jobs page and in toasts. Throw
`RetryLaterException` to reschedule without using an attempt (rate limits), `JobPermanentFailureException` to fail
without retries; other exceptions retry with back-off. Use `DedupeKey` to avoid queuing duplicates. Recurring
intervals can be overridden at runtime through an app setting (`IntervalSettingKey`). `IJobEvents` is an in-process
feed of job changes that open Blazor pages subscribe to for live refresh instead of polling.

### Etsy client
One shared token-bucket `EtsyRateLimiter` for the whole app (limits are per API key and both shops share it); it
spaces requests evenly because bursting caused 429s. Every call sends `x-api-key: keystring:secret`. Tokens are per
shop, encrypted with Data Protection (machine DPAPI), refreshed on demand one refresh per shop at a time; a rejected
refresh token marks the shop as needing reconnection.

### Listings
Import (`Web/Modules/Listings/ListingImporter.cs` and jobs) is idempotent, matched by Etsy id, resumable page by page;
listings gone from Etsy are flagged, never deleted. Etsy reuses `listing_image_id`s on copied listings, so image ids
are unique per listing only. Photos go into a content-hashed image store (`ImageStore`). `ManagedListingState` is the
canonical form + hash of the fields the app manages (quantity, Etsy product/offering ids, stats and timestamps are
deliberately excluded); it is the basis for Phase 3 drift detection. Bump `ManagedListingState.CurrentVersion` when
its field set or normalisation changes.

### Orders and Home Assistant
`OrderPoller` polls receipts per shop (default every 5 min, set in Admin → Settings). `OrdersModule` exposes a
read-only API at `/api/ha/v1` protected by API keys (`esm_…`, `ApiKeyEndpointFilter`). `HaPayloadBuilder` produces
the exact attribute shape the old `etsyapp` sensor had, so `homeassistant/www/etsy-orders-card.js` works unchanged;
keep that payload backward compatible. Order images in the HA payload are Etsy's public CDN URLs. The repo copy of the
card is the source of truth; the HA dashboard loads it with `?v=<version>`, so bump the card version on every change.

### Hosting details
- Live data root defaults to `C:\ProgramData\EtsyShopManager` (DB, images, Data Protection keys, backups, logs).
  `StorageOptions.Validate()` refuses a data root inside Dropbox/OneDrive: the repo itself lives in Dropbox, the
  database must not.
- Forwarded headers are trusted only from `ReverseProxy:TrustedProxies`. `SetupGateMiddleware` sends everything to
  the first-run wizard until an Admin exists and setup is finished.
- Interactive Server without prerendering; sign-in and "create Admin" are static SSR because they set the auth cookie.
- Store dates as UTC `DateTime`, not `DateTimeOffset` (EF's SQLite provider can't compare/sort `DateTimeOffset` in SQL).
- Roles: Admin, Editor, Viewer (`Core/Modules/Roles`).

### UI design system
The UI is styled after Etsy's Shop Manager, dark mode by default. **Read `docs/DESIGN.md` before adding or changing
UI** and follow it: every page starts with `<PageHeader>`, panels are `Elevation="0" Outlined="true"`, colours come only
from `Components/Layout/AppTheme.cs` and the tokens in `wwwroot/app.css` (no hex/rgb or inline styles in Razor), and
the listings page is the reference for list/filter layouts. `DesignRulesTests` enforces the checkable rules; if a rule
really needs to change, update `docs/DESIGN.md` and the test together.

## Tests
`TestDb` (in `ListingTestSupport.cs`) is an in-memory SQLite database with the real migrations applied.
`FakeEtsyShop` scripts Etsy's API (listings, receipts, failures mid-import/poll) for importer and poller tests;
`TestSupport.cs` has `ImmediateTimeProvider` (timers fire instantly, delays recorded), `ScriptedHandler` for HTTP,
and in-memory token/credential/event fakes. Inject `TimeProvider` rather than using `DateTime.UtcNow` so code stays testable.

## Definition of done
- Run lint and the relevant tests before saying a task is complete. If they fail, fix them or report the failure. Never claim success without running them.
- New behavior gets a test. Bug fixes get a test that fails before the fix.

## Scope discipline
- Make the smallest change that solves the task. Don't refactor, rename, or reformat unrelated code.
- Don't add dependencies without asking first.
- Don't delete or weaken tests to make them pass.
- If requirements are ambiguous, ask instead of guessing. For changes touching more than ~3 files, outline a plan first.