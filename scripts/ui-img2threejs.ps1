# Checks /img2threejs end to end from the app.
#
# A picture is attached with the paperclip, /img2threejs is sent, the output
# folder dialog is answered with the folder it suggests (assets\img2threejs in
# the workspace), and the job folder the pipeline writes is checked file by file.
# No model is needed: the command runs the bundled pipeline on its own.
#
#   ui-img2threejs.ps1 [-Image <png>] [-TimeoutSeconds 180]

param(
    [string]$Image = '',
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'dist\notecode.exe'
if (-not $Image) { $Image = Join-Path $root 'assets\notecode-256.png' }
$shots = Join-Path $env:TEMP 'notecode-ui-img2threejs'
New-Item -ItemType Directory -Path $shots -Force | Out-Null

. (Join-Path $PSScriptRoot 'winutil.ps1')

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

$cfg = Get-Content (Join-Path $root 'runtime\notecode.config.json') -Raw | ConvertFrom-Json
$workspace = $cfg.workspace.root
$jobsRoot = Join-Path $workspace 'assets\img2threejs'
$before = @(Get-ChildItem $jobsRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName)

# A profile of the test's own, so the user's last conversation is not reopened.
$env:NOTECODE_DATA_DIR = Join-Path $env:TEMP ('notecode-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))

# --- launch -------------------------------------------------------------------
$proc = Start-Process -FilePath $exe -WorkingDirectory (Join-Path $root 'dist') -PassThru
Write-Output "launched notecode pid=$($proc.Id)"

$win = $null
for ($i = 0; $i -lt 40 -and -not $win; $i++) {
    Start-Sleep -Milliseconds 500
    $win = Get-AppAutomationRoot -ProcessId $proc.Id
}
if (-not $win) { throw "the notecode window never appeared" }
Start-Sleep -Seconds 4

# --- the asset tree -----------------------------------------------------------
foreach ($name in 'blender', 'unity', 'img2threejs', 'threejs', 'textures', 'logos', 'REA_outputs') {
    Check "assets\$name exists" (Test-Path (Join-Path $workspace "assets\$name"))
}

Get-PromptBox $win
$promptBox = $script:PromptBox
$editHwnd = Get-ControlHandle $promptBox
Set-AppForeground -Hwnd ([Win32Shot]::PickWindow([uint32]$proc.Id))
Start-Sleep -Milliseconds 400
$promptBox.SetFocus()

# --- attach the picture with the paperclip ------------------------------------
$clip = Find-ByName $win 'Attach a file' 'Button'
Check "the paperclip is there" ($null -ne $clip)
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
    $nameEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Image)
    Start-Sleep -Milliseconds 400
    Send-KeyToControl -Hwnd (Get-ControlHandle $nameEdit) -VirtualKey $script:VK_RETURN
    Start-Sleep -Seconds 2
}
Check "the picture is attached" ($null -ne (Find-ByName $win 'Remove' 'Button'))

# --- send /img2threejs --------------------------------------------------------
Set-Prompt $promptBox '/img2threejs --name "notecode logo" --no-open'
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN
Write-Output "sent /img2threejs"

# The output folder is asked for, with assets\img2threejs already chosen.
$folderDlg = $null
for ($i = 0; $i -lt 40 -and -not $folderDlg; $i++) {
    Start-Sleep -Milliseconds 300
    $folderDlg = Find-ByName $win 'Output folder for this img2threejs job (a new folder is made inside it)' 'Window'
}
Check "the output folder is asked for" ($null -ne $folderDlg)
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'folder-dialog.png') -ProcessName notecode | Out-Null
if ($folderDlg) {
    $select = Find-ByName $folderDlg 'Select Folder' 'Button'
    if ($select) { $select.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    else { Check "the dialog has a Select Folder button" $false }
}

# --- the job ------------------------------------------------------------------
$done = $null
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline -and -not $done) {
    Start-Sleep -Seconds 2
    $done = Find-TextWith -Window $win -ProcessId $proc.Id -Needle 'is built'
}
Check "the chat reports the job is built" ($null -ne $done)
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'job-done.png') -ProcessName notecode | Out-Null

$job = Get-ChildItem $jobsRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $before -notcontains $_.FullName } | Sort-Object LastWriteTime | Select-Object -Last 1
Check "a new job folder was made in assets\img2threejs" ($null -ne $job) "$($job.FullName)"
if ($job) {
    foreach ($f in 'reference.png', 'intake\probe.json', 'assessment.json', 'object-sculpt-spec.json',
                   '.img2threejs\state.json', 'src\createObjectModel.js', 'model.glb', 'viewer.html',
                   'three.min.js', 'job.json') {
        Check "the job wrote $f" (Test-Path (Join-Path $job.FullName $f))
    }
    $manifest = Get-Content (Join-Path $job.FullName 'job.json') -Raw | ConvertFrom-Json
    $bad = @($manifest.steps | Where-Object { -not $_.ok } | ForEach-Object step)
    Check "every pipeline step passed" ($bad.Count -eq 0) "$(($manifest.steps | ForEach-Object step) -join ', ')$(if ($bad) { "; failed: $($bad -join ', ')" })"
    Write-Output "JOB=$($job.FullName)"
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Remove-Item $env:NOTECODE_DATA_DIR -Recurse -Force -ErrorAction SilentlyContinue
Write-Output "screenshots: $shots"
Write-Output ""
if ($fail -eq 0) { Write-Output "img2threejs: PASS"; exit 0 } else { Write-Output "img2threejs: FAIL ($fail)"; exit 1 }
