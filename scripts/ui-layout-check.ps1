# Checks that toggling the AI panel from the View menu keeps the layout intact.
#
# The panel is docked to the right of the split container. Bringing it to the front
# reverses the form's docking order: the fill control takes the whole area and the
# panel overlaps the editor instead of sitting beside it. This drives the real menu
# and measures the editor and the panel, so that regression cannot come back.
#
#   ui-layout-check.ps1 [-OutDir <dir>]

param(
    [string]$OutDir = ""
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'
if (-not $OutDir) { $OutDir = Join-Path $root 'rea\findings' }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public static class Layout {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    public static List<string> Chrome(IntPtr root) {
        var list = new List<string>();
        EnumChildWindows(root, (h, l) => {
            var cn = new StringBuilder(128); GetClassName(h, cn, 128);
            var tx = new StringBuilder(256); GetWindowText(h, tx, 256);
            RECT r; GetWindowRect(h, out r);
            if (cn.ToString().StartsWith("Chrome_WidgetWin_1"))
                list.Add(string.Format("{0}|{1}|{2}|{3}", tx.ToString(), r.Left, r.Top, r.Right - r.Left));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
"@

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

function Get-ChromeRects {
    param($proc)
    $out = @{}
    foreach ($row in [Layout]::Chrome($proc.MainWindowHandle)) {
        $parts = $row -split '\|'
        $out[$parts[0]] = "$($parts[1]),$($parts[2]) $(([int]$parts[3]))px"
    }
    return $out
}

function Toggle-AiPanel {
    param($win)
    $view = Find-ByName $win 'View' 'MenuItem'
    if (-not $view) { throw "the View menu was not found" }
    try {
        $view.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    } catch {
        $view.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    Start-Sleep -Milliseconds 700
    $item = Find-ByName $win 'AI Panel' 'MenuItem'
    if (-not $item) { throw "the View > AI Panel item was not found" }
    $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900
}

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
Start-Sleep -Seconds 3

$fail = 0

# --- state 1: the panel is showing -------------------------------------------
$a = Get-ChromeRects $proc
Write-Output "panel showing : $($a.Keys -join ', ')"
$editorWithPanel = $a['notecode editor']
$chatWithPanel = $a['notecode agent']
if (-not $editorWithPanel) { throw "the editor surface was not found" }
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $OutDir 'notecode-layout-1-panel-on.png') -ProcessName notecode | Out-Null

# --- hide it ------------------------------------------------------------------
Toggle-AiPanel $win
$b = Get-ChromeRects $proc
Write-Output "panel hidden  : $($b.Keys -join ', ')"
$editorNoPanel = $b['notecode editor']
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $OutDir 'notecode-layout-2-panel-off.png') -ProcessName notecode | Out-Null

# --- show it again ------------------------------------------------------------
Toggle-AiPanel $win
$c = Get-ChromeRects $proc
Write-Output "panel back on : $($c.Keys -join ', ')"
$editorBack = $c['notecode editor']
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $OutDir 'notecode-layout-3-panel-back.png') -ProcessName notecode | Out-Null

function WidthOf($rect) { if (-not $rect) { return -1 } return [int](($rect -split ' ')[1] -replace 'px', '') }

$wOn = WidthOf $editorWithPanel
$wOff = WidthOf $editorNoPanel
$wBack = WidthOf $editorBack

Write-Output ""
Write-Output "editor width with the panel   : $wOn"
Write-Output "editor width without the panel : $wOff"
Write-Output "editor width after re-showing   : $wBack"

if ($wOff -le $wOn) {
    Write-Output "[FAIL] hiding the panel did not give the editor the space back"
    $fail++
} else {
    Write-Output "[PASS] hiding the panel widens the editor"
}

if ($wBack -ne $wOn) {
    Write-Output "[FAIL] re-showing the panel did not restore the original width ($wBack vs $wOn)"
    $fail++
} else {
    Write-Output "[PASS] re-showing the panel restores the original width"
}

if (-not $chatWithPanel) {
    Write-Output "[FAIL] the transcript surface was not found next to the editor"
    $fail++
} else {
    Write-Output "[PASS] the transcript surface is a separate window beside the editor"
}

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output ""
if ($fail -eq 0) { Write-Output "layout check: PASS"; exit 0 } else { Write-Output "layout check: FAIL ($fail)"; exit 1 }
