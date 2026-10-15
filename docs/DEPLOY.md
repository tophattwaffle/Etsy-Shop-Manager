# Automatic deployment (Gitea Actions)

Every push to `master` (in practice: every PR you merge) is built, tested and deployed to the Windows host by a
self-hosted Gitea Actions runner installed **on that host**. The runner only makes outbound connections to Gitea,
so no inbound port is opened.

```
merge PR → master ──► Gitea ──(runner polls)──► act_runner on the Windows host
                                                  build + test + check migrations
                                                  publish win-x64
                                                  scripts/deploy.ps1:
                                                    stop service → back up DB + binaries
                                                    copy new build → start → wait for /health
                                                    unhealthy? restore binaries + DB, start old version, fail the run
```

Workflow: `.gitea/workflows/deploy.yml`. It can also be started by hand (Actions → Deploy → Run workflow).

## Security model — read this first

- **Anyone who can push to this repo can run code on the host as the runner account** (a workflow file on any
  branch runs on the runner). That includes the bot token Claude uses. So the runner account is deliberately
  **not an administrator**: it can only start/stop the EtsyShopManager service and write to its install and data
  folders. Its blast radius is this app and its data, not the rest of the machine.
- Protect `master` so only you can push or merge to it (step 6). Deploys then happen only when you merge.
- The app's own data (database, Data Protection keys) is reachable by the runner account by design: it has to
  back up and restore the database, and it installs the code that runs as the service anyway.
- Revoke the bot token in Gitea at any time to cut off pushes.

## One-time setup on the Windows host

### 1. Prerequisites
- .NET 10 SDK, Git, PowerShell 7 (`pwsh`)
- **Node.js LTS**, needed by `actions/checkout` (Gitea downloads that action from GitHub by default)
- The service already installed once with `scripts/install-service.ps1` (elevated). The pipeline only upgrades it.

### 2. Create the runner account (not an admin)
Elevated PowerShell:
```powershell
$pw = Read-Host -AsSecureString 'Password for gitea-runner'
New-LocalUser -Name gitea-runner -Password $pw -PasswordNeverExpires -UserMayNotChangePassword `
    -Description 'Gitea Actions runner for Etsy Shop Manager'
```

### 3. Grant it deploy rights
From the repo folder, elevated:
```powershell
pwsh -File scripts/grant-deploy-rights.ps1 -Account "$env:COMPUTERNAME\gitea-runner"
```
This adds start/stop/query on the `EtsyShopManager` service only, plus Modify on
`C:\Program Files\EtsyShopManager` and `C:\ProgramData\EtsyShopManager`.

### 4. Install and register act_runner
1. Download the Windows amd64 `act_runner` binary from https://gitea.com/gitea/act_runner/releases into
   `C:\act_runner\act_runner.exe`, then give the runner account Modify on that folder:
   `icacls C:\act_runner /grant "$env:COMPUTERNAME\gitea-runner:(OI)(CI)M"`
2. Get a registration token: repo → **Settings → Actions → Runners → Create new runner**
   (`https://gitea.example.lan/TopHATTwaffle/etsy-shop-manager/settings/actions/runners`).
   A repo-level runner only picks up jobs from this repo.
3. Generate the config and register:
   ```powershell
   cd C:\act_runner
   .\act_runner.exe generate-config > config.yaml
   # In config.yaml: runner.capacity: 1   (one deploy at a time)
   .\act_runner.exe register --no-interactive --config config.yaml `
       --instance https://git.example.lan --token <registration token> `
       --name esm-host --labels esm-deploy:host
   ```
   The `esm-deploy` label is what the workflow's `runs-on` targets; `:host` runs jobs directly on Windows
   (no Docker).

### 5. Run act_runner as a service under the runner account
act_runner has no built-in Windows service installer; [NSSM](https://nssm.cc/) is the usual wrapper:
```powershell
nssm install act_runner C:\act_runner\act_runner.exe daemon --config C:\act_runner\config.yaml
nssm set act_runner AppDirectory C:\act_runner
nssm set act_runner ObjectName "$env:COMPUTERNAME\gitea-runner" "<password>"
nssm set act_runner Start SERVICE_DELAYED_AUTO_START
nssm start act_runner
```
If it fails to start with a logon error, give `gitea-runner` the **Log on as a service** right
(`secpol.msc` → Local Policies → User Rights Assignment) and start it again.

The runner should now show as online under the repo's **Settings → Actions → Runners**.

### 6. Protect master
Repo → **Settings → Branches → Add rule** for `master`:
- Enable push restriction and allow only your account.
- Leave merging through pull requests on.

Claude then pushes to branches and opens PRs; nothing deploys until you merge.

## Using it

- **Deploy:** merge a PR into `master`. Watch it under the repo's **Actions** tab.
- **Redeploy / retry:** Actions → Deploy → Run workflow.
- **What's running:** `C:\Program Files\EtsyShopManager\DEPLOYED_VERSION.txt` holds the commit and time.
- **Backups:** each deploy copies the database to `C:\ProgramData\EtsyShopManager\backups\deploy-<time>-<commit>`
  (newest 10 kept) and the previous binaries to `C:\ProgramData\EtsyShopManager\deploy\previous`.
- **Failed deploy:** the run fails and the previous version is back up with the pre-deploy database. Check
  `C:\ProgramData\EtsyShopManager\logs` for why the new version didn't start.
- **Manual rollback to an older commit:** revert the commit on `master` (PR → merge); that deploys the revert.
  Restoring a database backup by hand: stop the service, copy the three `etsyshopmanager.db*` files back from a
  `backups\deploy-*` folder (delete any live `-wal`/`-shm` that the backup lacks), start the service.
