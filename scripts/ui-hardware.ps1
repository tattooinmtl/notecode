# Checks the hardware scan and the hardware settings page.
#
# The splash screen runs the scan before the app opens, and the settings page is
# where the scan can be repeated and the build chosen by hand. Both are checked
# here the way a person would see them: the splash is captured while it is still
# on screen, and the settings page is opened through the prompt with /settings hardware.
#
#   ui-hardware.ps1 [-OutDir <folder>]

param(
    [string]$OutDir = 'C:\NexusOne\prototypes\notecode\rea\findings'
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$exe = Join-Path $root 'dist\notecode.exe'

. (Join-Path $PSScriptRoot 'winutil.ps1')

$failures = @()
function Check($name, $ok, $detail = '') {
    if ($ok) { Write-Output "  ok    $name" }
    else { Write-Output "  FAIL  $name  $detail"; $script:failures += $name }
}

function Get-Windows {
    param([int]$ProcessId)
    $list = @()
    foreach ($w in [Win32Shot]::Windows([uint32]$ProcessId)) {
        $sb = New-Object System.Text.StringBuilder 256
        [void][Win32Shot]::GetWindowText($w, $sb, 256)
        $r = New-Object Win32Shot+RECT
        [void][Win32Shot]::RectOf($w, [ref]$r)
        $list += [pscustomobject]@{
            Handle = $w
            Title = $sb.ToString()
            Width = $r.Right - $r.Left
            Height = $r.Bottom - $r.Top
        }
    }
    return $list
}

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$proc = Start-Process -FilePath $exe -WorkingDirectory (Join-Path $root 'dist') -PassThru
Write-Output "launched notecode pid=$($proc.Id)"

# ---------------------------------------------------------------- the splash
#
# The splash is a borderless window with its own caption, so it can be told
# apart from the editor window. It is captured while it is up: the scan is
# written out line by line, and that is the thing worth seeing.
$splashShot = Join-Path $OutDir 'notecode-hardware-splash.png'

$splash = $null
for ($i = 0; $i -lt 40 -and -not $splash; $i++) {
    Start-Sleep -Milliseconds 250
    $splash = Get-Windows -ProcessId $proc.Id | Where-Object { $_.Title -like '*hardware scan*' } | Select-Object -First 1
}
Check 'the splash screen appeared' ($null -ne $splash)

if ($splash) {
    # A moment later, so the capture shows the scan being written out rather
    # than the first line of it.
    Start-Sleep -Milliseconds 1800
    & (Join-Path $PSScriptRoot 'screenshot.ps1') -Out $splashShot | Out-Null
    Write-Output "splash captured while it was up"
}

# ---------------------------------------------------------------- the app
#
# The editor window is the one whose caption is exactly "notecode". The splash
# is captioned "notecode - hardware scan" and is a different window, so waiting
# for the exact caption is what waits for the app.
$main = $null
for ($i = 0; $i -lt 120 -and -not $main; $i++) {
    Start-Sleep -Milliseconds 500
    $main = Get-Windows -ProcessId $proc.Id | Where-Object { $_.Title -eq 'notecode' } | Select-Object -First 1
}
if (-not $main) { throw 'the notecode window never appeared' }
Write-Output "main window found ($($main.Width)x$($main.Height))"

# The splash must not be left behind: it closes itself when the scan is done.
$gone = $false
for ($i = 0; $i -lt 60 -and -not $gone; $i++) {
    Start-Sleep -Milliseconds 500
    $gone = -not (Get-Windows -ProcessId $proc.Id | Where-Object { $_.Title -like '*hardware scan*' })
}
Check 'the splash closed when the scan finished' $gone

# The window has to be ready before it has a prompt box: WebView2 takes a
# moment to come up and the editor is built in the meantime.
$rootEl = $null
for ($i = 0; $i -lt 60 -and -not $rootEl; $i++) {
    Start-Sleep -Milliseconds 500
    $rootEl = Get-AppAutomationRoot -ProcessId $proc.Id
    if ($rootEl) {
        $send = Find-ByName $rootEl 'Send' 'Button'
        if (-not $send) { $rootEl = $null }
    }
}
if (-not $rootEl) { throw 'the app never showed a prompt box' }

# ------------------------------------------------------------ the status bar
#
# The status bar has to say which build is running, because that is the whole
# point of applying a profile: "server: up" on its own says nothing about the
# backend. The health poll rewrites that label, so a bare "server: up" means
# the backend tag was lost on the way.
$serverLabel = ''
foreach ($e in $rootEl.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    try {
        if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and
            $e.Current.Name -like 'server:*') {
            $serverLabel = $e.Current.Name
        }
    } catch { }
}
$serverTag = ''
if ($serverLabel -match 'server:\s*up\s*\((.+)\)') { $serverTag = $Matches[1] }
Check 'the status bar names the backend that is running' ($serverTag -match 'CPU|Vulkan|CUDA|Metal') "the label says '$serverLabel'"

