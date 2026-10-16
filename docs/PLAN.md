# Etsy Shop Manager — Project Plan

_Status: Phases 0 (Foundation), 1 (Import) and 2 (Orders + HA) implemented. Last updated 2026-10-16._

## Goals

A self-hosted Etsy shop manager running on a Windows host, used from any device on the LAN or over VPN. It manages 2 shops (both owned by TopHATTwaffle) through an existing Etsy developer app.

**This app is the system of record.** All listing edits happen here. Changes made on Etsy.com are detected and flagged, never overwritten automatically.

## Decisions

| Area | Decision |
|---|---|
| Runtime | .NET 10 (LTS), ASP.NET Core, runs as a Windows Service |
| UI | Blazor Web App, Interactive Server mode, MudBlazor components |
| Data | SQLite via EF Core, stored in `C:\ProgramData\EtsyShopManager\` (**not** in Dropbox) |
| Images | File store next to the DB, content-hashed filenames |
| Auth | ASP.NET Core Identity, local accounts. Roles: Admin, Editor, Viewer |
| HTTPS | Terminated at HAProxy. Kestrel listens on HTTP on the LAN; ForwardedHeaders trusts the HAProxy IP only. HAProxy must allow WebSockets (Blazor/SignalR), e.g. `timeout tunnel 1h` |
| Etsy auth | OAuth 2.0 + PKCE against the existing app. Redirect URI is the HAProxy hostname (the redirect happens in the browser, so it does not need to be publicly reachable). Tokens encrypted with ASP.NET Data Protection (DPAPI) |
| Etsy client | Generated from Etsy's OpenAPI spec, wrapped with a per-API-key rate limiter and retry |
| Background work | A persistent job queue table, processed by hosted services. Survives restarts and keeps under rate limits |
| Orders | Polling (`getShopReceipts` with `min_last_modified`), every 5 min by default (as the old HA integration), set in Admin → Settings. Webhooks are not used: they require a public URL |
| Source control | Git repo. Code may live in Dropbox, but the live DB and image store must not (Dropbox sync can corrupt SQLite) |

## Solution layout

```
EtsyShopManager/
  src/
    EtsyShopManager.Web/        Blazor UI, Identity, REST API (incl. /api/ha), hosting, Windows Service
    EtsyShopManager.Core/       Domain model, template resolver, render/diff/drift engine, module contracts
    EtsyShopManager.Etsy/       Generated Etsy client, OAuth, rate limiter
    EtsyShopManager.Data/       EF Core DbContext, migrations
  tests/
  homeassistant/
    custom_components/etsy_shop_manager/   New HA integration (Python, required by HA)
    www/etsy-orders-card.js                Existing card, carried over
  docs/
  ExistingHASS/                            Reference only, delete after migration
