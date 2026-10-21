<#
.SYNOPSIS
    Builds notecode.exe (self-contained, single file) into prototypes/notecode/dist.

.EXAMPLE
    .\scripts\build.ps1
    .\scripts\build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\Notecode.App\Notecode.App.csproj'
$dist = Join-Path $root 'dist'

Write-Host "notecode build" -ForegroundColor Cyan
Write-Host "  project : $proj"
Write-Host "  output  : $dist"
Write-Host "  config  : $Configuration"
Write-Host ''

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

Push-Location (Split-Path -Parent $proj)
try {
    dotnet publish $proj `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -p:GenerateDocumentationFile=false `
        -o $dist
}
finally { Pop-Location }

$exe = Join-Path $dist 'notecode.exe'
if (-not (Test-Path $exe)) { throw "publish did not produce $exe" }

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 2)
Write-Host ''
Write-Host "built: $exe  ($sizeMb MB)" -ForegroundColor Green
Write-Host "run  : $exe" -ForegroundColor Green
Write-Host ''
Write-Host "The runtime config lives at ..\runtime\notecode.config.json (found by walking up from the exe)."
Write-Host 'Start the local model first:  ..\runtime\notecode-llama.ps1 start'
