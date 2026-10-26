<#
.SYNOPSIS
    Turns the NoteCode Ultra brand film into the small WebM the splash plays.

.DESCRIPTION
    The splash shows the brand film, but it is a splash: it is on screen for
    about seven seconds while the hardware scan runs, and the film is ten
    seconds long. Left alone, the window would close in the middle of the
    build-up and the brand card - the logo, the name and the line under it -
    would never be seen.

    So the film is trimmed to the part that ends on that card, and the card is
    what the splash closes on.

    The format is WebM, not MP4. Chromium plays WebM without needing a
    proprietary codec, the codec itself is about a third of the size of the
    H.264 in the source, and the splash is a WebView2 that was already in the
    app, so nothing new was added to ship.

    The video is AV1 inside the WebM. The film is mostly black with fine
    dust in it, which is the worst case for a codec: H.264 spends most of its
    bits on the grain, and so did VP9. AV1 handles it far better, and the
    result is a fifth of the size of the MP4 for a frame that is just as
    sharp. The audio stays as Opus, because it is muted by default anyway.

.PARAMETER Source
    The brand film to convert. Defaults to NoteCode-Ultra.mp4 in the checkout.

.PARAMETER OutFile
    Where the WebM goes. Defaults to wwwroot\media under the app project.

.EXAMPLE
    .\scripts\build-splash-video.ps1
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path (Split-Path $PSScriptRoot -Parent) 'NoteCode-Ultra.mp4'),

    [string]$OutFile = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Notecode.App\wwwroot\media\notecode-ultra.webm')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Source)) { throw "the brand film is not there: $Source" }
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { throw 'ffmpeg is not on PATH' }

# The part of the film the splash can actually show: the assembly turning into
# the logo, the name arriving, the line under it, and the card they settle on.
$Start = 4.5
$Length = 5.63

# 1280x720 is the film's own shape and is enough for a window that is never
# wider than 840 pixels, even on a screen that doubles it.
$Width = 1280
$Height = 720

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null

$args = @(
    '-hide_banner', '-loglevel', 'error', '-y',
    '-ss', $Start, '-i', $Source, '-t', $Length,
    '-vf', "scale=${Width}:${Height}:flags=lanczos",
    '-c:v', 'libsvtav1', '-crf', '38', '-preset', '6', '-pix_fmt', 'yuv420p',
    '-c:a', 'libopus', '-b:a', '48k',
    $OutFile
)

& ffmpeg @args
if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed with $LASTEXITCODE" }

$size = (Get-Item $OutFile).Length
$source = (Get-Item $Source).Length
'{0}  {1:N0} bytes  ({2:P0} of the {3:N0} byte source)' -f `
    (Split-Path -Leaf $OutFile), $size, ($size / $source), $source
