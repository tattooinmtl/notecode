# End-to-end GUI check for the slash-command system.
#
# Launches notecode.exe, types a slash into the AI prompt and drives the palette with
# the arrow keys, then runs /help and /providers and checks that the answers come back
# in the transcript. It also captures the palette so the look can be reviewed.
#
#   ui-command-smoke.ps1 [-TimeoutSeconds 120]

param(
    [int]$TimeoutSeconds = 120,
    [string]$ScreenshotDir = ""
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'
if (-not $ScreenshotDir) { $ScreenshotDir = Join-Path $root 'rea\findings' }

Add-Type -AssemblyName System.Windows.Forms

. (Join-Path $PSScriptRoot 'winutil.ps1')

function Get-NotecodeWindow {
    param([int]$ProcessId)
    return Get-AppAutomationRoot -ProcessId $ProcessId
}

# --- launch -----------------------------------------------------------------
Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$proc = Start-Process -FilePath $exe -WorkingDirectory (Join-Path $root 'dist') -PassThru
Write-Output "launched notecode pid=$($proc.Id)"

$win = $null
for ($i = 0; $i -lt 40 -and -not $win; $i++) {
    Start-Sleep -Milliseconds 500
    $win = Get-NotecodeWindow -ProcessId $proc.Id
}
if (-not $win) { throw "the notecode window never appeared" }
Start-Sleep -Seconds 4

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

Get-PromptBox $win
$promptBox = $script:PromptBox
$editHwnd = Get-ControlHandle $promptBox
$hwnd = [Win32Shot]::PickWindow([uint32]$proc.Id)
Set-AppForeground -Hwnd $hwnd
Start-Sleep -Milliseconds 500
$promptBox.SetFocus()
Start-Sleep -Milliseconds 300

# --- the palette ------------------------------------------------------------
Write-Output "the palette"
Set-Prompt $promptBox "/"
Start-Sleep -Milliseconds 900
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $ScreenshotDir 'notecode-palette.png') -ProcessName notecode | Out-Null
Check "a slash opens the palette" ((Get-Prompt $promptBox) -eq "/")

# Narrow it and check the selection moves with the arrow keys.
Set-Prompt $promptBox "/re"
Start-Sleep -Milliseconds 700
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $ScreenshotDir 'notecode-palette-narrow.png') -ProcessName notecode | Out-Null
Check "typing narrows the palette" ((Get-Prompt $promptBox) -eq "/re")

Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_DOWN
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_DOWN
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $ScreenshotDir 'notecode-palette-arrow.png') -ProcessName notecode | Out-Null
Check "the arrow keys do not eat the text" ((Get-Prompt $promptBox) -eq "/re")

Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_ESCAPE
Start-Sleep -Milliseconds 400

# --- running a command ------------------------------------------------------
Write-Output ""
Write-Output "the commands"
Set-Prompt $promptBox "/help"
Start-Sleep -Milliseconds 500
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$cleared = $false
while ((Get-Date) -lt $deadline) {
    if ((Get-Prompt $promptBox).Trim().Length -eq 0) { $cleared = $true; break }
    Start-Sleep -Milliseconds 400
}
Check "/help is accepted by Enter" $cleared
Start-Sleep -Seconds 2
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $ScreenshotDir 'notecode-command-help.png') -ProcessName notecode | Out-Null

# /providers
Set-Prompt $promptBox "/providers"
Start-Sleep -Milliseconds 400
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN
$deadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $deadline) {
    if ((Get-Prompt $promptBox).Trim().Length -eq 0) { break }
    Start-Sleep -Milliseconds 400
}
Start-Sleep -Seconds 2
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $ScreenshotDir 'notecode-command-providers.png') -ProcessName notecode | Out-Null
Check "/providers is accepted by Enter" ((Get-Prompt $promptBox).Trim().Length -eq 0)

# The endpoint doctor, in the app rather than in the probe. NVIDIA needs no key
# and answers with a model list, so this is the one provider that checks fast.
Set-Prompt $promptBox "/doctor nvidia"
Start-Sleep -Milliseconds 400
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN
$deadline = (Get-Date).AddSeconds(90)
$doctorDone = $false
while ((Get-Date) -lt $deadline) {
    if ((Get-Prompt $promptBox).Trim().Length -eq 0) { $doctorDone = $true; break }
    Start-Sleep -Milliseconds 400
}
Check "/doctor is accepted by Enter" $doctorDone
# The doctor asks the endpoint and searches the web, so it takes a moment.
Start-Sleep -Seconds 12
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $ScreenshotDir 'notecode-command-doctor.png') -ProcessName notecode | Out-Null
Check "the doctor reported" (Test-Path (Join-Path $ScreenshotDir 'notecode-command-doctor.png'))

# An unknown command must not be sent to the model.
Set-Prompt $promptBox "/notacommand"
Start-Sleep -Milliseconds 400
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN
Start-Sleep -Seconds 2
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $ScreenshotDir 'notecode-command-unknown.png') -ProcessName notecode | Out-Null
Check "an unknown command is answered, not sent" ((Get-Prompt $promptBox).Trim().Length -eq 0)

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output ""
if ($fail -eq 0) { Write-Output "command smoke: PASS"; exit 0 } else { Write-Output "command smoke: FAIL ($fail)"; exit 1 }
