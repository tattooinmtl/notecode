# Checks the app's colours against WCAG 2.2 AA.
#
# The colours are read out of the source - Theme.cs for the app, chat.html for
# the transcript - so a colour that is changed cannot slip past a check that
# still remembers the old one.
#
#   check-contrast.ps1
#
# AA wants 4.5:1 for text and 3:1 for the parts of a picture that carry
# meaning: the borders, the icons and the marks.

$ErrorActionPreference = 'Stop'

$root = 'C:\NexusOne\prototypes\notecode'
$themePath = Join-Path $root 'src\Notecode.App\Theme.cs'
$chatPath = Join-Path $root 'src\Notecode.App\wwwroot\chat.html'

$fail = 0
function Ok($m) { Write-Output "  [PASS] $m" }
function Bad($m) { Write-Output "  [FAIL] $m"; $script:fail++ }

function Lum([string]$hex) {
    $h = $hex.TrimStart('#')
    $c = @(0, 2, 4) | ForEach-Object { [Convert]::ToInt32($h.Substring($_, 2), 16) / 255.0 }
    $l = $c | ForEach-Object { if ($_ -le 0.03928) { $_ / 12.92 } else { [Math]::Pow(($_ + 0.055) / 1.055, 2.4) } }
    0.2126 * $l[0] + 0.7152 * $l[1] + 0.0722 * $l[2]
}
function Ratio([string]$a, [string]$b) {
    $l1 = Lum $a; $l2 = Lum $b
    if ($l1 -lt $l2) { $t = $l1; $l1 = $l2; $l2 = $t }
    [Math]::Round(($l1 + 0.05) / ($l2 + 0.05), 2)
}

# --- read the colours out of the source ---------------------------------------
$theme = Get-Content $themePath -Raw
$colors = @{}
foreach ($m in [regex]::Matches($theme, 'public static readonly Color (\w+)\s*=\s*Hex\("#([0-9A-Fa-f]{6})"\)')) {
    $colors[$m.Groups[1].Value] = '#' + $m.Groups[2].Value
}

$chat = Get-Content $chatPath -Raw
$css = @{}
foreach ($m in [regex]::Matches($chat, '--([a-z0-9-]+):\s*#([0-9A-Fa-f]{6})')) {
    $css[$m.Groups[1].Value] = '#' + $m.Groups[2].Value
}

Write-Output 'notecode contrast check'
Write-Output ''

if ($colors.Count -lt 10) { Bad "the theme colours could not be read ($($colors.Count) found)"; exit 1 }
if ($css.Count -lt 10) { Bad "the transcript colours could not be read ($($css.Count) found)"; exit 1 }

Write-Output "  theme colours: $($colors.Count)   transcript colours: $($css.Count)"
Write-Output ''

# --- text on every surface ----------------------------------------------------
$surfaces = @('Void', 'Canvas', 'Surface', 'SurfaceRaised', 'SurfaceHover')
$texts = @('Text', 'TextMuted', 'TextDim')

Write-Output 'Text on every surface (needs 4.5:1)'
foreach ($t in $texts) {
    $worst = 99; $worstBg = ''
    foreach ($s in $surfaces) {
        $r = Ratio $colors[$t] $colors[$s]
        if ($r -lt $worst) { $worst = $r; $worstBg = $s }
    }
    if ($worst -ge 4.5) { Ok ("{0,-10} {1,6}:1 on the worst surface ({2})" -f $t, $worst, $worstBg) }
    else { Bad ("{0,-10} {1,6}:1 on {2} - needs 4.5" -f $t, $worst, $worstBg) }
}

Write-Output ''
Write-Output 'The transcript uses the same colours (needs 4.5:1)'
foreach ($pair in @(@('text', 'surface'), @('muted', 'surface'), @('dim', 'surface'), @('dim', 'canvas'), @('dim', 'void'))) {
    $r = Ratio $css[$pair[0]] $css[$pair[1]]
    if ($r -ge 4.5) { Ok ("--{0,-6} on --{1,-8} {2,6}:1" -f $pair[0], $pair[1], $r) }
    else { Bad ("--{0,-6} on --{1,-8} {2,6}:1 - needs 4.5" -f $pair[0], $pair[1], $r) }
}

