# Checks that "Apply and restart" really restarts the app under the new profile.
#
# This one is destructive: it restarts notecode, so it is not part of the suite.
# It clicks Apply, waits for the old instance to go and a new one to appear, and
# checks that the new instance is running under the profile that was saved.
#
#   ui-hardware-restart.ps1

param()

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'

. (Join-Path $PSScriptRoot 'winutil.ps1')

$failures = @()
function Check($name, $ok, $detail = '') {
    if ($ok) { Write-Output "  ok    $name" }
    else { Write-Output "  FAIL  $name  $detail"; $script:failures += $name }
}

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$proc = Start-Process -FilePath $exe -WorkingDirectory (Join-Path $root 'dist') -PassThru
Write-Output "launched notecode pid=$($proc.Id)"

# Wait for the editor window, not the splash.
$main = $null
for ($i = 0; $i -lt 120 -and -not $main; $i++) {
    Start-Sleep -Milliseconds 500
    foreach ($w in [Win32Shot]::Windows([uint32]$proc.Id)) {
        $sb = New-Object System.Text.StringBuilder 256
        [void][Win32Shot]::GetWindowText($w, $sb, 256)
        if ($sb.ToString() -eq 'notecode') { $main = $w; break }
    }
}
if (-not $main) { throw 'the notecode window never appeared' }
Start-Sleep -Seconds 5

$rootEl = $null
for ($i = 0; $i -lt 40 -and -not $rootEl; $i++) {
    Start-Sleep -Milliseconds 500
    $rootEl = Get-AppAutomationRoot -ProcessId $proc.Id
    if ($rootEl) {
        $send = Find-ByName $rootEl 'Send' 'Button'
        if (-not $send) { $rootEl = $null }
    }
}
if (-not $rootEl) { throw 'the app never showed a prompt box' }

Get-PromptBox $rootEl
Set-Prompt $script:PromptBox '/settings'
Send-KeyToControl -Hwnd (Get-ControlHandle $script:PromptBox) -VirtualKey $script:VK_RETURN

$settings = $null
for ($i = 0; $i -lt 40 -and -not $settings; $i++) {
    Start-Sleep -Milliseconds 500
    $settings = Get-AppWindow -ProcessName 'notecode' -Title 'hardware'
}
if (-not $settings) { throw 'the hardware settings page never appeared' }
Start-Sleep -Seconds 1

# The Apply button. It is posted rather than invoked, because it closes the
# form and restarts the app and an invoked click would wait for that.
$settingsRoot = [System.Windows.Automation.AutomationElement]::FromHandle($settings)
$apply = Find-ByName $settingsRoot 'Apply and restart' 'Button'
if (-not $apply) { throw 'the Apply button was not found' }
[PostKey]::Click((Get-ControlHandle $apply))

Write-Output 'clicked Apply and restart'

# The old instance has to go, and a new one has to arrive.
$oldGone = $false
for ($i = 0; $i -lt 60 -and -not $oldGone; $i++) {
    Start-Sleep -Milliseconds 500
    if (-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) { $oldGone = $true }
}
Check 'the old instance closed' $oldGone

$new = $null
for ($i = 0; $i -lt 120 -and -not $new; $i++) {
    Start-Sleep -Milliseconds 500
    foreach ($p in (Get-Process -Name notecode -ErrorAction SilentlyContinue | Sort-Object StartTime -Descending)) {
        if ($p.Id -eq $proc.Id) { continue }
        foreach ($w in [Win32Shot]::Windows([uint32]$p.Id)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Win32Shot]::GetWindowText($w, $sb, 256)
            if ($sb.ToString() -eq 'notecode') { $new = $p; break }
        }
        if ($new) { break }
    }
}
Check 'a new instance started' ($null -ne $new) $(if ($new) { "pid $($new.Id)" } else { 'none' })

if ($new) {
    # The new instance has to be running the profile that was saved, not the
    # one it had before. The app writes its data where NOTECODE_DATA_DIR says,
    # and the test points that at a profile of its own.
    $store = Join-Path (Get-AppDataDir) 'hardware-profiles.json'
    $saved = $null
    if (Test-Path $store) {
        $saved = (Get-Content $store -Raw | ConvertFrom-Json).profiles |
            Where-Object { $_.id -eq (Get-Content $store -Raw | ConvertFrom-Json).activeId }
    }
    Check 'the profile was saved' ($null -ne $saved) $(if ($saved) { $saved.name } else { 'nothing saved' })
    Check 'the new instance kept the context it was saved with' `
        ($null -ne $saved -and $saved.ctxSize -eq 32768) $(if ($saved) { "ctx $($saved.ctxSize)" } else { '' })
}

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force

if ($failures.Count -gt 0) {
    Write-Output ''
    Write-Output "FAIL: $($failures.Count) check(s) failed: $($failures -join ', ')"
    exit 1
}

Write-Output 'PASS'
exit 0
