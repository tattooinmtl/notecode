<#
.SYNOPSIS
    notecode llama.cpp wrapper - launches and supervises a CPU-only llama-server
    bound to the notecode workspace, with the MCP servers and built-in agent tools wired.

.DESCRIPTION
    Single entry point for the notecode local-model runtime. Everything is driven
    from notecode.config.json so the GUI, the scripts and the CLI never disagree.

    Subcommands:
      doctor   Validate exe, model, RAM budget, MCP config. Non-destructive.
      args     Print the exact argv that 'start' would use.
      start    Launch llama-server detached, wait for /health, write pid file.
      stop     Stop the server started by 'start'.
      status   Health, resident memory, KV/ctx in use.
      logs     Tail the server log.

.EXAMPLE
    .\notecode-llama.ps1 doctor
    .\notecode-llama.ps1 start -Ctx 16384
    .\notecode-llama.ps1 status
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('doctor', 'args', 'start', 'stop', 'status', 'logs')]
    [string]$Command = 'status',

    [int]$Ctx = 0,
    [int]$Threads = 0,
    [int]$Port = 0,
    [string]$Exe = '',
    [string]$Device = '',
    [int]$NGpuLayers = -1,
    [switch]$Foreground,
    [int]$Tail = 60,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$script:Root = Split-Path -Parent $PSScriptRoot
$script:RuntimeDir = $PSScriptRoot
$script:ConfigPath = Join-Path $PSScriptRoot 'notecode.config.json'

function Write-Info($m) { if (-not $Json) { Write-Host $m } }
function Write-Warn($m) { if (-not $Json) { Write-Host $m -ForegroundColor Yellow } }
function Write-Err($m) { if (-not $Json) { Write-Host $m -ForegroundColor Red } }

function Get-Config {
    if (-not (Test-Path $script:ConfigPath)) { throw "config not found: $script:ConfigPath" }
    # %VARIABLE% in a path is expanded, so the same file works on every
    # machine. A variable that is not set is left exactly as it is.
    #
    # The value goes back into the JSON as a JSON string, so its backslashes
    # are escaped on the way in. Putting the raw value in would break the
    # file: "C:\Users" is not a valid JSON string.
    $raw = Get-Content $script:ConfigPath -Raw
    $expanded = [regex]::Replace($raw, '%([A-Za-z_][A-Za-z0-9_]*)%', {
        param($m)
        $v = [Environment]::GetEnvironmentVariable($m.Groups[1].Value)
        if ($null -eq $v) { return $m.Value }
        return ($v | ConvertTo-Json -Compress).Trim('"')
    })
    $expanded | ConvertFrom-Json
}

function Get-PidFile { Join-Path $script:RuntimeDir 'llama-server.pid' }
function Get-LogFile { Join-Path $script:RuntimeDir 'llama-server.log' }

function Get-GiB($bytes) { [math]::Round($bytes / 1GB, 2) }

function Get-FreeSystemGiB {
    # Win32_OperatingSystem.FreePhysicalMemory is reported in kilobytes.
    $os = Get-CimInstance Win32_OperatingSystem
    Get-GiB ($os.FreePhysicalMemory * 1KB)
}

function Get-PidAlive([int]$ProcessId) {
    if (-not $ProcessId) { return $false }
    try { $null = Get-Process -Id $ProcessId -ErrorAction Stop; return $true } catch { return $false }
}

function Read-ServerPid {
    $pf = Get-PidFile
    if (-not (Test-Path $pf)) { return 0 }
    $raw = (Get-Content $pf -Raw).Trim()
    $pidValue = 0
    if ([int]::TryParse($raw, [ref]$pidValue)) { return $pidValue }
    return 0
}

