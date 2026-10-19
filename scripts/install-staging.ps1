#Requires -Version 7
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  One-time: creates the staging copy's Windows Service (docs/STAGING.md). Does not touch the live app.

.DESCRIPTION
  - Service EtsyShopManagerStaging, runs as "NT SERVICE\EtsyShopManagerStaging", Automatic (Delayed), restarts on failure.
  - Started as environment "Staging" (the app then never reaches Etsy), on its own port, data root and install folder.
    Those settings are on the service's command line, so deploys can't lose them.
  - Opens the port in Windows Firewall to private (RFC 1918) addresses, i.e. the LAN and VPN, or to the HAProxy host
    only when -HaproxyIp is given.
  It installs no binaries: run scripts/deploy-staging.ps1 -RefreshData next.

.EXAMPLE
  pwsh -File scripts/install-staging.ps1                            # LAN/VPN: http://<host>:5085
  pwsh -File scripts/install-staging.ps1 -HaproxyIp 192.168.1.10   # behind HAProxy instead
#>
param(
    [string]$HaproxyIp,
    [string[]]$RemoteAddress = @('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16'),
    [int]$Port = 5085,
    [string]$ServiceName = 'EtsyShopManagerStaging',
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'EtsyShopManagerStaging'),
    [string]$DataRoot = (Join-Path $env:ProgramData 'EtsyShopManagerStaging')
)

$ErrorActionPreference = 'Stop'

# Never the live app's service, port or folders.
$liveInstall = Join-Path $env:ProgramFiles 'EtsyShopManager'
$liveData = Join-Path $env:ProgramData 'EtsyShopManager'
if ($ServiceName -eq 'EtsyShopManager' -or $Port -eq 5080 -or
    [IO.Path]::GetFullPath($InstallDir).TrimEnd('\') -eq $liveInstall -or
    [IO.Path]::GetFullPath($DataRoot).TrimEnd('\') -eq $liveData) {
    throw 'That is the live app''s service, port or folder. Staging needs its own.'
}

$account = "NT SERVICE\$ServiceName"
$exe = Join-Path $InstallDir 'EtsyShopManager.exe'
$arguments = "--environment Staging --urls http://0.0.0.0:$Port --Storage:DataRoot `"$DataRoot`""
if ($HaproxyIp) { $arguments += " --ReverseProxy:TrustedProxies:0 $HaproxyIp" }
$binPath = "`"$exe`" $arguments"

# 1. Folders ----------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force $InstallDir, $DataRoot | Out-Null

# 2. Service ------------------------------------------------------------------------------------------------
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    sc.exe config $ServiceName binPath= $binPath | Out-Null
    if ($LASTEXITCODE) { throw "sc.exe config failed ($LASTEXITCODE)" }
    Write-Host "Service $ServiceName updated."
}
else {
    New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName 'Etsy Shop Manager (staging)' `
        -Description "Staging copy of Etsy Shop Manager on port $Port. Cannot reach Etsy. See docs/STAGING.md." `
        -StartupType AutomaticDelayedStart | Out-Null
    sc.exe config $ServiceName obj= $account | Out-Null
    sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    Write-Host "Service $ServiceName created (runs as $account)."
}

# 3. Permissions: the service account may modify only its own data folder -------------------------------------
icacls $DataRoot /grant "${account}:(OI)(CI)M" | Out-Null
icacls $InstallDir /grant "${account}:(OI)(CI)RX" | Out-Null

# 4. Firewall ---------------------------------------------------------------------------------------------------
$ruleName = "Etsy Shop Manager Staging (TCP $Port)"
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
$remote = if ($HaproxyIp) { @($HaproxyIp) } else { $RemoteAddress }
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port `
    -RemoteAddress $remote -Action Allow -Profile Any | Out-Null
Write-Host "Firewall: port $Port open to $($remote -join ', ')."

Write-Host "Installed. Next: pwsh scripts/deploy-staging.ps1 -RefreshData" -ForegroundColor Green
