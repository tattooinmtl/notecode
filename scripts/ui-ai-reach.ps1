# Checks that the AI can reach all three surfaces of the app: the terminal, the
# editor and the transcript.
#
# The checks go through the app's own MCP server, which is the same door the
# model goes through, so what passes here is what the model gets - for the
# local model and for a provider alike.
#
#   ui-ai-reach.ps1 [-TimeoutSeconds 180]

param(
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'
$shots = Join-Path $root 'rea\findings'
$work = Join-Path $root 'workspace'
$probe = Join-Path $root 'scripts\mcp-probe.mjs'

$file = Join-Path $work 'reach-check.md'
$first = 'the first line of the editor'
$second = 'the second line, written by the tool'
$termWord = 'ZEPHYRTERM'
$noteWord = 'ZEPHYRNOTE'

. (Join-Path $PSScriptRoot 'winutil.ps1')

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

Set-Content -Path $file -Value "$first`n" -Encoding utf8
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
Start-Sleep -Seconds 5

# --- the calls ----------------------------------------------------------------
#
# Open the file in the editor, read the buffer, write into it, read it again,
# run something in the terminal, write a note into the transcript and read the
# transcript back.
$calls = @(
    @{ name = 'open_in_editor'; arguments = @{ path = 'reach-check.md' }; waitMs = 0 },
    @{ name = 'get_editor_buffer'; arguments = @{}; waitMs = 2500 },
    @{ name = 'set_editor_text'; arguments = @{ text = "$second`n" }; waitMs = 2500 },
    @{ name = 'get_editor_buffer'; arguments = @{}; waitMs = 1500 },
    @{ name = 'run_command'; arguments = @{ command = "Write-Output $termWord" }; waitMs = 1000 },
    @{ name = 'post_to_chat'; arguments = @{ text = "a note from the model: $noteWord"; kind = 'agent' }; waitMs = 500 },
    @{ name = 'read_conversation'; arguments = @{ limit = 10 }; waitMs = 500 }
) | ConvertTo-Json -Depth 6 -Compress

Write-Output "calling the tools"
$raw = & node $probe $calls 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw "the probe failed: $raw" }

try { $answers = $raw | ConvertFrom-Json }
catch { throw "the probe did not answer with JSON: $raw" }

function Answer([string]$name, [int]$index = 0) {
    # The same tool can be called more than once, so the answer is picked by its
    # place in the list and not by its name alone.
    $matches = @($answers | Where-Object { $_.name -eq $name })
    if ($matches.Count -le $index) { return $null }
    return $matches[$index].text
}

# --- the editor ---------------------------------------------------------------
$opened = Answer 'open_in_editor'
Check "the file opens in the editor" ($opened -like '*Opened*') $opened

$buffer1 = Answer 'get_editor_buffer' 0
Check "the live buffer comes back" ($buffer1 -like "*$first*") 'the first line was read out of the editor'

$written = Answer 'set_editor_text'
Check "the text goes into the editor" ($written -like '*saved*' -or $written -like '*Wrote*') $written

$buffer2 = Answer 'get_editor_buffer' 1
Check "the editor holds the new text" ($buffer2 -like "*$second*") 'the buffer was read again after the write'

$onDisk = Get-Content -Path $file -Raw
Check "the file on disk was written too" ($onDisk -like "*$second*") 'the change reached the disk'

# --- the terminal -------------------------------------------------------------
$term = Answer 'run_command'
Check "the command ran in the terminal" ($term -like "*$termWord*") 'the output came back from the terminal'
Check "the terminal answered with the exit code" ($term -like '*exit*' -or $term -like '*code*') 'the exit code was reported'

# --- the transcript -----------------------------------------------------------
$note = Answer 'post_to_chat'
Check "the note went into the transcript" ($note -like '*transcript*') $note

$conversation = Answer 'read_conversation'
Check "the conversation can be read back" ($conversation -like "*$noteWord*") 'the note was in the transcript'

# The note has to be on screen too, not only in the app's own copy of it.
Start-Sleep -Seconds 2
$seen = $null -ne (Find-TextWith -Window $win -ProcessId $proc.Id -Needle $noteWord)
Check "the note is visible in the transcript" $seen 'the note is on screen'

& (Join-Path $root 'scripts\screenshot.ps1') -Out (Join-Path $shots 'notecode-ai-reach.png') -ProcessName notecode | Out-Null

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output ""
if ($fail -eq 0) { Write-Output "ai reach: PASS"; exit 0 } else { Write-Output "ai reach: FAIL ($fail)"; exit 1 }
