# Checks that the configuration the app resolves is the one on this machine.
#
# The config file is written with %USERPROFILE% in the paths that point into
# the user's own folders - the search key, the skills, the kimi-code install -
# so that one file works on every machine. The app expands those when it reads
# the file.
#
# This check is the guard on that expansion. It asks the app what it resolved
# and looks for a path that is still a variable. A path that did not expand
# does not fail loudly: it becomes a folder whose name is literally
# "%USERPROFILE%", the skills are installed into it, and nothing says so.
#
#   ui-config.ps1 [-Exe <path>]
#
# -Exe points at a different build, which is how the guard is proved: a build
# with the old expansion has to fail this check.

param(
    [string]$Exe = ""
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
if (-not $Exe) { $Exe = Join-Path $root 'dist\notecode.exe' }
if (-not (Test-Path $Exe)) { throw "there is no executable at $Exe" }

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

# --- the app tells us what it resolved ------------------------------------------
$out = Join-Path $env:TEMP ("notecode-config-{0}.json" -f [guid]::NewGuid().ToString('N'))
$proc = Start-Process -FilePath $Exe -ArgumentList '--print-config', $out -Wait -PassThru `
    -WorkingDirectory $root

Check "the app printed the configuration" ($proc.ExitCode -eq 0) "exit code $($proc.ExitCode)"
Check "the configuration was written" (Test-Path $out) $out

if (-not (Test-Path $out)) {
    Write-Output ""
    Write-Output "config: FAIL ($fail)"
    exit 1
}

$text = Get-Content -Path $out -Raw -Encoding UTF8
Remove-Item -Path $out -Force -ErrorAction SilentlyContinue

try {
    $cfg = $text | ConvertFrom-Json
    Check "the configuration is JSON" ($null -ne $cfg) 'it parses'
} catch {
    Check "the configuration is JSON" $false $_.Exception.Message
    Write-Output ""
    Write-Output "config: FAIL ($fail)"
    exit 1
}

# --- nothing is still a variable ------------------------------------------------
# The point of the check. A path that is still %USERPROFILE% is a path that
# will be used as the name of a folder.
$leftovers = @([regex]::Matches($text, '%[A-Za-z_][A-Za-z0-9_]*%') |
    ForEach-Object { $_.Value } | Sort-Object -Unique)
Check "no path is still an environment variable" ($leftovers.Count -eq 0) `
    $(if ($leftovers.Count -gt 0) { "still unexpanded: " + ($leftovers -join ', ') } else { "every %VAR% was expanded" })

# --- the paths that matter are real ---------------------------------------------
$roots = @($cfg.skills.roots)
Check "the skills roots were read" ($roots.Count -gt 0) "$($roots.Count) roots"
foreach ($r in $roots) {
    Check "the skills root exists: $r" (Test-Path $r) $r
}

$keyFile = $cfg.search.keyFile
Check "the search key file is an absolute path" ($keyFile -match '^[A-Za-z]:\\') $keyFile

$kimi = $cfg.kimi.exe
Check "the kimi-code executable is an absolute path" ($kimi -match '^[A-Za-z]:\\') $kimi

# The kimi-code install is under the user's own profile, so a path that points
# somewhere else is a path that was not expanded the way it was meant to be.
$profile = [Environment]::GetFolderPath('UserProfile')
Check "the search key file is under the user profile" ($keyFile.StartsWith($profile, [StringComparison]::OrdinalIgnoreCase)) `
    "the profile is $profile"

Write-Output ""
if ($fail -eq 0) { Write-Output "config: PASS"; exit 0 } else { Write-Output "config: FAIL ($fail)"; exit 1 }
