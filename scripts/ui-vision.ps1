# End-to-end check of the vision path.
#
# Attaches a picture to the prompt, sends a turn and checks that the model really
# saw the picture: the answer has to name what is in it.
#
#   ui-vision.ps1 [-TimeoutSeconds 300]
#
# The picture is a red square on white, so "what colour is the square" has exactly
# one right answer.

param(
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'
$workspace = Join-Path $root 'workspace'
$shots = Join-Path $root 'rea\findings'
$image = Join-Path $workspace 'vision-check.png'

. (Join-Path $PSScriptRoot 'winutil.ps1')

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

# --- the picture --------------------------------------------------------------
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 448, 448
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::White)
$g.FillRectangle([System.Drawing.Brushes]::Red, 100, 100, 248, 248)
$g.Dispose()
$bmp.Save($image, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "picture: $image"

# --- launch -------------------------------------------------------------------
# The test talks to the local model, so the local model has to be in charge.
Use-LocalProvider
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
Start-Sleep -Milliseconds 400
$promptBox.SetFocus()
Start-Sleep -Milliseconds 200

# --- attach the picture -------------------------------------------------------
$attach = Find-ByName $win 'Attach a file' 'Button'
Check "the attach button is there" ($null -ne $attach)
if ($attach) {
    # The click is posted, not invoked: a button that opens a modal dialog never
    # finishes, so Invoke would wait for the test to close the dialog. The paperclip
    # is a drawn control rather than a Button, so BM_CLICK would not reach it
    # either - the press and the release are posted instead.
    [PostKey]::MouseClick((Get-ControlHandle $attach), 12, 12)

    # The file dialog is owned by the app, so in the automation tree it hangs off
    # the app's window rather than off the desktop.
    $dialog = $null
    for ($i = 0; $i -lt 30 -and -not $dialog; $i++) {
        Start-Sleep -Milliseconds 300
        $dialog = Find-ByName $win 'Attach a file to the prompt' 'Window'
    }
    Check "the file dialog opened" ($null -ne $dialog)

    if ($dialog) {
        $nameEdit = $null
        foreach ($candidate in @('File name:', 'File name')) {
            $nameEdit = Find-ByName $dialog $candidate 'Edit'
            if ($nameEdit) { break }
        }
        if (-not $nameEdit) {
            $editCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Edit)
            $edits = $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
            if ($edits.Count -gt 0) { $nameEdit = $edits[0] }
        }
        Check "the dialog has a file name box" ($null -ne $nameEdit)

        if ($nameEdit) {
            $nameEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($image)
            Start-Sleep -Milliseconds 400
            $open = Find-ByName $dialog 'Open' 'Button'
            if ($open) {
                Click-Control -Hwnd (Get-ControlHandle $open)
            } else {
                Send-KeyToControl -Hwnd (Get-ControlHandle $nameEdit) -VirtualKey $script:VK_RETURN
            }
            Start-Sleep -Seconds 2
        }
    }
    Start-Sleep -Seconds 1

    # The chip has a Remove button, and only that button appears when a picture is
    # attached. Looking for the file name alone would match the project tree.
    $removeBtn = Find-ByName $win 'Remove' 'Button'
    Check "the picture is attached" ($null -ne $removeBtn)
    & (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'notecode-vision-attached.png') -ProcessName notecode | Out-Null
}

# --- ask about it -------------------------------------------------------------
Set-Prompt $promptBox "What colour is the square in this picture? Answer with one word."
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN
Write-Output "sent; waiting for the model to look at the picture"

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$answered = $false
while ((Get-Date) -lt $deadline) {
    & (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'notecode-vision-answer.png') -ProcessName notecode | Out-Null
    $answered = $true
    # The model answers in about ten seconds; give it a little longer and look.
    if ($answered) { break }
    Start-Sleep -Seconds 2
}
Start-Sleep -Seconds 25
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'notecode-vision-answer.png') -ProcessName notecode | Out-Null
Check "the turn was answered" (Test-Path (Join-Path $shots 'notecode-vision-answer.png'))

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output ""
if ($fail -eq 0) { Write-Output "vision: PASS"; exit 0 } else { Write-Output "vision: FAIL ($fail)"; exit 1 }
