# Checks that every MIME type the editor can pick is actually registered by a vendored
# CodeMirror mode. A MIME that no mode registers silently renders as plain text.
#
#   check-modes.ps1 [-RuntimeDir <runtime>]

param(
    [string]$RuntimeDir = ""
)

$ErrorActionPreference = 'Stop'

if (-not $RuntimeDir) {
    $RuntimeDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'runtime'
}

$vendor = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Notecode.App\wwwroot\vendor\codemirror5\mode'
$registryPath = Join-Path $RuntimeDir 'file-types.json'

$registered = @{}

foreach ($file in Get-ChildItem $vendor -Filter '*.js') {
    $text = Get-Content $file.FullName -Raw

    # CodeMirror.defineMIME("mime", ...) and defineMIME('mime', ...)
    foreach ($m in [regex]::Matches($text, 'defineMIME\(\s*["'']([^"'']+)["'']')) {
        $registered[$m.Groups[1].Value] = $file.Name
    }

    # clike.js registers a whole family through def(["mime", ...], {...})
    foreach ($m in [regex]::Matches($text, 'def\(\s*\[([^\]]*)\]')) {
        foreach ($s in [regex]::Matches($m.Groups[1].Value, '["'']([^"'']+)["'']')) {
            $registered[$s.Groups[1].Value] = $file.Name
        }
    }
}

$registry = Get-Content $registryPath -Raw | ConvertFrom-Json
$needed = @($registry.modes.PSObject.Properties |
    Where-Object { -not $_.Name.StartsWith('$') } |
    ForEach-Object { $_.Value } | Sort-Object -Unique)

$missing = @($needed | Where-Object { $_ -ne 'text/plain' -and -not $registered.ContainsKey($_) })

Write-Host ''
Write-Host 'CodeMirror mode coverage' -ForegroundColor Cyan
Write-Host '========================='
Write-Host ("  vendored mode files : {0}" -f (Get-ChildItem $vendor -Filter '*.js').Count)
Write-Host ("  registered MIME types: {0}" -f $registered.Count)
Write-Host ("  MIME types used      : {0}" -f $needed.Count)
Write-Host ''

if ($missing.Count -eq 0) {
    Write-Host '  [PASS] every MIME the registry can produce is registered by a vendored mode' -ForegroundColor Green
    exit 0
}

Write-Host '  [FAIL] these MIME types have no vendored mode - the file renders as plain text:' -ForegroundColor Red
foreach ($m in $missing) {
    $exts = @($registry.modes.PSObject.Properties |
        Where-Object { $_.Value -eq $m -and -not $_.Name.StartsWith('$') } |
        ForEach-Object { '.' + $_.Name })
    Write-Host ("         {0}  ({1})" -f $m, ($exts -join ' ')) -ForegroundColor Red
}
exit 1
