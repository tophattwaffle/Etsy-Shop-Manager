#Requires -Version 7
<#
.SYNOPSIS
  Upgrades the installed Windows Service from a publish folder, with backup, health check and rollback.

.DESCRIPTION
  Used by the Gitea Actions deploy workflow, and can be run by hand. Does not need Administrator: the account
  only needs the rights granted by scripts/grant-deploy-rights.ps1 (control of this one service, Modify on the
  install and data folders). The first install is done once with scripts/install-service.ps1.

  1. Stop the service.
  2. Back up the database (etsyshopmanager.db + -wal/-shm) and the current binaries.
  3. Copy the new build in (appsettings.Production.json is kept).
  4. Start the service and wait for /health.
  5. If it doesn't come up healthy: restore binaries and database, start the old version, and fail.

.EXAMPLE
  pwsh scripts/deploy.ps1 -PublishDir artifacts/publish -Version abc1234
#>
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [string]$Version = 'manual',
    [string]$ServiceName = 'EtsyShopManager',
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'EtsyShopManager'),
    [string]$DataRoot = (Join-Path $env:ProgramData 'EtsyShopManager'),
    [int]$HealthTimeoutSeconds = 90,
    [int]$KeepBackups = 10
)

$ErrorActionPreference = 'Stop'

function Invoke-Robocopy([string]$From, [string]$To) {
    New-Item -ItemType Directory -Force $To | Out-Null
    robocopy $From $To /MIR /XF appsettings.Production.json /R:3 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $From -> $To failed with exit code $LASTEXITCODE" }
    $global:LASTEXITCODE = 0
}

function Get-HealthUrl {
    $urls = $null
    $cfg = Join-Path $InstallDir 'appsettings.Production.json'
    if (Test-Path $cfg) { $urls = (Get-Content $cfg -Raw | ConvertFrom-Json).Urls }
    if (-not $urls) { $urls = 'http://0.0.0.0:5080' }
    $first = ($urls -split ';')[0] -replace '://(\*|\+|0\.0\.0\.0|\[::\])', '://127.0.0.1'
    $uri = [Uri]$first
    return "$($uri.Scheme)://127.0.0.1:$($uri.Port)/health"
}

function Stop-App {
    $svc = Get-Service $ServiceName
    if ($svc.Status -ne 'Stopped') {
        Stop-Service $ServiceName
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
}

function Start-AppAndWaitHealthy {
    Start-Service $ServiceName
    $url = Get-HealthUrl
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if ((Get-Service $ServiceName).Status -eq 'Stopped') { return $false } # crashed on startup
        try {
            $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5
            if ($r.StatusCode -eq 200) { return $true }
        }
        catch { }
    }
    return $false
}

# ---- Preconditions -----------------------------------------------------------------------------------
$PublishDir = (Resolve-Path $PublishDir).Path
if (-not (Test-Path (Join-Path $PublishDir 'EtsyShopManager.exe'))) {
    throw "$PublishDir does not contain EtsyShopManager.exe. Publish with -r win-x64 first."
}
if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    throw "Service '$ServiceName' is not installed. Run scripts/install-service.ps1 once as Administrator."
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$short = if ($Version.Length -gt 8) { $Version.Substring(0, 8) } else { $Version }
$dbBackup = Join-Path $DataRoot "backups\deploy-$stamp-$short"
$previous = Join-Path $DataRoot 'deploy\previous'
$dbFiles = 'etsyshopmanager.db', 'etsyshopmanager.db-wal', 'etsyshopmanager.db-shm'

Write-Host "Deploying $Version to $InstallDir"

# ---- Stop + back up --------------------------------------------------------------------------------------
Stop-App

New-Item -ItemType Directory -Force $dbBackup | Out-Null
foreach ($f in $dbFiles) {
    $src = Join-Path $DataRoot $f
    if (Test-Path $src) { Copy-Item $src $dbBackup }
}
Write-Host "Database backed up to $dbBackup"

Invoke-Robocopy $InstallDir $previous

# ---- Install + verify ------------------------------------------------------------------------------------
$ok = $false
try {
    Invoke-Robocopy $PublishDir $InstallDir
    Set-Content (Join-Path $InstallDir 'DEPLOYED_VERSION.txt') "$Version $(Get-Date -Format o)"
    $ok = Start-AppAndWaitHealthy
}
catch {
    Write-Warning "Deploy step failed: $_"
}

if ($ok) {
    Write-Host "Deployed $Version; /health OK." -ForegroundColor Green

    # Keep the newest $KeepBackups pre-deploy database backups.
    Get-ChildItem (Join-Path $DataRoot 'backups') -Directory -Filter 'deploy-*' |
        Sort-Object Name -Descending | Select-Object -Skip $KeepBackups |
        Remove-Item -Recurse -Force
    exit 0
}

# ---- Roll back --------------------------------------------------------------------------------------------
Write-Warning "New version did not become healthy within $HealthTimeoutSeconds s. Rolling back. See $DataRoot\logs."
try { Stop-App } catch { Write-Warning "Stop during rollback: $_" }

Invoke-Robocopy $previous $InstallDir
foreach ($f in $dbFiles) {
    $live = Join-Path $DataRoot $f
    $saved = Join-Path $dbBackup $f
    if (Test-Path $live) { Remove-Item $live -Force }
    if (Test-Path $saved) { Copy-Item $saved $live }
}

if (Start-AppAndWaitHealthy) {
    throw "Deploy of $Version failed; rolled back to the previous version (healthy)."
}
throw "Deploy of $Version failed AND the previous version did not come back healthy. Check $DataRoot\logs and the Application event log."
