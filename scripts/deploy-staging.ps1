#Requires -Version 7
<#
.SYNOPSIS
  Builds the branch checked out here and runs it as the staging copy (docs/STAGING.md), so it can be looked at
  before it is merged. Never touches the live app's service, port, binaries or data.

.DESCRIPTION
  1. Publishes this checkout (self-contained win-x64; uncommitted changes included).
  2. Stops the staging service, copies the build in and records the branch shown in the app's "Staging" badge.
  3. -RefreshData: replaces the staging database and photos with the newest pre-deploy backup of the live app
     (C:\ProgramData\EtsyShopManager\backups\deploy-*, taken while the live app was stopped; the live database
     itself is never opened). Use it the first time, and to reset staging after a branch with a migration.
  4. Starts the service and waits for /health.
  The staging service is created once with scripts/install-staging.ps1. It runs as environment "Staging", where the
  app points every Etsy address at a dead port and clears Etsy keys and shop tokens on every start; this script
  refuses to start a service that isn't set up that way. Deploys are one at a time (other callers wait).

.EXAMPLE
  pwsh scripts/deploy-staging.ps1                 # deploy this branch, keep staging's data
  pwsh scripts/deploy-staging.ps1 -RefreshData    # also reset the data to the latest live backup
  pwsh scripts/deploy-staging.ps1 -RefreshData -SkipBuild   # only reset the data
#>
param(
    [switch]$RefreshData,
    [switch]$SkipBuild,
    [switch]$RunTests,
    [string]$ServiceName = 'EtsyShopManagerStaging',
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'EtsyShopManagerStaging'),
    [string]$DataRoot = (Join-Path $env:ProgramData 'EtsyShopManagerStaging'),
    [string]$LiveDataRoot = (Join-Path $env:ProgramData 'EtsyShopManager'),
    [int]$HealthTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish-staging'
$dbFiles = 'etsyshopmanager.db', 'etsyshopmanager.db-wal', 'etsyshopmanager.db-shm'

function Invoke-Robocopy([string]$From, [string]$To) {
    New-Item -ItemType Directory -Force $To | Out-Null
    robocopy $From $To /MIR /R:3 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $From -> $To failed with exit code $LASTEXITCODE" }
    $global:LASTEXITCODE = 0
}

# ---- Preconditions: only ever the staging service, set up as staging ------------------------------------------
$fullInstall = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
$fullData = [IO.Path]::GetFullPath($DataRoot).TrimEnd('\')
if ($ServiceName -eq 'EtsyShopManager' -or $fullInstall -eq (Join-Path $env:ProgramFiles 'EtsyShopManager') -or
    $fullData -eq [IO.Path]::GetFullPath($LiveDataRoot).TrimEnd('\')) {
    throw 'That is the live app''s service or folder. This script only deploys the staging copy.'
}
$service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
if (-not $service) { throw "Service '$ServiceName' is not installed. An administrator runs scripts/install-staging.ps1 once." }
if ($service.PathName -notmatch '--environment\s+Staging\b') {
    throw "Service '$ServiceName' is not started as environment Staging ($($service.PathName)). Re-run scripts/install-staging.ps1."
}
if ($service.PathName -notmatch [regex]::Escape($fullData)) {
    throw "Service '$ServiceName' does not use the data root $fullData ($($service.PathName))."
}
$port = if ($service.PathName -match '--urls\s+\S+:(\d+)') { $Matches[1] } else { throw "No --urls port on $ServiceName's command line." }
if ($port -eq '5080') { throw 'Staging must not use the live app''s port 5080.' }

# One deploy at a time: threads share this service.
$mutex = [Threading.Mutex]::new($false, 'Global\EtsyShopManagerStagingDeploy')
try { $null = $mutex.WaitOne() } catch [Threading.AbandonedMutexException] { }
try {
    # ---- Build ----------------------------------------------------------------------------------------------------
    $branch = (git -C $root rev-parse --abbrev-ref HEAD).Trim()
    $commit = (git -C $root rev-parse --short HEAD).Trim()
    $dirty = [bool](git -C $root status --porcelain)
    if (-not $SkipBuild) {
        if ($RunTests) {
            & (Join-Path $PSScriptRoot 'build.ps1')
            if ($LASTEXITCODE) { throw 'build.ps1 failed' }
        }
        if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
        dotnet publish (Join-Path $root 'src/EtsyShopManager.Web') -c Release -r win-x64 --self-contained true -o $publishDir
        if ($LASTEXITCODE) { throw 'dotnet publish failed' }
    }

    # ---- Stop ----------------------------------------------------------------------------------------------------
    $svc = Get-Service $ServiceName
    if ($svc.Status -ne 'Stopped') {
        Stop-Service $ServiceName
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }

    # ---- Binaries --------------------------------------------------------------------------------------------------
    if (-not $SkipBuild) {
        Invoke-Robocopy $publishDir $fullInstall
        $label = if ($dirty) { "$branch (uncommitted changes)" } else { $branch }
        Set-Content (Join-Path $fullInstall 'STAGING_BRANCH.txt') $label
        Set-Content (Join-Path $fullInstall 'DEPLOYED_VERSION.txt') "$branch $commit$(if ($dirty) { '+dirty' }) $(Get-Date -Format o)"
        Write-Host "Deployed $branch @ $commit$(if ($dirty) { ' with uncommitted changes' })."
    }

    # ---- Data --------------------------------------------------------------------------------------------------------
    if ($RefreshData) {
        $backup = Get-ChildItem (Join-Path $LiveDataRoot 'backups') -Directory -Filter 'deploy-*' |
            Where-Object { Test-Path (Join-Path $_.FullName 'etsyshopmanager.db') } |
            Sort-Object Name -Descending | Select-Object -First 1
        if (-not $backup) { throw "No pre-deploy backup with a database under $LiveDataRoot\backups." }

        New-Item -ItemType Directory -Force $fullData | Out-Null
        foreach ($f in $dbFiles) {
            $target = Join-Path $fullData $f
            if (Test-Path $target) { Remove-Item $target -Force }
            $saved = Join-Path $backup.FullName $f
            if (Test-Path $saved) { Copy-Item $saved $target }
        }
        Invoke-Robocopy (Join-Path $LiveDataRoot 'images') (Join-Path $fullData 'images')
        Write-Host "Data reset from $($backup.Name) (the app clears Etsy keys and tokens as it starts)."
    }

    # ---- Start ---------------------------------------------------------------------------------------------------------
    Start-Service $ServiceName
    $health = "http://127.0.0.1:$port/health"
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    $ok = $false
    while (-not $ok -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if ((Get-Service $ServiceName).Status -eq 'Stopped') { break } # crashed on startup
        try { $ok = (Invoke-WebRequest $health -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch { }
    }
    if (-not $ok) {
        $log = Get-ChildItem (Join-Path $fullData 'logs') -Filter '*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($log) { Get-Content $log.FullName -Tail 40 | Write-Host }
        throw "Staging did not become healthy within $HealthTimeoutSeconds s. Logs: $fullData\logs"
    }
    Write-Host "Staging is up: http://$($env:COMPUTERNAME):$port/ (/health OK)." -ForegroundColor Green
}
finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
