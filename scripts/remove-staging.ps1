#Requires -Version 7
<#
.SYNOPSIS
  Takes a branch's staging slot down (docs/STAGING.md): stops and deletes its service, firewall rule, binaries and
  data. Run it once the branch's PR is merged. Never touches the live app. Needs an elevated shell.

.EXAMPLE
  pwsh scripts/remove-staging.ps1                          # the slot of the branch checked out here
  pwsh scripts/remove-staging.ps1 -Branch feature/order-filters
#>
param([string]$Branch)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'staging-slots.ps1')

if (-not $Branch) {
    $Branch = (git -C (Split-Path -Parent $PSScriptRoot) rev-parse --abbrev-ref HEAD).Trim()
    if ($Branch -eq 'HEAD') { throw 'Check out the branch, or pass -Branch.' }
}

Invoke-WithStagingLock {
    $slots = @(Get-StagingSlots | Where-Object Owner -eq $Branch)
    if (-not $slots) {
        Write-Host "Branch $Branch has no staging slot; nothing to remove."
        return
    }
    foreach ($slot in $slots) { Remove-StagingSlot $slot }
}
