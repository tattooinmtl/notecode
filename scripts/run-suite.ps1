# Runs the whole test suite, one script at a time.
#
# The GUI scripts each launch the app, and the app takes the single-instance
# lock, so they cannot run at the same time. They are also slow: the ones that
# need the local model wait for it, and the local model is slow.
#
#   run-suite.ps1 [-SkipSlow] [-Only <name>]
#
# -SkipSlow leaves out the scripts that need a model to answer.
# -Only runs the scripts whose name contains the text.

param(
    [switch]$SkipSlow,
    [string]$Only = ""
)

$ErrorActionPreference = 'Continue'

$here = $PSScriptRoot

# name, script, needs a model
$suite = @(
    @{ name = 'smoke';       script = 'smoke-test.ps1';       slow = $false },
    @{ name = 'modes';       script = 'check-modes.ps1';       slow = $false },
    @{ name = 'contrast';    script = 'check-contrast.ps1';    slow = $false },
    @{ name = 'a11y';        script = 'ui-a11y-audit.ps1';     slow = $false },
    @{ name = 'ai-reach';    script = 'ui-ai-reach.ps1';        slow = $false },
    @{ name = 'addtool';     script = 'ui-addtool.ps1';         slow = $false },
    @{ name = 'tab-close';   script = 'ui-tab-close.ps1';       slow = $false },
    @{ name = 'command';     script = 'ui-command-smoke.ps1';   slow = $false },
    @{ name = 'provider';    script = 'ui-provider-form.ps1';   slow = $false },
    @{ name = 'layout';      script = 'ui-layout-check.ps1';    slow = $false },
    @{ name = 'hardware';    script = 'ui-hardware.ps1';        slow = $false },
    @{ name = 'tray';        script = 'ui-tray.ps1';            slow = $false },
    @{ name = 'installer';   script = 'ui-installer.ps1';       slow = $false },
    @{ name = 'config';      script = 'ui-config.ps1';          slow = $false },
    @{ name = 'byok';        script = 'ui-byok-turn.ps1';       slow = $false },
    @{ name = 'tool-smoke';  script = 'ui-tool-smoke.ps1';      slow = $true },
    @{ name = 'agent-files'; script = 'ui-agent-files.ps1';     slow = $true },
    @{ name = 'reasoning';   script = 'ui-reasoning.ps1';       slow = $true },
    @{ name = 'attach';      script = 'ui-attach-file.ps1';     slow = $true },
    @{ name = 'vision';      script = 'ui-vision.ps1';          slow = $true },
    @{ name = 'addskills';   script = 'ui-addskills.ps1';       slow = $true },
    @{ name = 'blender';     script = 'ui-blender-skill.ps1';   slow = $true }
)

$results = @()

foreach ($item in $suite) {
    if ($SkipSlow -and $item.slow) {
        Write-Output "--- SKIP $($item.name) (needs a model)"
        $results += [pscustomobject]@{ Name = $item.name; Result = 'SKIP'; Detail = '' }
        continue
    }
    if ($Only -and $item.name -notlike "*$Only*") { continue }

    $path = Join-Path $here $item.script
    Write-Output ""
    Write-Output ("=" * 70)
    Write-Output "--- $($item.name)  ($($item.script))"
    Write-Output ("=" * 70)

    $started = Get-Date
    $output = & $path 2>&1 | Out-String
    $code = $LASTEXITCODE
    $took = [int]((Get-Date) - $started).TotalSeconds

    $output.TrimEnd() | Write-Output
    $result = if ($code -eq 0) { 'PASS' } else { 'FAIL' }
    $results += [pscustomobject]@{ Name = $item.name; Result = $result; Detail = "$took s" }

    # The app must not be left running between the scripts.
    Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
}

Write-Output ""
Write-Output ("=" * 70)
Write-Output "SUMMARY"
Write-Output ("=" * 70)
$results | ForEach-Object { "{0,-14} {1,-6} {2}" -f $_.Name, $_.Result, $_.Detail }

$failed = @($results | Where-Object { $_.Result -eq 'FAIL' })
Write-Output ""
if ($failed.Count -eq 0) {
    Write-Output "suite: PASS ($($results.Count) scripts)"
    exit 0
} else {
    Write-Output "suite: FAIL ($($failed.Count) of $($results.Count))"
    exit 1
}
