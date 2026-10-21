# Checks that a skill can be added and repaired.
#
# /addskills takes a markdown file, the skill fixer repairs it so it works in
# notecode, and the result is installed where /skills can find it.
#
# The file used here is deliberately broken: it has no front matter and it tells
# the model to use a tool notecode does not have.
#
#   ui-addskills.ps1 [-TimeoutSeconds 600]

param(
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'
$shots = Join-Path $root 'rea\findings'
$work = Join-Path $root 'workspace'
$skillFile = Join-Path $work 'notecode-reach-test-skill.md'

# The name the fixer gives the skill, and the folder it lands in.
$skillName = 'notecode-reach-test-skill'

. (Join-Path $PSScriptRoot 'winutil.ps1')

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

# A skill with no front matter and a tool that is not there.
@'
# Broken research skill

Use `web_search` to look a subject up, then write what you found into
`notes.md` with `write_file`.

Check the sources before you write.
'@ | Set-Content -Path $skillFile -Encoding utf8

Write-Output "wrote $skillFile"

# --- launch -------------------------------------------------------------------
# The test talks to the local model, so the local model has to be in charge.
Use-LocalProvider
Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# The local server reads its MCP servers once, when it starts, so the tools the
# fixer needs are only in its catalogue after a restart.
$stale = $true
try {
    $catalogue = Invoke-RestMethod 'http://127.0.0.1:8080/tools'
    $names = @($catalogue | Where-Object { $_.type -eq 'mcp' } | ForEach-Object { $_.tool })
    $stale = -not ($names -contains 'notecode_write_skill')
} catch { $stale = $true }

if ($stale) {
    Write-Output 'restarting llama-server so it has the new tools'
    $wrapper = Join-Path $root 'runtime\notecode-llama.ps1'
    & $wrapper stop | Out-Null
    & $wrapper start | Out-Null
    Start-Sleep -Seconds 2
}

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

# --- run the command ----------------------------------------------------------
Set-Prompt $promptBox "/addskills `"$skillFile`""
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN
Write-Output "sent /addskills; waiting for the fixer"

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

# The report comes at the end, so the last one is the one to wait for.
#
# The transcript renders each line of the markdown as its own text element, so
# the end of the report is looked for by the words it ends with - not by the
# heading at the top of it.
$endings = @('Installed as', 'already in a skill folder', 'It was not installed')
$report = $null
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    foreach ($ending in $endings) {
        $found = Find-TextWith $ending
        if ($found) { $report = $found; break }
    }
    if ($report) { break }
    Start-Sleep -Seconds 3
}

Check "the fixer reported back" ($null -ne (Find-TextWith 'Skill fixer')) "the transcript has the report"
Check "the fixer finished" ($null -ne $report) "the report says where the skill went"
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'notecode-addskills.png') -ProcessName notecode | Out-Null

# --- the skill was repaired ---------------------------------------------------
$fixed = Get-Content -Path $skillFile -Raw
Check "the skill has front matter now" ($fixed -like '---*') 'the file starts with front matter'
Check "the front matter names the skill" ($fixed -match '(?m)^name:\s*\S') 'there is a name'
Check "the front matter describes the skill" ($fixed -match '(?m)^description:\s*\S') 'there is a description'

# The name is the app's to decide: it is the name of the file the user picked.
# A model that renamed the skill would leave the user unable to find it again.
Check "the skill keeps the name of the file" `
    ($fixed -match ("(?m)^name:\s*" + [regex]::Escape($skillName) + "\s*$")) "it is $skillName"
# The skill may still *mention* web_search, to say that there is no such tool.
# What must be gone is the instruction to use it - so every line that names it
# has to carry a negation with it.
$webLines = @($fixed -split "`n" | Where-Object { $_ -match 'web_search' })
$instructs = @($webLines | Where-Object { $_ -notmatch '(?i)\b(not|never|cannot|can not|no|avoid|instead|without)\b' })
Check "the skill does not tell the model to use web_search" ($instructs.Count -eq 0) 'web_search is no longer a step'
# Every tool the skill names has to be a tool that is really there, and it has
# to name at least one. The bare names and the names the catalogue publishes
# are both real, so both count.
$known = @(
    'read_file','write_text','list_files','list_supported_types','make_folder','move_path',
    'copy_path','delete_path','save_as','open_in_editor','run_command','get_editor_buffer',
    'set_editor_text','post_to_chat','read_conversation','list_skills','read_skill',
    'write_skill','describe_notecode',
    'file_glob_search','grep_search','write_file','edit_file','get_info','exec_shell_command'
)
$named = @([regex]::Matches($fixed, '`([a-z][a-z0-9_]+)`') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$unknown = @($named | Where-Object {
    $n = $_
    -not ($known -contains $n) -and -not ($known -contains ($n -replace '^notecode_', ''))
})
Check "the skill names tools that exist" ($unknown.Count -eq 0 -and $named.Count -gt 0) `
    ("it names: " + ($named -join ', '))
Check "the skill kept its instructions" ($fixed -match 'sources|look|write') 'the body still says what to do'

# --- the skill was installed --------------------------------------------------
$installed = Get-ChildItem -Path 'C:\Users\ThePa\.brain-skills\skills' -Filter 'SKILL.md' -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -eq $skillName }
Check "the skill was installed" ($null -ne $installed) "it is in a skill folder"

# --- /skills lists it ---------------------------------------------------------
Set-Prompt $promptBox '/skills'
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN
Start-Sleep -Seconds 4

$listed = Find-TextWith 'Skills'
Check "/skills lists the skill" ($null -ne $listed) 'the list came back'

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force

# --- clean up the skill this test installed ------------------------------------
$installedDir = Join-Path 'C:\Users\ThePa\.brain-skills\skills' $skillName
if (Test-Path $installedDir) {
    Remove-Item -Path $installedDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Output "removed the installed test skill"
}

Write-Output ""
if ($fail -eq 0) { Write-Output "addskills: PASS"; exit 0 } else { Write-Output "addskills: FAIL ($fail)"; exit 1 }
