<#
.SYNOPSIS
    Starts the program an MCP server lives in, and waits for its port.

.DESCRIPTION
    A doctor fix for a server that is served by a running program (Blender,
    the Meshy DCC gateway). The program is started detached, so closing
    notecode does not close it, and the script waits up to a minute for the
    server's port to answer.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [int]$Port = 0
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) {
    Write-Output "FAIL $Exe is not there"
    exit 2
}
Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe -Parent)
Write-Output "started $Exe"

if ($Port -le 0) { Write-Output 'OK started'; exit 0 }

for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 1
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        if ($client.ConnectAsync('127.0.0.1', $Port).Wait(500) -and $client.Connected) {
            Write-Output "OK port $Port answers"
            exit 0
        }
    } catch { } finally { $client.Dispose() }
}
Write-Output "FAIL the program started, but nothing answered on port $Port within a minute. Its MCP server may need to be switched on inside it."
exit 1
