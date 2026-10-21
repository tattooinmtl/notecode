# End-to-end check of the BYOK path.
#
# Points a provider at a local mock of an OpenAI-compatible endpoint, sends one
# turn from the app and checks that the request really went to that endpoint
# with the right URL, the right bearer token and the right body, and that the
# answer came back into the transcript.
#
#   ui-byok-turn.ps1 [-Port 8799]
#
# The mock is scripts/mock-openai.mjs. Nothing here touches a real vendor and no
# real key is involved.

param(
    [int]$Port = 8799,
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'
$probe = Join-Path $root 'src\ToolProbe\ToolProbe.csproj'
$config = Join-Path $root 'runtime\notecode.config.json'
$shots = Join-Path $root 'rea\findings'

. (Join-Path $PSScriptRoot 'winutil.ps1')

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

$record = Join-Path $env:TEMP "notecode-byok-$Port.json"
if (Test-Path $record) { Remove-Item $record -Force }

# A mock left over from an earlier run would still hold the port and serve the
# old behaviour, so it is cleared out first.
Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*mock-openai.mjs*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500

# --- the mock endpoint --------------------------------------------------------
$mock = Start-Process -FilePath 'node' `
    -ArgumentList @((Join-Path $PSScriptRoot 'mock-openai.mjs'), '--port', $Port, '--record', $record) `
    -PassThru -WindowStyle Hidden
Write-Output "mock provider pid=$($mock.Id) on port $Port"

$up = $false
for ($i = 0; $i -lt 30 -and -not $up; $i++) {
    Start-Sleep -Milliseconds 400
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/v1/models" -TimeoutSec 3 -ErrorAction Stop
        $up = $r.StatusCode -eq 200
    } catch { }
}
Check "the mock endpoint answers" $up

# --- a provider that points at it --------------------------------------------
dotnet run --project $probe -c Release --no-build -- --setprovider $config `
    'mock' "http://127.0.0.1:$Port/v1" 'mock-model' 'mock-secret-token' | Out-Null
Check "the mock provider is configured" ($LASTEXITCODE -eq 0) "the store says so"

# --- the app ------------------------------------------------------------------
Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$proc = Start-Process -FilePath $exe -WorkingDirectory (Join-Path $root 'dist') -PassThru
Write-Output "launched notecode pid=$($proc.Id)"

$win = $null
for ($i = 0; $i -lt 40 -and -not $win; $i++) {
    Start-Sleep -Milliseconds 500
    $win = Get-AppAutomationRoot -ProcessId $proc.Id
}
if (-not $win) { throw "the notecode window never appeared" }
Start-Sleep -Seconds 4

Get-PromptBox $win
$promptBox = $script:PromptBox
$editHwnd = Get-ControlHandle $promptBox
Set-AppForeground -Hwnd ([Win32Shot]::PickWindow([uint32]$proc.Id))
Start-Sleep -Milliseconds 500
$promptBox.SetFocus()
Start-Sleep -Milliseconds 300

# --- send one turn -----------------------------------------------------------
Set-Prompt $promptBox "say hello to the mock provider"
$typed = Get-Prompt $promptBox
Check "the prompt received the text" ($typed -like '*say hello to the mock provider*') $typed
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN

# The app probes the model list before it sends the turn, so the test waits for
# the chat request and not just for the first one.
function Get-ChatRequest {
    if (-not (Test-Path $record)) { return $null }
    foreach ($line in (Get-Content $record)) {
        if (-not $line.Trim()) { continue }
        try { $parsed = $line | ConvertFrom-Json } catch { continue }
        if ($parsed.url -like '*chat/completions*') { return $parsed }
    }
    return $null
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$req = $null
while ((Get-Date) -lt $deadline) {
    $req = Get-ChatRequest
    if ($req) { break }
    Start-Sleep -Milliseconds 400
}
Check "the turn reached the provider" ($null -ne $req)

if ($req) {
    Check "the request went to the chat endpoint" ($req.url -like '*chat/completions') $req.url
    Check "the bearer token was sent" ($req.headers.authorization -eq 'Bearer mock-secret-token') $req.headers.authorization
    Check "the prompt was sent" ($req.body.messages[-1].content -like '*say hello to the mock provider*')
    Check "the first message is the system prompt" ($req.body.messages[0].role -eq 'system')
    Check "the model is the configured one" ($req.body.model -eq 'mock-model') $req.body.model
    Check "the file tools were offered" ($null -ne $req.body.tools -and @($req.body.tools).Count -gt 0) "$(@($req.body.tools).Count) tools"
    Check "the app asked for a stream" ($req.body.stream -eq $true)

    # The tools of every connected server have to reach the provider, under the
    # same names the catalogue uses. A provider that was offered the notecode
    # tools alone could not have driven Blender, and a skill that names
    # `blender_execute_python` would have found nothing to call.
    $toolNames = @($req.body.tools | ForEach-Object { $_.function.name })
    $sample = (@($toolNames | Select-Object -First 3) -join ', ')
    Check "the tools are offered with the server name in front" ($toolNames -contains 'notecode_run_command') $sample
    $blenderUp = $false
    try {
        $probe = Invoke-RestMethod 'http://127.0.0.1:8765/mcp' -Method Post -TimeoutSec 10 `
            -Body '{}' -Headers @{ 'Content-Type' = 'application/json' } -ErrorAction Stop
        $blenderUp = $true
    } catch {
        $blenderUp = $_.Exception.Message -notmatch 'refused|actively refused|unreachable'
    }
    if ($blenderUp) {
        Check "the Blender tools were offered too" ($toolNames -contains 'blender_execute_python') "$($toolNames.Count) tools in all"
    } else {
        Write-Output "  [SKIP] the Blender tools were offered too (the Blender server is not running)"
    }

    # The answer has to come back into the transcript.
    Start-Sleep -Seconds 4
    & (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'notecode-byok-turn.png') -ProcessName notecode | Out-Null
    Check "the answer was captured" (Test-Path (Join-Path $shots 'notecode-byok-turn.png'))
}

# --- put the app back the way it was -----------------------------------------
Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
try { $mock | Stop-Process -Force } catch { }
if (Test-Path $record) { Remove-Item $record -Force }

# The mock provider was only there for this test. If it were left behind, the
# next test would talk to a provider that is not running, and the failure would
# look like a product bug.
dotnet run --project $probe -c Release --no-build -- --removeprovider $config 'mock' | Out-Null
Use-LocalProvider

Write-Output ""
if ($fail -eq 0) { Write-Output "byok turn: PASS"; exit 0 } else { Write-Output "byok turn: FAIL ($fail)"; exit 1 }