# ---------------------------------------------------------------- arg builder
function Build-ServerArgs {
    param($Cfg, [int]$CtxOverride = 0, [int]$ThreadsOverride = 0, [int]$PortOverride = 0,
          [string]$ExeOverride = '', [string]$DeviceOverride = '', [int]$LayersOverride = -1)

    $L = $Cfg.llama
    $ctx = if ($CtxOverride -gt 0) { $CtxOverride } else { [int]$L.context.ctxSize }
    $threads = if ($ThreadsOverride -gt 0) { $ThreadsOverride } else { [int]$L.cpu.threads }
    $port = if ($PortOverride -gt 0) { $PortOverride } else { [int]$L.port }

    # The backend is chosen by the hardware scan and handed in here. The wrapper
    # itself does not decide anything: it runs the executable it is told to run,
    # with the device it is told to use.
    $device = if ($DeviceOverride) { $DeviceOverride } else { [string]$L.cpu.device }
    $layers = if ($LayersOverride -ge 0) { $LayersOverride } else { [int]$L.cpu.nGpuLayers }

    $a = [System.Collections.Generic.List[string]]::new()
    $a.Add('-m'); $a.Add($L.model)

    # The vision projector that goes with the model. MiniCPM-V is a vision model and
    # the projector is the part that turns an image into tokens; without it the
    # server refuses any request that carries an image. It is 1.1 GiB, so it is only
    # loaded when vision is turned on (llama.loadMmproj): on this machine that RAM is
    # what keeps the model from paging.
    if ($L.loadMmproj -and $L.mmproj -and (Test-Path $L.mmproj)) {
        $a.Add('--mmproj'); $a.Add($L.mmproj)
    }

    $a.Add('--host'); $a.Add($L.host)
    $a.Add('--port'); $a.Add([string]$port)
    $a.Add('--alias'); $a.Add($L.alias)

    # CPU only by default. --device none also stops the Vulkan backend from grabbing the iGPU.
    # A GPU build passes --device vulkan or --device cuda and a layer count.
    $a.Add('--device'); $a.Add($device)
    $a.Add('--n-gpu-layers'); $a.Add([string]$layers)

    # Threading: one decode thread per physical core, every logical core for the batch (prefill).
    $a.Add('--threads'); $a.Add([string]$threads)
    $a.Add('--threads-batch'); $a.Add([string]$L.cpu.threadsBatch)
    $a.Add('--threads-http'); $a.Add([string]$L.cpu.threadsHttp)

    # Context + KV quantisation. q8_0 halves the KV cache versus f16.
    $a.Add('--ctx-size'); $a.Add([string]$ctx)
    $a.Add('--cache-type-k'); $a.Add($L.context.cacheTypeK)
    $a.Add('--cache-type-v'); $a.Add($L.context.cacheTypeV)
    $a.Add('--parallel'); $a.Add([string]$L.context.parallel)

    if ($L.server.jinja) { $a.Add('--jinja') }

    # Reasoning. b10988 exposes --reasoning [on|off|auto]; --reasoning-format takes
    # a named format (deepseek, ...) and would reject 'off'.
    if ($L.agent.reasoning) {
        $a.Add('--reasoning'); $a.Add($L.agent.reasoning)
    }

    # Agent surface: built-in file tools + MCP servers the model can call directly.
    $tools = @($L.agent.tools)
    if ($tools.Count -gt 0) {
        $a.Add('--tools'); $a.Add(($tools -join ','))
    }
    $mcpPath = Join-Path $script:RuntimeDir $L.agent.mcpServersConfig
    if (Test-Path $mcpPath) {
        $a.Add('--mcp-servers-config'); $a.Add($mcpPath)
    }
    if ($L.agent.webuiMcpProxy) { $a.Add('--webui-mcp-proxy') }

    # Reuse cached KV chunks when only part of the prompt changed.
    if ([int]$L.server.cacheReuse -gt 0) { $a.Add('--cache-reuse'); $a.Add([string]$L.server.cacheReuse) }

    # Pin the model in RAM. Only with enough free memory - see the config comment.
    # This build spells it --load-mode mmap+mlock; there is no --mlock flag.
    if ($L.server.mlock) { $a.Add('--load-mode'); $a.Add('mmap+mlock') }

    if ($L.server.slots) { $a.Add('--slots') }
    $a.Add('--cors-origins'); $a.Add($L.server.corsOrigins)
    $a.Add('--timeout'); $a.Add([string]$L.server.timeoutSeconds)

    return [pscustomobject]@{ Args = $a; Ctx = $ctx; Threads = $threads; Port = $port }
}

function Get-KvGiB {
    param([int]$Ctx, [string]$CacheType)
    # qwen35moe: 40 layers, 2 kv heads, 256 head_dim. K and V.
    $bytesPerElem = switch ($CacheType) { 'q8_0' { 1.0625 } 'q4_0' { 0.5625 } default { 2.0 } }
    $perToken = 2 * 40 * 2 * 256 * $bytesPerElem
    Get-GiB ($perToken * $Ctx)
}

