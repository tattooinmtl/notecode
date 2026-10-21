# Checks that the reasoning a provider sends shows up on the line under the
# blinker in the transcript.
#
# The local model never thinks out loud, so this points a provider at the mock
# script (scripts/mock-openai.mjs) with --reasoning, which streams the thinking
# as reasoning_content the way a reasoning model does.
#
#   ui-reasoning.ps1 [-Port 8801] [-TimeoutSeconds 60]

param(
    [int]$Port = 8801,
    [int]$TimeoutSeconds = 60
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

# A marker that cannot be confused with the answer or with any chrome.
$marker = 'REASONING-MARK-42'

# --- the mock endpoint --------------------------------------------------------
Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*mock-openai.mjs*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500

$mock = Start-Process -FilePath 'node' `
    -ArgumentList @((Join-Path $PSScriptRoot 'mock-openai.mjs'), '--port', $Port, '--reasoning', "$marker the agent is weighing the tool list") `
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

Set-Prompt $promptBox 'say hello to the mock provider'
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN
Write-Output "sent the turn"

# --- the reasoning shows up ---------------------------------------------------
# The transcript lives in a WebView2, whose text is exposed to UI Automation.
$found = $null
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    $found = Find-ByName $win $marker
    if ($found) { break }
    Start-Sleep -Milliseconds 700
}
Check "the reasoning is in the transcript" ($null -ne $found)

& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'notecode-reasoning.png') -ProcessName notecode | Out-Null
Check "the app was captured" (Test-Path (Join-Path $shots 'notecode-reasoning.png'))

# --- put the app back the way it was -----------------------------------------
Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
try { $mock | Stop-Process -Force } catch { }
dotnet run --project $probe -c Release --no-build -- --removeprovider $config 'mock' | Out-Null
Use-LocalProvider

Write-Output ""
if ($fail -eq 0) { Write-Output "reasoning: PASS"; exit 0 } else { Write-Output "reasoning: FAIL ($fail)"; exit 1 }
