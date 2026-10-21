# End-to-end GUI check for the AI -> tool -> editor loop.
#
# Launches notecode.exe, opens the AI panel, types a prompt that makes the model call
# save_as, waits for the file to appear in the workspace and for the editor to open it,
# then captures a screenshot.
#
#   ui-tool-smoke.ps1 [-FileName gui-check.md] [-TimeoutSeconds 420] [-SendWith Click|Enter]
#
# -SendWith Enter exercises the keyboard binding instead of the Send button: the prompt
# is typed into the AI input and then the Enter key is sent to the focused control.

param(
    [string]$FileName = "gui-check.md",
    [int]$TimeoutSeconds = 420,
    [string]$Screenshot = "",
    [ValidateSet('Click', 'Enter')]
    [string]$SendWith = 'Click'
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$workspace = Join-Path $root 'workspace'
$target = Join-Path $workspace $FileName
$exe = Join-Path $root 'dist\notecode.exe'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

. (Join-Path $PSScriptRoot 'winutil.ps1')


if (-not $Screenshot) {
    $Screenshot = Join-Path $root 'rea\findings\notecode-tool-loop.png'
}

function Get-NotecodeWindow {
    $p = Get-Process -Name notecode -ErrorAction SilentlyContinue |
         Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $cond)
}

function Find-ByName($root, [string]$name, [string]$type = '') {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    if ($type) {
        $tc = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::$type)
        $both = New-Object System.Windows.Automation.AndCondition($cond, $tc)
        return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $both)
    }
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Dump-Tree($root, [int]$depth = 0) {
    if ($depth -gt 3) { return }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $walker.GetFirstChild($root)
    while ($child) {
        Write-Output ("{0}[{1}] '{2}'" -f ('  ' * $depth), $child.Current.ControlType.ProgrammaticName, $child.Current.Name)
        Dump-Tree $child ($depth + 1)
        $child = $walker.GetNextSibling($child)
    }
}

# --- launch -----------------------------------------------------------------
if (Test-Path $target) {
    Write-Output "note: $FileName already exists; the run will overwrite it via the AI"
}

# The test talks to the local model, so the local model has to be in charge:
# a test that pointed the app at a mock and did not take it away would leave
# the next one talking to a provider that is not running.
Use-LocalProvider
Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$proc = Start-Process -FilePath $exe -WorkingDirectory (Join-Path $root 'dist') -PassThru
Write-Output "launched notecode pid=$($proc.Id)"

$win = $null
for ($i = 0; $i -lt 40 -and -not $win; $i++) {
    Start-Sleep -Milliseconds 500
    $win = Get-NotecodeWindow
}
if (-not $win) { throw "the notecode window never appeared" }
Write-Output "window found"

# --- open the AI panel -------------------------------------------------------
# The menu item only exists in the automation tree once its parent menu is open,
# so the View menu has to be expanded first. The panel ships visible, so it is only
# toggled when the Send button is missing - toggling blindly would close it.
$send = Find-ByName $win 'Send' 'Button'
if (-not $send) {
    $view = Find-ByName $win 'View' 'MenuItem'
    if (-not $view) {
        Write-Output "control tree:"
        Dump-Tree $win
        throw "the View menu was not found"
    }
    try {
        $view.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    } catch {
        $view.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    Start-Sleep -Milliseconds 700

    $aiPanel = Find-ByName $win 'AI Panel' 'MenuItem'
    if (-not $aiPanel) {
        Write-Output "control tree after expanding View:"
        Dump-Tree $win
        throw "the View > AI Panel item was not found"
    }
    $aiPanel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output "AI panel toggled via View menu"
    Start-Sleep -Milliseconds 800
    $send = Find-ByName $win 'Send' 'Button'
}
if (-not $send) { throw "the Send button was not found" }

# --- type the prompt ---------------------------------------------------------
# There are several Edit elements in the tree - CodeMirror and xterm.js each hide a
# textarea - so the AI input is found by walking up from the Send button to the panel
# that holds it, instead of guessing by index.
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$panel = $walker.GetParent($send)
if (-not $panel) { throw "the Send button has no parent panel" }
Write-Output "AI panel container: '$($panel.Current.Name)' [$($panel.Current.ControlType.ProgrammaticName)]"

$editCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Edit)
$edits = $panel.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
Write-Output "edit controls in the AI panel: $($edits.Count)"
if ($edits.Count -eq 0) { throw "no AI input box found in the AI panel" }

$prompt = "Save a markdown file named $FileName with the content `"# GUI check`""
$input = $edits[0]
$set = $input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$set.SetValue($prompt)

$readBack = $input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
Write-Output "prompt typed: $readBack"
if ($readBack -ne $prompt) { throw "the AI input box did not take the text (got '$readBack')" }

# --- send --------------------------------------------------------------------
if ($SendWith -eq 'Enter') {
    # The key is posted straight to the prompt box. SendKeys only reaches the
    # foreground window, and Windows will not hand the foreground to a background
    # process - on this machine the foreground is a system service window, so the
    # keystroke went nowhere. Posting it to the control needs none of that.
    Send-KeyToControl -Hwnd (Get-ControlHandle $input) -VirtualKey $script:VK_RETURN
    Write-Output "sent with the Enter key; waiting for the model to call save_as"
} else {
    $send.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output "sent; waiting for the model to call save_as"
}

# The input is cleared the moment a prompt is accepted, so an empty box means the
# prompt was picked up rather than left sitting there. The app can still be busy with
# its startup work when the key arrives, so this polls instead of checking once.
$cleared = $false
$deadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $deadline) {
    $after = $input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($after.Trim().Length -eq 0) { $cleared = $true; break }
    Start-Sleep -Milliseconds 400
}
if (-not $cleared) {
    $after = $input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    Write-Output "FAILED: the Enter key did not submit the prompt (the input still holds '$after')"
    exit 1
}
Write-Output "the prompt box is empty: the turn was accepted"

# --- wait for the file -------------------------------------------------------
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$seen = $false
while ((Get-Date) -lt $deadline) {
    if (Test-Path $target) {
        $len = (Get-Item $target).Length
        if ($len -gt 0) { $seen = $true; break }
    }
    Start-Sleep -Seconds 2
}

if (-not $seen) {
    Write-Output "FAILED: $FileName was not created within $TimeoutSeconds seconds"
} else {
    Write-Output "created: $target ($((Get-Item $target).Length) bytes)"
    Write-Output "content: $((Get-Content $target -Raw).Trim())"
}

# give the editor a moment to pick the file up from the queue, then capture
Start-Sleep -Seconds 4
& (Join-Path $root 'scripts\screenshot.ps1') -Out $Screenshot -ProcessName notecode

Write-Output "screenshot: $Screenshot"
if ($seen) { exit 0 } else { exit 1 }