```

**Extensibility:** each feature is a module (`IModule` registers its services, jobs, UI pages and API endpoints). Modules communicate through in-process domain events (`OrderReceived`, `ListingPushed`, `DriftDetected`, `StockRefilled`, …), so new features subscribe to events instead of editing existing code.

## Core model: render → diff → push

Each managed listing is stored locally as its own fields plus links to templates. The **renderer** combines them into the exact payload Etsy should hold. Nothing goes to Etsy without a diff preview.

1. **Render** the desired state (listing fields, then text blocks, then the variation table, then photo order).
2. **Diff** it against the last known Etsy state.
3. **Preview**, showing every affected listing and field. This is required for template changes that touch many listings.
4. **Push** through the job queue. Snapshot each listing before writing, so any push can be rolled back.

### Drift detection
- A scheduled pull (nightly, plus on demand) fetches each listing and hashes its *managed* fields.
- If a hash differs from the last pushed hash, the listing is flagged as **Drifted**, with a field-by-field diff.
- For each field, the user chooses: **keep local** (overwrite Etsy on next push), **accept Etsy** (import into local), or **ignore**.
- Quantity is excluded from drift checks, because sales and auto-refill change it legitimately.
- Pushes to a drifted listing are blocked until the drift is resolved.

## Features

### 1. Listing editor (full field coverage)
The editor covers every field the API can write: title, description, price, quantity, SKU, tags, materials, styles, category (taxonomy) and its properties, who/when made, shipping profile, processing profile (`readiness_state_id`), return policy, personalization settings, production partners, section, featured rank, auto-renew, state.

Per-shop lookups (shipping, processing and return profiles, sections, taxonomy) are cached locally and refreshed on demand. Each shop gets defaults for new listings.

### 2. New listing from an existing one (clone)
Copies the local record, including template links, into a new draft. It can also clone across shops: profile IDs are remapped to the target shop's defaults.

### 3. New listing from scratch
A guided form that enforces Etsy's required fields before allowing a push. Created on Etsy as a draft, then activated.

### 4. Order dashboard (view only)
- Open orders for one or both shops, sorted by ship-by date.
- Shows items, variations, personalization, quantity, buyer note, gift message, thumbnail.
- An aggregated "to make" list.
- Orders are stored locally so history survives, and the HA API reads from them.

### 5. Text templates
- Named blocks, e.g. "PLA care instructions" or "Heat warning".
- A listing description is made of ordered segments: its own text plus template blocks at positions it chooses.
- Editing a template marks all linked listings as pending changes. The user previews, then pushes.
- Supports simple placeholders, e.g. `{{shop.name}}` or `{{listing.material}}`. Optional, but cheap to add now.

### 6. Variation templates
- A template is a **named list of option names** (e.g. "PLA Colors": Black, White, Galaxy Purple…). Each option can be marked enabled or disabled.
- A listing uses up to 2 variation properties. A property can come from a template (e.g. Color) or be specific to the listing (e.g. Size, with its own prices).
- The renderer builds the full offering grid. Price, SKU and quantity can vary on the listing's own property (`price_on_property` and related settings). Template options carry no price.
- Template properties use Etsy's custom property IDs (513/514), so any name works.
- **Adding a color** means a template edit, a preview of N listings, then a push. Each listing's inventory is rewritten in full, because Etsy replaces the whole table per call.
- **Disabling a color** (e.g. retired filament) turns those offerings off on every linked listing without deleting them.
- SKU pattern per listing, e.g. `{listing.sku}-{size}-{color}`, so SKUs stay stable when colors are added.

### 7. Photo templates
- A template holds one image plus fixed alt text.
- **Order is filled from the end:** the shop specials template is last, the color swatch template second to last, and the listing's own images come first. Each template has a rank that sets its position from the end.
- Updating a template image means uploading the new photo to every linked listing, deleting the old one and re-ranking. These run as queued jobs.
- **Tracking:** the local DB maps each `listing_image_id` to its template (primary). The template's alt text doubles as a fingerprint to rebuild that map if the DB is lost. Alt text stays real, descriptive text (e.g. "Color swatch chart of all available filament colors"), not a code, because buyers using screen readers hear it.

### 8. Auto-refill (per listing)
- Turned on per listing, with a threshold and a refill-to value (e.g. below 5, refill to 999).
- Evaluated per offering (each variation combination), after every order poll.
- Refills happen while stock is still above 0, so a listing never sells out (selling out deactivates it and reactivating costs a renewal fee).

### 9. Home Assistant integration
The existing card keeps its styling and logic. Only its data source changes. **Home Assistant talks only to this service, never to Etsy.**

- **Service side:** `GET /api/ha/v1/shops` and `GET /api/ha/v1/shops/{shopId}/open-orders`. Authenticated with an API key that an Admin issues in the app.
  - The C# service builds the order payload (porting `build_receipt_summary` and `build_transaction_detail` from the old integration). That includes splitting personalization out of variations (property 54), applying the money divisor, ship-by timestamps and image URLs. `image_url` is Etsy's CDN URL (stored with each imported image), as the old integration used, so the card needs nothing from this app to show photos.
  - The payload keeps the exact shape the card already reads: `open_orders[]` (`receipt_id`, `buyer_name`, `ship_by_ts`, `order_total`, `currency_code`, `items[]` with `title`, `quantity`, `variations[{property,value}]`, `personalization`, `image_url`) and `total_items_needed`.
- **HA side:** a new integration `etsy_shop_manager` (a different domain from the old `etsyapp`, so both can run side by side during migration).
  - Config flow takes the service URL and API key, then shows a device and an Open Orders sensor per shop.
  - Polls every 30 s by default (options flow, 5–3600 s). This is cheap because it stays on the LAN.
  - Fires an `etsy_shop_manager_new_order` event and a device trigger.
  - Large attributes (`open_orders`, `items_needed`) are kept out of the HA recorder (`_unrecorded_attributes`), because HA's 16 KB attribute limit is easy to exceed.
- **Card:** carried over unchanged. Its config simply points `shops[].entity` at the new sensors. A later option could link orders to this app's order page instead of Etsy.
- Install via HACS custom repository, or by copying files manually. HACS only installs from GitHub repositories with `custom_components/` at the root, so `homeassistant/` is laid out to become its own GitHub repository as-is.
- Phase 2 option: push updates over SSE/WebSocket instead of polling, if the 30–60 s delay matters.

## Additions to build in early
- Push history with rollback (from the per-push snapshots).
- Full local backup of listings and images, plus a one-click export (zip).
- Bulk edit: percentage price changes, tags, materials, shipping profile across selected listings.
- Audit log of who changed what (now that there are user accounts).
- Pick list / packing slip printing from the order dashboard.

## Phases

| Phase | Scope | Why this order |
|---|---|---|
| 0. Foundation | Solution skeleton, Windows Service, Identity/users, HAProxy notes, Etsy OAuth for both shops, token refresh, job queue, rate limiter | Everything depends on it |
| 1. Import | Pull all listings, images, inventory, profiles, taxonomy into the local DB. Read-only browsing | Establishes the local master copy |
| 2. Orders + HA | Order polling, dashboard, `/api/ha`, new HA integration, card switched over | Read-only and low risk; retires the public integration early |
| 3. Editor + push | Full listing editor, render/diff/push, snapshots/rollback, drift detection, clone | The write path everything else builds on |
| 4. Templates | Text, then variation, then photo templates | Each builds on the renderer |
| 5. Create + refill | New listing from scratch, auto-refill | Uses the editor and the order events |

## First-run setup wizard
On first launch (no Admin exists), the app runs a setup wizard:
1. Create the first Admin account.
2. **Base URL**, e.g. `https://etsy.example.lan`, the hostname HAProxy serves. The app builds the Etsy redirect URI from it (`{BaseUrl}/auth/etsy/callback`) and shows it with a copy button, to register in the Etsy developer portal.
3. Etsy app keystring and shared secret, stored encrypted.
4. Connect shops: one OAuth sign-in per shop.

