<#
.SYNOPSIS
    Builds the Windows installer: Notecode-Setup.exe.

.DESCRIPTION
    The installer carries two things and not a third:

      - the app, published as a single self-contained executable
      - the llama.cpp runtime it needs: llama-server.exe and its libraries

    It does **not** carry the models. The weights are 17 GB and more, they
    live where the machine put them, and the app points at one in the
    settings. An installer that shipped a model would be a 20 GB download
    that is wrong the moment there is a new one.

    The payload is zipped and embedded in the installer, so the result is one
    file that can be copied anywhere and run.

.PARAMETER LlamaDir
    The llama.cpp build output to take llama-server from. It has to be the
    build that matches this machine: a llama-server is compiled for one
    backend.

.PARAMETER OutputDir
    Where Notecode-Setup.exe is written.

.EXAMPLE
    .\scripts\build-installer.ps1
#>
[CmdletBinding()]
param(
    [string]$LlamaDir = 'C:\Llama\llama.cpp\build-notecode\bin\Release',
    [string]$OutputDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'installer\output')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $root 'installer\stage'
$payload = Join-Path $root 'installer\payload.zip'

function Write-Step($m) { Write-Host "`n$m" -ForegroundColor Cyan }
function Write-Ok($m) { Write-Host "  ok    $m" -ForegroundColor Green }
function Write-Warn($m) { Write-Host "  warn  $m" -ForegroundColor Yellow }
function Write-Err($m) { Write-Host "  FAIL  $m" -ForegroundColor Red }

# --- the app -----------------------------------------------------------------
Write-Step "Publishing the app"
Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force

# The stage is the install folder exactly as it will be on disk:
#
#   <install>\notecode.exe
#   <install>\wwwroot\...
#   <install>\runtime\...      the wrapper, the MCP server, the config
#   <install>\llama\...        llama-server.exe and its libraries
#
# The app looks for runtime\notecode.config.json by walking up from its own
# folder, so this is the layout it expects.
$appOut = $stage
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $appOut -Force | Out-Null

dotnet publish (Join-Path $root 'src\Notecode.App\Notecode.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:GenerateDocumentationFile=false `
    -o $appOut | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Err "the app did not publish"; exit 1 }
Write-Ok "the app is published"

# The runtime files the app reads beside itself: the wrapper that starts the
# server, the MCP server, the file type registry, the chat template and the
# seed that describes the other machine.
$runtimeSrc = Join-Path $root 'runtime'
$runtimeOut = Join-Path $stage 'runtime'
New-Item -ItemType Directory -Path $runtimeOut -Force | Out-Null
foreach ($name in @('notecode-llama.ps1', 'notecode-mcp.mjs', 'mcp-stdio-bridge.mjs',
                    'mcp-servers.json', 'file-types.json', 'minicpm-chat-template.jinja',
                    'hardware-profiles.seed.json', 'build-llama-gpu.ps1')) {
    $src = Join-Path $runtimeSrc $name
    if (Test-Path $src) { Copy-Item $src $runtimeOut -Force }
}
Copy-Item (Join-Path $root 'runtime\notecode.config.json') $runtimeOut -Force
# The img2threejs pipeline /img2threejs runs: forge, grimoire, docs, the job
# runner and the vendored three.js for the offline viewer.
$i2tSrc = Join-Path $runtimeSrc 'img2threejs'
if (Test-Path $i2tSrc) {
    Copy-Item $i2tSrc $runtimeOut -Recurse -Force
    Get-ChildItem (Join-Path $runtimeOut 'img2threejs') -Recurse -Directory -Filter '__pycache__' |
        Remove-Item -Recurse -Force
}
Write-Ok "the runtime files are staged"

# The icon, so the installed shortcut wears the same one.
Copy-Item (Join-Path $root 'src\Notecode.App\notecode.ico') $appOut -Force

# --- the llama runtime -------------------------------------------------------
Write-Step "Staging the llama.cpp runtime"
if (-not (Test-Path (Join-Path $LlamaDir 'llama-server.exe'))) {
    Write-Err "there is no llama-server.exe in $LlamaDir"
    Write-Host "        the build that matches this machine is the one that has to be installed." -ForegroundColor Gray
    exit 1
}

$llamaOut = Join-Path $stage 'llama'
if (Test-Path $llamaOut) { Remove-Item $llamaOut -Recurse -Force }
New-Item -ItemType Directory -Path $llamaOut -Force | Out-Null

# The server and the libraries it loads. The other executables in the build
# output - the bench, the quantizer, the CLI - are not needed to serve a
# model, and they are not installed.
$needed = @('llama-server.exe')
foreach ($dll in Get-ChildItem $LlamaDir -Filter *.dll -File) {
    # Only the libraries the server actually loads.
    if ($dll.Name -match '^(ggml|llama|mtmd)') { $needed += $dll.Name }
}
foreach ($name in $needed) {
    $src = Join-Path $LlamaDir $name
    if (Test-Path $src) { Copy-Item $src $llamaOut -Force }
}
$llamaSize = (Get-ChildItem $llamaOut -File | Measure-Object Length -Sum).Sum
Write-Ok ("the llama runtime is staged ({0} files, {1:N1} MB)" -f (Get-ChildItem $llamaOut -File).Count, ($llamaSize / 1MB))

# The GPU builds go into their own folders, so the hardware settings page
# can offer them if they are ever built on this machine.
foreach ($backend in 'vulkan', 'cuda', 'metal') {
    $dir = Join-Path $LlamaDir "..\..\build-$backend\bin\Release"
    $exe = Join-Path $dir 'llama-server.exe'
    if (Test-Path $exe) {
        $dest = Join-Path $llamaOut $backend
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        Copy-Item $exe $dest -Force
        foreach ($dll in Get-ChildItem $dir -Filter *.dll -File) {
            if ($dll.Name -match '^(ggml|llama|mtmd)') { Copy-Item $dll $dest -Force }
        }
        Write-Ok "the $backend build is staged as well"
    }
}

# --- the payload -------------------------------------------------------------
Write-Step "Zipping the payload"
if (Test-Path $payload) { Remove-Item $payload -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $payload -CompressionLevel Optimal
Write-Ok ("the payload is {0:N1} MB" -f ((Get-Item $payload).Length / 1MB))

# The install folder inside the zip has to be flat, because the installer
# unpacks it into the install directory itself.
$zip = [System.IO.Compression.ZipFile]::OpenRead($payload)
$first = $zip.Entries | Select-Object -First 1
$zip.Dispose()
Write-Ok "the payload has $($first.FullName) at the top"

# --- the installer -----------------------------------------------------------
Write-Step "Building the installer"
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

dotnet publish (Join-Path $root 'src\Notecode.Installer\Notecode.Installer.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:GenerateDocumentationFile=false `
    -o $OutputDir | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Err "the installer did not publish"; exit 1 }

$setup = Join-Path $OutputDir 'Notecode-Setup.exe'
if (-not (Test-Path $setup)) { Write-Err "the installer is not where it should be"; exit 1 }

Write-Host ''
Write-Ok ("$setup  ({0:N1} MB)" -f ((Get-Item $setup).Length / 1MB))
Write-Host ''
Write-Host '  Running it installs notecode, the llama.cpp runtime and the shortcuts,' -ForegroundColor Gray
Write-Host '  and adds an entry to the list of installed programs.' -ForegroundColor Gray
Write-Host ''
Write-Host '  The models are not in it. Point the app at one in the settings.' -ForegroundColor DarkGray
