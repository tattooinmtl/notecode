# Checks that a tool source can be added.
#
# /addtool registers an MCP server so the model gets new tools. The server is
# written into both places that need it - the app's config and the one
# llama-server reads - and the app connects to it at once.
#
# The tool source used here is notecode's own MCP server under a second name,
# so the test needs no extra file and no network.
#
#   ui-addtool.ps1

param(
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'
$shots = Join-Path $root 'rea\findings'
$appConfig = Join-Path $root 'runtime\notecode.config.json'
$llamaConfig = Join-Path $root 'runtime\mcp-servers.json'
$serverName = 'notecode-probe'

. (Join-Path $PSScriptRoot 'winutil.ps1')

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

# The config files are put back at the end, whatever happens.
$appBackup = Get-Content $appConfig -Raw
$llamaBackup = Get-Content $llamaConfig -Raw

# --- launch -------------------------------------------------------------------
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

Get-PromptBox $win
$promptBox = $script:PromptBox
$editHwnd = Get-ControlHandle $promptBox
Set-AppForeground -Hwnd ([Win32Shot]::PickWindow([uint32]$proc.Id))
Start-Sleep -Milliseconds 400
$promptBox.SetFocus()
Start-Sleep -Milliseconds 200

function Transcript-Text {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $sb = New-Object System.Text.StringBuilder
    foreach ($t in $all) { [void]$sb.AppendLine($t.Current.Name) }
    return $sb.ToString()
}

# --- the list -----------------------------------------------------------------
Set-Prompt $promptBox '/addtool'
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN
Start-Sleep -Seconds 4

$text = Transcript-Text
Check "the tool sources are listed" ($text -like '*Tool sources*') 'the list came back'
Check "the notecode server is in the list" ($text -like '*notecode*') 'the list has the servers'

# --- add one ------------------------------------------------------------------
$mcpScript = Join-Path $root 'runtime\notecode-mcp.mjs'
Set-Prompt $promptBox "/addtool $serverName stdio node `"$mcpScript`""
Send-KeyToControl -Hwnd $editHwnd -VirtualKey $script:VK_RETURN
Write-Output "sent /addtool; waiting for it to connect"
Start-Sleep -Seconds 8

$text = Transcript-Text
Check "the new tool source was added" ($text -like "*$serverName*") 'the transcript names it'

$app = Get-Content $appConfig -Raw | ConvertFrom-Json
Check "the app config has the server" ($null -ne $app.mcp.servers.$serverName) 'it is in notecode.config.json'
Check "the app config kept its command" (
    $null -ne $app.mcp.servers.$serverName -and
    $app.mcp.servers.$serverName.command -eq 'node') 'the command was written'

$llama = Get-Content $llamaConfig -Raw | ConvertFrom-Json
Check "the llama config has the server" ($null -ne $llama.mcpServers.$serverName) 'it is in mcp-servers.json'

# The app connects at once, so the tools are there without a restart.
$connected = $text -match "$serverName.*\d+ tools"
Check "the app connected to it at once" $connected 'the tool count was reported'

& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'notecode-addtool.png') -ProcessName notecode | Out-Null

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force

# --- put the config files back ------------------------------------------------
Set-Content -Path $appConfig -Value $appBackup -Encoding utf8 -NoNewline
Set-Content -Path $llamaConfig -Value $llamaBackup -Encoding utf8 -NoNewline
Write-Output "put the config files back"

Write-Output ""
if ($fail -eq 0) { Write-Output "addtool: PASS"; exit 0 } else { Write-Output "addtool: FAIL ($fail)"; exit 1 }
