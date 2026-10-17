# Etsy Shop Manager — Project Plan

_Status: Phases 0 (Foundation), 1 (Import) and 2 (Orders + HA) implemented; Phase 3 editor (all writable fields, photos, variations, push, drift, clone, auto-refill) implemented; Phase 4 (text blocks, shared variation lists, photo templates) implemented. Last updated 2026-10-17._

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
- Bulk edit from the listings page (Admin/Editor): "Bulk edit auto-refill" switches to the list with checkboxes (select a page, or all listings matching the filters) and sets on/off, threshold and refill-to; a blank field keeps each listing's own value. It only saves settings (after a confirmation that counts what the next check will refill); refills happen at the next check.

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

## Live job updates
- The job queue and processor publish every change (queued, running, finished, rescheduled, retried, cancelled) on an in-process feed (`IJobEvents`). Pages subscribe, coalesce bursts (~0.4 s) and re-render; subscriptions are disposed with the page. Handlers set a short `Result` ("2 new, 3 updated", "46 listings"), stored on the job and shown on the Jobs page.
- Notifications: when a job the signed-in user started finishes (e.g. "Check for orders now", "Import now", "Run now"), a snackbar shows its outcome or error on whatever page they're on.
- Pages: Jobs (live; consecutive runs of one job type collapse into an expandable stack, failures always on their own row), Orders (reloads after order checks; countdowns and "synced … ago" tick every 30 s), Listings (import status and list), listing detail, Lookups, Shops, Dashboard (job counts, shop health, open orders; quota re-rendered every 10 s).

