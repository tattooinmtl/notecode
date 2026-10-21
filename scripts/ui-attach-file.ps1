# Checks that a file the user attaches is read by the model.
#
# The paperclip in the composer opens the file picker. A picture goes into the
# turn as an image for the model to look at; any other file is read and put in
# front of the model as its own turn, so it can answer a question about it.
#
#   ui-attach-file.ps1 [-TimeoutSeconds 300]

param(
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'
$shots = Join-Path $root 'rea\findings'
$work = Join-Path $root 'workspace'
$file = Join-Path $work 'attach-check.md'

# A word that cannot be guessed, so the answer proves the file was read.
$secret = 'ZEPHYR47'

. (Join-Path $PSScriptRoot 'winutil.ps1')

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

Set-Content -Path $file -Value "# Notes`n`nThe secret word is $secret.`n" -Encoding utf8
Write-Output "wrote $file"

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

# --- the paperclip ------------------------------------------------------------
$clip = Find-ByName $win 'Attach a file' 'Button'
Check "the paperclip is there" ($null -ne $clip)

if ($clip) {
    # The click is posted, not invoked: a button that opens a modal dialog never
    # finishes, so Invoke would wait for the test to close the dialog. The paperclip
    # is a drawn control rather than a Button, so BM_CLICK would not reach it
    # either - the press and the release are posted instead.
    [PostKey]::MouseClick((Get-ControlHandle $clip), 12, 12)

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
            $nameEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($file)
            Start-Sleep -Milliseconds 400
            $open = Find-ByName $dialog 'Open' 'Button'
            if ($open) { Click-Control -Hwnd (Get-ControlHandle $open) }
            else { Send-KeyToControl -Hwnd (Get-ControlHandle $nameEdit) -VirtualKey $script:VK_RETURN }
            Start-Sleep -Seconds 2
        }
    }

    # The chip has a Remove button, and only that button appears when something is
    # attached. Looking for the file name alone would match the project tree.
    $removeBtn = Find-ByName $win 'Remove' 'Button'
    Check "the file is attached" ($null -ne $removeBtn)
    & (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'notecode-attach-file.png') -ProcessName notecode | Out-Null
}

# --- ask about it -------------------------------------------------------------
Set-Prompt $promptBox 'What is the secret word in the attached file? Answer with the word only.'
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN
Write-Output "sent the question; waiting for the model"

# The transcript is a WebView2, so its text is exposed to UI Automation.
function Find-TextWith([string]$needle) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($t in $all) {
        if ($t.Current.Name -like "*$needle*") { return $t }
    }
    return $null
}

$answer = $null
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    $answer = Find-TextWith $secret
    if ($answer) { break }
    Start-Sleep -Seconds 3
}
Check "the model read the attached file" ($null -ne $answer) "the word $secret came back"

& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'notecode-attach-answer.png') -ProcessName notecode | Out-Null

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output ""
if ($fail -eq 0) { Write-Output "attach file: PASS"; exit 0 } else { Write-Output "attach file: FAIL ($fail)"; exit 1 }
