<#
.SYNOPSIS
  Installs everything the Windows host needs, in one go. Safe to re-run: anything already present is skipped.

.DESCRIPTION
  To build and run the service:
    - .NET 10 SDK
    - PowerShell 7 (pwsh), which the other scripts use
  For automatic deployment (skip with -SkipRunner):
    - Git and Node.js LTS (used by the workflow's checkout step)
    - NSSM (runs gitea-runner as a Windows service)
    - gitea-runner (the Gitea Actions runner, formerly act_runner) in C:\gitea-runner, plus a config.yaml
      with capacity 1. The checksum is verified against the release's checksums.txt.

  Uses winget when it's available, otherwise downloads the official installers directly.

  Written for the built-in Windows PowerShell 5.1, because PowerShell 7 may not be installed yet.
  Run it from an elevated window:

    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-prereqs.ps1

.PARAMETER SkipRunner
  Only install what's needed to build and run the service.

.PARAMETER RunnerDir
  Folder for gitea-runner.exe and its config. Default C:\gitea-runner.
#>
[CmdletBinding()]
param(
    [switch]$SkipRunner,
    [string]$RunnerDir = 'C:\gitea-runner'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest is very slow with the progress bar in 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# ---- Preconditions -----------------------------------------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell window (Run as administrator).'
}
if (-not [Environment]::Is64BitOperatingSystem) { throw '64-bit Windows is required.' }

$temp = Join-Path $env:TEMP 'esm-prereqs'
New-Item -ItemType Directory -Force $temp | Out-Null

# ---- Helpers -----------------------------------------------------------------------------------------
function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "    $Message" -ForegroundColor Green }

function Test-Command([string]$Name) { return [bool](Get-Command $Name -ErrorAction SilentlyContinue) }

function Update-SessionPath {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
}

function Add-MachinePath([string]$Dir) {
    $parts = @([Environment]::GetEnvironmentVariable('Path', 'Machine') -split ';' | Where-Object { $_ })
    if ($parts -notcontains $Dir) {
        [Environment]::SetEnvironmentVariable('Path', (($parts + $Dir) -join ';'), 'Machine')
    }
    Update-SessionPath
}

function Install-WithWinget([string]$Id) {
    if (-not $script:HasWinget) { return $false }
    # Machine scope first (so the service accounts can see it), then the package's default scope.
    foreach ($scope in @('machine', $null)) {
        $wingetArgs = @('install', '--id', $Id, '--exact', '--silent',
            '--accept-package-agreements', '--accept-source-agreements')
        if ($scope) { $wingetArgs += @('--scope', $scope) }
        & winget @wingetArgs | Out-Host
        if ($LASTEXITCODE -eq 0) { Update-SessionPath; return $true }
    }
    Write-Warning "winget could not install $Id; falling back to a direct download."
    return $false
}

function Save-Download([string]$Url, [string]$FileName) {
    $path = Join-Path $temp $FileName
    Invoke-WebRequest -Uri $Url -OutFile $path -UseBasicParsing
    return $path
}

function Invoke-Installer([string]$File, [string[]]$Arguments) {
    $p = Start-Process -FilePath $File -ArgumentList $Arguments -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "$File exited with code $($p.ExitCode)" }
    Update-SessionPath
}

function Get-GitHubAsset([string]$Repo, [string]$Pattern) {
    $release = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -UseBasicParsing
    $asset = $release.assets | Where-Object { $_.name -match $Pattern } | Select-Object -First 1
    if (-not $asset) { throw "No asset matching '$Pattern' in the latest $Repo release." }
    return Save-Download $asset.browser_download_url $asset.name
}

$script:HasWinget = Test-Command winget
if ($script:HasWinget) { Write-Host 'Using winget where possible.' }
else { Write-Host 'winget not found; using direct downloads.' }

# ---- .NET 10 SDK -------------------------------------------------------------------------------------
function Test-DotnetSdk {
    if (-not (Test-Command dotnet)) { return $false }
    return [bool](& dotnet --list-sdks | Select-String '^10\.')
}

Write-Step '.NET 10 SDK'
if (Test-DotnetSdk) { Write-Ok 'already installed' }
else {
    if (-not (Install-WithWinget 'Microsoft.DotNet.SDK.10')) {
        $installer = Save-Download 'https://dot.net/v1/dotnet-install.ps1' 'dotnet-install.ps1'
        $dotnetDir = Join-Path $env:ProgramFiles 'dotnet'
        & $installer -Channel 10.0 -InstallDir $dotnetDir -NoPath
        Add-MachinePath $dotnetDir
    }
    if (-not (Test-DotnetSdk)) { throw '.NET 10 SDK is still not available after installing.' }
    Write-Ok 'installed'
}

