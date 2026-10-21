<#
.SYNOPSIS
    Builds the GPU versions of llama-server for notecode.

.DESCRIPTION
    notecode runs one llama-server per backend, because a llama.cpp binary is
    compiled for exactly one backend. This script builds the extra ones into their
    own directories and never touches the build directories that already exist.

    It has to be run **on the machine with the card**. A CUDA build is compiled
    for the GPU it finds, so a build made on a machine without an NVIDIA card
    cannot run on one with a 5060 Ti.

    Backends:
      cuda    NVIDIA. The fastest of the three. Needs the CUDA toolkit.
      vulkan  One driver, every vendor: NVIDIA, AMD and Intel. Needs the Vulkan SDK.
      metal   macOS only.

    Prerequisites, and what happens without them:

      CUDA     CUDA toolkit 12.8 or newer for a Blackwell card (RTX 50 series).
               Older toolkits do not know the sm_120 architecture and the build
               fails with "unsupported gpu architecture".
      Vulkan   The Vulkan SDK, for glslc. The SDK installs glslc; without it the
               build fails with "glslc not found".

    The script never installs anything. Installing a toolkit is the user's call.

.EXAMPLE
    .\build-llama-gpu.ps1 -Backend cuda
    .\build-llama-gpu.ps1 -Backend vulkan -Arch 120
    .\build-llama-gpu.ps1 -Backend cuda -DryRun
#>
[CmdletBinding()]
param(
    [ValidateSet('cuda', 'vulkan', 'metal')]
    [string]$Backend = 'cuda',

    # The CUDA architecture to compile for. "native" means the card in this
    # machine. 120 is a Blackwell card such as the RTX 5060 Ti.
    [string]$Arch = 'native',

    [string]$LlamaRoot = 'C:\Llama\llama.cpp',

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Write-Ok($m) { Write-Host "  ok    $m" -ForegroundColor Green }
function Write-Warn($m) { Write-Host "  warn  $m" -ForegroundColor Yellow }
function Write-Err($m) { Write-Host "  FAIL  $m" -ForegroundColor Red }
function Write-Step($m) { Write-Host "`n$m" -ForegroundColor Cyan }

# The generator and instance the existing CPU build used, so every build lands
# its executable in the same place: bin\Release\llama-server.exe.
$Generator = 'Visual Studio 17 2022'
$VsInstance = 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools'

if (-not (Test-Path (Join-Path $LlamaRoot 'CMakeLists.txt'))) {
    Write-Err "llama.cpp is not at $LlamaRoot"
    exit 1
}

if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
    Write-Err 'cmake is not on PATH. Install it, or run this from a developer prompt.'
    exit 1
}

# ---------------------------------------------------------------- preflight

$missing = @()
$envPatches = @{}

switch ($Backend) {
    'cuda' {
        $cudaRoot = Get-ChildItem 'C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA' -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending | Select-Object -First 1
        if (-not $cudaRoot) {
            $missing += 'the CUDA toolkit (C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA)'
        } else {
            $nvcc = Join-Path $cudaRoot.FullName 'bin\nvcc.exe'
            if (Test-Path $nvcc) {
                $version = (& $nvcc --version | Select-Object -Last 1)
                Write-Ok "CUDA toolkit: $version"
                if ($version -match 'release (\d+)\.(\d+)') {
                    $major = [int]$Matches[1]
                    $minor = [int]$Matches[2]
                    if ($major -lt 12 -or ($major -eq 12 -and $minor -lt 8)) {
                        Write-Warn 'CUDA 12.8 or newer is needed for a Blackwell card (RTX 50 series).'
                    }
                }
                $envPatches['CUDA_PATH'] = $cudaRoot.FullName
                $envPatches['PATH'] = "$(Join-Path $cudaRoot.FullName 'bin');$env:PATH"
            } else {
                $missing += "nvcc.exe (in $($cudaRoot.FullName)\bin)"
            }
        }
    }

    'vulkan' {
        $sdk = Get-ChildItem 'C:\VulkanSDK' -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending | Select-Object -First 1
        if (-not $sdk) {
            $missing += 'the Vulkan SDK (C:\VulkanSDK)'
        } else {
            $glslc = Join-Path $sdk.FullName 'Bin\glslc.exe'
            if (Test-Path $glslc) {
                Write-Ok "Vulkan SDK: $($sdk.FullName)"
                $envPatches['VULKAN_SDK'] = $sdk.FullName
                $envPatches['PATH'] = "$(Join-Path $sdk.FullName 'Bin');$env:PATH"
            } else {
                $missing += "glslc.exe (in $($sdk.FullName)\Bin)"
            }
        }
    }

    'metal' {
        if (-not $IsMacOS -and $PSVersionTable.Platform -ne 'Unix') {
            Write-Err 'Metal only exists on macOS. Run this script on the Mac.'
            exit 1
        }
    }
}

