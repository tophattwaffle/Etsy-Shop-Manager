# Staging copy

A second copy of the app on the same Windows host, for looking at a branch before it is merged. It runs on a copy
of the live data, cannot reach Etsy, and never touches the live service, port, binaries or data.

|              | Live                              | Staging                                   |
|--------------|-----------------------------------|-------------------------------------------|
| Service      | `EtsyShopManager`                 | `EtsyShopManagerStaging`                  |
| Port         | 5080                              | 5085                                      |
| Binaries     | `C:\Program Files\EtsyShopManager` | `C:\Program Files\EtsyShopManagerStaging` |
| Data         | `C:\ProgramData\EtsyShopManager`  | `C:\ProgramData\EtsyShopManagerStaging`   |
| Deployed by  | merging to `master`               | `scripts/deploy-staging.ps1`, any branch  |

## Why it can't reach Etsy

The staging service is started as environment **Staging** (on its command line, so a deploy can't drop it). In that
environment the app (`StagingCopy`):

- points every Etsy address (API, OAuth authorize, token) at `http://127.0.0.1:9`, where nothing listens, whatever
  the configuration says;
- on every start, before any job runs, deletes the Etsy app keys and blanks every shop's tokens, marking the shops as
  needing reconnection, so the order poller, imports and stock refills skip them.

The copied tokens couldn't be used anyway: they are encrypted with the live app's Data Protection keys, and staging has
its own. `deploy-staging.ps1` refuses to start a service that isn't set up as Staging. The app bar shows a
**Staging** badge with the deployed branch.

User accounts come across with the data, so you sign in with your normal login. Home Assistant API keys come across
too, but nothing points Home Assistant at staging.

## One-time install (Administrator)

From the repo folder, in an elevated PowerShell 7 window:

```powershell
pwsh -File scripts/install-staging.ps1
pwsh -File scripts/deploy-staging.ps1 -RefreshData
```

Staging is reached on the LAN or VPN at `http://<host>:5085`; the firewall rule allows private (RFC 1918) addresses
only (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16). To put it behind HAProxy instead, install with
`-HaproxyIp <address>` (the rule then allows only that host, and the app trusts its forwarded headers) and add a
hostname with a backend to port 5085, configured like the live one (Blazor needs WebSockets).

## Deploying a branch

From any checkout of the repo on the host (a thread's own clone or worktree):

```powershell
pwsh scripts/deploy-staging.ps1                 # build this checkout and run it; staging keeps its data
pwsh scripts/deploy-staging.ps1 -RefreshData    # also reset the data from the newest live pre-deploy backup
pwsh scripts/deploy-staging.ps1 -RunTests       # run build.ps1 (tests included) first
```

- Uncommitted changes are included, and the badge says so.
- Only one deploy runs at a time; a second one waits. Whichever branch was deployed last is what staging shows.
- **Migrations:** staging's database is upgraded by whatever branch runs. After a branch with a new migration,
  deploying a branch without it can fail to start or misbehave: deploy with `-RefreshData` to reset.
- **Data refresh** copies the newest `C:\ProgramData\EtsyShopManager\backups\deploy-*` (taken by each live deploy while
  the live app was stopped) and mirrors the photo store. The live database file itself is never opened.
- Logs: `C:\ProgramData\EtsyShopManagerStaging\logs`. The script prints the end of the latest one if staging doesn't
  come up.

## Removing it

Elevated: `Stop-Service EtsyShopManagerStaging; sc.exe delete EtsyShopManagerStaging`, remove the firewall rule
`Etsy Shop Manager Staging (TCP 5085)` and delete the two staging folders.
