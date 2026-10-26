# Etsy Shop Manager

Self-hosted manager for two Etsy shops. Runs as a Windows Service, web UI (Blazor Server + MudBlazor) used from any
device on the LAN or over VPN, behind HAProxy. This app is the system of record for listings; see
[docs/PLAN.md](docs/PLAN.md) for the full design.

**Status: every phase of the plan is implemented** (foundation, listing import, orders + Home Assistant, the listing
editor with preview → push and drift handling, templates, new listings and auto-refill). Phases 0–2 are summarised
below; the rest is in [docs/PLAN.md](docs/PLAN.md). What is left to do: [docs/BACKLOG.md](docs/BACKLOG.md).

## Quick start (Windows)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-prereqs.ps1  # elevated: .NET 10 SDK, PowerShell 7, deploy tools
pwsh scripts/build.ps1                                          # restore, build, check migrations, test
pwsh -File scripts/install-service.ps1 -HaproxyIp 192.168.1.10  # elevated: install/upgrade the service
```

Then open your HAProxy hostname and follow the first-run wizard. Details: [docs/SETUP.md](docs/SETUP.md).
After that, merges to `master` deploy automatically: [docs/DEPLOY.md](docs/DEPLOY.md).

## What's in Phase 0

- Windows Service hosting, self-contained publish, firewall rule, event log + daily rolling file logs.
- Local accounts (ASP.NET Core Identity) with Admin / Editor / Viewer roles, lockout, user management, audit log.
- First-run wizard: Admin → base URL (shows the Etsy redirect URI) → Etsy keystring/secret → connect shops.
- Etsy OAuth 2.0 + PKCE per shop; tokens encrypted with Data Protection (DPAPI); refresh on demand with
  one refresh per shop at a time; rejected refresh tokens flag the shop for reconnection.
- Etsy HTTP pipeline: `x-api-key: keystring:secret`, shared token-bucket rate limiter, retry with back-off and
  `Retry-After`, long waits turned into job rescheduling.
- Persistent job queue (SQLite) with retries, back-off, dedupe keys, restart recovery, recurring jobs, admin UI.
- Module system (`IModule`) and in-process domain events (`ShopConnected`, `ShopNeedsReauth`, …).
- Reverse-proxy support: forwarded headers trusted from the HAProxy IP only.

## What's in Phase 1

- Import of every listing (active, inactive, sold out, draft, expired) with images, inventory (products, offerings,
  variation values) and category attributes; per-shop shipping/processing/return profiles, sections and production
  partners; Etsy's seller taxonomy and the properties of categories in use.
- Runs as background jobs: on demand per shop, automatically when a shop is connected, and every 24 h. Idempotent
  (matched by Etsy id) and resumable page by page; listings no longer on Etsy are flagged, never deleted.
- Photos downloaded into a content-hashed image store next to the database and served by the app.
- The last known Etsy state of each listing (raw JSON + canonical managed fields + hash, quantity excluded), for
  Phase 3 drift detection.
- Read-only UI: Listings (search, filters, paging), listing detail (fields, photos, inventory grid, Etsy state) and Lookups.

## What's in Phase 2

- Order polling for every shop (default every 5 minutes, set in Admin → Settings), stored locally with full history.
- Orders page: open orders by ship-by date with items, variations, personalization, notes and photos; a combined
  "to make" list; searchable history.
- API keys (Admin → API keys) and a read-only Home Assistant API (`/api/ha/v1`) with the open-orders payload the
  existing card reads.
- A new Home Assistant integration, `etsy_shop_manager`: an Open orders sensor per shop, a new-order event and device
  trigger, in its own repository ([Etsy-Shop-Manager-HA](https://github.com/TopHATTwaffle/Etsy-Shop-Manager-HA)),
  with the dashboard card in [Etsy-Shop-Manager-HA-Card](https://github.com/TopHATTwaffle/Etsy-Shop-Manager-HA-Card).

## Solution layout

```
src/
  EtsyShopManager.Web/    Host: Program.cs, Blazor UI, Identity, setup wizard, job processor, modules, OAuth endpoints
  EtsyShopManager.Core/   Domain entities, job/event/module contracts, storage options
  EtsyShopManager.Etsy/   OAuth/PKCE, token service, rate limiter, retry, API client
  EtsyShopManager.Data/   EF Core DbContext (SQLite), Identity stores, migrations
tests/EtsyShopManager.Tests/
scripts/                  install-prereqs, build, add-migration, install-service, deploy, grant-deploy-rights, uninstall-service,
                          deploy-staging, list-staging, remove-staging
.gitea/workflows/         deploy on push to master (self-hosted runner on the Windows host)
docs/                     PLAN.md, BACKLOG.md, SETUP.md, DEPLOY.md, STAGING.md, DESIGN.md
```

## Adding a feature module

1. Implement `IModule` (see `src/EtsyShopManager.Web/Modules/BuiltInModules.cs`): register services, job handlers
   (`services.AddJobHandler<T>()`), recurring jobs, endpoints and nav items.
2. Subscribe to events with `IDomainEventHandler<TEvent>` instead of editing other modules.
3. Add it to `builder.AddModules(...)` in `Program.cs`.
4. Changed the model? `pwsh scripts/add-migration.ps1 -Name <Change>`.
