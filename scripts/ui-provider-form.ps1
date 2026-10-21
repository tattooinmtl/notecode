# Opens the /addprovider form and captures it, so the layout can be reviewed.
#
#   ui-provider-form.ps1 [-ScreenshotDir <dir>]
#
# The form is a modal dialog, so it is a window of its own: the capture is taken
# by caption ("Add provider") rather than by the largest-window rule, which would
# pick the main window behind it.

param(
    [string]$ScreenshotDir = ""
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'
if (-not $ScreenshotDir) { $ScreenshotDir = Join-Path $root 'rea\findings' }

. (Join-Path $PSScriptRoot 'winutil.ps1')

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

# --- launch -----------------------------------------------------------------
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
$hwnd = [Win32Shot]::PickWindow([uint32]$proc.Id)
Set-AppForeground -Hwnd $hwnd
Start-Sleep -Milliseconds 500
$promptBox.SetFocus()
Start-Sleep -Milliseconds 300

# --- open the form ----------------------------------------------------------
Set-Prompt $promptBox "/addprovider"
Start-Sleep -Milliseconds 400
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN

$formHwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 30 -and $formHwnd -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 300
    $formHwnd = [Win32Shot]::PickWindowByTitle([uint32]$proc.Id, 'Add provider')
}
Check "/addprovider opens the form" ($formHwnd -ne [IntPtr]::Zero)

if ($formHwnd -ne [IntPtr]::Zero) {
    & (Join-Path $root 'scripts\screenshot.ps1') `
        -Out (Join-Path $ScreenshotDir 'notecode-addprovider.png') `
        -ProcessName notecode -WindowTitle 'Add provider' | Out-Null
    Check "the form was captured" (Test-Path (Join-Path $ScreenshotDir 'notecode-addprovider.png'))

    # The form must not swallow the rest of the app: the main window is still
    # there and the prompt still answers.
    $mainHwnd = [Win32Shot]::PickWindow([uint32]$proc.Id)
    Check "the main window is still open" ($mainHwnd -ne [IntPtr]::Zero)
}

# Close the dialog.
Send-KeyToControl -Hwnd $formHwnd -VirtualKey $script:VK_ESCAPE
Start-Sleep -Milliseconds 800

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output ""
if ($fail -eq 0) { Write-Output "provider form: PASS"; exit 0 } else { Write-Output "provider form: FAIL ($fail)"; exit 1 }