# ---- PowerShell 7 ------------------------------------------------------------------------------------
function Test-Pwsh {
    return (Test-Command pwsh) -or (Test-Path (Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'))
}

Write-Step 'PowerShell 7'
if (Test-Pwsh) { Write-Ok 'already installed' }
else {
    if (-not (Install-WithWinget 'Microsoft.PowerShell')) {
        $msi = Get-GitHubAsset 'PowerShell/PowerShell' '^PowerShell-[\d\.]+-win-x64\.msi$'
        Invoke-Installer 'msiexec.exe' @('/i', "`"$msi`"", '/quiet', '/norestart', 'ADD_PATH=1')
    }
    if (-not (Test-Pwsh)) { throw 'PowerShell 7 is still not available after installing.' }
    Write-Ok 'installed'
}

if ($SkipRunner) {
    Write-Host ''
    Write-Host 'Skipped the auto-deploy tools (-SkipRunner).' -ForegroundColor Yellow
}
else {
    # ---- Git -----------------------------------------------------------------------------------------
    Write-Step 'Git'
    if ((Test-Command git) -or (Test-Path (Join-Path $env:ProgramFiles 'Git\cmd\git.exe'))) { Write-Ok 'already installed' }
    else {
        if (-not (Install-WithWinget 'Git.Git')) {
            $exe = Get-GitHubAsset 'git-for-windows/git' '^Git-[\d\.]+-64-bit\.exe$'
            Invoke-Installer $exe @('/VERYSILENT', '/NORESTART', '/NOCANCEL', '/SP-', '/SUPPRESSMSGBOXES')
        }
        Add-MachinePath (Join-Path $env:ProgramFiles 'Git\cmd')
        if (-not (Test-Command git)) { throw 'Git is still not available after installing.' }
        Write-Ok 'installed'
    }

    # ---- Node.js LTS ---------------------------------------------------------------------------------
    function Test-Node {
        if (-not (Test-Command node)) { return $false }
        $v = (& node --version) -replace '^v', ''
        return ([version]$v).Major -ge 20
    }

    Write-Step 'Node.js LTS'
    if (Test-Node) { Write-Ok 'already installed' }
    else {
        if (-not (Install-WithWinget 'OpenJS.NodeJS.LTS')) {
            $lts = Invoke-RestMethod 'https://nodejs.org/dist/index.json' -UseBasicParsing |
                Where-Object { $_.lts } | Select-Object -First 1
            $msi = Save-Download "https://nodejs.org/dist/$($lts.version)/node-$($lts.version)-x64.msi" "node-$($lts.version)-x64.msi"
            Invoke-Installer 'msiexec.exe' @('/i', "`"$msi`"", '/quiet', '/norestart')
        }
        Add-MachinePath (Join-Path $env:ProgramFiles 'nodejs')
        if (-not (Test-Node)) { throw 'Node.js 20+ is still not available after installing.' }
        Write-Ok 'installed'
    }

    # ---- NSSM ----------------------------------------------------------------------------------------
    # NSSM registers its own path as the service binary, so it must be a real file the runner account can read.
    # winget installs it as a link under C:\Program Files\WinGet\Links, which a service account can't start
    # ("Access is denied"). Always keep a real copy in C:\Program Files\nssm.
    Write-Step 'NSSM'
    $nssmDir = Join-Path $env:ProgramFiles 'nssm'
    $nssmExe = Join-Path $nssmDir 'nssm.exe'
    if (Test-Path $nssmExe) { Write-Ok "already installed ($nssmExe)" }
    else {
        New-Item -ItemType Directory -Force $nssmDir | Out-Null
        $existing = Get-Command nssm -ErrorAction SilentlyContinue
        if (-not $existing -and (Install-WithWinget 'NSSM.NSSM')) {
            $existing = Get-Command nssm -ErrorAction SilentlyContinue
        }
        if ($existing) {
            Copy-Item $existing.Source $nssmExe -Force # copies the real file behind a winget link
        }
        else {
            $zip = Save-Download 'https://nssm.cc/release/nssm-2.24.zip' 'nssm-2.24.zip'
            $extract = Join-Path $temp 'nssm'
            Expand-Archive $zip -DestinationPath $extract -Force
            Copy-Item (Join-Path $extract 'nssm-2.24\win64\nssm.exe') $nssmExe -Force
        }
        if (-not (Test-Path $nssmExe)) { throw 'NSSM is still not available after installing.' }
        Write-Ok "installed ($nssmExe)"
    }

    # ---- gitea-runner --------------------------------------------------------------------------------
    Write-Step 'gitea-runner'
    $runnerExe = Join-Path $RunnerDir 'gitea-runner.exe'
    if (Test-Path $runnerExe) { Write-Ok "already present in $RunnerDir" }
    else {
        $release = Invoke-RestMethod 'https://gitea.com/api/v1/repos/gitea/runner/releases/latest' -UseBasicParsing
        $asset = $release.assets | Where-Object { $_.name -match '^gitea-runner-[\d\.]+-windows-amd64\.exe$' } | Select-Object -First 1
        if (-not $asset) { throw "No Windows amd64 build found in gitea-runner $($release.tag_name)." }
        $downloaded = Save-Download $asset.browser_download_url $asset.name

        $sums = $release.assets | Where-Object { $_.name -eq 'checksums.txt' } | Select-Object -First 1
        if ($sums) {
            $sumsFile = Save-Download $sums.browser_download_url 'gitea-runner-checksums.txt'
            # Accepts "<hash>  <file>" (sha256sum) and "SHA256 (<file>) = <hash>" (BSD) lines.
            $namePattern = '(^|[\s(*])' + [regex]::Escape($asset.name) + '($|[\s)])'
            $line = Get-Content $sumsFile | Where-Object { $_ -match $namePattern } | Select-Object -First 1
            $hashMatch = if ($line) { [regex]::Match($line, '\b[0-9a-fA-F]{64}\b') } else { $null }
            if (-not $hashMatch -or -not $hashMatch.Success) { throw "checksums.txt has no SHA-256 entry for $($asset.name)." }
            $expected = $hashMatch.Value
            $actual = (Get-FileHash $downloaded -Algorithm SHA256).Hash
            if ($actual -ne $expected) { throw "Checksum mismatch for $($asset.name). Expected $expected, got $actual." }
            Write-Ok 'checksum verified'
        }
        else {
            Write-Warning 'The release has no checksums.txt; the download was not verified.'
        }

        New-Item -ItemType Directory -Force $RunnerDir | Out-Null
        Copy-Item $downloaded $runnerExe -Force
        Write-Ok "$($release.tag_name) saved to $runnerExe"
    }

    # A config with capacity 1 (one deploy at a time). Written as UTF-8 without BOM: Windows PowerShell's
    # ">" redirection would write UTF-16, which the runner can't read.
    $configPath = Join-Path $RunnerDir 'config.yaml'
    if (Test-Path $configPath) { Write-Ok 'config.yaml already exists (left unchanged)' }
    else {
        $config = (& $runnerExe generate-config) -join "`n"
        if ($LASTEXITCODE) { throw 'gitea-runner generate-config failed.' }
        $config = [regex]::Replace($config, '(?m)^(\s*capacity:\s*)\d+', '${1}1')
        [IO.File]::WriteAllText($configPath, $config, (New-Object Text.UTF8Encoding($false)))
        Write-Ok "created $configPath (capacity 1)"
    }
}

# ---- Summary -----------------------------------------------------------------------------------------
Update-SessionPath
Write-Host ''
Write-Host 'Installed versions:' -ForegroundColor Cyan
Write-Host ("  .NET SDK   : " + ((& dotnet --list-sdks | Select-String '^10\.' | Select-Object -Last 1) -replace '\s.*$', ''))
$pwshExe = if (Test-Command pwsh) { 'pwsh' } else { Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe' }
Write-Host ("  PowerShell : " + (& $pwshExe -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'))
if (-not $SkipRunner) {
    Write-Host ("  Git        : " + ((& git --version) -replace '^git version ', ''))
    Write-Host ("  Node.js    : " + (& node --version))
    Write-Host ("  NSSM       : " + (Join-Path $env:ProgramFiles 'nssm\nssm.exe'))
    Write-Host ("  Runner     : " + (Join-Path $RunnerDir 'gitea-runner.exe'))
}

Write-Host ''
Write-Host 'Next:' -ForegroundColor Cyan
Write-Host '  1. Open a NEW elevated PowerShell 7 window (pwsh) so the PATH changes apply.'
Write-Host '  2. pwsh -File scripts\install-service.ps1 -HaproxyIp <HAProxy IP>'
if (-not $SkipRunner) {
    Write-Host '  3. Auto-deploy: docs\DEPLOY.md, from step 2 (runner account).'
}
