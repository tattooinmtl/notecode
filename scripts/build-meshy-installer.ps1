<#
.SYNOPSIS
    Builds the Meshy installer: meshy_installer.exe.

.DESCRIPTION
    Meshy's MCP server (@meshy-ai/meshy-mcp-server, MIT) is not part of
    notecode. It is a separate download, hosted beside rea_installer.exe, so
    the app stays small and the server can be taken away without touching it.

    The script takes the server from a checkout of it, builds it, and stages
    exactly what has to be installed:

      <install>\server.mjs        the entry point notecode starts (MESHY_MCP)
      <install>\dist\...          the built server
      <install>\node_modules\...  its runtime packages only, installed fresh
      <install>\package.json      the server's own, so node loads it as ESM

    Nothing else from the checkout is copied. In particular its .env, which
    holds a Meshy API key, never goes into the installer: the key is the
    user's, and the installer asks for it.

.PARAMETER Source
    The checkout of meshy-mcp-server to package.

.PARAMETER OutputDir
    Where meshy_installer.exe is written.

.EXAMPLE
    .\scripts\build-meshy-installer.ps1
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path (Split-Path $PSScriptRoot -Parent) 'meshy\meshy-mcp-server'),
    [string]$OutputDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'installer\meshy-output')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $root 'installer\meshy-stage'
$payload = Join-Path $root 'installer\meshy-payload.zip'

function Write-Step($m) { Write-Host "`n$m" -ForegroundColor Cyan }
function Write-Ok($m) { Write-Host "  ok    $m" -ForegroundColor Green }
function Write-Err($m) { Write-Host "  FAIL  $m" -ForegroundColor Red }

# --- the server ---------------------------------------------------------------
Write-Step "Building the Meshy server in $Source"
if (-not (Test-Path (Join-Path $Source 'package.json'))) { Write-Err "there is no meshy-mcp-server at $Source"; exit 1 }

$pkg = Get-Content (Join-Path $Source 'package.json') -Raw | ConvertFrom-Json
$version = $pkg.version
Write-Ok "$($pkg.name) $version"

# The checkout is built as it is, so the installer carries what was tested
# here. Its own node_modules has the build tools in it; they stay there.
Push-Location $Source
try {
    if (-not (Test-Path 'node_modules')) {
        npm ci --no-audit --no-fund --loglevel=error
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed with code $LASTEXITCODE" }
    }
    npm run build --silent
    if ($LASTEXITCODE -ne 0) { throw "the build failed with code $LASTEXITCODE" }
}
finally { Pop-Location }
if (-not (Test-Path (Join-Path $Source 'dist\index.js'))) { Write-Err "the build left no dist\index.js"; exit 1 }
Write-Ok "built"

# --- the stage ------------------------------------------------------------------
Write-Step "Staging what is installed"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item (Join-Path $root 'MESHY_MCP\server.mjs') $stage
Copy-Item (Join-Path $Source 'dist') $stage -Recurse
foreach ($f in 'package.json', 'package-lock.json', 'LICENSE', 'README.md', '.env.example') {
    $p = Join-Path $Source $f
    if (Test-Path $p) { Copy-Item $p $stage }
}

# The runtime packages, fresh from the lock file, without the build tools.
Push-Location $stage
try {
    npm ci --omit=dev --ignore-scripts --no-audit --no-fund --loglevel=error
    if ($LASTEXITCODE -ne 0) { throw "npm ci --omit=dev failed with code $LASTEXITCODE" }
}
finally { Pop-Location }

# A key must never be shipped. The checkout's .env was not copied; this makes
# sure nothing that looks like one got in some other way.
$leaked = Get-ChildItem $stage -Recurse -Force -File -Filter '.env' -ErrorAction SilentlyContinue
if ($leaked) { Write-Err "a .env got into the stage: $($leaked.FullName -join ', ')"; exit 1 }

$measure = Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum
Write-Ok ("staged {0:N1} MB in {1} files" -f ($measure.Sum / 1MB), $measure.Count)

# The staged server has to start on its own before it is shipped: an MCP
# initialize and a tools/list, over stdio, the way notecode talks to it.
$probe = @'
import { spawn } from "node:child_process";
const p = spawn(process.execPath, [process.argv[2]], { stdio: ["pipe", "pipe", "inherit"], env: { ...process.env, MESHY_API_KEY: "" } });
let buf = ""; const send = (m) => p.stdin.write(JSON.stringify(m) + "\n");
p.stdout.on("data", (d) => {
  buf += d; let i;
  while ((i = buf.indexOf("\n")) >= 0) {
    const line = buf.slice(0, i); buf = buf.slice(i + 1); if (!line.trim()) continue;
    const m = JSON.parse(line);
    if (m.id === 1) { send({ jsonrpc: "2.0", method: "notifications/initialized" }); send({ jsonrpc: "2.0", id: 2, method: "tools/list" }); }
    if (m.id === 2) { console.log(m.result.tools.length); p.kill(); process.exit(0); }
  }
});
send({ jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-03-26", capabilities: {}, clientInfo: { name: "build-check", version: "1" } } });
setTimeout(() => { console.log("timeout"); p.kill(); process.exit(1); }, 30000);
'@
$probeFile = Join-Path $env:TEMP 'meshy-stage-probe.mjs'
Set-Content -Path $probeFile -Value $probe -Encoding utf8
$tools = & node $probeFile (Join-Path $stage 'server.mjs')
Remove-Item $probeFile -ErrorAction SilentlyContinue
if ($LASTEXITCODE -ne 0 -or -not ($tools -as [int])) { Write-Err "the staged server did not answer: $tools"; exit 1 }
Write-Ok "the staged server answers with $tools tools"

if (Test-Path $payload) { Remove-Item $payload -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $payload -CompressionLevel Optimal
Write-Ok ("payload {0:N1} MB" -f ((Get-Item $payload).Length / 1MB))

# --- the installer --------------------------------------------------------------
Write-Step "Publishing meshy_installer.exe"
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

dotnet publish (Join-Path $root 'src\Notecode.MeshyInstaller\Notecode.MeshyInstaller.csproj') -c Release -r win-x64 --self-contained true `
    -p:Version=$version -p:FileVersion="$version.0" -p:AssemblyVersion="$version.0" `
    -p:DebugType=none -o $OutputDir -v minimal | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Err "the publish failed"; exit 1 }

$exe = Join-Path $OutputDir 'meshy_installer.exe'
if (-not (Test-Path $exe)) { Write-Err "meshy_installer.exe was not produced"; exit 1 }
Get-ChildItem $OutputDir | Where-Object { $_.Name -ne 'meshy_installer.exe' } | Remove-Item -Recurse -Force

Write-Ok ("meshy_installer.exe {0:N1} MB  (Meshy server {1})" -f ((Get-Item $exe).Length / 1MB), $version)
Write-Host ""
Write-Host "  The file to upload:  $exe" -ForegroundColor Cyan
Write-Host "  It is hosted at:     https://notecode.globalwarningnetworks.com/downloads/mcp/meshy_installer.exe" -ForegroundColor Cyan
Write-Host ("  SHA-256:             " + (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()) -ForegroundColor Cyan
