# Staging

Copies of the app on the same Windows host, one per branch, for looking at a branch before it is merged. Each runs
on a copy of the live data, cannot reach Etsy, and never touches the live service, port, binaries or data.

## Slots

Staging runs in numbered **slots**, one branch each:

|                 | Live                               | Staging slot 0                            | Staging slot n (1-9)                          |
|-----------------|------------------------------------|-------------------------------------------|-----------------------------------------------|
| Service         | `EtsyShopManager`                  | `EtsyShopManagerStaging`                  | `EtsyShopManagerStaging<n>`                   |
| Port            | 5080                               | 5085                                      | 5100+n (5101, 5102, …)                        |
| Binaries        | `C:\Program Files\EtsyShopManager` | `C:\Program Files\EtsyShopManagerStaging` | `C:\Program Files\EtsyShopManagerStaging<n>`  |
| Data            | `C:\ProgramData\EtsyShopManager`   | `C:\ProgramData\EtsyShopManagerStaging`   | `C:\ProgramData\EtsyShopManagerStaging<n>`    |
| Deployed by     | merging to `master`                | `scripts/deploy-staging.ps1`              | `scripts/deploy-staging.ps1`                  |

Each slot is reached on the LAN or VPN at `http://<host LAN address>:<port>/`; its firewall rule allows private
(RFC 1918) addresses only. The branch a slot belongs to is in `STAGING_OWNER.txt` next to its binaries, and the app
bar shows a **Staging** badge with the branch.

## The workflow for threads

1. **Deploy** from the branch's own checkout (elevated shell, which Claude's sessions on the host are):
   `pwsh scripts/deploy-staging.ps1`. It uses the slot the branch already has; otherwise a free slot (no owner, or
   one showing `master`); otherwise it creates a new slot. It never takes another branch's slot.
2. **Send TopHATTwaffle the link** whenever the script prints `NEW staging instance for <branch> … <- send TopHATTwaffle this link`.
   Later deploys of the same branch update that slot in place (same link).
3. **Take it down once the PR is merged:** `pwsh scripts/remove-staging.ps1` (or `-Branch <name>`). It deletes the
   slot's service, firewall rule, binaries and data.

`pwsh scripts/list-staging.ps1` shows every slot, its branch, link and state.

Details:

- Uncommitted changes are included, and the badge says so. A detached checkout is refused: slots belong to a branch.
- A slot that is new to a branch always gets fresh data. `-RefreshData` resets a branch's slot again (needed after
  deploying an older commit of the branch than one with a migration). `-RefreshData -SkipBuild` only resets the data.
- Data comes from the newest `C:\ProgramData\EtsyShopManager\backups\deploy-*` (taken by each live deploy while the
  live app was stopped), plus a mirror of the photo store. The live database file itself is never opened. Staging
  never polls Etsy, so its data stays as it was at the last reset.
- Slot changes run one at a time; a second caller waits. With all ten slots taken, the script stops and lists them:
  ask TopHATTwaffle which one to take down.
- Each slot takes about 400 MB (binaries, database, photos).
- Logs: `<slot data root>\logs`. The script prints the end of the latest one if a slot doesn't come up.

## Why it can't reach Etsy

Every slot's service is started as environment **Staging** (on its command line, so a deploy can't drop it). In that
environment the app (`StagingCopy`):

- points every Etsy address (API, OAuth authorize, token) at `http://127.0.0.1:9`, where nothing listens, whatever
  the configuration says;
- on every start, before any job runs, deletes the Etsy app keys and blanks every shop's tokens, marking the shops as
  needing reconnection, so the order poller, imports and stock refills skip them.

The copied tokens couldn't be used anyway: they are encrypted with the live app's Data Protection keys, and each slot
has its own. `deploy-staging.ps1` refuses to start a slot that isn't set up as Staging on its own port and data root.

User accounts come across with the data, so TopHATTwaffle signs in with his normal login. Home Assistant API keys come across
too, but nothing points Home Assistant at staging.

## Scripts

- `scripts/deploy-staging.ps1`: build, pick or create the slot, deploy, (re)set data, start, print the link.
- `scripts/remove-staging.ps1`: take a branch's slot down.
- `scripts/list-staging.ps1`: list slots.
- `scripts/staging-slots.ps1`: shared slot helpers (naming, create, remove, lock), dot-sourced by the others.
