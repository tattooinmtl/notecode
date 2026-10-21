<#
.SYNOPSIS
    End-to-end smoke test for the notecode stack.

.DESCRIPTION
    Checks each layer in dependency order and reports PASS/FAIL per check:
      1. config + wrapper resolve
      2. llama-server binary present and new enough (b10988+)
      3. model file present and large enough
      4. MCP server definitions parse
      5. REA MCP server answers tools/list over stdio
      6. llama-server /health
      7. llama-server /v1/models lists the model
      8. llama-server chat completion returns text
     8b. llama-server advertises the tool catalogue (built-ins + the 19 notecode tools)
      9. kimi-code.exe is present and runnable
     10. notecode.exe is present and is a valid PE binary
     11. the notecode MCP server answers tools/list over stdio
     12. the shared file-type registry parses

.EXAMPLE
    .\scripts\smoke-test.ps1
    .\scripts\smoke-test.ps1 -SkipModelChat
#>
[CmdletBinding()]
param(
    [switch]$SkipModelChat
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$runtime = Join-Path $root 'runtime'
$cfgPath = Join-Path $runtime 'notecode.config.json'

# For Get-NotecodeConfig, which reads the configuration the way the app does.
. (Join-Path $PSScriptRoot 'winutil.ps1')

$script:Pass = 0
$script:Fail = 0
$script:Skip = 0

function Ok($name, $detail) {
    $script:Pass++
    Write-Host "  [PASS] $name" -ForegroundColor Green
    if ($detail) { Write-Host "         $detail" -ForegroundColor DarkGray }
}
function Bad($name, $detail) {
    $script:Fail++
    Write-Host "  [FAIL] $name" -ForegroundColor Red
    if ($detail) { Write-Host "         $detail" -ForegroundColor DarkGray }
}
function Skipped($name, $detail) {
    $script:Skip++
    Write-Host "  [SKIP] $name" -ForegroundColor Yellow
    if ($detail) { Write-Host "         $detail" -ForegroundColor DarkGray }
}

Write-Host ''
Write-Host 'notecode smoke test' -ForegroundColor Cyan
Write-Host '==================='

# 1 ---------------------------------------------------------------- config
Write-Host ''
Write-Host '[1] configuration'
if (Test-Path $cfgPath) {
    try {
        $cfg = Get-NotecodeConfig -Config $cfgPath
        Ok 'config parses' "$cfgPath"
    } catch { Bad 'config parses' $_.Exception.Message }
} else {
    Bad 'config parses' "missing $cfgPath"
    $cfg = $null
}

# 2 ------------------------------------------------------------ llama build
Write-Host ''
Write-Host '[2] llama-server binary'
if ($cfg) {
    $exe = $cfg.llama.serverExe
    if (Test-Path $exe) {
        $ver = (& $exe --version 2>&1 | Out-String)
        $build = ([regex]::Match($ver, 'version:\s*(\S+)')).Groups[1].Value
        $help = (& $exe --help 2>&1 | Out-String)
        $hasMcp = $help -match 'mcp-servers-config'
        $hasTools = $help -match '--tools'
        $hasProxy = $help -match 'webui-mcp-proxy'
        if ($hasMcp -and $hasTools -and $hasProxy) {
            Ok 'llama-server supports MCP + tools + proxy' "build $build at $exe"
        } else {
            Bad 'llama-server supports MCP + tools + proxy' "build $build missing mcp=$hasMcp tools=$hasTools proxy=$hasProxy"
        }
    } else {
        Bad 'llama-server present' "missing $exe"
    }
}

# 3 ----------------------------------------------------------------- model
Write-Host ''
Write-Host '[3] model file'
if ($cfg) {
    $model = $cfg.llama.model
    if (Test-Path $model) {
        $gb = [math]::Round((Get-Item $model).Length / 1GB, 2)
        Ok 'model present' "$model ($gb GiB)"
    } else {
        Bad 'model present' "missing $model"
    }

    # The vision projector that goes with the model. Without it the server refuses
    # any request that carries a picture.
    $mmproj = $cfg.llama.mmproj
    if ($mmproj -and (Test-Path $mmproj)) {
        $mb = [math]::Round((Get-Item $mmproj).Length / 1MB, 0)
        Ok 'the vision projector is present' "$mmproj ($mb MB)"
    } else {
        Bad 'the vision projector is present' "missing $mmproj"
    }
}

# 4 ------------------------------------------------------------- mcp config
Write-Host ''
Write-Host '[4] MCP server definitions'
$mcpPath = Join-Path $runtime 'mcp-servers.json'
if (Test-Path $mcpPath) {
    try {
        $m = Get-Content $mcpPath -Raw | ConvertFrom-Json
        $names = @($m.mcpServers.PSObject.Properties.Name)
        Ok 'mcp-servers.json parses' ($names -join ', ')
    } catch { Bad 'mcp-servers.json parses' $_.Exception.Message }
} else {
    Bad 'mcp-servers.json parses' "missing $mcpPath"
}

# 5 ------------------------------------------------------------------- REA
Write-Host ''
Write-Host '[5] REA MCP server (stdio)'
$inFile = Join-Path $env:TEMP 'notecode-smoke-mcp.jsonl'
@(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"notecode-smoke","version":"0.1"}}}'
    '{"jsonrpc":"2.0","method":"notifications/initialized"}'
    '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
) | Set-Content -Encoding utf8 $inFile
try {
    $raw = Get-Content $inFile -Raw | npx -y rea-agents@6.4.0 mcp 2>$null | Out-String
    $first = $raw.Trim().Split("`n")[0]
    $parsed = $first | ConvertFrom-Json
    $count = @($parsed.result.tools).Count
    if ($count -gt 0) { Ok 'REA tools/list' "$count tools" }
    else { Bad 'REA tools/list' 'no tools returned' }
} catch {
    Bad 'REA tools/list' $_.Exception.Message
}

