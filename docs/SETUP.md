# Setup and operations

## 1. Prerequisites (Windows host)

One script installs everything (skipping anything already present). In an elevated **Windows PowerShell** window,
from the repo folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-prereqs.ps1
```

It installs the .NET 10 SDK and PowerShell 7, plus the auto-deploy tools (Git, Node.js LTS, NSSM, gitea-runner).
Add `-SkipRunner` to install only the first two. Then open a **new** elevated PowerShell 7 (`pwsh`) window for the
remaining steps, so the PATH changes apply.

- The repo can live in Dropbox. The live data cannot: it goes to `C:\ProgramData\EtsyShopManager`, and the app
  refuses to start if `Storage:DataRoot` points into Dropbox or OneDrive.
- Run the scripts from a local path on the host, not a mapped drive or `\\server\share` path: elevated sessions
  don't see mapped drives, and PowerShell may block scripts on network paths.

## 2. First build

```powershell
pwsh scripts/build.ps1
```

EF Core migrations are committed in `src/EtsyShopManager.Data/Migrations`. After a model change, run
`pwsh scripts/add-migration.ps1 -Name <Change>` and commit the result; the build fails if the model and migrations
don't match. Pending migrations are applied automatically when the app starts.

## 3. Try it locally (optional)

```powershell
dotnet run --project src/EtsyShopManager.Web
```

Opens http://localhost:5080 with a separate dev data folder (`%LOCALAPPDATA%\EtsyShopManager-dev`), including
its own image store.
You can create the Admin and look around, but **connecting a shop needs the HTTPS base URL** (Etsy only accepts
https redirect URIs), so do that against the installed service behind HAProxy.

## 4. HAProxy

Kestrel listens on plain HTTP (`:5080`). HAProxy terminates TLS and forwards. Blazor Server uses a long-lived
WebSocket, so the backend needs a long tunnel timeout.

```haproxy
frontend https_in
    bind :443 ssl crt /etc/haproxy/certs/
    mode http
    option forwardfor
    http-request set-header X-Forwarded-Proto https
    use_backend etsy_shop_manager if { hdr(host) -i etsy.example.lan }

backend etsy_shop_manager
    mode http
    timeout tunnel 1h          # Blazor/SignalR WebSocket
    option httpchk GET /health
    server esm 192.168.1.50:5080 check
```

Then put the HAProxy IP in `ReverseProxy:TrustedProxies` (the install script does this). Only that IP may set
`X-Forwarded-For` / `X-Forwarded-Proto`.

## 5. Install as a Windows Service

From an elevated PowerShell 7 prompt:

```powershell
pwsh -File scripts/install-service.ps1 -HaproxyIp 192.168.1.10
```

This publishes a self-contained build to `C:\Program Files\EtsyShopManager`, creates the service
(`NT SERVICE\EtsyShopManager`, delayed auto-start, restart on failure), grants it modify rights on
`C:\ProgramData\EtsyShopManager` only, and opens TCP 5080 in Windows Firewall for the HAProxy host only.

Re-run the same command to upgrade. Host-specific settings live in
`C:\Program Files\EtsyShopManager\appsettings.Production.json`, which upgrades never overwrite.

After this first install, upgrades can be automatic: see [DEPLOY.md](DEPLOY.md).

Uninstall: `pwsh -File scripts/uninstall-service.ps1` (add `-RemoveData` to delete the data folder too).

## 6. First-run wizard

Browse to your HAProxy hostname (e.g. `https://etsy.example.lan`). With no Admin yet, every page goes to the wizard:

1. **Create the Admin account.**
2. **Base URL**: the HTTPS hostname HAProxy serves. The page shows the redirect URI
   (`{BaseUrl}/auth/etsy/callback`) with a copy button. Add it to your app in the Etsy developer portal
   (Your Apps → your app → callback URLs). It only has to be reachable from your browser, not from the internet.
3. **Etsy keystring and shared secret** from the same portal page. Both are stored encrypted (DPAPI).
4. **Connect shops**: one OAuth sign-in per shop. Etsy connects whichever Etsy account is signed in on etsy.com
   in that browser, so sign in to the second shop's account (or use its browser) before connecting it.

The base URL and keys can be changed later under **Admin → Settings**. Changing the base URL shows the new
redirect URI and a reminder to update it in the Etsy portal; existing shop connections keep working.

## 7. Where things are

| What | Where |
|---|---|
| Database (SQLite, WAL) | `C:\ProgramData\EtsyShopManager\etsyshopmanager.db` |
| Data Protection keys (encrypt tokens, secrets, cookies) | `C:\ProgramData\EtsyShopManager\keys` |
| Images (content-hashed listing photos) | `C:\ProgramData\EtsyShopManager\images` |
| Logs (14 days) | `C:\ProgramData\EtsyShopManager\logs\esm-yyyyMMdd.log`, warnings also in the Application event log |
| Health check | `GET /health` |

**Back up the whole data folder, including `keys`.** Without the keys, stored tokens and the shared secret
cannot be decrypted; you would re-enter the secret and reconnect both shops. Keys are protected with machine-scope
DPAPI, so a backup restores only on the same Windows installation.

To copy the database safely while the service runs, use SQLite's backup (`sqlite3 etsyshopmanager.db ".backup copy.db"`)
or stop the service first. Don't copy just the `.db` file while it's running (the `-wal` file holds recent writes).

## 8. Background jobs

All Etsy work runs through a persistent job queue (survives restarts; interrupted jobs are re-queued on startup).
**Jobs** (Admin) shows the queue, recurring jobs with "Run now", and retry/cancel.

| Recurring job | Every | Purpose |
|---|---|---|
| Etsy token keep-alive | 12 h | Refreshes both shops' tokens so the ~90-day refresh token keeps rolling |
| Refresh shop details | 24 h | Updates shop name, URL, icon, currency |
| Clean up old jobs | 24 h | Deletes succeeded jobs after 30 days, failed after 90 |
| Import listings (all shops) | 24 h | Pulls every shop's listings, lookups, then photos and taxonomy |

**Listing import.** Connecting a shop queues its first import. After that it runs every 24 h, or on demand from
**Listings → Import now** (Admin/Editor). "Full re-import" also re-reads every listing's category attributes (normally
only changed listings are re-read). The import saves its position after every page, so a restart continues where it
stopped. Photos download in a separate, lower-priority job; the Listings page shows how many are still pending.
Lookups can be refreshed on their own from the **Lookups** page.

A rejected refresh token marks the shop **Reconnect needed** (dashboard and Shops page) and is recorded in the audit log.

## 9. Etsy rate limits

One token bucket is shared by both shops (Etsy limits per API key): 5 requests/second by default (`Etsy:RequestsPerSecond`).
429 and 5xx responses are retried with back-off and `Retry-After`. Waits longer than 30 seconds reschedule the job
instead of blocking. The dashboard shows Etsy's reported daily quota.
