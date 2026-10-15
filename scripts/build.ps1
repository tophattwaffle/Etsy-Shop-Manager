#Requires -Version 7
<#
.SYNOPSIS
  Restores, (first time only) creates the initial EF Core migration, builds and tests the solution.

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

    $migrations = Join-Path $root 'src/EtsyShopManager.Data/Migrations'
    if (-not (Get-ChildItem $migrations -Filter '*.cs' -ErrorAction SilentlyContinue)) {
        Write-Host 'No migrations yet - creating InitialCreate...' -ForegroundColor Cyan
        dotnet ef migrations add InitialCreate -p src/EtsyShopManager.Data -s src/EtsyShopManager.Web -o Migrations
        if ($LASTEXITCODE) { throw 'Creating the initial migration failed' }
        Write-Host 'Commit src/EtsyShopManager.Data/Migrations to git.' -ForegroundColor Yellow
    }

    dotnet build EtsyShopManager.slnx -c $Configuration --no-restore
    if ($LASTEXITCODE) { throw 'Build failed' }

    if (-not $SkipTests) {
        dotnet test EtsyShopManager.slnx -c $Configuration --no-build
        if ($LASTEXITCODE) { throw 'Tests failed' }
    }

    Write-Host 'Build OK' -ForegroundColor Green
}
finally {
    Pop-Location
}