# 6 -------------------------------------------------------------- llama up
Write-Host ''
Write-Host '[6] llama-server health'
if ($cfg) {
    $base = "http://$($cfg.llama.host):$($cfg.llama.port)"
    $health = $null
    try { $health = (Invoke-WebRequest "$base/health" -UseBasicParsing -TimeoutSec 5).StatusCode } catch { }
    if ($health -eq 200) { Ok 'server healthy' "$base/health -> 200" }
    else { Bad 'server healthy' "$base/health did not answer (start it: runtime\notecode-llama.ps1 start)" }

    # The running server has to have the projector loaded, otherwise a picture in
    # the prompt is refused. The server says so in its properties.
    try {
        $props = Invoke-RestMethod "$base/props" -TimeoutSec 10
        if ($props.modalities.vision) { Ok 'the server sees' "vision: $($props.modalities.vision), video: $($props.modalities.video)" }
        else { Bad 'the server sees' 'the running server has no vision modality - it was started without --mmproj' }
    } catch { Bad 'the server sees' $_.Exception.Message }

    # 7 models
    Write-Host ''
    Write-Host '[7] model list'
    try {
        $m = Invoke-RestMethod "$base/v1/models" -TimeoutSec 10
        $ids = @($m.data | ForEach-Object { $_.id })
        if ($ids.Count -gt 0) { Ok 'models listed' ($ids -join ', ') }
        else { Bad 'models listed' 'empty list' }
    } catch { Bad 'models listed' $_.Exception.Message }

    # 8 chat
    Write-Host ''
    Write-Host '[8] chat completion'
    if ($SkipModelChat) {
        Skipped 'chat completion' '-SkipModelChat'
    } else {
        try {
            $body = @{
                model = $cfg.llama.alias
                messages = @(@{ role = 'user'; content = 'Reply with exactly: notecode-ok' })
                max_tokens = 24
                temperature = 0.1
                stream = $false
            } | ConvertTo-Json -Depth 6
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $resp = Invoke-RestMethod "$base/v1/chat/completions" -Method Post -Body $body `
                -ContentType 'application/json' -TimeoutSec 300
            $sw.Stop()
            $text = $resp.choices[0].message.content
            Ok 'chat completion' ("{0:F1}s -> {1}" -f $sw.Elapsed.TotalSeconds, ($text -replace '\s+', ' ').Trim())
        } catch {
            Bad 'chat completion' $_.Exception.Message
        }
    }

    # 8b tool catalogue
    Write-Host ''
    Write-Host '[8b] tool catalogue'
    try {
        $tools = Invoke-RestMethod "$base/tools" -TimeoutSec 20
        $all = @($tools)
        $builtin = @($all | Where-Object { $_.type -eq 'server' })
        $notecode = @($all | Where-Object { $_.tool -like 'notecode_*' })
        $rea = @($all | Where-Object { $_.tool -like 'rea_*' })
        $blender = @($all | Where-Object { $_.tool -like 'blender_*' })
        # Nineteen notecode tools: the five file tools, the five filesystem tools,
        # the five that reach the three surfaces of the app and the four that
        # work on skills.
        # The REA count matters too: llama-server gives each MCP server ten seconds
        # to list its tools at startup, and a cold npx cache used to eat that
        # window and leave the server with none of the 170.
        # The Blender count matters for the same reason, and for another: the
        # HTTP MCP client used to hand llama-server a disposed document, so the
        # server never published a single one of them.
        if ($builtin.Count -gt 0 -and $notecode.Count -eq 19 -and $rea.Count -eq 170 -and
            $blender.Count -eq 26) {
            Ok 'tool catalogue' ("{0} built-in, {1} notecode, {2} rea, {3} blender" -f $builtin.Count, $notecode.Count, $rea.Count, $blender.Count)
        } else {
            Bad 'tool catalogue' "builtin=$($builtin.Count) notecode=$($notecode.Count) rea=$($rea.Count) blender=$($blender.Count)"
        }
    } catch {
        Bad 'tool catalogue' $_.Exception.Message
    }
}

# 9 ------------------------------------------------------------------ kimi
Write-Host ''
Write-Host '[9] kimi-code'
if ($cfg -and (Test-Path $cfg.kimi.exe)) {
    Ok 'kimi.exe present' $cfg.kimi.exe
    if (Test-Path $cfg.kimi.mcpConfig) {
        try {
            $k = Get-Content $cfg.kimi.mcpConfig -Raw | ConvertFrom-Json
            Ok 'kimi mcp.json parses' (@($k.mcpServers.PSObject.Properties.Name) -join ', ')
        } catch { Bad 'kimi mcp.json parses' $_.Exception.Message }
    } else {
        Skipped 'kimi mcp.json parses' 'not present'
    }
} else {
    Bad 'kimi.exe present' 'not found'
}

# 10 ------------------------------------------------------------- notecode
Write-Host ''
Write-Host '[10] notecode.exe'
$appExe = Join-Path $root 'dist\notecode.exe'
if (Test-Path $appExe) {
    $bytes = [System.IO.File]::ReadAllBytes($appExe)
    $mz = [char]$bytes[0] + [char]$bytes[1]
    # The PE header offset lives at 0x3C (e_lfanew); the "PE\0\0" signature is there.
    $peOff = [System.BitConverter]::ToInt32($bytes, 0x3C)
    $pe = $false
    if ($peOff -gt 0 -and ($peOff + 4) -le $bytes.Length) {
        $pe = ([System.Text.Encoding]::ASCII.GetString($bytes, $peOff, 4) -match '^PE')
    }
    $size = [math]::Round((Get-Item $appExe).Length / 1MB, 2)
    if ($mz -eq 'MZ' -and $pe) { Ok 'notecode.exe is a valid PE image' "$size MB (PE header @ 0x$($peOff.ToString('X')))" }
    else { Bad 'notecode.exe is a valid PE image' "magic=$mz pe=$pe offset=0x$($peOff.ToString('X'))" }
} else {
    Bad 'notecode.exe present' "run scripts\build.ps1 first"
}

# 11 --------------------------------------------------------- notecode MCP
Write-Host ''
Write-Host '[11] notecode MCP server (stdio)'
$mcpScript = Join-Path $runtime 'notecode-mcp.mjs'
if (Test-Path $mcpScript) {
    $inFile2 = Join-Path $env:TEMP 'notecode-smoke-notecode-mcp.jsonl'
    @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"notecode-smoke","version":"0.1"}}}'
        '{"jsonrpc":"2.0","method":"notifications/initialized"}'
        '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
    ) | Set-Content -Encoding utf8 $inFile2
    try {
        $raw = Get-Content $inFile2 -Raw | node $mcpScript 2>$null | Out-String
        # Pick the tools/list reply (id 2). The initialize reply also mentions "tools"
        # because of the capabilities object, so matching on the word alone is not enough.
        $line = ($raw.Trim().Split("`n") | Where-Object { $_ -match '"id"\s*:\s*2' } | Select-Object -First 1)
        if (-not $line) { throw "no tools/list reply in the server output" }
        $parsed = $line | ConvertFrom-Json
        $names = @($parsed.result.tools | ForEach-Object { $_.name })
        $expected = @('save_as', 'open_in_editor', 'list_files', 'read_file', 'list_supported_types',
                      'make_folder', 'move_path', 'copy_path', 'delete_path', 'write_text')
        $missing = @($expected | Where-Object { $names -notcontains $_ })
        if ($missing.Count -eq 0) {
            Ok 'notecode MCP tools/list' ($names -join ', ')
        } else {
            Bad 'notecode MCP tools/list' "missing: $($missing -join ', ')"
        }

        # The file tools have to work, not just be advertised. A folder is made, a
        # file is written into it, the file is moved, copied and deleted.
        $probeDir = 'smoke-folder-test'
        $probeFile = "$probeDir/probe.txt"
        $inFile3 = Join-Path $env:TEMP 'notecode-smoke-mcp-calls.jsonl'
        @(
            '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"notecode-smoke","version":"0.1"}}}'
            '{"jsonrpc":"2.0","method":"notifications/initialized"}'
            "{`"jsonrpc`":`"2.0`",`"id`":10,`"method`":`"tools/call`",`"params`":{`"name`":`"make_folder`",`"arguments`":{`"path`":`"$probeDir`"}}}"
            "{`"jsonrpc`":`"2.0`",`"id`":11,`"method`":`"tools/call`",`"params`":{`"name`":`"write_text`",`"arguments`":{`"path`":`"$probeFile`",`"content`":`"hello`"}}}"
            "{`"jsonrpc`":`"2.0`",`"id`":12,`"method`":`"tools/call`",`"params`":{`"name`":`"move_path`",`"arguments`":{`"from`":`"$probeFile`",`"to`":`"$probeDir/moved.txt`"}}}"
            "{`"jsonrpc`":`"2.0`",`"id`":13,`"method`":`"tools/call`",`"params`":{`"name`":`"copy_path`",`"arguments`":{`"from`":`"$probeDir/moved.txt`",`"to`":`"$probeDir/copied.txt`"}}}"
            "{`"jsonrpc`":`"2.0`",`"id`":14,`"method`":`"tools/call`",`"params`":{`"name`":`"delete_path`",`"arguments`":{`"path`":`"$probeDir`"}}}"
        ) | Set-Content -Encoding utf8 $inFile3
        $raw3 = Get-Content $inFile3 -Raw | node $mcpScript 2>$null | Out-String
        $replies = @($raw3.Trim().Split("`n") | Where-Object { $_ -match '"id"\s*:\s*(10|11|12|13|14)' })
        $texts = @()
        foreach ($r in $replies) {
            try { $texts += (($r | ConvertFrom-Json).result.content[0].text) } catch { }
        }
        $allText = $texts -join "`n"
        $workspace = Join-Path $root 'workspace'
        $madeDir = Test-Path (Join-Path $workspace $probeDir)

        if ($allText -match 'Created the folder' -and $allText -match 'Moved ' -and
            $allText -match 'Copied ' -and $allText -match 'Deleted ' -and -not $madeDir) {
            Ok 'the file tools work' 'make_folder, write_text, move_path, copy_path, delete_path'
        } else {
            Bad 'the file tools work' ($allText -replace "`n", ' | ')
        }
    } catch {
        Bad 'notecode MCP tools/list' $_.Exception.Message
    }
} else {
    Bad 'notecode MCP tools/list' "missing $mcpScript"
}

