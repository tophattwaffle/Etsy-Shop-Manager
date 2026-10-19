#Requires -Version 7
<#
.SYNOPSIS
  Shared by deploy-staging.ps1, remove-staging.ps1 and list-staging.ps1 (docs/STAGING.md); dot-source it.

.DESCRIPTION
  Staging runs in numbered slots, one branch each. Slot 0 is service EtsyShopManagerStaging on port 5085; slot n
  (1-9) is EtsyShopManagerStaging<n> on port 5100+n. Each slot has its own install folder, data root, Windows Service
  (started as environment Staging, which can't reach Etsy) and firewall rule (private RFC 1918 addresses only).
  The branch a slot belongs to is in STAGING_OWNER.txt next to its binaries.
#>

$script:MaxStagingSlot = 9
$script:StagingDeployMutexName = 'Global\EtsyShopManagerStagingDeploy'

function Get-StagingSlotInfo([int]$Slot) {
    $suffix = if ($Slot -eq 0) { '' } else { "$Slot" }
    $port = if ($Slot -eq 0) { 5085 } else { 5100 + $Slot }
    [pscustomobject]@{
        Slot       = $Slot
        Service    = "EtsyShopManagerStaging$suffix"
        Port       = $port
        InstallDir = Join-Path $env:ProgramFiles "EtsyShopManagerStaging$suffix"
        DataRoot   = Join-Path $env:ProgramData "EtsyShopManagerStaging$suffix"
        Firewall   = "Etsy Shop Manager Staging (TCP $port)"
        Url        = "http://$(Get-StagingHostAddress):$port/"
    }
}

# The host's LAN address (the one with a default gateway), which is what works over the VPN.
function Get-StagingHostAddress {
    if (-not $script:StagingHostAddress) {
        $ip = Get-NetIPConfiguration -ErrorAction SilentlyContinue | Where-Object IPv4DefaultGateway |
            ForEach-Object { $_.IPv4Address.IPAddress } | Select-Object -First 1
        $script:StagingHostAddress = if ($ip) { $ip } else { $env:COMPUTERNAME }
    }
    $script:StagingHostAddress
}

# Installed slots with their owner branch and service state.
function Get-StagingSlots {
    foreach ($n in 0..$script:MaxStagingSlot) {
        $s = Get-StagingSlotInfo $n
        $svc = Get-CimInstance Win32_Service -Filter "Name='$($s.Service)'"
        if (-not $svc) { continue }
        $owner = $null
        $ownerFile = Join-Path $s.InstallDir 'STAGING_OWNER.txt'
        $labelFile = Join-Path $s.InstallDir 'STAGING_BRANCH.txt' # slot 0 from before slots existed
        if (Test-Path $ownerFile) { $owner = (Get-Content $ownerFile -Raw).Trim() }
        elseif (Test-Path $labelFile) { $owner = ((Get-Content $labelFile -Raw).Trim() -replace ' \(uncommitted changes\)$', '') }
        $s | Add-Member -NotePropertyMembers @{ Owner = $owner; State = $svc.State; PathName = $svc.PathName } -PassThru
    }
}

# Refuses anything that is the live app's service, port or folders.
function Assert-NotLiveApp($s) {
    $liveInstall = Join-Path $env:ProgramFiles 'EtsyShopManager'
    $liveData = Join-Path $env:ProgramData 'EtsyShopManager'
    if ($s.Service -eq 'EtsyShopManager' -or $s.Port -eq 5080 -or
        [IO.Path]::GetFullPath($s.InstallDir).TrimEnd('\') -eq $liveInstall -or
        [IO.Path]::GetFullPath($s.DataRoot).TrimEnd('\') -eq $liveData) {
        throw 'That is the live app''s service, port or folder. Staging only ever uses its own.'
    }
}

function Assert-Elevated {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Staging slots are Windows Services: run this from an elevated (Administrator) shell.'
    }
}

# Creates the slot's service, folders, permissions and firewall rule. Installs no binaries.
function New-StagingSlot($s) {
    Assert-NotLiveApp $s
    Assert-Elevated
    $account = "NT SERVICE\$($s.Service)"
    $exe = Join-Path $s.InstallDir 'EtsyShopManager.exe'
    $binPath = "`"$exe`" --environment Staging --urls http://0.0.0.0:$($s.Port) --Storage:DataRoot `"$($s.DataRoot)`""

    New-Item -ItemType Directory -Force $s.InstallDir, $s.DataRoot | Out-Null
    New-Service -Name $s.Service -BinaryPathName $binPath -DisplayName "Etsy Shop Manager (staging $($s.Slot))" `
        -Description "Staging copy of Etsy Shop Manager on port $($s.Port). Cannot reach Etsy. See docs/STAGING.md." `
        -StartupType AutomaticDelayedStart | Out-Null
    sc.exe config $s.Service obj= $account | Out-Null
    sc.exe failure $s.Service reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

    icacls $s.DataRoot /grant "${account}:(OI)(CI)M" | Out-Null
    icacls $s.InstallDir /grant "${account}:(OI)(CI)RX" | Out-Null

    Get-NetFirewallRule -DisplayName $s.Firewall -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $s.Firewall -Direction Inbound -Protocol TCP -LocalPort $s.Port `
        -RemoteAddress '10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16' -Action Allow -Profile Any | Out-Null
    Write-Host "Created staging slot $($s.Slot) (service $($s.Service), port $($s.Port))."
}

# Stops and deletes the slot's service, firewall rule, binaries and data.
function Remove-StagingSlot($s) {
    Assert-NotLiveApp $s
    Assert-Elevated
    $svc = Get-Service $s.Service -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -ne 'Stopped') {
            Stop-Service $s.Service
            $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
        }
        sc.exe delete $s.Service | Out-Null
        if ($LASTEXITCODE) { throw "sc.exe delete $($s.Service) failed ($LASTEXITCODE)" }
    }
    Get-NetFirewallRule -DisplayName $s.Firewall -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    foreach ($dir in $s.InstallDir, $s.DataRoot) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    }
    Write-Host "Removed staging slot $($s.Slot) ($($s.Owner))."
}

# Runs $Action while holding the staging lock, so slot changes happen one at a time.
function Invoke-WithStagingLock([scriptblock]$Action) {
    $mutex = [Threading.Mutex]::new($false, $script:StagingDeployMutexName)
    try { $null = $mutex.WaitOne() } catch [Threading.AbandonedMutexException] { }
    try { & $Action }
    finally {
        $mutex.ReleaseMutex()
        $mutex.Dispose()
    }
}