if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Err 'Something is missing:'
    foreach ($m in $missing) { Write-Host "        - $m" -ForegroundColor Yellow }
    Write-Host ''
    Write-Host '  This script does not install anything on its own. Install the toolkit' -ForegroundColor Gray
    Write-Host '  or SDK, then run it again.' -ForegroundColor Gray
    Write-Host ''
    Write-Host '  CUDA   https://developer.nvidia.com/cuda-downloads' -ForegroundColor DarkGray
    Write-Host '  Vulkan https://vulkan.lunarg.com/download/latest/windows.html' -ForegroundColor DarkGray
    exit 1
}

foreach ($k in $envPatches.Keys) { Set-Item -Path "Env:$k" -Value $envPatches[$k] }

# ---------------------------------------------------------------- configure

$buildDir = Join-Path $LlamaRoot "build-$Backend"
$buildName = "build-$Backend"

Write-Step "Configuring $buildName"

$cmakeArgs = @(
    '-S', $LlamaRoot,
    '-B', $buildDir,
    '-G', $Generator,
    "-DCMAKE_GENERATOR_INSTANCE=$VsInstance",
    '-DCMAKE_BUILD_TYPE=Release'
)

switch ($Backend) {
    'cuda' {
        $cmakeArgs += '-DGGML_CUDA=ON'
        $cmakeArgs += "-DCMAKE_CUDA_ARCHITECTURES=$Arch"
        $cmakeArgs += '-DGGML_NATIVE=ON'
    }
    'vulkan' {
        $cmakeArgs += '-DGGML_VULKAN=ON'
        if ($env:VULKAN_SDK) { $cmakeArgs += "-DGGML_VULKAN_SDK_PATH=$($env:VULKAN_SDK)" }
        $cmakeArgs += '-DGGML_NATIVE=ON'
    }
    'metal' {
        $cmakeArgs += '-DGGML_METAL=ON'
        $cmakeArgs += '-DGGML_METAL_EMBED_LIBRARY=ON'
    }
}

Write-Host "  $cmakeArgs -join ' '".Replace('-join', '') -ForegroundColor DarkGray

if ($DryRun) {
    Write-Host ''
    Write-Ok 'dry run: nothing was configured or built'
    exit 0
}

& cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) {
    Write-Err "cmake configure failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# -------------------------------------------------------------------- build

Write-Step "Building llama-server ($Backend)"

# Only the server is built. The whole project takes a lot longer and the app
# only ever runs the server.
& cmake --build $buildDir --config Release --target llama-server
if ($LASTEXITCODE -ne 0) {
    Write-Err "the build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# ------------------------------------------------------------------- result

$exe = Join-Path $buildDir 'bin\Release\llama-server.exe'
$exeMac = Join-Path $buildDir 'bin\Release\llama-server'

Write-Host ''
if (Test-Path $exe) { Write-Ok "built $exe" }
elseif (Test-Path $exeMac) { Write-Ok "built $exeMac" }
else {
    Write-Err 'the build finished but llama-server.exe is not where it should be'
    exit 1
}

$device = switch ($Backend) {
    'cuda' { 'cuda' }
    'vulkan' { 'vulkan' }
    'metal' { 'metal' }
}

Write-Host ''
Write-Host '  notecode.config.json already points at this build:' -ForegroundColor Gray
Write-Host "      build-$Backend -> $exe" -ForegroundColor DarkGray
Write-Host ''
Write-Host '  Start notecode and open Tools > Hardware settings. The build is' -ForegroundColor Gray
Write-Host '  offered as a checkbox, and Apply restarts the app under it.' -ForegroundColor Gray
Write-Host ''
Write-Host "  Device : --device $device" -ForegroundColor DarkGray
Write-Host '  Layers : --n-gpu-layers 99 (as many as fit on the card)' -ForegroundColor DarkGray