# ------------------------------------------------------------------- commands
function Invoke-Doctor {
    $cfg = Get-Config
    $report = [ordered]@{}
    $ok = $true

    $exe = $cfg.llama.serverExe
    $report['serverExe'] = $exe
    if (Test-Path $exe) {
        $report['serverExeExists'] = $true
        $report['serverExeMB'] = [math]::Round((Get-Item $exe).Length / 1MB, 2)
    } else {
        $report['serverExeExists'] = $false; $ok = $false
    }

    $model = $cfg.llama.model
    $report['model'] = $model
    if (Test-Path $model) {
        $report['modelExists'] = $true
        $report['modelGiB'] = Get-GiB (Get-Item $model).Length
    } else {
        $report['modelExists'] = $false; $ok = $false
    }

    $mcpPath = Join-Path $script:RuntimeDir $cfg.llama.agent.mcpServersConfig
    $report['mcpConfig'] = $mcpPath
    $report['mcpConfigExists'] = (Test-Path $mcpPath)

    $ws = $cfg.workspace.root
    $report['workspace'] = $ws
    $report['workspaceExists'] = (Test-Path $ws)
    if (-not (Test-Path $ws)) {
        New-Item -ItemType Directory -Force -Path $ws | Out-Null
        $report['workspaceCreated'] = $true
    }

    $free = Get-FreeSystemGiB
    $report['freeSystemGiB'] = $free
    $report['budgetGiB'] = $cfg.memory.budgetGiB
    $report['modelGiB'] = $cfg.memory.modelGiB

    $built = Build-ServerArgs -Cfg $cfg
    $report['ctx'] = $built.Ctx
    $report['threads'] = $built.Threads
    $report['kvGiB'] = Get-KvGiB -Ctx $built.Ctx -CacheType $cfg.llama.context.cacheTypeK
    $report['estimatedResidentGiB'] = [math]::Round(
        $cfg.memory.modelGiB + (Get-KvGiB -Ctx $built.Ctx -CacheType $cfg.llama.context.cacheTypeK) + 1.3, 2)
    $report['freeSystemSufficient'] = ($free -ge $cfg.memory.warnFreeSystemGiB)
    if ($free -lt $cfg.memory.warnFreeSystemGiB) { $ok = $false }

    $report['ok'] = $ok
    if ($Json) { $report | ConvertTo-Json -Depth 5; return }

    Write-Host ''
    Write-Host 'notecode llama doctor' -ForegroundColor Cyan
    Write-Host '--------------------'
    Write-Host ("  exe        : {0} {1}" -f $exe, $(if ($report['serverExeExists']) { '[ok]' } else { '[MISSING]' }))
    Write-Host ("  model      : {0} {1} GiB {2}" -f $model, $report['modelGiB'], $(if ($report['modelExists']) { '[ok]' } else { '[MISSING]' }))
    Write-Host ("  mcp config : {0} {1}" -f $mcpPath, $(if ($report['mcpConfigExists']) { '[ok]' } else { '[MISSING]' }))
    Write-Host ("  workspace  : {0} {1}" -f $ws, $(if ($report['workspaceExists']) { '[ok]' } else { '[created]' }))
    Write-Host ("  ctx        : {0}  threads: {1}" -f $built.Ctx, $built.Threads)
    Write-Host ("  kv cache   : {0} GiB (q8_0)" -f $report['kvGiB'])
    Write-Host ("  est. rss   : {0} GiB (model + kv + ~1.3 GiB runtime)" -f $report['estimatedResidentGiB'])
    Write-Host ("  free RAM   : {0} GiB  {1}" -f $free, $(if ($report['freeSystemSufficient']) { '[ok]' } else { '[LOW - close apps or the model will page]' }))
    Write-Host ''
    if (-not $ok) { Write-Err 'doctor found problems (see [MISSING]/[LOW] markers)' } else { Write-Host 'all good' -ForegroundColor Green }
}

