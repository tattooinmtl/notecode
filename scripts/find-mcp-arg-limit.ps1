# Finds the size at which llama.cpp's MCP client gives up.
#
# One tool argument that is too large wedges the MCP client: the call never
# reaches the server, and every later call queues behind it. So each size is
# measured against a server that has just been restarted.
#
#   find-mcp-arg-limit.ps1 [-Sizes 50,100,200,400,800]

param(
    [int[]]$Sizes = @(50, 100, 200, 400, 800)
)

$ErrorActionPreference = 'Continue'

$root = 'C:\NexusOne\prototypes\notecode'
$wrapper = Join-Path $root 'runtime\notecode-llama.ps1'
$probe = 'C:\Users\ThePa\.brain-skills\skills\zz-probe'
$base = 'http://127.0.0.1:8080'

New-Item -ItemType Directory -Force -Path $probe | Out-Null

foreach ($n in $Sizes) {
    & $wrapper stop | Out-Null
    Start-Sleep -Seconds 2
    & $wrapper start | Out-Null
    Start-Sleep -Seconds 2

    $content = 'x' * $n
    $body = @{
        tool   = 'notecode_write_skill'
        params = @{ path = "$probe\limit$n.md"; content = $content }
        stream = $false
    } | ConvertTo-Json -Depth 4 -Compress

    $t0 = Get-Date
    $ok = $false
    try {
        $r = Invoke-RestMethod "$base/tools" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 20
        $ok = $null -ne $r.plain_text_response
    } catch { $ok = $false }

    $ms = [int]((Get-Date) - $t0).TotalMilliseconds
    if ($ok) { Write-Output ("{0,5} chars of arguments: OK   ({1} ms)" -f $n, $ms) }
    else { Write-Output ("{0,5} chars of arguments: HUNG ({1} ms)" -f $n, $ms) }
}

& $wrapper stop | Out-Null
