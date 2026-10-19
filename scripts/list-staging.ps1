#Requires -Version 7
<#
.SYNOPSIS
  Lists the staging slots (docs/STAGING.md): which branch each one runs, its link and whether it is running.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'staging-slots.ps1')

$slots = @(Get-StagingSlots)
if (-not $slots) { Write-Host 'No staging slots.'; return }
$slots | Select-Object Slot, Owner, Url, State | Format-Table -AutoSize
