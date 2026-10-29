# Etsy Shop Manager — Project Plan

_Status: every phase implemented: 0 (Foundation), 1 (Import), 2 (Orders + HA), 3 (editor: all writable fields, photos, variations, push, drift, clone, auto-refill), 4 (text blocks, shared variation lists, photo templates) and 5 (new listing from scratch). What is left to do is in [BACKLOG.md](BACKLOG.md). Last updated 2026-10-28._

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
  docs/
```

The Home Assistant integration and the dashboard card live in their own repositories, `Etsy-Shop-Manager-HA` and
`Etsy-Shop-Manager-HA-Card` (mirrored to GitHub for HACS), with their history carried over from this repository's
former `homeassistant/` folder.

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
A guided form that enforces Etsy's required fields before allowing a push. Created on Etsy as a draft; the app never publishes it (activate it on Etsy).

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
- Bulk edit from the listings page (Admin/Editor): "Bulk edit" (with "Auto-refill" chosen) switches to the list with checkboxes (select a page, or all listings matching the filters) and sets on/off, threshold and refill-to; a blank field keeps each listing's own value. It only saves settings (after a confirmation that counts what the next check will refill); refills happen at the next check.

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
- **Card:** carried over with its styling; its config points `shops[].entity` at the new sensors. Since card 3.0 the app draws the order colors (`OrderColorEngine`) and the payload carries them, so the card only paints them.
- Install via HACS custom repository, or by copying files manually. HACS only installs from GitHub repositories with `custom_components/` at the root, so the integration has its own repository, `Etsy-Shop-Manager-HA` (and the card `Etsy-Shop-Manager-HA-Card`).
- Phase 2 option: push updates over SSE/WebSocket instead of polling, if the 30–60 s delay matters.

## Additions to build in early
- Push history with rollback (from the per-push snapshots). **Done:** snapshots load back into the draft (Phase 3).
- Full local backup of listings and images, plus a one-click export (zip). **Done** (Phase 5).
- Bulk edit: percentage price changes, tags, materials, shipping profile across selected listings. **Done (2026-10-18):**
  the listings page's "Bulk edit" (Admin/Editor) selects listings as for auto-refill and changes one thing at a time:
  auto-refill, price (percent or amount on every variation, optionally rounded to .99 or whole amounts; never below
  Etsy's 0.20), tags or materials added or removed (case-insensitive, within Etsy's 13 and its text rules), or shipping
  profile or section chosen per shop (`BulkListingEditor`). A preview lists each listing's now and after, or why it's
  skipped (that field changed on Etsy, over a limit, nothing chosen for its shop); saving makes unpushed edits from the
  app's version of each listing, and "Review and push" runs each listing's normal push, as the templates do.
- Audit log of who changed what (now that there are user accounts). **Done** (Admin → Audit log).
- Pick list / packing slip printing from the order dashboard. **Done (2026-10-18):** open orders get checkboxes and "Select all unshipped"; "Print pick list" and "Print packing slips" open static, print-friendly pages (`OrderPrintService`, `PrintLayout`, `wwwroot/print.css`; US letter, one slip per page). The pick list groups items by listing and variations with each personalization and its order under the line. A slip follows Etsy's own packing slip (2026-10-18): shop logo, name and name.etsy.com; ship-to, ship-by, order, order date, buyer, payment method and shipping method on the left; items (qty x price, variations, personalization) and totals (item total, discount, tax, shipping, order total) on the right, all from the stored receipt; buyer note and gift message under the items; the shop's packing slip note as "A note from the shop" above the items (Shops page, kept under `Shops.SlipThanks.{id}`; Etsy's API doesn't share the note set on Etsy). Etsy's logo and blurbs aren't copied. The buyer's Etsy username isn't in a receipt, so it's left out. Sizes are Etsy's, measured from its PDF (9.5pt text, 36pt margins). The print pages set `@page { margin: 0 }` so Chrome has no margin to print its date, title, URL and page number in; the margin is padding inside each page (the pick list repeats spacer rows in its header and footer). **Gift orders** (Etsy's `is_gift`, `gift_message`, `gift_sender`) print a gift slip like Etsy's instead: top half blank for the shipping label, a cut line, the order on the left without prices (shop icon, name, tagline, an optional location line set per shop on the Shops page under `Shops.SlipLocation.{id}` because Etsy's API has no shop city, and Etsy's sales count from getShop `transaction_sold_count`, read at startup for shops that lack it; no Star Seller flag in the API, so a per-shop Star Seller switch on the Shops page, `Shops.StarSeller.{id}`, prints Etsy's badge before the sales count and on normal slips under the shop), and past the fold "A gift for you" in a small serif with five gold sparkles (not the shop name, which read as if the gift were for the shop), the first item's photo, the gift message, who it's from ("- Kate": Etsy leaves `gift_sender` empty, as re-reading all 414 gift receipts showed, so it is the buyer's first name from getUser, which needs the `email_r` scope (shops connected before it must be reconnected); stored as `Order.GiftFromName` by the poller and, for older orders, `GiftBuyerNamesJob` at startup and on reconnect), and a QR code to the shop's Etsy page (QRCoder, drawn on the server). The location line also prints under the shop on normal slips. Orders and History show a Gift badge; History filters to gift orders. Read-only: nothing is written to Etsy or marked shipped. **Editable layouts (2026-10-18):** a Packing slips section in the sidebar holds which layout each shop prints (packing and gift), each shop's note, location line and Star Seller switch (moved from the Shops page), and the layouts: Liquid templates (Fluid) plus CSS, with a live preview of a made-up order. The built-in layouts are `Modules/Orders/Slips/*.liquid` and `*.css`; Admins copy them into `SlipLayout` rows to edit. A broken layout prints a short message in its slip's place instead of stopping the printout. **Two gift slips per sheet (2026-10-18):** a gift slip is half a letter page, so gift slips print two to a sheet with a cut line between them (10 orders with 2 gifts print on 9 sheets); a gift whose address was changed keeps a sheet of its own, with the note to change the label in the top half that is cut away. The gift layout preview shows a whole sheet with two made-up gift orders. A gift order whose buyer wrote no message prints a default one, "Enjoy your gift from Etsy" unless the shop sets its own on the Packing slips page (`Shops.GiftMessage.{id}`; the one shared message from before, `Slips.DefaultGiftMessage`, still applies to a shop without its own). Packing slips print the shop's ships-from address under From (set per shop on the Packing slips page, `Shops.FromAddress.{id}`; Etsy's API has no return address), and its city and state ("LOMBARD, IL" → "Lombard, Illinois") are the location line under the shop's name. Buyer shows the name on the buyer's Etsy profile (getUser, `Order.BuyerProfileName`, for open and gift orders), not the ship-to name; the buyer's username isn't in the API.

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
- **Not yet:** translations, digital files (videos were added later, see Phase 5). A full import of a ~300-listing shop costs roughly 10 calls for listings and lookups plus one attribute call per changed listing.

## Phase 2 implementation notes
- **Polling:** `etsy.orders.poll-all` (recurring) queues `etsy.orders.poll` per shop (priority above imports). Each poll asks `getShopReceipts` for receipts modified since the previous poll *started* (minus a 2-minute overlap for clock differences), 100 per page in creation order. Creation order matters: in update order a receipt modified mid-poll jumps to the end and shifts the rest, so offset paging could skip one; in creation order it can't. The first poll pulls the whole order history. Orders and items are matched by Etsy id (idempotent); the page offset is saved after every page so an interrupted backfill resumes. The interval comes from the `Orders.PollIntervalSeconds` setting (Admin → Settings, minimum 1 minute); recurring jobs can now take their interval from a setting (`RecurringJobDefinition.IntervalSettingKey`). Connecting a shop starts a poll.
- **Stored:** `Order` (receipt, with the raw JSON incl. address for later packing slips) and `OrderItem` (transaction, with variations incl. personalization and the photo URL). Open = paid, not shipped, status not completed/canceled/refunded (the old integration's rule). `OrderReceived` is raised for orders that become paid after the first backfill.
- **Photos:** each item's `image_url` is Etsy's CDN URL: the transaction's own listing image from the imported listings, or one `getListingsByListingIds` call (includes=Images) for listings that aren't imported (e.g. deleted). No sign-in or app access is needed to show them.
- **HA API** (`/api/ha/v1`, API key in `Authorization: Bearer` or `X-Api-Key`): `GET /shops`, `GET /open-orders` (every shop, what the integration polls), `GET /shops/{etsyShopId}/open-orders`. `HaPayloadBuilder` ports the old `build_receipt_summary` / `build_transaction_detail` / `_aggregate_items_needed`; a test compares its output with a golden file produced by running the old Python on the same receipts. Date strings use the host's time zone; `ship_by_ts` is Unix time, as before.
- **API keys:** Admin → API keys. Shown once; stored as SHA-256; last use recorded (at most every 5 minutes); revocable.
- **Job retention:** succeeded jobs the scheduler queued (order polls) are kept 24 h instead of 30 days; cleanup runs every 6 h.
- **Orders page:** open orders as cards sorted by ship-by (urgent ones tinted), a "to make" list (same aggregation as HA), and searchable history.
- **HA integration** (`etsy_shop_manager` in the `Etsy-Shop-Manager-HA` repository, HA 2024.11+): config flow (URL, key, SSL verification), re-authentication when the key is revoked, options flow for the interval, one device + Open orders sensor per shop (new shops appear automatically), `etsy_shop_manager_new_order` event + device trigger (orders open at startup are not announced), large attributes excluded from the recorder, last data kept for up to 5 minutes while the service is unreachable (deploy restarts), interval changes applied without a reload. Tests run in a real HA (`pytest-homeassistant-custom-component`) against a mocked service. The card is in `Etsy-Shop-Manager-HA-Card`; since card 3.0 the app draws its colors (`OrderColorEngine`).

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
- **Photos:** measured with the Admin photo order test on a draft listing (TopHATTwaffle, 2026-10-17, three runs). A photo's rank is just a number: uploadListingImage with an existing listing_image_id and a rank gives that photo the rank and **renumbers nothing else**, so ranks can tie (two photos at rank 1; Etsy then shows them in no fixed order) and have gaps (the real listing had 1, 2, 3, 4, 6, 7). The first run only looked like a "move" because Etsy happened to show the tie the other way. With overwrite=true the photo takes the slot and the photo shown at that rank **drops off the listing** (what broke the reverted attempt #29, which always sent overwrite=true). Reads show the change right away. The editor therefore: deletes removed photos, uploads new ones at their target rank (uploads first if every photo is replaced, so the listing always has one), then gives **every photo whose rank isn't yet its position its rank 1..n** with overwrite=false, so the end state has unique ranks, then sets changed alt text in place. The review lists every call and names any tie on Etsy; calls go one at a time and the first rejection stops the push. Photos are tracked for drift; alt text is HTML-decoded (managed state version 4). Etsy does renumber on its own, though (live shop, 2026-10-25): after two photos were deleted the photo at rank 10 was at 8, so a photo uploaded at 8 tied with it, and hours later Etsy had replaced the ties and gaps with 1..n. So a push reads the photos again after its calls and, if they aren't 1..n in the wanted order, sends a second plan from that read; and drift, pending and "did Etsy take it" compare photos by order and alt text, not rank numbers (`PhotoOrder`; photos tied in the agreed state may come back in either order). Photos added in the app stay in the image store, protected from cleanup while a draft refers to them. The test tool records each photo's rank on every read and has "number every photo 1–n" runs; the editor waits on a full-rank run on the draft listing confirming the reorder method before it is merged.
- **Clone (2026-10-17):** Clone on a listing copies its local version (edits not pushed included) into a new Etsy **draft**, in the same shop or the other one: createDraftListing (fields, price, stock, profiles), category attributes (updateListingProperty, one call each), the inventory (variations, prices and stock, from a fresh read of the source; for the other shop every combination gets the chosen processing profile), then the photos uploaded in order (rank 1..n, overwrite=false; ranks fixed only if Etsy shows a tie), then the draft is imported. For the other shop the dialog asks for shipping, processing and return profiles and section, pre-filled with the last choice for that shop (`Clone.LastChoice.{shopId}`). The review lists every call; the first rejection stops the copy and the clone record says which draft exists on Etsy and what it lacks. Personalization questions are copied too (as new questions, after the attributes); production partners only within the same shop. The app never publishes the draft.
- **Category attributes and personalization (2026-10-17):** both are editable and tracked for drift (managed state version 5, so every listing must be imported once after deploying).
  - **Attributes** are offered per the listing's category from Etsy's taxonomy properties (imported for the categories listings use): a list of values (single, or up to the property's maximum) and a scale where the property has scales. Managed state keeps `{property_id, scale_id, value_ids, values}` with values in id order and without the property name (Etsy's name can differ from the taxonomy's). A push sends updateListingProperty for each attribute that differs from Etsy and deleteListingProperty for each one removed, after updateListing so a new category is in place first. Blocked: values not in Etsy's list, too many values, a missing scale, a category whose properties the app hasn't read. Typed properties without a value list (Height, Width, Depth: 3 attributes on real listings) are shown and can be removed, but not set: Etsy gives a typed value its own value id and how it wants a new one sent can't be checked without writing to a real listing.
  - **Personalization** is edited as Etsy's question list (up to five: text box, drop-down, file upload, labeled file uploads; title ≤45, drop-down choices ≤20, upload labels ≤45 and one per file, add-on price only on an optional text box, no instructions on a drop-down). Managed state keeps the questions HTML-decoded and without Etsy's ids. A push sends one updateListingPersonalization (JSON, `supports_multiple_personalization_questions=true`, since the app reads and writes the full list) that replaces all questions, keeping Etsy's question id for each question still there (matched by type and title, else an unused one of its type) and option ids by label; an empty list sends deleteListingPersonalization.
  - **Styles and featured rank stay read-only:** Etsy's API (OpenAPI spec of 2026-10-17) no longer accepts `styles` or `featured_rank` in updateListing; styles can only be set by createDraftListing (the clone copies them), and featured rank has no write call at all.
  - Checked offline against all 131 real listings: unedited, no listing plans an attribute or personalization call; every one of the 19 settable attribute values re-selects to exactly Etsy's form; all 19 personalized listings rebuild their questions with Etsy's ids.
- **Testing:** no real Etsy writes in development or tests. `FakeEtsyShop` records every write; tests assert the exact `updateListing` form and `updateListingInventory` JSON. Offline checks against a sanitized copy of production: the migration applies, all 130 listings load without false drift, and the inventory body reproduces every real inventory exactly (48 with variations).

## Phase 4 implementation notes
- **Templates page (2026-10-18):** one sidebar entry, with a tab each for Text templates, Variation templates and Photo templates (`/templates/text`, `/templates/variations`, `/templates/photos`); the old addresses (`/text-blocks`, `/variation-lists`, `/photo-templates` and their pages) still open them. In code they keep their first names (`TextBlock`, `VariationList`, `PhotoTemplate`). Admin pages (general settings, jobs, users, audit log, API keys, Etsy writes, order colors, photo order test) sit under one collapsible Settings heading (`NavItem.Group`).
- **Text blocks (2026-10-17, placeholders since TopHATTwaffle's review):** named reusable description text (Templates page, Text templates tab; `TextBlockService`, `Modules/Templates`), placed in a description as `{{text.slug}}`, like a WordPress shortcode. A listing can't edit a block's text; only the block can.
  - **Where it lives:** the app keeps each description with its placeholders (`ListingDescription`: the source and the text it rendered to); the listing's description (draft, push, Etsy) always has the blocks' text, so Etsy never sees a placeholder. Every read checks the saved source against the listing's description; when they differ (taking Etsy's version, restoring a snapshot, discarding edits) the source is rebuilt, putting placeholders back wherever the description still has a block's text word for word. A placeholder with no block stays as written and blocks the push.
  - **Slugs:** lowercase letters, digits and hyphens, made from the name and editable; renaming one updates every description's placeholders (no change on Etsy).
  - **Using a block:** the editor's "Insert text block" puts the placeholder at the cursor or replaces the selection (the cursor is remembered on every change, so a tap on the button, which takes the focus away on phones, still knows where). A new block lists the listings whose description already contains its text word for word; "Use the block" swaps that text for the placeholder (nothing changes on Etsy).
  - **Editing a block** changes nothing by itself: each listing using it shows up to date, will change (with a line diff) or changed on Etsy; "Apply" writes the new descriptions as drafts; "Review and push" runs each listing's normal push preview and push. "Stop using" turns a listing's placeholder back into text; deleting a block does that everywhere.
  - **Upgrade from the first version:** at startup, blocks get a slug from their name and the old links become placeholders wherever the description has the block's text (the 2 production links did).
  - Built-in placeholders (`{{shop.name}}`, `{{listing.sku}}`, …) came later, see Phase 5.
- **Shared variation lists (2026-10-17):** named option lists such as "PLA Colors" (Templates page, Variation templates tab; `VariationListService`). Each option has a stable id (so a rename is a rename) and is on or off.
  - **Linking:** a list offers the listing variations whose values are mostly in it (at least 2, and at least half; on the real data a 15-colour list matches 39 variations, 15 of them completely). A link remembers the listing's variation (property) and the options as last applied to it (`ListingVariationList.AppliedOptions`), starting from what the listing has (available or not).
  - **Changes are worked out against what was last applied:** an option new to the listing and on is added (combinations available, price copied from what the price varies by, as the editor's "add value" does; stock from the listing's current stock when pushed; at the end of Etsy's dropdown); switched off makes its combinations unavailable (never deleted); switched back on makes them available; renamed renames the value. Removing an option from the list leaves listings alone. A listing's own values (not in the list) are never touched; an option removed from a listing by hand isn't put back (shown as a note); a listing whose variations changed on Etsy waits until resolved; a listing without the linked variation is flagged.
  - Apply writes drafts; "Review and push" runs each listing's normal push preview and push, as for text blocks.
  - **Adding a list to any listing (2026-10-18):** "Add listings" on the list's page (a search of every listing with room for a variation) and "Add from variation list" in the editor add the list's switched-on options as a new variation and link it. It's a custom variation (slot 513/514, as the editor's "Add variation"), named as the list's linked listings name theirs (else the list's name), editable. Every existing combination takes each option (the count is shown first), copying its price, processing and availability, so the price doesn't vary by the new variation and stock stays shared (new combinations get the listing's stock when pushed). Blocked with the reason: a listing with 2 variations already, a name it already has, more than 70 options (Etsy's limit for one variation), variations changed on Etsy. Only a draft; pushed through the normal review. Etsy's own color properties (200) aren't used for new variations: whether Etsy takes free-text values on them can't be checked without a real write.
  - **Linking from the editor (2026-10-18):** each variation in the editor shows the list it follows (Apply list now: that listing only, as drafts; Unlink: values kept) or a "Link to variation list" picker with any list (no match threshold, since it's chosen by hand): how many values are in the list, a warning when few are, and what applying would change. Linking only records the link. Blocked with the reason: the variation already follows a list, another of its variations follows this list, variations changed on Etsy.
  - **Combined templates (2026-10-25):** one variation can follow several templates in an order chosen per listing (e.g. "Basic Colors" then "Special Colors"), so no "Basic + Special" copy has to be kept. Each link keeps its own applied options and a `Position`; `PlanCombined` works out each template's changes in turn as before, then orders the values once (the templates' switched-on options in template order, the listing's own values after). A variation with one template plans exactly as before. An option in two templates belongs to the first (a note says so); later templates never add, rename, switch or remove it. Applying from any of the templates (or "Apply templates now" in the editor) brings the whole variation in line. The editor shows the templates in order (move up/down, Unlink each: its values stay as the listing's own) and a "Combine with another template" picker with a preview; moving only records the order until the next apply. On a template's page, uses show what they're combined with, and "Listings with matching variations" also offers variations that follow another template, counted on the values that template doesn't cover. Etsy's limit of 70 options per variation applies to the combined total: linking or combining past it is refused with the reason, a template that would push a listing past it shows "Too many options for Etsy" and isn't applied there, and a variation over 70 is a push blocker. Inventory payloads are unchanged (shared stock as before; whether Etsy accepts a variation grown from a list with shared stock is still unverified).
  - Reordering values and SKU patterns came later, see Phase 5.
- **Photo templates (2026-10-17):** one image with fixed alt text and a position counted from the end (Templates page, Photo templates tab; `PhotoTemplateService`). A listing's own photos come first, then its templates (position 1 last).
  - **Identity:** the template's photo on a listing is found by its alt text (the fingerprint the plan describes; each template's alt text must be unique), or by the template file while it waits in a draft. The link records which template image is on Etsy (`AppliedFileName`), the alt text it has there, and an image waiting in a draft (`PendingFileName`); after each push that includes photos, the `ListingPushed` handler records what reached Etsy. Etsy recompresses photos, so they can't be compared by content.
  - **Linking:** listings that already have a photo with the template's alt text keep it as the template's photo (taken as the current image; on the real data, 15 listings share "Shop Informational Image", the only alt text in use); others get it on the next apply.
  - **Apply** writes the listing's photos as a draft: adds the image, replaces it when the template has a new image (old copy deleted, new one uploaded), moves it into place, updates its alt text. A listing already in the right order is left exactly as it is, whatever Etsy's raw ranks (gaps included). Listings that would go over 20 photos, or whose photos changed on Etsy, are skipped. Pushing is the normal photo push (every photo its own rank 1..n, overwrite off). Template images are kept by the image cleanup.

## Phase 5 and later additions
- **New listing from scratch** (`/listings/new`, `ListingCreationService`): a guided form with Etsy's required fields and
  text rules, pre-filled from per-shop defaults (`ShopDefaultsService`, Shops page); creates an Etsy draft, imports it
  and opens the editor. Never published by the app.
- **Backups** (`BackupService`, Admin settings): scheduled and one-click backups (database snapshot, media, Data
  Protection keys, manifest) with retention, restore with a safety backup first, and a portable listing export zip.
- **Placeholders** (`PlaceholderEngine`): built-in `{{shop.*}}` and `{{listing.*}}` values next to `{{text.slug}}`
  blocks, nested blocks with loop detection; an unresolved placeholder blocks the push.
- **Variations:** values can be reordered (in the editor and in variation templates) and SKUs follow a per-listing
  pattern such as `{listing.sku}-{size}-{color}` (`SkuPatternResolver`).
- **Videos:** up to two per listing, imported into a local video store, edited and pushed like photos; Etsy shows them
  in upload order.
- **Template filters:** search, shop and status filters with bulk selection limited to the rows shown.
- **Bulk edit** is described under "Additions to build in early". Pending changes and Changed on Etsy have their own
  pages (Changed on Etsy with a side-by-side diff and bulk keep mine / take Etsy's), and their counts are buttons in
  the title bar for Admins (`ListingChangeCounts`).
- **Orders since Phase 2:** local changes to an order (personalization, variations, address, buyer note:
  `OrderOverrideService`), made marks shared with the HA card (`OrderMadeService`), listing instructions printed on
  slips (`SlipDocumentService`), automatic slip printing on the host's printer (`AutoPrint.cs`), and order colors drawn
  by the app (`OrderColorEngine`). `AGENTS.md` describes each.
- **Packing slip item divider:** multi-item packing slips (`Modules/Orders/Slips/packing.css`, `.slip-item + .slip-item`)
  render a horizontal divider line (0.75pt solid `var(--print-ink)`) with 12pt margin between adjacent items, separating
  items clearly without an extra top border or doubled bottom border.
- **Home Assistant Ingress support:** the full application can run inside Home Assistant (`X-Ingress-Path` header).
  `IngressPathBaseMiddleware` sets `HttpContext.Request.PathBase` from a sanitized header, `App.razor` computes dynamic
  `<base href="@BaseHref">`, all navigation, media URLs and redirects resolve relative to base href, and the sidebar
  starts collapsed when loaded under an Ingress path.
- **Collapsed sidebar mini icon rail:** `MudDrawer` uses `Variant="DrawerVariant.Mini"` so collapsing the sidebar keeps
  top-level icons visible in a 56px mini rail with tooltips, while hiding Settings subcategories and the footer reorder
  button until the Settings group icon is clicked to uncollapse and expand.
- **USPS shipping address verification:** domestic US shipping addresses are verified against the USPS Addresses API v3
  (OAuth 2.0 client credentials, token cached in memory until near expiry, rate limits rescheduled via `RetryLaterException`)
  when an order arrives (`OrderReceived`), when its shipping address changes (on an Etsy poll or via local override), and
  across all open US orders when enabled in Admin settings (`/admin/address-verification`). Hard alerts flag addresses
  not found (DPV "N" / 404), missing units (DPV "D"), or invalid units (DPV "S"), and soft notes flag substantive
  corrections. Order cards on the Orders page and dashboard provide **Use USPS's version** (saves as local `AddressOverride`
  preserving recipient name, and rechecks) and **Address is fine** (dismisses for that address hash). Alerts and shared CSS
  flow into the Home Assistant open orders payload (`address_alerts` and `open_orders[].address_alert`), sensor, and card.
  Staging isolates USPS calls to `http://127.0.0.1:9` and scrubs credentials on startup.
- **Personalization verification:** buyer personalization text is verified against configurable per-listing rules (color count via `OrderColorEngine.ColorsIn` scaling with item quantity and counting multi-color filaments once, must mention one of with synonyms, not empty, and regular expressions with timeout protection; optional variation filter scopes a rule to matching variation choices such as `Dual Color` vs `Single Color`; random text or variation choices recognized by `OrderColorEngine.IsRandom` always pass). Evaluated automatically on order arrival (`OrderReceived`), when personalization or variations are overridden (`OrderOverrideService`), or when rules and listing links change. Failing orders display an alert naming the failed rule on order cards on the Orders page and dashboard, with a **Looks fine** button that dismisses the alert for that text hash (invalidating automatically if personalization text or variations change). Managed under Admin Settings (`/admin/personalization-rules` and alias `/admin/personalization-verification`) with a candidate listing picker and master on/off toggle. Alerts and CSS flow into the Home Assistant open orders payload (`personalization_alerts` and `open_orders[].personalization_alert`).
- **Variation pricing and editor tables (2026-10-29):** when a listing has variations and price/processing vary on at most one variation, the editor (`EditListing.razor`, `VariationEditor`) displays each variation in its own compact sortable table (value, price on the variation that varies by price, processing profile on the variation that varies by processing, and available toggle) matching Etsy's editor, with a single non-varying price or processing profile control above the tables when either does not vary and a toggle to show all combinations. When price varies on both variations (dual-variation pricing), a **Set prices by one variation** helper above the combinations table lets the seller price all options of the other variation per option of the chosen variation at once, and then adjust exceptions (such as Random) individually in the combinations table below. The combinations table shows one sortable column per variation property plus sortable price, processing and availability columns.
- **UI polish (2026-10-29):** inline text boxes and selects share zero vertical margin in `wwwroot/app.css` so labeled and unlabeled inputs on a row sit on the same line; table filter bars live inside the `<MudPaper>` card of the table they filter (`TextBlockPage.razor`, `VariationListPage.razor`, `PhotoTemplatePage.razor`, `ProcessingProfilesPage.razor`); the Order colors page (`OrderColors.razor`, `OrderColorService`, `OrderColorEngine`) names the **Metallic** effect card with a live `<ColorBox>` preview and independent tint strength controls for Metallic (`MetallicTint`), Random (`RandomTint`), and regular Colors (`Tint`); Shop defaults (`ShopDefaultsPanel.razor`) uses compact per-shop rows; and the order polling interval setting (`OrderPollingForm.razor`) describes the default interval without referencing the old Home Assistant integration.

## Etsy errors in plain words (2026-10-17)
- Etsy's error replies (`{"error": "…"}`, or a list of field checks `{"path": "/title/0", "type": "all_caps", "message": "…"}`) are turned into one sentence: what Etsy refused, its reason (known checks and messages reworded, others cleaned up), and what to do ("Change the title and try again", "Reconnect the shop", "Try again in a few minutes" for Etsy's own errors). It is the message of every failed Etsy call, so job results, toasts, the editor's push history, the clone page and the refill panel all show it; the raw reply stays in the Etsy write log.
- The push and clone reviews check Etsy's documented text rules before anything is sent: title characters (letters, numbers, punctuation, math symbols, spaces, ™ © ®), `% : & +` once each, at most 3 words starting with two capital letters (the rule behind the rejected clone "TEST LISTING!!! APP CLONE"), and the allowed characters in tags and materials. All 136 real listings pass.

## Etsy write journal
- Every write the app sends to Etsy (each attempt, retries included) and Etsy's exact reply is kept for 90 days and shown to Admins under Etsy writes, with the job that sent it. Added after the reverted photo push (2026-10-17), whose only trace of Etsy's answers was the service log. Uploaded files are recorded by name and size only; OAuth calls and request headers are never recorded.

## Deployment
- Source: Gitea repo `TopHATTwaffle/etsy-shop-manager`, default branch `master`, mirrored publicly to GitHub (github.com/TopHATTwaffle/etsy-shop-manager), so nothing personal or private goes into it.
- Claude pushes to branches and opens PRs; `master` is protected so only TopHATTwaffle merges.
- Every push to `master` deploys automatically: Gitea Actions on a self-hosted `gitea-runner` on the Windows host (label `esm-deploy`, host mode) builds, tests, publishes, backs up the DB, upgrades the service, checks `/health`, and rolls back on failure. Setup: `docs/DEPLOY.md`.
- The runner account is not an admin: it can only control the EtsyShopManager service and write its install and data folders.