The base URL can be changed later under Admin → Settings. When it changes, the app shows the new redirect URI and warns that it must also be updated in the Etsy portal; otherwise new shop connections and re-auth will fail. Existing refresh tokens are unaffected. Forwarded-header handling stays separate: it uses a configured trusted-proxy IP, not the base URL.

## Resolved
- **Host uptime:** the Windows host runs 24/7, so order polling and auto-refill run continuously. No catch-up logic is needed beyond normal restart recovery from the job queue.
- **Roles:** Admin / Editor / Viewer confirmed.
- **Card order links:** unchanged. Orders link straight to the Etsy order page, using the card's existing per-shop `order_url` override (e.g. `microsoft-edge:https://…` for one shop, the default browser for the other), so each shop stays signed in on its own browser. The service only has to keep sending `receipt_id`; link building stays in the card config.

## Phase 0 implementation notes
- **Etsy client:** Phase 0 uses a thin hand-written client (`users/me`, `shops/{id}`) on the same HTTP pipeline the generated client will use (x-api-key, rate limiter, retry). The OpenAPI-generated client is added in Phase 1, when listing and inventory calls are needed.
- **x-api-key:** Etsy v3 requires `keystring:shared_secret` in the header; the app sends it on every `/v3/application` call.
- **Migrations:** committed in `src/EtsyShopManager.Data/Migrations` (starting with `InitialCreate`). The build fails if the model changed without a migration; pending migrations apply on startup.
- **Dates:** stored as UTC `DateTime`, not `DateTimeOffset`, because EF's SQLite provider can't sort or compare `DateTimeOffset` in SQL.
- **Rendering:** Interactive Server without prerendering. Sign-in and the "create Admin" step are static SSR, because they set the auth cookie.
- **Service account:** `NT SERVICE\EtsyShopManager`, with modify rights on `C:\ProgramData\EtsyShopManager` only. Data Protection keys use machine-scope DPAPI; back up the `keys` folder with the database.
- **Packages:** EF Core / ASP.NET 10.0.13, MudBlazor 9.x. Verified with a real build, the test suite and a live run of the first-run flow.
- **Recurring jobs:** token keep-alive every 12 h (keeps the ~90-day refresh token rolling), shop details every 24 h, job cleanup every 24 h.

