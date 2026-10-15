# Etsy Shop Manager

Self-hosted manager for two Etsy shops. Runs as a Windows Service, web UI (Blazor Server + MudBlazor) used from any
device on the LAN or over VPN, behind HAProxy. This app is the system of record for listings; see
[docs/PLAN.md](docs/PLAN.md) for the full design.

**Status: Phase 0 (Foundation) implemented.** Next is Phase 1 (import listings, images, inventory, profiles, taxonomy).

## Quick start (Windows)

```powershell
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

## Solution layout

```
src/
  EtsyShopManager.Web/    Host: Program.cs, Blazor UI, Identity, setup wizard, job processor, modules, OAuth endpoints
  EtsyShopManager.Core/   Domain entities, job/event/module contracts, storage options
  EtsyShopManager.Etsy/   OAuth/PKCE, token service, rate limiter, retry, API client
  EtsyShopManager.Data/   EF Core DbContext (SQLite), Identity stores, migrations
tests/EtsyShopManager.Tests/
scripts/                  build, add-migration, install-service, deploy, grant-deploy-rights, uninstall-service
.gitea/workflows/         deploy on push to master (self-hosted runner on the Windows host)
docs/                     PLAN.md, SETUP.md, DEPLOY.md
homeassistant/            (Phase 2) HA integration and the existing card
```

## Adding a feature module

1. Implement `IModule` (see `src/EtsyShopManager.Web/Modules/BuiltInModules.cs`): register services, job handlers
   (`services.AddJobHandler<T>()`), recurring jobs, endpoints and nav items.
2. Subscribe to events with `IDomainEventHandler<TEvent>` instead of editing other modules.
3. Add it to `builder.AddModules(...)` in `Program.cs`.
4. Changed the model? `pwsh scripts/add-migration.ps1 -Name <Change>`.
