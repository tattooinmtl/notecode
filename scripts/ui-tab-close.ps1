# Checks the cross on an editor tab.
#
# Opens a file through the queue - the same path the MCP server uses - finds the
# cross that belongs to it, clicks it, and checks that the tab is gone.
#
# It needs no model, so it runs in well under a minute.
#
#   ui-tab-close.ps1 [-FileName agents.md]

param(
    [string]$FileName = 'agents.md'
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'
$queue = Join-Path $root 'runtime\open-queue.jsonl'
$shots = Join-Path $root 'rea\findings'

. (Join-Path $PSScriptRoot 'winutil.ps1')

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

$target = Join-Path $root "workspace\$FileName"
if (-not (Test-Path $target)) { throw "$FileName is not in the workspace" }

# --- launch -------------------------------------------------------------------
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

# --- open the file the way the MCP server does --------------------------------
$entry = @{ path = $target; reason = 'save_as'; at = (Get-Date).ToUniversalTime().ToString('o') }
Add-Content -Path $queue -Value (ConvertTo-Json $entry -Compress) -Encoding utf8
Write-Output "queued $FileName"

$cross = $null
for ($i = 0; $i -lt 30 -and -not $cross; $i++) {
    $cross = Find-ByName $win "Close $FileName" 'Button'
    if (-not $cross) { Start-Sleep -Milliseconds 500 }
}
Check "the tab has a cross" ($null -ne $cross)

if ($cross) {
    $r = $cross.Current.BoundingRectangle
    Write-Output "the cross is at ($([int]$r.X),$([int]$r.Y))"

    # A real click: the cross is a custom control, so BM_CLICK would not reach it.
    [PostKey]::MouseClick((Get-ControlHandle $cross), 7, 7)
    Start-Sleep -Seconds 2

    # A tab with unsaved work asks before it goes.
    $dialog = $null
    for ($i = 0; $i -lt 10 -and -not $dialog; $i++) {
        $dialog = Find-ByName $win 'notecode' 'Window'
        if (-not $dialog) { Start-Sleep -Milliseconds 400 }
    }
    if ($dialog) {
        $no = Find-ByName $dialog 'No' 'Button'
        if ($no) { Click-Control -Hwnd (Get-ControlHandle $no) }
        Start-Sleep -Seconds 2
    }

    $gone = Find-ByName $win "Close $FileName" 'Button'
    Check "the cross closed the tab" ($null -eq $gone)
}

$alive = Get-Process -Name notecode -ErrorAction SilentlyContinue
Check "the app is still alive" ($null -ne $alive)

& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'notecode-tab-close.png') -ProcessName notecode | Out-Null
Check "the app was captured" (Test-Path (Join-Path $shots 'notecode-tab-close.png'))

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output ""
if ($fail -eq 0) { Write-Output "tab close: PASS"; exit 0 } else { Write-Output "tab close: FAIL ($fail)"; exit 1 }
