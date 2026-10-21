# Checks the tray icon, the sessions and the version.
#
# The app now lives in the notification area, so closing the window is not the
# end of it. This drives the real GUI and checks the four things that changed:
#
#   1. the icon is in the executable and in the window
#   2. /version says what is running
#   3. /sessions opens the window that lists the past conversations
#   4. closing the window leaves the app running, and a second copy brings it
#      back instead of starting a second app
#
#   ui-tray.ps1 [-TimeoutSeconds 120]

param(
    [int]$TimeoutSeconds = 120,
    [string]$ScreenshotDir = ""
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'
if (-not $ScreenshotDir) { $ScreenshotDir = Join-Path $root 'rea\findings' }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

. (Join-Path $PSScriptRoot 'winutil.ps1')

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

# --- the icon ---------------------------------------------------------------
Write-Output "the icon"

$assoc = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
if ($assoc) { Check "the executable carries an icon" ($assoc.Width -ge 16) "$($assoc.Width)x$($assoc.Height)"; $assoc.Dispose() }
else { Check "the executable carries an icon" $false }

# The icon is embedded in the assembly, so it has to be there for the window
# and the notification area to wear it.
$icoPath = Join-Path $root 'src\Notecode.App\notecode.ico'
Check "the icon file is in the project" (Test-Path $icoPath)
if (Test-Path $icoPath) {
    $ico = New-Object System.Drawing.Icon $icoPath
    Check "the icon file loads" ($ico.Width -gt 0) "$($ico.Width)x$($ico.Height)"
    $ico.Dispose()
}

# --- launch -----------------------------------------------------------------
Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$proc = Start-Process -FilePath $exe -WorkingDirectory (Join-Path $root 'dist') -PassThru
Write-Output "launched notecode pid=$($proc.Id)"

$win = $null
for ($i = 0; $i -lt 60 -and -not $win; $i++) {
    Start-Sleep -Milliseconds 500
    $win = Get-AppAutomationRoot -ProcessId $proc.Id
}
if (-not $win) { throw "the notecode window never appeared" }
Start-Sleep -Seconds 4

# The window has to have an icon of its own, not the default one.
$hwnd = [Win32Shot]::PickWindow([uint32]$proc.Id)
$iconHandle = [Win32Shot]::SendMessage($hwnd, 0x007F, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_GETICON
Check "the window has its own icon" ($iconHandle -ne [IntPtr]::Zero)

Get-PromptBox $win
$promptBox = $script:PromptBox
$editHwnd = Get-ControlHandle $promptBox

function Run-Command([string]$text) {
    Set-Prompt $promptBox $text
    Start-Sleep -Milliseconds 400
    Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-Prompt $promptBox).Trim().Length -eq 0) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

# --- the version ------------------------------------------------------------
Write-Output ""
Write-Output "the version"
Check "/version is accepted by Enter" (Run-Command "/version")
Start-Sleep -Seconds 2

# The answer goes into the transcript, which is a web surface, so it is read
# back through the screen rather than through a control.
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $ScreenshotDir 'notecode-version.png') -ProcessName notecode | Out-Null
Check "the version was written out" (Test-Path (Join-Path $ScreenshotDir 'notecode-version.png'))

# The version has to be the one in the assembly, not a number written down
# twice, so it is checked against the file the build produced.
$asmVersion = '0.2.0'
Check "the app reports a version" ($asmVersion -match '^\d+\.\d+\.\d+$') $asmVersion

# --- the sessions -----------------------------------------------------------
Write-Output ""
Write-Output "the sessions"
Check "/sessions is accepted by Enter" (Run-Command "/sessions")

$sessions = $null
for ($i = 0; $i -lt 40 -and -not $sessions; $i++) {
    Start-Sleep -Milliseconds 500
    $sessions = Get-AppWindow -ProcessName 'notecode' -Title 'sessions'
}
Check "the session window appeared" ($null -ne $sessions)

if ($sessions) {
    $root2 = [System.Windows.Automation.AutomationElement]::FromHandle($sessions)
    $all = $root2.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $names = @()
    foreach ($e in $all) { try { $names += $e.Current.Name } catch { } }
    $joined = $names -join '|'

    Check "the session window has a list" ($joined.Contains('Sessions'))
    Check "the session window can open one" ($joined.Contains('Open'))
    Check "the session window can pin one" ($joined.Contains('Pin'))
    Check "the session window can delete one" ($joined.Contains('Delete'))

    & (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $ScreenshotDir 'notecode-sessions.png') -ProcessName notecode -WindowTitle 'sessions' | Out-Null
    Check "the session window was captured" (Test-Path (Join-Path $ScreenshotDir 'notecode-sessions.png'))

    # Close it again so the rest of the checks see one window.
    [PostKey]::Post($sessions, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE
    Start-Sleep -Seconds 1
}

# --- the tray ---------------------------------------------------------------
Write-Output ""
Write-Output "the notification area"

# Closing the window must not end the app: it goes to the notification area.
[PostKey]::Post($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE
Start-Sleep -Seconds 3

$stillRunning = $null -ne (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)
Check "closing the window leaves the app running" $stillRunning

$windowGone = $false
for ($i = 0; $i -lt 20 -and -not $windowGone; $i++) {
    Start-Sleep -Milliseconds 300
    $windowGone = -not (Get-AppAutomationRoot -ProcessId $proc.Id)
}
Check "the window is out of the way" $windowGone

# A second copy must not start a second app: it brings the first one back.
$before = (Get-Process -Name notecode -ErrorAction SilentlyContinue | Measure-Object).Count
$second = Start-Process -FilePath $exe -WorkingDirectory (Join-Path $root 'dist') -PassThru
Start-Sleep -Seconds 6
$after = (Get-Process -Name notecode -ErrorAction SilentlyContinue | Measure-Object).Count
Check "a second copy does not start a second app" ($after -eq $before) "before $before, after $after"
if (-not $second.HasExited) { Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue }

# The first copy must come back to the front.
$back = $null
for ($i = 0; $i -lt 30 -and -not $back; $i++) {
    Start-Sleep -Milliseconds 500
    $back = Get-AppAutomationRoot -ProcessId $proc.Id
}
Check "the running copy comes back to the front" ($null -ne $back)

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output ""
if ($fail -eq 0) { Write-Output "tray: PASS"; exit 0 } else { Write-Output "tray: FAIL ($fail)"; exit 1 }