# 12 ------------------------------------------------------- file type table
Write-Host ''
Write-Host '[12] file type registry'
$ftPath = Join-Path $runtime 'file-types.json'
if (Test-Path $ftPath) {
    try {
        $ft = Get-Content $ftPath -Raw | ConvertFrom-Json
        $modes = @($ft.modes.PSObject.Properties.Name)
        $aliases = @($ft.aliases.PSObject.Properties.Name | Where-Object { -not $_.StartsWith('$') })
        if ($modes.Count -ge 80 -and $aliases.Count -ge 50) {
            Ok 'file-types.json parses' "$($modes.Count) extensions, $($aliases.Count) aliases"
        } else {
            Bad 'file-types.json parses' "modes=$($modes.Count) aliases=$($aliases.Count)"
        }
    } catch {
        Bad 'file-types.json parses' $_.Exception.Message
    }
} else {
    Bad 'file-types.json parses' "missing $ftPath"
}

# 13 ------------------------------------------------- codemirror coverage
Write-Host ''
Write-Host '[13] CodeMirror mode coverage'
$checkModes = Join-Path $root 'scripts\check-modes.ps1'
if (Test-Path $checkModes) {
    $out = & pwsh -NoProfile -ExecutionPolicy Bypass -File $checkModes 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) {
        Ok 'every registry MIME has a vendored mode' (($out -split "`n" | Where-Object { $_ -match 'registered MIME types' }) -join '').Trim()
    } else {
        Bad 'every registry MIME has a vendored mode' (($out -split "`n" | Where-Object { $_ -match 'text/|application/|script/' }) -join '; ').Trim()
    }
} else {
    Bad 'every registry MIME has a vendored mode' "missing $checkModes"
}

