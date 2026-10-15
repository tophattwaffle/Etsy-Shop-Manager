#Requires -Version 7
<#
.SYNOPSIS
  Restores, builds, checks that EF Core migrations match the model, and runs the tests.

.EXAMPLE
  pwsh scripts/build.ps1
  pwsh scripts/build.ps1 -SkipTests
#>
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not (dotnet --list-sdks | Select-String '^10\.')) {
        throw '.NET 10 SDK not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0'
    }

    dotnet tool restore
    if ($LASTEXITCODE) { throw 'dotnet tool restore failed' }

    dotnet restore EtsyShopManager.slnx
    if ($LASTEXITCODE) { throw 'dotnet restore failed' }

    dotnet build EtsyShopManager.slnx -c $Configuration --no-restore
    if ($LASTEXITCODE) { throw 'Build failed' }

    # Migrations are committed with the code. A model change without a migration would deploy a schema mismatch.
    dotnet ef migrations has-pending-model-changes -p src/EtsyShopManager.Data -s src/EtsyShopManager.Web `
        --configuration $Configuration --no-build
    if ($LASTEXITCODE) { throw 'The EF model changed without a migration. Run: pwsh scripts/add-migration.ps1 -Name <Change>' }

    if (-not $SkipTests) {
        dotnet test EtsyShopManager.slnx -c $Configuration --no-build
        if ($LASTEXITCODE) { throw 'Tests failed' }
    }

    Write-Host 'Build OK' -ForegroundColor Green
}
finally {
    Pop-Location
}
