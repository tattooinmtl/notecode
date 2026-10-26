# ---------------------------------------------------------------------------
# setup-meshy.ps1
#
# Puts the Meshy stack back the way it needs to be on a notecode checkout:
# the upstream repositories under meshy/, the MCP server built, the Meshy DCC
# core installed, and the Blender and Unity adapters placed.
#
# meshy/ is gitignored - each upstream repository is its own git repository
# with its own history, so they are checked out rather than committed here.
# This script is what puts them back.
#
# Usage:
#   pwsh -File scripts/setup-meshy.ps1
#   pwsh -File scripts/setup-meshy.ps1 -SkipDcc     # MCP server only
#   pwsh -File scripts/setup-meshy.ps1 -UnityProject C:\path\to\project
# ---------------------------------------------------------------------------

[CmdletBinding()]
param(
    [string] $UnityProject = "$env:USERPROFILE\Blasted",
    [string] $BlenderPath  = "C:\Program Files\Blender Foundation\Blender 5.2\blender.exe",
    [switch] $SkipDcc
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$meshy = Join-Path $root 'meshy'

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "  OK   $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "  WARN $msg" -ForegroundColor Yellow }
function Die($msg)  { Write-Host "  FAIL $msg" -ForegroundColor Red; exit 1 }

# --------------------------------------------------------------- the sources

$repos = @(
    'https://github.com/meshy-dev/meshy-mcp-server.git',
    'https://github.com/meshy-dev/meshy-3d-agent.git',
    'https://github.com/meshy-dev/meshy-dcc-adapters.git',
    'https://github.com/meshy-dev/meshy-unity-plugin.git'
)

Step 'Cloning the Meshy repositories'
New-Item -ItemType Directory -Force -Path $meshy | Out-Null
foreach ($r in $repos) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($r)
    $dest = Join-Path $meshy $name
    if (Test-Path (Join-Path $dest '.git')) { Ok "$name already cloned"; continue }
    git clone --depth 1 --quiet $r $dest
    if ($LASTEXITCODE -ne 0) { Die "git clone $name failed" }
    Ok "$name cloned"
}

# ------------------------------------------------------------ the MCP server

Step 'Building the Meshy MCP server'
Push-Location (Join-Path $meshy 'meshy-mcp-server')
try {
    if (-not (Test-Path 'node_modules')) {
        npm install --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { Die 'npm install failed' }
    }
    npm run build
    if ($LASTEXITCODE -ne 0) { Die 'npm run build failed' }
    $count = (Get-ChildItem 'dist' -Recurse -File | Measure-Object).Count
    Ok "dist/ built ($count files)"
}
finally { Pop-Location }

$keyFile = Join-Path $meshy 'meshy-mcp-server\.env'
if (-not (Test-Path $keyFile)) {
    New-Item -ItemType File -Path $keyFile -Force | Out-Null
    @(
        '# Meshy MCP server - local key. This file is gitignored on purpose.',
        '# Get a key from https://www.meshy.ai/ - the API needs a Pro plan or above.',
        'MESHY_API_KEY='
    ) | Set-Content -Path $keyFile -Encoding utf8
    Ok "created $keyFile - paste your key into it"
}
elseif ((Get-Content $keyFile -Raw) -match '(?m)^MESHY_API_KEY=\s*$') {
    Warn "$keyFile has no key yet - Meshy calls will fail with 401"
}

if ($SkipDcc) { Step 'Skipping the DCC side'; exit 0 }

# ---------------------------------------------------------- the Meshy DCC core

Step 'Installing the Meshy DCC core'
$cli = Join-Path $env:USERPROFILE '.local\bin\meshy-dcc.exe'
if (Test-Path $cli) { Ok "meshy-dcc already installed ($cli)" }
else {
    $tmp = Join-Path $meshy '_install'
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    $ps1 = Join-Path $tmp 'install.ps1'
    Invoke-WebRequest -Uri 'https://github.com/meshy-dev/meshy-dcc-core/releases/download/v0.2.0/install.ps1' -OutFile $ps1 -UseBasicParsing
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ps1
    if (-not (Test-Path $cli)) { Die 'meshy-dcc install did not produce meshy-dcc.exe' }
    Ok "meshy-dcc installed ($cli)"
}

# ------------------------------------------------------------- the Blender side

Step 'Installing the Blender adapter'
if (-not (Test-Path $BlenderPath)) { Warn "no Blender at $BlenderPath - skipping the Blender adapter" }
else {
    & $cli install --dcc-type blender --dcc-path $BlenderPath --execute --json --non-interactive | Out-Null
    $ext = Join-Path $env:APPDATA 'Blender Foundation\Blender'
    $installed = Get-ChildItem $ext -Recurse -Directory -Filter 'meshy_dcc_blender' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($installed) { Ok "extension installed at $($installed.FullName)" }
    else { Warn 'the Blender extension was not found after the install' }
}

# --------------------------------------------------------------- the Unity side

Step 'Placing the Unity adapter'
if (-not (Test-Path $UnityProject)) { Warn "no Unity project at $UnityProject - skipping the Unity adapter" }
elseif (-not (Test-Path (Join-Path $UnityProject 'ProjectSettings\ProjectVersion.txt'))) {
    Warn "$UnityProject is not a Unity project - skipping"
}
else {
    $sidecarRoot = Join-Path $env:APPDATA 'meshy-dcc\adapters\meshy-dcc-unity\0.1.0\meshy-dcc-unity-windows-x64'
    $py = Join-Path $sidecarRoot 'python\python.exe'
    if (-not (Test-Path $py)) { Warn "the Unity sidecar is not installed - run the meshy-dcc install for unity first" }
    else {
        $unityExe = 'C:\Program Files\Unity\Hub\Editor\2022.3.40f1\Editor\Unity.exe'
        & $py (Join-Path $PSScriptRoot 'place-unity-meshy-package.py') $UnityProject $unityExe | Out-Null
        $pkg = Join-Path $UnityProject 'Packages\ai.meshy.dcc'
        if (Test-Path $pkg) { Ok "package placed at $pkg" }
        else { Warn "the package was not placed in $UnityProject" }
    }
}

Step 'Done'
Write-Host '  The Meshy MCP server is built, the DCC core is installed and the' -ForegroundColor Gray
Write-Host '  adapters are placed. Put your key in meshy/meshy-mcp-server/.env' -ForegroundColor Gray
Write-Host '  before the first generation call.' -ForegroundColor Gray
