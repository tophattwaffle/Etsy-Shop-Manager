#Requires -Version 7
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Builds, publishes (self-contained, win-x64) and installs or upgrades the Windows Service.

.DESCRIPTION
  - Installs to C:\Program Files\EtsyShopManager (upgrades keep appsettings.Production.json).
  - Runs as the virtual account "NT SERVICE\EtsyShopManager", start type Automatic (Delayed), restarts on failure.
  - Data stays in C:\ProgramData\EtsyShopManager (database, images, keys, logs) and is never touched by upgrades.
  - Opens the port in Windows Firewall for the HAProxy host only (or the local subnet if no IP is given).

.EXAMPLE
  pwsh -File scripts/install-service.ps1 -HaproxyIp 192.168.1.10
#>
param(
    [string]$HaproxyIp,
    [int]$Port = 5080,
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'EtsyShopManager'),
    [string]$ServiceName = 'EtsyShopManager'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish'
$account = "NT SERVICE\$ServiceName"
$dataRoot = Join-Path $env:ProgramData 'EtsyShopManager'

# 1. Build + publish ---------------------------------------------------------------------------------
& (Join-Path $PSScriptRoot 'build.ps1') -SkipTests
Push-Location $root
try {
    dotnet publish src/EtsyShopManager.Web -c Release -r win-x64 --self-contained true -o $publishDir
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }
}
finally { Pop-Location }

# 2. Stop the existing service ------------------------------------------------------------------------
$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') {
    Write-Host "Stopping $ServiceName..."
    Stop-Service $ServiceName
    $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
}

# 3. Copy files (keep host-specific config) -----------------------------------------------------------
New-Item -ItemType Directory -Force $InstallDir | Out-Null
robocopy $publishDir $InstallDir /MIR /XF appsettings.Production.json /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
$global:LASTEXITCODE = 0

$prodConfig = Join-Path $InstallDir 'appsettings.Production.json'
if (-not (Test-Path $prodConfig)) {
    $proxies = @()
    if ($HaproxyIp) { $proxies = @($HaproxyIp) }
    @{
        Urls         = "http://0.0.0.0:$Port"
        ReverseProxy = @{ TrustedProxies = $proxies }
    } | ConvertTo-Json -Depth 5 | Set-Content $prodConfig -Encoding utf8
    Write-Host "Created $prodConfig (edit it for host-specific settings; upgrades keep it)."
}

# 4. Event log source (the service account cannot create it) --------------------------------------------
if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    [System.Diagnostics.EventLog]::CreateEventSource($ServiceName, 'Application')
}

# 5. Create or update the service ----------------------------------------------------------------------
$exe = Join-Path $InstallDir 'EtsyShopManager.exe'
if (-not $svc) {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'Etsy Shop Manager' `
        -Description "Self-hosted Etsy shop manager (web UI on port $Port)." -StartupType AutomaticDelayedStart | Out-Null
    sc.exe config $ServiceName obj= $account | Out-Null
    sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    Write-Host "Service $ServiceName created (runs as $account)."
}

# 6. Permissions: the service account may modify only its data folder ------------------------------------
New-Item -ItemType Directory -Force $dataRoot | Out-Null
icacls $dataRoot /grant "${account}:(OI)(CI)M" | Out-Null
icacls $InstallDir /grant "${account}:(OI)(CI)RX" | Out-Null

# 7. Firewall: only HAProxy needs to reach Kestrel ----------------------------------------------------------
$ruleName = "Etsy Shop Manager (TCP $Port)"
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
$remote = if ($HaproxyIp) { $HaproxyIp } else { 'LocalSubnet' }
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port `
    -RemoteAddress $remote -Action Allow -Profile Any | Out-Null

# 8. Start ------------------------------------------------------------------------------------------------
Start-Service $ServiceName
(Get-Service $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
Write-Host "Running. Local check: http://localhost:$Port/health" -ForegroundColor Green
Write-Host "Logs: $dataRoot\logs"
