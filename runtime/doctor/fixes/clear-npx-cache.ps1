<#
.SYNOPSIS
    Empties npx's package cache.

.DESCRIPTION
    A doctor fix for a server started through npx (REA). A download that was
    cut off leaves a half-written package in the cache, and npx then fails on
    it every time. Removing the cache makes the next start download it again,
    which needs the internet.
#>
$ErrorActionPreference = 'Stop'
$cache = Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'
if (-not (Test-Path $cache)) {
    Write-Output "OK there is no npx cache at $cache, so there is nothing to clear"
    exit 0
}
Remove-Item $cache -Recurse -Force
Write-Output "OK cleared $cache. The next start downloads the package again, which needs the internet."