## Phase 3 implementation notes (first slice)
- **Decisions (TopHATTwaffle, 2026-10-16):** edits save locally and go to Etsy only on an explicit push after a preview; a listing changed on etsy.com blocks pushes until resolved per field; quantity is not edited (Etsy, sales and auto-refill manage stock); this slice covers listing fields, prices and processing; photos and variation structure come next. Auto-refill is included.
- **Model:** `ListingEditModel` keeps Etsy (last read), base (last agreed) and draft (local edits) as managed-state JSON. Tracked fields (title, description, photos, variations/prices/processing, tags, materials, category, category attributes, personalization, profiles, section, who/when made, supply, taxable, auto-renew, weight/size, production partners) drift; untracked ones (styles, featured rank, state) follow Etsy. Resolutions: **keep mine** (the next push restores the local value) or **take Etsy's**. The planned third option, "ignore", turned out to be identical to one of these, so it was left out.
- **Push:** a job that re-reads the listing, stops if Etsy changed meanwhile, snapshots Etsy's version, sends `updateListing` (only changed fields) and `updateListingInventory` (Etsy's inventory from a fresh read with only prices/processing changed), then re-reads and keeps anything Etsy didn't take in the draft. Never retried automatically. Snapshots can be loaded back into the draft (rollback is a normal reviewed push).
- **Preview blockers:** drift, shop not connected, listing gone from Etsy, a push already queued, Etsy limits (title 140, 13 tags of ≤20 chars, 13 materials), prices > 0, one price/processing profile for all variations unless it varies by a property, status only active/inactive, and a listing read by an older managed-state version. Warnings: activating an inactive/expired listing or publishing a draft costs Etsy's fee.
- **Text encoding:** Etsy returns title/description/tags HTML-encoded (`&#39;`, `&quot;`; 101 of 130 real descriptions). Import decodes them (managed-state version 2); pushes send plain text.
- **Auto-refill:** per listing (on/off, threshold, refill-to ≤ 999). An enabled offering below the threshold is set to refill-to while the listing is active (shared quantity → all variations together); sold-out/inactive listings are flagged, not touched. Runs for the listings in each new order (right after the order check) and in an hourly sweep that finds low stock from one bulk read per 100 listings. Each refill is a job with the changes as its result, raises `StockRefilled` and is audited.
- **Variations (2026-10-17):** the editor adds, renames and removes values and variations (up to two; new ones use Etsy's custom slots 513/514), sets what price and processing vary by, and switches combinations off. The inventory is always the full grid of combinations. A push sends the whole inventory built from a fresh read: existing combinations keep Etsy's stock, product order and variation order (the order of the dropdowns buyers see); new ones start with the listing's current stock. Etsy's inventory write is all-or-nothing, so a rejection leaves the listing as it was, and the Etsy writes page shows Etsy's reason. Managed state version 3 keeps variation names, so every listing must be imported once after deploying (pushes are blocked until then). Checked offline against all 131 real listings: with no edits the editor's request equals Etsy's current inventory exactly (this caught the variation order on 4 listings).
- **Photos:** measured with the Admin photo order test on a draft listing (TopHATTwaffle, 2026-10-17, three runs). A photo's rank is just a number: uploadListingImage with an existing listing_image_id and a rank gives that photo the rank and **renumbers nothing else**, so ranks can tie (two photos at rank 1; Etsy then shows them in no fixed order) and have gaps (the real listing had 1, 2, 3, 4, 6, 7). The first run only looked like a "move" because Etsy happened to show the tie the other way. With overwrite=true the photo takes the slot and the photo shown at that rank **drops off the listing** (what broke the reverted attempt #29, which always sent overwrite=true). Reads show the change right away. The editor therefore: deletes removed photos, uploads new ones at their target rank (uploads first if every photo is replaced, so the listing always has one), then gives **every photo whose rank isn't yet its position its rank 1..n** with overwrite=false, so the end state has unique ranks, then sets changed alt text in place. The review lists every call and names any tie on Etsy; calls go one at a time and the first rejection stops the push. Photos are tracked for drift; alt text is HTML-decoded (managed state version 4). Photos added in the app stay in the image store, protected from cleanup while a draft refers to them. The test tool records each photo's rank on every read and has "number every photo 1–n" runs; the editor waits on a full-rank run on the draft listing confirming the reorder method before it is merged.
- **Clone (2026-10-17):** Clone on a listing copies its local version (edits not pushed included) into a new Etsy **draft**, in the same shop or the other one: createDraftListing (fields, price, stock, profiles), category attributes (updateListingProperty, one call each), the inventory (variations, prices and stock, from a fresh read of the source; for the other shop every combination gets the chosen processing profile), then the photos uploaded in order (rank 1..n, overwrite=false; ranks fixed only if Etsy shows a tie), then the draft is imported. For the other shop the dialog asks for shipping, processing and return profiles and section, pre-filled with the last choice for that shop (`Clone.LastChoice.{shopId}`). The review lists every call; the first rejection stops the copy and the clone record says which draft exists on Etsy and what it lacks. Personalization questions are copied too (as new questions, after the attributes); production partners only within the same shop. The app never publishes the draft.
- **Category attributes and personalization (2026-10-17):** both are editable and tracked for drift (managed state version 5, so every listing must be imported once after deploying).
  - **Attributes** are offered per the listing's category from Etsy's taxonomy properties (imported for the categories listings use): a list of values (single, or up to the property's maximum) and a scale where the property has scales. Managed state keeps `{property_id, scale_id, value_ids, values}` with values in id order and without the property name (Etsy's name can differ from the taxonomy's). A push sends updateListingProperty for each attribute that differs from Etsy and deleteListingProperty for each one removed, after updateListing so a new category is in place first. Blocked: values not in Etsy's list, too many values, a missing scale, a category whose properties the app hasn't read. Typed properties without a value list (Height, Width, Depth: 3 attributes on real listings) are shown and can be removed, but not set: Etsy gives a typed value its own value id and how it wants a new one sent can't be checked without writing to a real listing.
  - **Personalization** is edited as Etsy's question list (up to five: text box, drop-down, file upload, labeled file uploads; title ≤45, drop-down choices ≤20, upload labels ≤45 and one per file, add-on price only on an optional text box, no instructions on a drop-down). Managed state keeps the questions HTML-decoded and without Etsy's ids. A push sends one updateListingPersonalization (JSON, `supports_multiple_personalization_questions=true`, since the app reads and writes the full list) that replaces all questions, keeping Etsy's question id for each question still there (matched by type and title, else an unused one of its type) and option ids by label; an empty list sends deleteListingPersonalization.
  - **Styles and featured rank stay read-only:** Etsy's API (OpenAPI spec of 2026-10-17) no longer accepts `styles` or `featured_rank` in updateListing; styles can only be set by createDraftListing (the clone copies them), and featured rank has no write call at all.
  - Checked offline against all 131 real listings: unedited, no listing plans an attribute or personalization call; every one of the 19 settable attribute values re-selects to exactly Etsy's form; all 19 personalized listings rebuild their questions with Etsy's ids.
- **Testing:** no real Etsy writes in development or tests. `FakeEtsyShop` records every write; tests assert the exact `updateListing` form and `updateListingInventory` JSON. Offline checks against a sanitized copy of production: the migration applies, all 130 listings load without false drift, and the inventory body reproduces every real inventory exactly (48 with variations).

## Phase 4 implementation notes
- **Text blocks (2026-10-17):** named reusable description text (Text blocks page; `TextBlockService`, `Modules/Templates`).
  - **No markers on Etsy.** Descriptions stay plain text. The app keeps which listings use a block and the exact text it last put there (`ListingTextBlock.AppliedText`), and finds the block in a description by that text, so the link survives Etsy round-trips and buyers never see anything extra. Block text is normalized (
 newlines, no trailing spaces, no blank lines around it).
  - **Linking:** a new block lists the listings whose description already contains it word for word (on the real data, a choking-hazard paragraph was in 54 listings), to link in one go. The editor's "Insert text block" adds a block to the end of the description (a draft) and links it; the text can then be moved anywhere in the description.
  - **Editing a block** changes nothing by itself. Each linked listing shows a status: up to date; has the old text (with a line diff of the description); edited by hand (the applied text isn't in the description any more: skipped, never overwritten; "Link again" once it contains the block's text); changed on Etsy (description drift: resolve in the editor first). "Apply" writes the new descriptions as drafts; "Review and push" shows each listing's normal push preview (all of its unpushed edits, blockers included) and queues the normal push for those that can go. Deleting a block only unlinks; listings keep their text.
  - Not yet: placeholders (`{{shop.name}}`).
- **Shared variation lists (2026-10-17):** named option lists such as "PLA Colors" (Variation lists page; `VariationListService`). Each option has a stable id (so a rename is a rename) and is on or off.
  - **Linking:** a list offers the listing variations whose values are mostly in it (at least 2, and at least half; on the real data a 15-colour list matches 39 variations, 15 of them completely). A link remembers the listing's variation (property) and the options as last applied to it (`ListingVariationList.AppliedOptions`), starting from what the listing has (available or not).
  - **Changes are worked out against what was last applied:** an option new to the listing and on is added (combinations available, price copied from what the price varies by, as the editor's "add value" does; stock from the listing's current stock when pushed; at the end of Etsy's dropdown); switched off makes its combinations unavailable (never deleted); switched back on makes them available; renamed renames the value. Removing an option from the list leaves listings alone. A listing's own values (not in the list) are never touched; an option removed from a listing by hand isn't put back (shown as a note); a listing whose variations changed on Etsy waits until resolved; a listing without the linked variation is flagged.
  - Apply writes drafts; "Review and push" runs each listing's normal push preview and push, as for text blocks.
  - Not yet: reordering existing values, SKU patterns, adding a variation to a listing from a list.
- **Photo templates (2026-10-17):** one image with fixed alt text and a position counted from the end (Photo templates page; `PhotoTemplateService`). A listing's own photos come first, then its templates (position 1 last).
  - **Identity:** the template's photo on a listing is found by its alt text (the fingerprint the plan describes; each template's alt text must be unique), or by the template file while it waits in a draft. The link records which template image is on Etsy (`AppliedFileName`), the alt text it has there, and an image waiting in a draft (`PendingFileName`); after each push that includes photos, the `ListingPushed` handler records what reached Etsy. Etsy recompresses photos, so they can't be compared by content.
  - **Linking:** listings that already have a photo with the template's alt text keep it as the template's photo (taken as the current image; on the real data, 15 listings share "Shop Informational Image", the only alt text in use); others get it on the next apply.
  - **Apply** writes the listing's photos as a draft: adds the image, replaces it when the template has a new image (old copy deleted, new one uploaded), moves it into place, updates its alt text. A listing already in the right order is left exactly as it is, whatever Etsy's raw ranks (gaps included). Listings that would go over 20 photos, or whose photos changed on Etsy, are skipped. Pushing is the normal photo push (every photo its own rank 1..n, overwrite off). Template images are kept by the image cleanup.

## Etsy errors in plain words (2026-10-17)
- Etsy's error replies (`{"error": "…"}`, or a list of field checks `{"path": "/title/0", "type": "all_caps", "message": "…"}`) are turned into one sentence: what Etsy refused, its reason (known checks and messages reworded, others cleaned up), and what to do ("Change the title and try again", "Reconnect the shop", "Try again in a few minutes" for Etsy's own errors). It is the message of every failed Etsy call, so job results, toasts, the editor's push history, the clone page and the refill panel all show it; the raw reply stays in the Etsy write log.
- The push and clone reviews check Etsy's documented text rules before anything is sent: title characters (letters, numbers, punctuation, math symbols, spaces, ™ © ®), `% : & +` once each, at most 3 words starting with two capital letters (the rule behind the rejected clone "TEST LISTING!!! APP CLONE"), and the allowed characters in tags and materials. All 136 real listings pass.

## Etsy write journal
- Every write the app sends to Etsy (each attempt, retries included) and Etsy's exact reply is kept for 90 days and shown to Admins under Etsy writes, with the job that sent it. Added after the reverted photo push (2026-10-17), whose only trace of Etsy's answers was the service log. Uploaded files are recorded by name and size only; OAuth calls and request headers are never recorded.

## Deployment
- Source: private Gitea repo `github.com/TopHATTwaffle/etsy-shop-manager`, default branch `master`.
- Claude pushes to branches and opens PRs; `master` is protected so only TopHATTwaffle merges.
- Every push to `master` deploys automatically: Gitea Actions on a self-hosted `gitea-runner` on the Windows host (label `esm-deploy`, host mode) builds, tests, publishes, backs up the DB, upgrades the service, checks `/health`, and rolls back on failure. Setup: `docs/DEPLOY.md`.
- The runner account is not an admin: it can only control the EtsyShopManager service and write its install and data folders.
