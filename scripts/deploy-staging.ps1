#Requires -Version 7
<#
.SYNOPSIS
  Builds the branch checked out here and runs it in its own staging slot (docs/STAGING.md), so it can be looked at
  before it is merged. Never touches the live app's service, port, binaries or data.

.DESCRIPTION
  1. Publishes this checkout (self-contained win-x64; uncommitted changes included).
  2. Picks the slot: the one this branch already has; else a free one (no owner, or showing master); else a new
     slot (service, folders and firewall rule are created). Another branch's slot is never taken.
  3. Stops that slot, copies the build in and records the branch (shown in the app's "Staging" badge).
  4. Resets the slot's data from the newest pre-deploy backup of the live app when the slot is new to this branch,
     or with -RefreshData (C:\ProgramData\EtsyShopManager\backups\deploy-*, taken while the live app was stopped;
     the live database itself is never opened).
  5. Starts it, waits for /health and prints the link. When the slot is new to this branch it says so: send TopHATTwaffle
     that link. Take the slot down with scripts/remove-staging.ps1 once the branch's PR is merged.
  Slots run as environment "Staging", where the app points every Etsy address at a dead port and clears Etsy keys
  and shop tokens on every start; this script refuses to start a slot that isn't set up that way. Slot changes
  are one at a time (other callers wait). Needs an elevated shell.

.EXAMPLE
  pwsh scripts/deploy-staging.ps1                 # deploy this branch to its slot (creating one if needed)
  pwsh scripts/deploy-staging.ps1 -RefreshData    # also reset the slot's data to the latest live backup
  pwsh scripts/deploy-staging.ps1 -RefreshData -SkipBuild   # only reset the data of this branch's slot
#>
param(
    [switch]$RefreshData,
    [switch]$SkipBuild,
    [switch]$RunTests,
    [string]$LiveDataRoot = (Join-Path $env:ProgramData 'EtsyShopManager'),
    [int]$HealthTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'staging-slots.ps1')
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish-staging'
$dbFiles = 'etsyshopmanager.db', 'etsyshopmanager.db-wal', 'etsyshopmanager.db-shm'

function Invoke-Robocopy([string]$From, [string]$To) {
    New-Item -ItemType Directory -Force $To | Out-Null
    robocopy $From $To /MIR /R:3 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $From -> $To failed with exit code $LASTEXITCODE" }
    $global:LASTEXITCODE = 0
}

Assert-Elevated
$branch = (git -C $root rev-parse --abbrev-ref HEAD).Trim()
if ($branch -eq 'HEAD') { throw 'Check out a branch first: staging slots belong to a branch.' }
$commit = (git -C $root rev-parse --short HEAD).Trim()
$dirty = [bool](git -C $root status --porcelain)

# ---- Build (outside the lock: each checkout builds into its own folder) ------------------------------------
if (-not $SkipBuild) {
    if ($RunTests) {
        & (Join-Path $PSScriptRoot 'build.ps1')
        if ($LASTEXITCODE) { throw 'build.ps1 failed' }
    }
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish (Join-Path $root 'src/EtsyShopManager.Web') -c Release -r win-x64 --self-contained true -o $publishDir
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }
}

Invoke-WithStagingLock {
    # ---- Pick the slot ---------------------------------------------------------------------------------------
    $slots = @(Get-StagingSlots)
    $slot = $slots | Where-Object Owner -eq $branch | Select-Object -First 1
    $newToBranch = -not $slot
    if (-not $slot) {
        if ($SkipBuild) { throw "Branch $branch has no staging slot yet; deploy it without -SkipBuild." }
        $slot = $slots | Where-Object { -not $_.Owner -or $_.Owner -eq 'master' } | Select-Object -First 1
    }
    if (-not $slot) {
        $taken = $slots.Slot
        $n = 0..$MaxStagingSlot | Where-Object { $_ -notin $taken } | Select-Object -First 1
        if ($null -eq $n) {
            throw "All staging slots are in use: $(($slots | ForEach-Object { "$($_.Slot)=$($_.Owner)" }) -join ', '). Ask TopHATTwaffle which one to take down."
        }
        New-StagingSlot (Get-StagingSlotInfo $n)
        $slot = Get-StagingSlots | Where-Object Slot -eq $n
    }
    Assert-NotLiveApp $slot

    # ---- Only ever a slot set up as staging --------------------------------------------------------------------
    $fullData = [IO.Path]::GetFullPath($slot.DataRoot).TrimEnd('\')
    if ($slot.PathName -notmatch '--environment\s+Staging\b') {
        throw "Service '$($slot.Service)' is not started as environment Staging ($($slot.PathName))."
    }
    if ($slot.PathName -notmatch [regex]::Escape($fullData)) {
        throw "Service '$($slot.Service)' does not use the data root $fullData ($($slot.PathName))."
    }
    if ($slot.PathName -notmatch "--urls\s+\S+:$($slot.Port)\b") {
        throw "Service '$($slot.Service)' does not listen on port $($slot.Port) ($($slot.PathName))."
    }

    # ---- Stop ----------------------------------------------------------------------------------------------------
    $svc = Get-Service $slot.Service
    if ($svc.Status -ne 'Stopped') {
        Stop-Service $slot.Service
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }

    # ---- Binaries --------------------------------------------------------------------------------------------------
    if (-not $SkipBuild) {
        Invoke-Robocopy $publishDir $slot.InstallDir
        $label = if ($dirty) { "$branch (uncommitted changes)" } else { $branch }
        Set-Content (Join-Path $slot.InstallDir 'STAGING_OWNER.txt') $branch
        Set-Content (Join-Path $slot.InstallDir 'STAGING_BRANCH.txt') $label
        Set-Content (Join-Path $slot.InstallDir 'DEPLOYED_VERSION.txt') "$branch $commit$(if ($dirty) { '+dirty' }) $(Get-Date -Format o)"
        Write-Host "Deployed $branch @ $commit$(if ($dirty) { ' with uncommitted changes' }) to slot $($slot.Slot)."
    }

    # ---- Data: always fresh for a branch new to the slot ---------------------------------------------------------------
    if ($RefreshData -or $newToBranch) {
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
    Start-Service $slot.Service
    $health = "http://127.0.0.1:$($slot.Port)/health"
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    $ok = $false
    while (-not $ok -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if ((Get-Service $slot.Service).Status -eq 'Stopped') { break } # crashed on startup
        try { $ok = (Invoke-WebRequest $health -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch { }
    }
    if (-not $ok) {
        $log = Get-ChildItem (Join-Path $fullData 'logs') -Filter '*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($log) { Get-Content $log.FullName -Tail 40 | Write-Host }
        throw "Staging slot $($slot.Slot) did not become healthy within $HealthTimeoutSeconds s. Logs: $fullData\logs"
    }
    if ($newToBranch) {
        Write-Host "NEW staging instance for $branch (slot $($slot.Slot)): $($slot.Url)  <- send TopHATTwaffle this link" -ForegroundColor Green
    }
    else {
        Write-Host "Staging for $branch updated (slot $($slot.Slot)): $($slot.Url)" -ForegroundColor Green
    }
}
