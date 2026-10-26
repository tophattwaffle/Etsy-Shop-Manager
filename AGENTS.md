# AGENTS.md

This file provides guidance to AI coding agents (Claude Code, Antigravity, Codex, and others) when working with code in
this repository.

Self-hosted Etsy manager for two shops: .NET 10, Blazor Web App (Interactive Server, MudBlazor), SQLite via EF Core,
running as a Windows Service behind HAProxy (which terminates HTTPS). Everything must build and run on a Windows host;
code is C# wherever possible and the UI is web-based. Full design and phase plan: `docs/PLAN.md`. Install/first run:
`docs/SETUP.md`. CI/CD: `docs/DEPLOY.md`.

**This app is the system of record for listings.** Edits happen here; changes made on Etsy.com are detected and flagged
(drift), never silently overwritten. Phases 0–2 (foundation, import, orders + Home Assistant) are done; Phase 3's
editor (listing fields, photos, variations, prices and processing; preview → push with snapshots; drift; auto-refill)
is in, including category attributes and personalization (styles and featured rank stay read-only: Etsy's API
can't change them on an existing listing). Photo pushes follow what Etsy was measured to do (`PhotoEditor`, docs/PLAN.md "Photos"): a photo's rank is
just a number and Etsy never renumbers the others (ranks tie and have gaps), so a push sets every photo's rank 1..n
explicitly, always with overwrite=false (overwrite=true drops the photo shown at that rank). `ListingCloner` copies a
listing into a new Etsy draft (same shop or the other; profiles chosen per target shop), never published by the app.
Phase 4 (templates): text blocks (`Modules/Templates`, `TextBlockService`) are `{{text.slug}}` placeholders kept in the
app's copy of a description (`ListingDescription`); the listing's description and Etsy always get the rendered text.
Applying a block writes drafts, and pushing goes through the normal reviewed push.
Shared variation lists (`VariationListService`) drive a listing variation each; changes are worked out against the options
last applied to that listing, so a listing's own values and hand edits are left alone. Photo templates
(`PhotoTemplateService`) find their photo on a listing by its alt text, keep it at its position from the end, and only
change a listing whose photo order or alt text is wrong (raw rank gaps don't count); `ListingPushed` records what reached Etsy.

**Never write to the real Etsy shops while developing or testing.** Verify write paths with `FakeEtsyShop` (it records
every PATCH/PUT body) and previews only. A sanitized copy of production data (tokens, keys and users removed) may be
used locally, with `--Etsy:ApiBaseUrl` pointed at a dead address; TopHATTwaffle does real pushes himself.

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

The Home Assistant side lives in its own repositories (mirrored to GitHub for HACS): the integration in
`TopHATTwaffle/Etsy-Shop-Manager-HA` (Python, its own pytest suite) and the dashboard card in
`TopHATTwaffle/Etsy-Shop-Manager-HA-Card`, both on git.example.lan.

- `build.ps1` fails if the EF model changed without a migration (`dotnet ef migrations has-pending-model-changes`).
  Migrations are committed in `src/EtsyShopManager.Data/Migrations` and applied automatically on startup.
- `dotnet-ef` is a local tool (`.config/dotnet-tools.json`); package versions are central in `Directory.Packages.props`
  (bump there, never in a `.csproj`).
- Merges to `master` deploy automatically: `.gitea/workflows/deploy.yml` runs on a self-hosted runner on the Windows
  host (build.ps1 → self-contained win-x64 publish → `scripts/deploy.ps1`, which backs up DB + binaries, swaps,
  waits for `/health`, and rolls back on failure). `master` is protected; work goes on branches and is merged by PR.
- To let TopHATTwaffle look at a branch before merging, run `pwsh scripts/deploy-staging.ps1` from its checkout: it runs that
  build in the branch's own staging slot on the host (its own service, port and data, environment Staging, which
  can't reach Etsy), creating a slot if every one is taken by another branch. **When it prints `NEW staging
  instance`, send TopHATTwaffle that link.** Once the branch's PR is merged, take the slot down with
  `pwsh scripts/remove-staging.ps1 -Branch <branch>`. `scripts/list-staging.ps1` lists slots. See `docs/STAGING.md`.

## Architecture

```
src/EtsyShopManager.Core   Domain entities, job/event/module contracts, StorageOptions, ManagedListingState
src/EtsyShopManager.Etsy   Etsy v3 client: OAuth+PKCE, token service, rate limiter, retry handlers, API client (partial classes per area)
src/EtsyShopManager.Data   AppDbContext (SQLite + Identity), migrations
src/EtsyShopManager.Web    Host (Program.cs), Blazor UI, setup wizard, job processor, feature modules
tests/EtsyShopManager.Tests  xUnit
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
refresh token marks the shop as needing reconnection. Every write (POST/PUT/PATCH/DELETE on the application API, each
attempt) is recorded with Etsy's reply by `EtsyWriteJournalHandler` (innermost) into `EtsyWriteLogs` (Admin → Etsy writes,
kept 90 days); OAuth calls and headers are never recorded, file uploads only as name and size. `EtsyCallContext` names
the job that made the call. A failed call's exception message is `EtsyErrorText`'s plain-words version of Etsy's reply
(what was refused, Etsy's reason, what to do); that is what job results, the editor, clone page and refill panel show,
while the raw reply stays on `EtsyApiException.ResponseBody` and in the write log. `ListingTextRules` checks Etsy's
title/tag/material rules in the push and clone reviews.

### Listings
Import (`Web/Modules/Listings/ListingImporter.cs` and jobs) is idempotent, matched by Etsy id, resumable page by page;
listings gone from Etsy are flagged, never deleted. Etsy reuses `listing_image_id`s on copied listings, so image ids
are unique per listing only. Photos go into a content-hashed image store (`ImageStore`). `ManagedListingState` is the
canonical form + hash of the fields the app manages (quantity, Etsy product/offering ids, stats and timestamps are
deliberately excluded); it is the basis for drift detection. Bump `ManagedListingState.CurrentVersion` when
its field set or normalisation changes (listings read with an older version must be re-imported before they can be
pushed). Etsy returns title/description/tags HTML-encoded; the mapper decodes them once, and pushes send plain text. Receipt text is encoded the same way: `OrderMapper.Text` decodes names, notes, gift messages, item titles and variations, and `OrderPrintService` decodes what it reads from the stored receipt.

### Editor, push and auto-refill
`ListingEditModel` (Core) holds three versions as managed JSON: **Etsy** (last read), **base** (last agreed) and
**draft** (local edits). Drift = a tracked field where Etsy ≠ base; pending = local ≠ Etsy. `ListingEditor` saves drafts,
resolves drift and builds the preview (blockers stop a push); `PushListingJob` re-reads, refuses on drift, snapshots,
then `updateListing` (form), attributes (`AttributeEditor`, one call each), personalization (`PersonalizationEditor`, one
call replacing all questions), photos and `updateListingInventory`, re-reads and reconciles. Etsy replaces the whole inventory
on every write, so `InventoryWriter` always rebuilds it from a fresh read and changes only what it means to (the edited
variations, prices, processing and availability for pushes, keeping Etsy's stock, product order and variation order;
quantities for refills). `VariationEditor` edits the variation structure in managed-state JSON (always the full grid of
combinations; new values have no Etsy ids until Etsy assigns them, adopted on reconcile). `StockRefiller` refills enabled offerings below a listing's
threshold while the listing is active, on each new order (`OrderReceived`) and in an hourly sweep.

### Orders and Home Assistant
`OrderPoller` polls receipts per shop (default every 5 min, set in Admin → Settings). `OrdersModule` exposes a
read-only API at `/api/ha/v1` protected by API keys (`esm_…`, `ApiKeyEndpointFilter`). `HaPayloadBuilder` produces
the exact attribute shape the old `etsyapp` sensor had, so the `etsy-orders-card` (its own repository) keeps working;
keep that payload backward compatible (add keys, never change or remove them). Order images in the HA payload are
Etsy's public CDN URLs.
Order colors live only in the app: the list and look (`OrderColorService`, Order colors page) and the engine
(`OrderColorEngine`, a port of the card's old one). The Orders page draws `ColorBox`es with it on the server, and the
HA payload carries every box already drawn (`paint` on variations, `personalization_paint` on items, seeded by receipt
and position) plus the engine's CSS in `color_config.css`, so card 3.0 has no color logic. Change coloring here, never
in the card. `color_config` still carries the list and look keys for cards before 3.0.
Order changes a buyer asks for after checkout (personalization, variation values, shipping address) are kept on the
local order only (`OrderOverrides`, `OrderOverrideService`; polling never touches them). Read item variations through
`OrderOverrides.Variations` and the address through `OrderPrintService.Address`, so every view shows the change;
`EtsyAddress` is Etsy's, and slips mark a changed address so the shipping label gets changed too.
Made marks: `OrderItem.MadeQuantity` (units made, local only, polling never touches it) is set from the order cards and
the To make rows through `OrderMadeService`; `ToMakeList` ties each To make row to its order items (soonest ship-by
first) so both views mark the same items. The HA payload carries it per item (`made_quantity`), and the HA card's Made
checkboxes set it through the integration's `set_made` action and `POST /api/ha/v1/orders/{receipt}/items/{transaction}/made`,
the one write in the HA API.
Packing slips (the Packing slips section) are Liquid templates rendered with Fluid (`SlipTemplates.cs`): the built-in
packing and gift layouts are embedded from `Modules/Orders/Slips`, Admins save copies as `SlipLayout` rows, and each shop
picks one per kind. A gift slip is half a sheet and `SlipSheets` prints two per sheet (a gift with a changed address gets a sheet of its own, the note in the cut-away top half). A layout's CSS is nested under `.slip-layout-{key}` so it only reaches its own slips; values are
HTML-encoded unless a template uses `| raw`. Change the built-in look in those files, not in Razor.
Listing instructions (`SlipDocument`, `SlipDocumentService`, Packing slips page) are text plus a QR code link linked to
listings (one per listing, `ListingSlipDocuments`); a slip's `documents` holds those of the order's listings, each once,
printed at the foot of the packing slip (two to a row when there are several, so it stays one page) and under the items
on the gift slip. Layouts saved before this need a `{% for doc in documents %}` block to print them.
Automatic printing (`AutoPrint.cs`): slips join a queue (`Order.SlipQueuedAtUtc`, by the timing mode set on the Packing
slips page: on `OrderReceived`, or a daily batch from `AutoPrintScheduler`) and `PrintSlipsJob` prints it on the host
printer chosen there (`WindowsSlipPrinter`: headless Edge prints `orders/print/slips` from the app's own local address to
a PDF, SumatraPDF sends it to the printer; printers listed with EnumPrinters), which stamps `Order.SlipPrintedAtUtc` (the
printed icon on order cards). Printing slips from the Orders page stamps it too, once the print dialog closes. The
service only sees printers installed on the host for all users, not shares connected per user.

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