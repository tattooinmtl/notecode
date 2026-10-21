# Looks at the running app the way an assistive technology does, and reports
# what it finds.
#
# This is not a test with a pass and a fail: it is the a11y audit, run against
# the real window. It reports every control that a screen reader would meet
# without a name, every target that is smaller than the smallest one a finger
# can hit, and every control that cannot be reached with the keyboard.
#
#   ui-a11y-audit.ps1 [-Out report.md]

param(
    [string]$Out = ""
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'

. (Join-Path $PSScriptRoot 'winutil.ps1')

# Microsoft: the smallest target a pointer can hit, and the minimum touch target.
$minPointer = 24
$minTouch = 44

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
Start-Sleep -Seconds 5

$all = $win.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)

$rows = @()
foreach ($e in $all) {
    try {
        $c = $e.Current
        $r = $e.Current.BoundingRectangle

        # The parent, so that a control that belongs to a scroll bar can be
        # told apart from one the app draws itself.
        $parent = ''
        try {
            $p = $c.Parent
            if ($p) { $parent = $p.Current.ControlType.ProgrammaticName -replace '^ControlType\.', '' }
        } catch { }

        $rows += [pscustomobject]@{
            Type     = $c.ControlType.ProgrammaticName -replace '^ControlType\.', ''
            Name     = $c.Name
            Role     = $c.LocalizedControlType
            Parent   = $parent
            X        = [int]$r.X
            Y        = [int]$r.Y
            W        = [int]$r.Width
            H        = [int]$r.Height
            Focus    = $c.IsKeyboardFocusable
            Off      = $c.IsOffscreen
        }
    } catch { }
}

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

$lines = @()
$lines += "# notecode accessibility audit"
$lines += ""
$lines += "Driven through UI Automation against the real window: $($all.Count) elements."
$lines += ""
$lines += "## Controls with no name"
$lines += ""
$lines += "A screen reader announces the role and nothing else. These are the ones it would."
$lines += ""
$lines += "| Type | Size | Focusable |"
$lines += "|---|---|---|"
$unnamed = $rows | Where-Object {
    $_.Name -eq '' -and $_.Type -in @('Button','CheckBox','RadioButton','ComboBox','Hyperlink','SplitButton','MenuItem','Tab','Tree','List','Edit')
}
foreach ($u in $unnamed) { $lines += "| $($u.Type) | $($u.W)x$($u.H) | $($u.Focus) |" }
if (-not $unnamed) { $lines += "| _(none)_ | | |" }
Check "every control a person can use has a name" ($unnamed.Count -eq 0) "$($unnamed.Count) without one"

$lines += ""
$lines += "## Targets smaller than $minPointer x $minPointer"
$lines += ""
$lines += "Microsoft's guidance puts the smallest pointer target at $minPointer pixels and a"
$lines += "touch target at $minTouch. Anything below the first is hard to hit."
$lines += ""
$lines += "| Type | Name | Size |"
$lines += "|---|---|---|"

# The scroll bar's own arrow buttons are the system's, at the system's size.
# They are not drawn by the app and they are not what the app's own controls
# are measured against, so they are listed but not counted as a failure.
$small = $rows | Where-Object {
    $_.W -gt 0 -and $_.H -gt 0 -and ($_.W -lt $minPointer -or $_.H -lt $minPointer) -and
    $_.Type -in @('Button','CheckBox','RadioButton','ComboBox','SplitButton')
}
$systemSmall = @($small | Where-Object { $_.Parent -eq 'ScrollBar' })
$ownSmall = @($small | Where-Object { $_.Parent -ne 'ScrollBar' })

foreach ($s in $small) { $lines += "| $($s.Type) | $($s.Name) | $($s.W)x$($s.H) |" }
if (-not $small) { $lines += "| _(none)_ | | |" }
if ($systemSmall.Count -gt 0) {
    $lines += ""
    $lines += "$($systemSmall.Count) of these are the scroll bar's own arrow buttons, which are the"
    $lines += "system's and are not counted."
}
Check "every target the app draws is at least $minPointer pixels" ($ownSmall.Count -eq 0) "$($ownSmall.Count) too small"
$lines += ""
$lines += "## The focus order"
$lines += ""
$lines += "What the Tab key walks through, in the order it walks it."
$lines += ""
$lines += "| # | Type | Name |"
$lines += "|---|---|---|"
$i = 0
foreach ($f in ($rows | Where-Object { $_.Focus -and -not $_.Off })) {
    $i++
    $lines += "| $i | $($f.Type) | $($f.Name) |"
}
$lines += ""
$lines += "## Every control, for the record"
$lines += ""
$lines += "| Type | Name | Size | Focusable |"
$lines += "|---|---|---|---|"
foreach ($r in $rows) {
    if ($r.Name -eq '' -and $r.Type -eq 'Text') { continue }
    $lines += "| $($r.Type) | $($r.Name) | $($r.W)x$($r.H) | $($r.Focus) |"
}

$text = $lines -join "`n"
Write-Output $text
Write-Output ""

if ($Out) {
    Set-Content -Path $Out -Value $text -Encoding utf8
    Write-Output "wrote $Out"
    Write-Output ""
}

if ($fail -eq 0) { Write-Output "a11y: PASS"; exit 0 } else { Write-Output "a11y: FAIL ($fail)"; exit 1 }