function Invoke-PrintArgs {
    $cfg = Get-Config
    $built = Build-ServerArgs -Cfg $cfg -CtxOverride $Ctx -ThreadsOverride $Threads -PortOverride $Port `
        -ExeOverride $Exe -DeviceOverride $Device -LayersOverride $NGpuLayers
    $exe = if ($Exe) { $Exe } else { $cfg.llama.serverExe }
    if ($Json) {
        [ordered]@{ exe = $exe; args = $built.Args; ctx = $built.Ctx; threads = $built.Threads; port = $built.Port } | ConvertTo-Json -Depth 4
        return
    }
    Write-Host ''
    Write-Host ("`"{0}`"" -f $exe) -ForegroundColor Cyan
    foreach ($x in $built.Args) {
        if ($x -match '^\s*$') { continue }
        if ($x.StartsWith('-')) { Write-Host "  $x" -ForegroundColor Yellow }
        else { Write-Host "    `"$x`"" }
    }
    Write-Host ''
}

function Prime-McpPackages {
    <#
      .SYNOPSIS
        Warms the npx cache for the MCP servers that are started with npx.
      .NOTES
        llama-server lists each MCP server's tools at startup and gives it ten
        seconds to answer (MCP_WARMUP_TIMEOUT_SECONDS in server-mcp.cpp). A cold
        npx cache makes the first start of the package take longer than that, and
        the server is then registered with none of its tools - 170 REA tools gone,
        with nothing in the log but a line that reads like a success.

        Fetching the package once before the server starts moves the download out
        of that ten second window.
    #>
    param($Cfg)

    $mcpPath = Join-Path $script:RuntimeDir $Cfg.llama.agent.mcpServersConfig
    if (-not (Test-Path $mcpPath)) { return }

    try { $mcp = Get-Content $mcpPath -Raw | ConvertFrom-Json } catch { return }

    foreach ($prop in $mcp.mcpServers.PSObject.Properties) {
        $entry = $prop.Value
        $args = @($entry.args)
        if ($args.Count -lt 2) { continue }

        # cmd.exe /d /c npx -y <package> mcp
        $npxAt = [array]::IndexOf($args, 'npx')
        if ($npxAt -lt 0) { continue }

        $package = $null
        for ($i = $npxAt + 1; $i -lt $args.Count; $i++) {
            if ($args[$i].StartsWith('-')) { continue }
            $package = $args[$i]
            break
        }
        if (-not $package) { continue }

        Write-Info ("priming the npx cache for the {0} MCP server ({1})" -f $prop.Name, $package)
        $outFile = Join-Path $env:TEMP "notecode-npx-prime-$($prop.Name).out"
        $errFile = Join-Path $env:TEMP "notecode-npx-prime-$($prop.Name).err"
        $p = Start-Process -FilePath 'cmd.exe' `
            -ArgumentList (@('/d', '/c', 'npx', '-y', $package, '--help')) `
            -NoNewWindow -PassThru -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        if (-not $p.WaitForExit(180000)) {
            try { $p.Kill() } catch { }
        }
        Remove-Item $outFile, $errFile -ErrorAction SilentlyContinue
    }
}

function Invoke-Start {
    $cfg = Get-Config
    $built = Build-ServerArgs -Cfg $cfg -CtxOverride $Ctx -ThreadsOverride $Threads -PortOverride $Port `
        -ExeOverride $Exe -DeviceOverride $Device -LayersOverride $NGpuLayers
    $exe = if ($Exe) { $Exe } else { $cfg.llama.serverExe }
    $model = $cfg.llama.model
    $device = if ($Device) { $Device } else { [string]$cfg.llama.cpu.device }

    if (-not (Test-Path $exe)) { throw "llama-server not found: $exe" }
    if (-not (Test-Path $model)) { throw "model not found: $model" }

    $existing = Read-ServerPid
    if (Get-PidAlive $existing) {
        Write-Warn "server already running (pid $existing)"
        return
    }

    Prime-McpPackages -Cfg $cfg

    # The CPU build must not see the Vulkan devices, or the runtime can put work on
    # the integrated GPU behind the build's back. A GPU build needs them, so the
    # variable is only set when the CPU is doing the work.
    if ($device -eq 'none') {
        $env:GGML_VK_VISIBLE_DEVICES = '-1'
    } else {
        Remove-Item Env:\GGML_VK_VISIBLE_DEVICES -ErrorAction SilentlyContinue
    }

    $ws = $cfg.workspace.root
    if (-not (Test-Path $ws)) { New-Item -ItemType Directory -Force -Path $ws | Out-Null }
    $log = Get-LogFile

    $free = Get-FreeSystemGiB
    if ($free -lt $cfg.memory.warnFreeSystemGiB) {
        Write-Warn ("free RAM is {0} GiB; the model is {1} GiB. Expect paging." -f $free, $cfg.memory.modelGiB)
    }

    Write-Info ("starting: {0}  ctx={1} threads={2} port={3}" -f $exe, $built.Ctx, $built.Threads, $built.Port)

    if ($Foreground) {
        & $exe @($built.Args)
        return
    }

    $proc = Start-Process -FilePath $exe -ArgumentList $built.Args -WorkingDirectory $ws `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -WindowStyle Hidden -PassThru
    Set-Content -Path (Get-PidFile) -Value $proc.Id -Encoding ascii
    Write-Info "pid $($proc.Id)  log $log"

    # Wait for the server to report healthy. Loading 19.7 GiB from disk takes a while.
    $url = "http://127.0.0.1:$($built.Port)/health"
    $deadline = (Get-Date).AddMinutes(20)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) {
            Write-Err "server exited with code $($proc.ExitCode)"
            if (Test-Path "$log.err") { Get-Content "$log.err" -Tail 30 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray } }
            throw 'llama-server failed to start'
        }
        try {
            $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) {
                Write-Info "healthy: http://127.0.0.1:$($built.Port)"
                return
            }
        } catch { }
        Start-Sleep -Seconds 3
    }
    Write-Err 'timed out waiting for /health'
    throw 'llama-server did not become healthy in time'
}

function Invoke-Stop {
    $pidValue = Read-ServerPid
    if (-not (Get-PidAlive $pidValue)) {
        Write-Info 'not running'
        if (Test-Path (Get-PidFile)) { Remove-Item (Get-PidFile) -Force }
        return
    }
    Stop-Process -Id $pidValue -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    if (Get-PidAlive $pidValue) { Stop-Process -Id $pidValue -Force -ErrorAction SilentlyContinue }
    if (Test-Path (Get-PidFile)) { Remove-Item (Get-PidFile) -Force }
    Write-Info "stopped pid $pidValue"
}

function Invoke-Status {
    $cfg = Get-Config
    $pidValue = Read-ServerPid
    $alive = Get-PidAlive $pidValue
    $port = [int]$cfg.llama.port
    $health = $null
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:$port/health" -UseBasicParsing -TimeoutSec 3
        $health = $r.StatusCode
    } catch { }

    $rss = 0
    if ($alive) { try { $rss = [math]::Round((Get-Process -Id $pidValue).WorkingSet64 / 1GB, 2) } catch { } }

    $built = Build-ServerArgs -Cfg $cfg
    $out = [ordered]@{
        running = $alive
        pid = $pidValue
        health = $health
        rssGiB = $rss
        freeSystemGiB = Get-FreeSystemGiB
        ctx = $built.Ctx
        kvGiB = Get-KvGiB -Ctx $built.Ctx -CacheType $cfg.llama.context.cacheTypeK
        baseUrl = "http://127.0.0.1:$port/v1"
    }
    if ($Json) { $out | ConvertTo-Json -Depth 4; return }

    Write-Host ''
    Write-Host ('notecode llama status') -ForegroundColor Cyan
    Write-Host ('----------------------')
    Write-Host ("  running   : {0}" -f $alive)
    Write-Host ("  pid       : {0}" -f $pidValue)
    Write-Host ("  health    : {0}" -f $health)
    Write-Host ("  rss       : {0} GiB" -f $rss)
    Write-Host ("  free RAM  : {0} GiB" -f $out['freeSystemGiB'])
    Write-Host ("  ctx / kv  : {0} / {1} GiB" -f $built.Ctx, $out['kvGiB'])
    Write-Host ("  api       : {0}" -f $out['baseUrl'])
    Write-Host ''
}

function Invoke-Logs {
    $log = Get-LogFile
    if (-not (Test-Path $log)) { Write-Info "no log at $log"; return }
    Get-Content $log -Tail $Tail
    if (Test-Path "$log.err") {
        $e = Get-Content "$log.err" -Tail ([math]::Max(10, [int]($Tail / 3)))
        if ($e) { Write-Host '--- stderr ---' -ForegroundColor DarkGray; $e }
    }
}

# ---------------------------------------------------------------------- main
try {
    switch ($Command) {
        'doctor' { Invoke-Doctor }
        'args'   { Invoke-PrintArgs }
        'start'  { Invoke-Start }
        'stop'   { Invoke-Stop }
        'status' { Invoke-Status }
        'logs'   { Invoke-Logs }
    }
} catch {
    if ($Json) { [ordered]@{ ok = $false; error = $_.Exception.Message } | ConvertTo-Json } else { Write-Err $_.Exception.Message }
    exit 1
}