# ------------------------------------------------------------ the settings
Get-PromptBox $rootEl
Set-Prompt $script:PromptBox '/settings hardware'
Send-KeyToControl -Hwnd (Get-ControlHandle $script:PromptBox) -VirtualKey $script:VK_RETURN

$settings = $null
for ($i = 0; $i -lt 40 -and -not $settings; $i++) {
    Start-Sleep -Milliseconds 500
    $settings = Get-AppWindow -ProcessName 'notecode' -Title 'hardware'
}
if (-not $settings) { throw 'the hardware settings page never appeared' }
Write-Output "settings page found"

$settingsShot = Join-Path $OutDir 'notecode-hardware-settings.png'
[PostKey]::TopMost($settings)
Start-Sleep -Milliseconds 600
& (Join-Path $PSScriptRoot 'screenshot.ps1') -Out $settingsShot -ProcessName 'notecode' -WindowTitle 'hardware' | Out-Null

# The page has to show what the scan found.
$settingsRoot = [System.Windows.Automation.AutomationElement]::FromHandle($settings)
$all = $settingsRoot.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)

$names = @()
$texts = @()
foreach ($e in $all) {
    try {
        $names += $e.Current.Name
        if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit) {
            $texts += $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        }
    } catch { }
}

$cpu = (Get-CimInstance Win32_Processor).Name.Trim()
$factsText = ($texts -join "`n")
Check 'the page shows the processor it found' ($factsText.Contains($cpu)) "looking for '$cpu'"
Check 'the page shows the core count' ($factsText -match 'physical / \d+ logical')

# The context has to come back as the config says it is, not as a rounded or
# clipped number.
$ctxValue = ''
foreach ($e in $all) {
    try {
        if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit -and
            $e.Current.Name -eq 'context') {
            $ctxValue = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        }
    } catch { }
}
$cfgCtx = (Get-Content (Join-Path $root 'runtime\notecode.config.json') -Raw | ConvertFrom-Json).llama.context.ctxSize
# The control shows the number with a thousands separator, so the digits are what
# are compared.
$ctxDigits = ($ctxValue -replace '[^\d]', '')
Check 'the page shows the context the config asks for' ($ctxDigits -eq [string]$cfgCtx) "page says '$ctxValue', config says $cfgCtx"

foreach ($build in 'CPU only', 'Vulkan', 'CUDA (NVIDIA)', 'Metal (macOS)') {
    Check "the page offers '$build'" (($names -join '|').Contains($build))
}

Check 'the page has a scan button' (($names -join '|').Contains('Scan hardware'))
Check 'the page has an apply button' (($names -join '|').Contains('Apply and restart'))

# The saved profile has to be there, because the splash saved it.
$hasProfile = $false
foreach ($e in $all) {
    try {
        if ($e.Current.Name -like 'Profile *') { $hasProfile = $true }
    } catch { }
}
Check 'the splash saved a profile for this machine' $hasProfile

Write-Output ''
Write-Output "screenshots: $splashShot, $settingsShot"

if ($failures.Count -gt 0) {
    Write-Output ''
    Write-Output "FAIL: $($failures.Count) check(s) failed: $($failures -join ', ')"
    exit 1
}

Write-Output 'PASS'
exit 0
