# Checks the Windows installer.
#
# The installer is the one thing that has to work on a machine that has never
# seen this project, so it is checked the way a person would meet it: it is
# run, the files land where they should, the configuration points at the
# installed llama-server, the app starts from the installed folder, and the
# uninstaller takes it all away again.
#
# The check installs into a folder of its own, so the real installation and
# the real desktop shortcut are not touched.
#
#   ui-installer.ps1 [-Setup <path>]

param(
    [string]$Setup = '',
    [string]$TestDir = ''
)

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
if (-not $Setup) { $Setup = Join-Path $root 'installer\output\Notecode-Setup.exe' }
if (-not $TestDir) { $TestDir = Join-Path $env:TEMP 'notecode-install-check' }

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  [PASS] $name$(if ($detail) { " - $detail" })" }
    else { Write-Output "  [FAIL] $name$(if ($detail) { " - $detail" })"; $script:fail++ }
}

Write-Output "the installer"
Check "the installer was built" (Test-Path $Setup) $Setup
if (-not (Test-Path $Setup)) { Write-Output "installer: FAIL"; exit 1 }
Check "the installer is a program" ((Get-Item $Setup).Length -gt 1MB) ("{0:N1} MB" -f ((Get-Item $Setup).Length / 1MB))

# --- the install ------------------------------------------------------------
Write-Output ""
Write-Output "the install"

Get-Process -Name notecode -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $TestDir) { Remove-Item $TestDir -Recurse -Force -ErrorAction SilentlyContinue }

& $Setup --silent --dir $TestDir --no-shortcuts 2>&1 | Out-String | Write-Output
Start-Sleep -Seconds 4

Check "the folder was created" (Test-Path $TestDir) $TestDir
Check "the app is in it" (Test-Path (Join-Path $TestDir 'notecode.exe'))
Check "the editor is in it" (Test-Path (Join-Path $TestDir 'wwwroot\chat.html'))
Check "the runtime is in it" (Test-Path (Join-Path $TestDir 'runtime\notecode.config.json'))
Check "the wrapper is in it" (Test-Path (Join-Path $TestDir 'runtime\notecode-llama.ps1'))
Check "the icon is in it" (Test-Path (Join-Path $TestDir 'notecode.ico'))
Check "the uninstaller is in it" (Test-Path (Join-Path $TestDir 'Uninstall.exe'))

# The llama.cpp runtime is what makes the installed app work without a
# separate llama.cpp build.
$llama = Join-Path $TestDir 'llama\llama-server.exe'
Check "the llama-server is in it" (Test-Path $llama)
Check "its libraries are in it" ((Get-ChildItem (Join-Path $TestDir 'llama') -Filter *.dll -File -ErrorAction SilentlyContinue).Count -ge 3)

# The models must not be in it. They are tens of gigabytes.
$big = Get-ChildItem $TestDir -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Length -gt 200MB }
Check "no model weights were shipped" ($null -eq $big) ($big | ForEach-Object { $_.Name })

# The configuration has to point at the installed llama-server, not at the
# build machine.
$cfgPath = Join-Path $TestDir 'runtime\notecode.config.json'
if (Test-Path $cfgPath) {
    $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
    Check "the config points at the installed llama-server" ($cfg.llama.serverExe -eq $llama) $cfg.llama.serverExe
    Check "the cpu build points at it too" ($cfg.llama.builds.cpu.exe -eq $llama) $cfg.llama.builds.cpu.exe
    Check "the workspace is inside the install" ($cfg.workspace.root -like "$TestDir*") $cfg.workspace.root
}

# --- the installed app runs -------------------------------------------------
Write-Output ""
Write-Output "the installed app"

$proc = Start-Process -FilePath (Join-Path $TestDir 'notecode.exe') -WorkingDirectory $TestDir -PassThru
Start-Sleep -Seconds 14
$alive = $null -ne (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)
Check "the installed app starts" $alive

if ($alive) {
    Add-Type -AssemblyName UIAutomationClient
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $wins = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $cond)
    $named = @()
    foreach ($w in $wins) { $named += $w.Current.Name }
    Check "its window came up" (($named -join '|').Contains('notecode')) ($named -join ', ')
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}

# --- the uninstall ----------------------------------------------------------
Write-Output ""
Write-Output "the uninstall"

& (Join-Path $TestDir 'Uninstall.exe') --uninstall --quiet --dir $TestDir 2>&1 | Out-String | Write-Output
Start-Sleep -Seconds 8

Check "the folder is gone" (-not (Test-Path $TestDir))
$keyName = Split-Path $TestDir -Leaf
Check "the registry entry is gone" (-not (Test-Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$keyName")) $keyName

# The real installation must not have been touched.
$real = Join-Path $env:LOCALAPPDATA 'Programs\notecode'
$realShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'notecode.lnk'
if (Test-Path $real) {
    Check "the real installation was left alone" (Test-Path $realShortcut) $real
}

Write-Output ""
if ($fail -eq 0) { Write-Output "installer: PASS"; exit 0 } else { Write-Output "installer: FAIL ($fail)"; exit 1 }
