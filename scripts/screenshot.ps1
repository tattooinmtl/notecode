# Captures a window (or the whole screen) to a PNG.
#
#   screenshot.ps1 -Out <path> [-ProcessName notecode] [-WindowTitle "notecode"]
#
# Without -ProcessName it captures the full virtual screen. With it, the visible
# window of the newest instance of that process is captured.
#
# The pixels are copied from the screen (CopyFromScreen) rather than printed with
# PrintWindow. PrintWindow asks DWM for the window's cached surface, and for a
# window that has just opened where another window used to be, that surface can be
# the OLD one - the capture then shows a picture of an app that is no longer there.

param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$ProcessName = "",
    [string]$WindowTitle = ""
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

. (Join-Path $PSScriptRoot 'winutil.ps1')

$bounds = [System.Drawing.Rectangle]::new(0, 0, 0, 0)

if ($ProcessName) {
    # Pick the newest instance that owns a visible window. Several notecode processes
    # can be alive at once - a test launches one and the previous one may still be
    # closing - and capturing the wrong window shows an old state.
    $hwnd = Get-AppWindow -ProcessName $ProcessName -Title $WindowTitle
    if ($hwnd -eq $null -or $hwnd -eq [IntPtr]::Zero) {
        throw "no visible window for process '$ProcessName'"
    }

    [void][Win32Shot]::ShowWindow($hwnd, 9)   # SW_RESTORE
    Set-AppForeground -Hwnd $hwnd
    # The capture reads the screen, so the window has to be on top of whatever else
    # is there. Being topmost does not need the foreground, which this machine
    # refuses to hand over.
    [PostKey]::TopMost($hwnd)
    Start-Sleep -Milliseconds 900

    $rect = New-Object Win32Shot+RECT
    [void][Win32Shot]::RectOf($hwnd, [ref]$rect)
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) {
        throw ("window has no area (hwnd {0}, rect {1},{2} {3}x{4})" -f `
            $hwnd, $rect.Left, $rect.Top, $w, $h)
    }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
    $gfx.Dispose()
    [PostKey]::NotTopMost($hwnd)
} else {
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen($vs.Left, $vs.Top, 0, 0, $bmp.Size)
    $gfx.Dispose()
}

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Output ("saved {0} ({1:N0} bytes)" -f $Out, (Get-Item $Out).Length)