# 14 --------------------------------------- slash commands and provider store
Write-Host ''
Write-Host '[14] slash commands, providers and the model chat template'
$probeProj = Join-Path $root 'src\ToolProbe\ToolProbe.csproj'
if (Test-Path $probeProj) {
    $out = & dotnet run --project $probeProj -c Release -- --commands $cfgPath 2>&1 | Out-String
    $code = $LASTEXITCODE
    $passes = ([regex]::Matches($out, '\[PASS\]')).Count
    $fails = ([regex]::Matches($out, '\[FAIL\]')).Count
    if ($code -eq 0 -and $fails -eq 0 -and $passes -ge 25) {
        Ok 'the command, provider and GGUF probe' "$passes checks"
    } else {
        Bad 'the command, provider and GGUF probe' (($out -split "`n" | Where-Object { $_ -match '\[FAIL\]' }) -join '; ').Trim()
    }
} else {
    Bad 'the command, provider and GGUF probe' "missing $probeProj"
}

# 15 --------------------------------------------------- the provider presets
Write-Host ''
Write-Host '[15] provider presets'
$presets = @('local', 'minimax', 'moonshot', 'anthropic', 'openai', 'grok', 'antigravity',
    'deepseek', 'nvidia', 'qwen', 'openrouter', 'groq', 'mistral', 'ollama', 'agnes',
    'xkiro', 'atria', 'reflection')
$providerFile = Join-Path $runtime 'providers.json'
$providerText = ''
if (Test-Path $providerFile) { $providerText = Get-Content $providerFile -Raw }
$missing = @($presets | Where-Object { $providerText -notmatch ('"' + $_ + '"') })
if ($missing.Count -eq 0) {
    Ok 'every preset provider is configured' "$($presets.Count) presets"
} else {
    Bad 'every preset provider is configured' "missing: $($missing -join ', ')"
}

Write-Host ''
Write-Host '===================' -ForegroundColor Cyan
Write-Host ("PASS {0}  FAIL {1}  SKIP {2}" -f $script:Pass, $script:Fail, $script:Skip)
Write-Host ''
if ($script:Fail -gt 0) { exit 1 }
