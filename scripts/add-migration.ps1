#Requires -Version 7
<#
.SYNOPSIS
  Adds an EF Core migration after changing the model. The app applies pending migrations on startup.

.EXAMPLE
  pwsh scripts/add-migration.ps1 -Name AddListings
#>
param([Parameter(Mandatory)][string]$Name)

$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    dotnet tool restore
    dotnet ef migrations add $Name -p src/EtsyShopManager.Data -s src/EtsyShopManager.Web -o Migrations
    if ($LASTEXITCODE) { throw 'dotnet ef failed' }
}
finally {
    Pop-Location
}
