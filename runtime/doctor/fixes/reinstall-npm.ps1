<#
.SYNOPSIS
    Reinstalls a Node server's packages and rebuilds it.

.DESCRIPTION
    A doctor fix. It runs in the server's own folder: npm ci when there is a
    lock file (the exact versions the server was tested with), npm install when
    there is not, and then npm run build when package.json has a build script.

    npm needs the internet unless the packages are already in its cache, so a
    failure while offline is said plainly rather than left as an npm error.
#>
param([Parameter(Mandatory)][string]$Folder)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path (Join-Path $Folder 'package.json'))) {
    Write-Output "FAIL there is no package.json in $Folder, so there is nothing to reinstall"
    exit 2
}
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Output 'FAIL npm was not found. Install Node.js (https://nodejs.org), which brings npm.'
    exit 2
}

Set-Location $Folder
$install = if (Test-Path 'package-lock.json') { 'ci' } else { 'install' }
Write-Output "running npm $install in $Folder"
& npm $install --no-audit --no-fund 2>&1 | Select-Object -Last 15
if ($LASTEXITCODE -ne 0) {
    Write-Output "FAIL npm $install stopped with code $LASTEXITCODE. Offline, npm can only use packages already in its cache."
    exit 1
}

$pkg = Get-Content 'package.json' -Raw | ConvertFrom-Json
if ($pkg.scripts -and $pkg.scripts.build) {
    Write-Output 'running npm run build'
    & npm run build 2>&1 | Select-Object -Last 15
    if ($LASTEXITCODE -ne 0) {
        Write-Output "FAIL the build stopped with code $LASTEXITCODE"
        exit 1
    }
}
Write-Output 'OK packages reinstalled'
