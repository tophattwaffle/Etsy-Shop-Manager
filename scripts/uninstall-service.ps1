#Requires -Version 7
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Stops and removes the Windows Service and its firewall rule. Data in C:\ProgramData\EtsyShopManager is kept
  unless -RemoveData is given.
#>
param(
    [string]$ServiceName = 'EtsyShopManager',
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'EtsyShopManager'),
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Service $ServiceName removed."
}

Get-NetFirewallRule -DisplayName 'Etsy Shop Manager (TCP *' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }

if ($RemoveData) {
    Remove-Item (Join-Path $env:ProgramData 'EtsyShopManager') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Data folder removed.'
}
else {
    Write-Host "Data kept in $(Join-Path $env:ProgramData 'EtsyShopManager')."
}