## Phase 1 implementation notes
- **Etsy client:** typed methods hand-written from Etsy's OpenAPI spec (`https://www.etsy.com/openapi/generated/oas/3.0.0.json`), in `EtsyApiClient.Listings.cs` with models in `Models/ListingModels.cs`. Code generation was skipped: the spec is large and the generated client would bypass the existing pipeline style; the hand-written calls use the same `Etsy` HttpClient (x-api-key, shared rate limiter, retry). Collection reads keep each item's raw JSON next to the typed value.
- **What is imported per shop:** listings in every state Etsy still serves (active, inactive, sold_out, draft, expired), with images, inventory (products, offerings, property values, `*_on_property`) and personalization through `getListingsByShop` `includes=Images,Inventory,Personalization` (100 per page, oldest first). If an include comes back empty, the importer falls back to `getListingImages` / `getListingInventory`. Category attributes need `getListingProperties` per listing, so they are fetched only for new listings, listings whose `last_modified_timestamp` changed, or on a forced re-import. Lookups: shipping profiles, processing profiles (readiness-state definitions), return policies, sections, production partners. Taxonomy: the whole seller tree (one call, refreshed weekly) plus properties for the taxonomy nodes listings use.
- **Last known Etsy state:** `ListingEtsyState` keeps the raw listing JSON, the canonical JSON of the managed fields and its SHA-256 (`ManagedListingState` in Core, versioned). Quantity, Etsy's product/offering ids, stats and timestamps are excluded; `sold_out` counts as `active`. Products are compared by their variation values, not ids, because Etsy issues new ids on every inventory write. Phase 3 drift detection compares against this hash and shows a field diff from the managed JSON.
- **Idempotent and resumable:** listings, images, products and offerings are matched by Etsy id, so an unchanged re-import changes nothing (same rows, same hash). Progress (lookups done, listing state, offset) is saved per shop in `ShopSyncState` after every page; an interrupted job resumes from that page (within 12 h, then it starts over). A completed run flags listings it didn't see as **gone from Etsy** (`IsMissingOnEtsy`); nothing is deleted locally. A listing that changes state in the middle of a run can be missed by paging and is then flagged until the next run.
- **Images:** a separate low-priority job downloads the full-size photo and Etsy's 570px copy (for lists) from the CDN (not counted against the API limit) into `images/ab/<sha256>.<ext>`. A changed source URL re-downloads; files no image references are deleted after a day. Served at `/media/images/<sha256>.<ext>` without sign-in (the hashed names are unguessable, and the same photos are public on Etsy's CDN) with immutable caching. The local copies are the app's master copy (backup/export, photo templates); the HA card keeps using Etsy's CDN URLs, which the import stores.
- **Jobs:** `etsy.listings.import` (per shop, then queues images + taxonomy), `etsy.lookups.import`, `etsy.images.download`, `etsy.taxonomy.refresh`, and the recurring `etsy.listings.import-all` every 24 h (an interval, not a time of day). Connecting or reconnecting a shop queues its import. Dedupe keys make repeated clicks harmless.
- **UI:** Listings (search title/SKU/tag/listing id, filter by shop/state/gone, sort, server paging, import status and "Import now"/"Full re-import" for Admin/Editor), listing detail (fields with lookup names, attributes, personalization, photos, inventory grid, Etsy state with hash and raw JSON) and Lookups (per-shop tables with usage counts, taxonomy search with properties).
- **Found on the first real import:** Etsy reuses `listing_image_id` across copied listings, so image rows are unique per listing, not globally. The rate limiter now adds one token every 1/RPS seconds (burst 2) instead of refilling 5 tokens at once, which drew frequent 429s.
- **Not yet:** videos, translations, digital files. A full import of a ~300-listing shop costs roughly 10 calls for listings and lookups plus one attribute call per changed listing.

