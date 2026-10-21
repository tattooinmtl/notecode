# Checks the Blender path end to end: the skill is added through /addskills,
# the fixer keeps the Blender tool names, and the skill lands where /skills
# can find it.
#
# The point of the check is the names. The catalogue publishes the MCP tools
# with the server name in front - `blender_execute_python`, not
# `execute_python` - so a fixer that only knows the bare names would take the
# real tools out of the skill. This test is what catches that.
#
#   ui-blender-skill.ps1 [-TimeoutSeconds 900]

param(
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'
$shots = Join-Path $root 'rea\findings'
$skillFile = Join-Path $root 'skills\blender-scene\SKILL.md'
$skillName = 'blender-scene'
$skillsRoot = 'C:\Users\ThePa\.brain-skills\skills'

. (Join-Path $PSScriptRoot 'winutil.ps1')

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

if (-not (Test-Path $skillFile)) { throw "the skill is not there: $skillFile" }

# The source is kept, so a failed run can be compared against it.
$before = Get-Content -Path $skillFile -Raw

# --- the Blender server is up ----------------------------------------------------
$blenderUp = $false
try {
    $r = Invoke-RestMethod 'http://127.0.0.1:8765/mcp' -Method Post -TimeoutSec 15 -Body '{}' `
        -Headers @{ 'Content-Type' = 'application/json' } -ErrorAction Stop
    $blenderUp = $true
} catch {
    # A POST without a proper initialize fails, but a failure that is not a
    # connection failure still means the port is answering.
    $blenderUp = $_.Exception.Message -notmatch 'refused|actively refused|unreachable|timeout'
}
Check "the Blender MCP server is answering" $blenderUp 'http://127.0.0.1:8765/mcp'

# --- launch ---------------------------------------------------------------------
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
Start-Sleep -Seconds 6

# The connection is announced in the transcript, not in the log: the app puts
# "[mcp:blender] connected - 26 tools" in the chat panel.
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

$connected = $false
for ($i = 0; $i -lt 30 -and -not $connected; $i++) {
    $connected = $null -ne (Find-TextWith 'mcp:blender')
    if (-not $connected) { Start-Sleep -Seconds 2 }
}
Check "the app connected to the Blender server" $connected 'the transcript says so'

Get-PromptBox $win
$promptBox = $script:PromptBox
$editHwnd = Get-ControlHandle $promptBox
Set-AppForeground -Hwnd ([Win32Shot]::PickWindow([uint32]$proc.Id))
Start-Sleep -Milliseconds 400
$promptBox.SetFocus()
Start-Sleep -Milliseconds 200

# --- run the command ------------------------------------------------------------
Set-Prompt $promptBox "/addskills `"$skillFile`""
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN
Write-Output "sent /addskills; waiting for the fixer"

# The report comes at the end, so the last one is the one to wait for.
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

Check "the fixer reported back" ($null -ne (Find-TextWith 'Skill fixer')) 'the transcript has the report'
Check "the fixer finished" ($null -ne $report) 'the report says where the skill went'
& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'notecode-blender-skill.png') -ProcessName notecode | Out-Null

# --- the skill survived the fixer ------------------------------------------------
$after = Get-Content -Path $skillFile -Raw
Check "the skill has front matter" ($after -like '---*') 'the file starts with front matter'
Check "the front matter names the skill" ($after -match '(?m)^name:\s*\S') 'there is a name'
Check "the front matter describes the skill" ($after -match '(?m)^description:\s*\S') 'there is a description'

# The names the catalogue really uses. A fixer that only knew the bare names
# would have taken every one of these out.
foreach ($tool in @('blender_execute_python', 'blender_scene_info', 'blender_get_addon_info')) {
    # .Contains, not -like: a backtick is the wildcard escape character, so a
    # pattern with one in it does not mean what it looks like.
    Check "the skill still names $tool" ($after.Contains("``$tool``")) 'the real tool name is there'
}
Check "the skill kept its instructions" ($after.Length -gt ($before.Length / 2)) "it went from $($before.Length) to $($after.Length) characters"

# --- it was installed ------------------------------------------------------------
$installed = Join-Path $skillsRoot $skillName
$installedFile = Join-Path $installed 'SKILL.md'
Check "the skill was installed" (Test-Path $installedFile) "it is in $installed"
if (Test-Path $installedFile) {
    $installedText = Get-Content -Path $installedFile -Raw
    Check "the installed copy has front matter" ($installedText -like '---*') 'the file starts with front matter'
    Check "the installed copy names the skill" ($installedText -match '(?m)^name:\s*\S') 'there is a name'
}

# --- /skills lists it ------------------------------------------------------------
Set-Prompt $promptBox '/skills'
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN
Start-Sleep -Seconds 5

$listed = Find-TextWith 'blender-scene'
Check "/skills lists the skill" ($null -ne $listed) 'the list came back'

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Output ""
if ($fail -eq 0) { Write-Output "blender skill: PASS"; exit 0 } else { Write-Output "blender skill: FAIL ($fail)"; exit 1 }