Write-Output ''
Write-Output 'The transcript and the app agree on the dim colour'
if ($css['dim'] -eq $colors['TextDim']) { Ok "both are $($colors['TextDim'])" }
else { Bad "the transcript has $($css['dim']), the app has $($colors['TextDim'])" }

Write-Output ''
Write-Output 'Marks and borders (needs 3:1)'
foreach ($a in @('Violet', 'VioletBright', 'Cyan', 'Mint', 'Amber', 'Rose')) {
    $worst = 99; $worstBg = ''
    foreach ($s in $surfaces) {
        $r = Ratio $colors[$a] $colors[$s]
        if ($r -lt $worst) { $worst = $r; $worstBg = $s }
    }
    if ($worst -ge 3.0) { Ok ("{0,-13} {1,6}:1 on the worst surface ({2})" -f $a, $worst, $worstBg) }
    else { Bad ("{0,-13} {1,6}:1 on {2} - needs 3" -f $a, $worst, $worstBg) }
}

Write-Output ''
Write-Output 'The label on the accent button (needs 4.5:1)'
# The button is filled with the bright gradient, so its label is the near-black
# one; a flat button on a dark surface takes the white one.
foreach ($bg in @('Violet', 'VioletBright', 'Cyan')) {
    $r = Ratio $colors['Void'] $colors[$bg]
    if ($r -ge 4.5) { Ok ("the dark label on {0,-13} {1,6}:1" -f $bg, $r) }
    else { Bad ("the dark label on {0,-13} {1,6}:1 - needs 4.5" -f $bg, $r) }
}
$r = Ratio '#FFFFFF' $colors['SurfaceRaised']
if ($r -ge 4.5) { Ok ("the light label on a flat button {0,6}:1" -f $r) }
else { Bad ("the light label on a flat button {0,6}:1 - needs 4.5" -f $r) }

Write-Output ''
Write-Output 'The size of the text (Microsoft: 10 point unless the user can change it)'

# The font sizes are read out of Theme.cs, so a smaller one cannot slip past.
$fonts = @{}
foreach ($m in [regex]::Matches($theme, 'public static Font (\w+) \{ get; private set; \} = new\([^,]+,\s*([0-9.]+)f')) {
    $fonts[$m.Groups[1].Value] = [double]$m.Groups[2].Value
}
if ($fonts.Count -lt 5) { Bad "the font sizes could not be read ($($fonts.Count) found)" }
else {
    # The body text has to clear the floor. The quiet text and the code text
    # clear the Windows 11 minimum of 12 pixels (9 point is 12 pixels).
    if ($fonts['Ui'] -ge 10) { Ok ("the body text is {0} point" -f $fonts['Ui']) }
    else { Bad ("the body text is {0} point - needs 10" -f $fonts['Ui']) }

    foreach ($f in @('UiSmall', 'CodeSmall')) {
        if ($fonts[$f] -ge 9) { Ok ("{0,-9} is {1} point, which is 12 pixels" -f $f, $fonts[$f]) }
        else { Bad ("{0,-9} is {1} point - needs 9, which is 12 pixels" -f $f, $fonts[$f]) }
    }

    # The rule only applies to a size the user cannot change. There has to be a
    # way to change it, and a way to set it.
    if ($theme -match 'ApplyFontScale' -and $theme -match 'fontScale') {
        Ok "the size is the user's to change"
    } else {
        Bad "there is no way for the user to change the size of the text"
    }

    $cfg = Get-Content (Join-Path $root 'runtime\notecode.config.json') -Raw
    if ($cfg -match '"fontScale"') { Ok "the factor is in the config" }
    else { Bad "the factor is not in the config" }
}

Write-Output ''
if ($fail -eq 0) { Write-Output 'contrast: PASS'; exit 0 } else { Write-Output "contrast: FAIL ($fail)"; exit 1 }