## Phase 2 implementation notes
- **Polling:** `etsy.orders.poll-all` (recurring) queues `etsy.orders.poll` per shop (priority above imports). Each poll asks `getShopReceipts` for receipts modified since the previous poll *started* (minus a 2-minute overlap for clock differences), 100 per page in creation order. Creation order matters: in update order a receipt modified mid-poll jumps to the end and shifts the rest, so offset paging could skip one; in creation order it can't. The first poll pulls the whole order history. Orders and items are matched by Etsy id (idempotent); the page offset is saved after every page so an interrupted backfill resumes. The interval comes from the `Orders.PollIntervalSeconds` setting (Admin → Settings, minimum 1 minute); recurring jobs can now take their interval from a setting (`RecurringJobDefinition.IntervalSettingKey`). Connecting a shop starts a poll.
- **Stored:** `Order` (receipt, with the raw JSON incl. address for later packing slips) and `OrderItem` (transaction, with variations incl. personalization and the photo URL). Open = paid, not shipped, status not completed/canceled/refunded (the old integration's rule). `OrderReceived` is raised for orders that become paid after the first backfill.
- **Photos:** each item's `image_url` is Etsy's CDN URL: the transaction's own listing image from the imported listings, or one `getListingsByListingIds` call (includes=Images) for listings that aren't imported (e.g. deleted). No sign-in or app access is needed to show them.
- **HA API** (`/api/ha/v1`, API key in `Authorization: Bearer` or `X-Api-Key`): `GET /shops`, `GET /open-orders` (every shop, what the integration polls), `GET /shops/{etsyShopId}/open-orders`. `HaPayloadBuilder` ports the old `build_receipt_summary` / `build_transaction_detail` / `_aggregate_items_needed`; a test compares its output with a golden file produced by running the old Python on the same receipts. Date strings use the host's time zone; `ship_by_ts` is Unix time, as before.
- **API keys:** Admin → API keys. Shown once; stored as SHA-256; last use recorded (at most every 5 minutes); revocable.
- **Job retention:** succeeded jobs the scheduler queued (order polls) are kept 24 h instead of 30 days; cleanup runs every 6 h.
- **Orders page:** open orders as cards sorted by ship-by (urgent ones tinted), a "to make" list (same aggregation as HA), and searchable history.
- **HA integration** (`homeassistant/custom_components/etsy_shop_manager`, HA 2024.11+): config flow (URL, key, SSL verification), re-authentication when the key is revoked, options flow for the interval, one device + Open orders sensor per shop (new shops appear automatically), `etsy_shop_manager_new_order` event + device trigger (orders open at startup are not announced), large attributes excluded from the recorder, last data kept for up to 5 minutes while the service is unreachable (deploy restarts), interval changes applied without a reload. Tests run in a real HA (`pytest-homeassistant-custom-component`) against a mocked service. The existing card is in `homeassistant/www/`, unchanged.

## Deployment
- Source: private Gitea repo `github.com/TopHATTwaffle/etsy-shop-manager`, default branch `master`.
- Claude pushes to branches and opens PRs; `master` is protected so only TopHATTwaffle merges.
- Every push to `master` deploys automatically: Gitea Actions on a self-hosted `gitea-runner` on the Windows host (label `esm-deploy`, host mode) builds, tests, publishes, backs up the DB, upgrades the service, checks `/health`, and rolls back on failure. Setup: `docs/DEPLOY.md`.
- The runner account is not an admin: it can only control the EtsyShopManager service and write its install and data folders.
