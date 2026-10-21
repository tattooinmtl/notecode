# Checks the three things the agent does with files.
#
#   1. an HTML page the agent writes opens as a tab in the editor
#   2. the cross on a tab closes it
#   3. a file the agent is told to move really moves, into a folder it makes
#
#   ui-agent-files.ps1
#
# The agent is the local model, so a turn takes a while on this machine.

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'
$workspace = Join-Path $root 'workspace'
$shots = Join-Path $root 'rea\findings'

. (Join-Path $PSScriptRoot 'winutil.ps1')

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

$page = Join-Path $workspace 'agent-page.html'
$folder = Join-Path $workspace 'website'
if (Test-Path $page) { Remove-Item $page -Force }
if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }

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

function Send-Turn([string]$text) {
    Set-Prompt $promptBox $text
    Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN
    Write-Output "sent: $text"

    # The prompt box is emptied the moment the turn is accepted, so an empty box
    # means the message was picked up, not that the answer has arrived.
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        if ((Get-Prompt $promptBox).Trim().Length -eq 0) { return }
        Start-Sleep -Milliseconds 400
    }
    Write-Output "  (the prompt box still holds the text - was the turn accepted?)"
}

# Waits for a thing to become true. The local model needs 40-80 s a turn, so the
# wait has to be on the result, never on a fixed sleep.
function Wait-For([scriptblock]$condition, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Seconds 2
    }
    Write-Output "  (gave up after $seconds s waiting for $what)"
    return $false
}

# --- 1. a page the agent writes opens as a tab --------------------------------
Send-Turn 'Make an HTML page called agent-page.html that says hello.'

Wait-For { (Test-Path $page) -and (Get-Item $page).Length -gt 0 } 300 'the agent to write the page' | Out-Null

Check "the page was written" (Test-Path $page)
if (Test-Path $page) {
    $html = Get-Content $page -Raw
    Check "the page has real HTML in it" ($html -match '<html' -and $html -match 'hello') "$($html.Length) bytes"
}

# The tab. The strip paints its captions, so the cross is what proves a tab is
# open: there is one per tab, named after the file it closes.
$closeButton = $null
for ($i = 0; $i -lt 30 -and -not $closeButton; $i++) {
    $closeButton = Find-ByName $win 'Close agent-page.html' 'Button'
    if (-not $closeButton) { Start-Sleep -Milliseconds 500 }
}
Check "the page opened as a tab" ($null -ne $closeButton)

& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'notecode-agent-page.png') -ProcessName notecode | Out-Null
Check "the app was captured" (Test-Path (Join-Path $shots 'notecode-agent-page.png'))

# --- 2. the cross on the tab closes it ----------------------------------------
if ($closeButton) {
    # A real click on the cross: it is a custom control, so BM_CLICK would not
    # reach it - the press and the release are posted instead.
    [PostKey]::MouseClick((Get-ControlHandle $closeButton), 7, 7)
    Start-Sleep -Seconds 2

    # A tab with unsaved work asks before it goes, so the answer is given here
    # rather than left blocking the run.
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

    $stillThere = Find-ByName $win 'Close agent-page.html' 'Button'
    Check "the cross closed the tab" ($null -eq $stillThere)
}

# --- 3. the agent moves a file into a folder it makes --------------------------
if (Test-Path $page) {
    Send-Turn 'Move agent-page.html into a new folder called website.'

    Wait-For { (Test-Path $folder) -and (Test-Path (Join-Path $folder 'agent-page.html')) } 300 'the folder and the moved file' | Out-Null

    Check "the folder was made" (Test-Path $folder)
    Check "the file moved into it" (Test-Path (Join-Path $folder 'agent-page.html'))
    Check "the file is gone from the old place" (-not (Test-Path $page))
} else {
    Check "the folder was made" $false "the first turn never wrote the page, so there was nothing to move"
    Check "the file moved into it" $false
    Check "the file is gone from the old place" $true
}

& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'notecode-agent-move.png') -ProcessName notecode | Out-Null

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output ""
if ($fail -eq 0) { Write-Output "agent files: PASS"; exit 0 } else { Write-Output "agent files: FAIL ($fail)"; exit 1 }
