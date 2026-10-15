#Requires -Version 7
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  One-time: lets a non-admin account (the Gitea Actions runner) deploy upgrades with scripts/deploy.ps1.

.DESCRIPTION
  Grants the account:
  - query/start/stop on the EtsyShopManager service only (added to the service's security descriptor),
  - Modify on the install folder and the data folder.
  It does not make the account an administrator. Run scripts/install-service.ps1 first.

.EXAMPLE
  pwsh -File scripts/grant-deploy-rights.ps1 -Account "$env:COMPUTERNAME\gitea-runner"
#>
param(
    [Parameter(Mandatory)][string]$Account,
    [string]$ServiceName = 'EtsyShopManager',
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'EtsyShopManager'),
    [string]$DataRoot = (Join-Path $env:ProgramData 'EtsyShopManager')
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    throw "Service '$ServiceName' not found. Run scripts/install-service.ps1 first."
}

$sid = ([System.Security.Principal.NTAccount]$Account).Translate([System.Security.Principal.SecurityIdentifier]).Value

# Service rights: CC query config, LC query status, SW enumerate dependents, RP start, WP stop,
# DT pause/continue, LO interrogate, CR user-defined control, RC read permissions.
$sddl = (sc.exe sdshow $ServiceName | Where-Object { $_ -match '^D:' } | Select-Object -First 1).Trim()
if (-not $sddl) { throw "Could not read the security descriptor of $ServiceName." }

if ($sddl -like "*;;;$sid)*") {
    Write-Host "$Account already has rights on $ServiceName."
}
else {
    $ace = "(A;;CCLCSWRPWPDTLOCRRC;;;$sid)"
    $i = $sddl.IndexOf('S:')
    $new = if ($i -ge 0) { $sddl.Insert($i, $ace) } else { $sddl + $ace }
    sc.exe sdset $ServiceName $new | Out-Null
    if ($LASTEXITCODE) { throw "sc.exe sdset failed ($LASTEXITCODE)" }
    Write-Host "Granted $Account start/stop/query on $ServiceName."
}

foreach ($dir in $InstallDir, $DataRoot) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    icacls $dir /grant "${Account}:(OI)(CI)M" | Out-Null
    if ($LASTEXITCODE) { throw "icacls failed on $dir" }
    Write-Host "Granted $Account Modify on $dir."
}
