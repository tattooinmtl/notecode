using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Notecode.App;

internal sealed record McpTool(string Server, string Name, string Title, string Description, JsonElement Schema);

/// <summary>
/// Minimal MCP client. Supports the two transports this project actually uses:
/// stdio (REA) and streamable-http (Blender).
///
/// The JSON-RPC framing is the same in both cases - one JSON object per line for
/// stdio, a single POST body for http - so the request/response plumbing is shared.
/// </summary>
internal sealed class McpHub : IAsyncDisposable
{
    private readonly Dictionary<string, IMcpConnection> _connections = new();
    private readonly Dictionary<string, string> _failed = new();
    private int _nextId = 1;

    public event Action<string, string>? Log;      // (server, message)
    public event Action? ToolsChanged;

    public IReadOnlyCollection<string> ConnectedServers => _connections.Keys;

    /// <summary>The servers that would not start, and why.</summary>
    public IReadOnlyDictionary<string, string> FailedServers => _failed;

    public async Task ConnectAllAsync(AppConfig cfg, CancellationToken ct = default)
    {
        foreach (var (name, def) in cfg.Mcp.Servers)
        {
            try
            {
                IMcpConnection? conn = def.Transport switch
                {
                    "stdio" => await StdioMcpClient.ConnectAsync(name, def, ct).ConfigureAwait(false),
                    "streamable-http" or "http" => new HttpMcpClient(name, def),
                    _ => null
                };
                if (conn is null) continue;

                _connections[name] = conn;
                _failed.Remove(name);
                var tools = await conn.ListToolsAsync(ct).ConfigureAwait(false);
                Log?.Invoke(name, $"connected - {tools.Count} tools");
            }
            catch (Exception e)
            {
                // The server is remembered, so it can be tried again. The Blender
                // server is only listening while Blender is open, and it is not
                // a fault of the app that Blender was not open when it started.
                _failed[name] = e.Message;
                Log?.Invoke(name, $"connect failed: {e.Message}");
            }
        }
        ToolsChanged?.Invoke();
    }

    /// <summary>
    /// Tries again the servers that would not start.
    ///
    /// A server that is not up yet is not broken: the Blender server only
    /// listens while Blender is open, and the app is quite often started before
    /// it. This is called every few seconds, so the server joins as soon as it
    /// is there.
    ///
    /// Only the servers that failed are tried, and the whole set is not
    /// reconnected - the REA server is an npx cold start that publishes 170
    /// tools.
    /// </summary>
    /// <returns>the names of the servers that came up.</returns>
    public async Task<IReadOnlyList<string>> RetryFailedAsync(AppConfig cfg, CancellationToken ct = default)
    {
        if (_failed.Count == 0) return Array.Empty<string>();

        var cameUp = new List<string>();
        foreach (var name in _failed.Keys.ToList())
        {
            if (!cfg.Mcp.Servers.TryGetValue(name, out var def)) continue;

            try
            {
                IMcpConnection? conn = def.Transport switch
                {
                    "stdio" => await StdioMcpClient.ConnectAsync(name, def, ct).ConfigureAwait(false),
                    "streamable-http" or "http" => new HttpMcpClient(name, def),
                    _ => null
                };
                if (conn is null) continue;

                var tools = await conn.ListToolsAsync(ct).ConfigureAwait(false);
                _connections[name] = conn;
                _failed.Remove(name);
                cameUp.Add(name);
                Log?.Invoke(name, $"connected - {tools.Count} tools");
            }
            catch (Exception e)
            {
                // Still not there. The message is kept, but it is not logged
                // every time, or the app log would fill with the same line.
                _failed[name] = e.Message;
            }
        }

        if (cameUp.Count > 0) ToolsChanged?.Invoke();
        return cameUp;
    }

    /// <summary>
    /// Connects one server, for a server that was added while the app was running.
    ///
    /// The whole set is not reconnected: the REA server is an npx cold start
    /// that publishes 170 tools, and reconnecting it would make adding one tool
    /// take tens of seconds.
    /// </summary>
    /// <returns>the number of tools the server has, or -1 when it would not start.</returns>
    public async Task<int> ConnectOneAsync(string name, McpServerDef def, CancellationToken ct = default)
    {
        try
        {
            IMcpConnection? conn = def.Transport switch
            {
                "stdio" => await StdioMcpClient.ConnectAsync(name, def, ct).ConfigureAwait(false),
                "streamable-http" or "http" => new HttpMcpClient(name, def),
                _ => null
            };
            if (conn is null)
            {
                Log?.Invoke(name, $"connect failed: unknown transport '{def.Transport}'");
                return -1;
            }

            if (_connections.TryGetValue(name, out var old)) await old.DisposeAsync().ConfigureAwait(false);
            _connections[name] = conn;

            var tools = await conn.ListToolsAsync(ct).ConfigureAwait(false);
            Log?.Invoke(name, $"connected - {tools.Count} tools");
            ToolsChanged?.Invoke();
            return tools.Count;
        }
        catch (Exception e)
        {
            Log?.Invoke(name, $"connect failed: {e.Message}");
            return -1;
        }
    }

    public async Task<List<McpTool>> ListAllToolsAsync(CancellationToken ct = default)
    {
        var all = new List<McpTool>();
        foreach (var (name, conn) in _connections)
        {
            all.AddRange(await ListOneAsync(name, conn, ct).ConfigureAwait(false));
        }
        return all;
    }

    /// <summary>
    /// Lists the tools of the named servers only.
    ///
    /// Asking for every server is slow: the REA server is an npx cold start that
    /// publishes 170 tools, and listing it can take tens of seconds. A turn that
    /// does not want those tools should not wait for them, so the caller names
    /// the servers it needs.
    /// </summary>
    public async Task<List<McpTool>> ListToolsAsync(IEnumerable<string> servers, CancellationToken ct = default)
    {
        var all = new List<McpTool>();
        foreach (var name in servers)
        {
            if (!_connections.TryGetValue(name, out var conn)) continue;
            all.AddRange(await ListOneAsync(name, conn, ct).ConfigureAwait(false));
        }
        return all;
    }

    private async Task<List<McpTool>> ListOneAsync(string name, IMcpConnection conn, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var tools = await conn.ListToolsAsync(ct).ConfigureAwait(false);
            Log?.Invoke(name, $"tools/list took {watch.ElapsedMilliseconds} ms ({tools.Count} tools)");
            return tools;
        }
        catch (Exception e)
        {
            Log?.Invoke(name, $"tools/list failed after {watch.ElapsedMilliseconds} ms: {e.Message}");
            return new List<McpTool>();
        }
    }

    public async Task<string> CallToolAsync(string server, string tool, JsonElement arguments, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(server, out var conn))
            return $"[error] unknown MCP server '{server}'";
        try
        {
            var result = await conn.CallToolAsync(tool, arguments, ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception e)
        {
            Log?.Invoke(server, $"tools/call failed: {e.Message}");
            return $"[error] {e.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var conn in _connections.Values) await conn.DisposeAsync().ConfigureAwait(false);
        _connections.Clear();
    }

    internal interface IMcpConnection : IAsyncDisposable
    {
        Task<List<McpTool>> ListToolsAsync(CancellationToken ct);
        Task<string> CallToolAsync(string tool, JsonElement arguments, CancellationToken ct);
    }

    // ------------------------------------------------------------------ stdio
    private sealed class StdioMcpClient : IMcpConnection
    {
        private readonly Process _proc;
        private readonly string _name;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly CancellationTokenSource _cts = new();

        private StdioMcpClient(string name, Process proc)
        {
            _name = name;
            _proc = proc;
            _ = Task.Run(ReadLoopAsync);
        }

        private int _nextId;

        public static async Task<StdioMcpClient> ConnectAsync(string name, McpServerDef def, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = def.Command ?? "cmd.exe",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in def.Args) psi.ArgumentList.Add(a);

            var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start MCP server");
            var client = new StdioMcpClient(name, proc);

            var init = await client.RequestAsync("initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "notecode", version = "0.1.0" }
            }, ct).ConfigureAwait(false);

            await client.NotifyAsync("notifications/initialized", new { }, ct).ConfigureAwait(false);
            _ = init;
            return client;
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var line = await _proc.StandardOutput.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (line is null) break;
                    if (line.Length == 0) continue;

                    JsonElement root;
                    try { root = JsonDocument.Parse(line).RootElement.Clone(); }
                    catch (JsonException) { continue; }

                    if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id) && _pending.Remove(id, out var tcs))
                    {
                        tcs.TrySetResult(root);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            finally
            {
                foreach (var tcs in _pending.Values) tcs.TrySetException(new IOException($"MCP server '{_name}' exited"));
                _pending.Clear();
            }
        }

        private async Task<JsonElement> RequestAsync(string method, object? @params, CancellationToken ct)
        {
            int id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            var payload = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method
            };
            if (@params is not null) payload["params"] = @params;

            await WriteAsync(JsonSerializer.Serialize(payload), ct).ConfigureAwait(false);

            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            var response = await tcs.Task.ConfigureAwait(false);
            if (response.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"MCP error: {err.GetRawText()}");
            return response.TryGetProperty("result", out var result) ? result : default;
        }

        private async Task NotifyAsync(string method, object? @params, CancellationToken ct)
        {
            var payload = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method
            };
            if (@params is not null) payload["params"] = @params;
            await WriteAsync(JsonSerializer.Serialize(payload), ct).ConfigureAwait(false);
        }

        private async Task WriteAsync(string line, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _proc.StandardInput.WriteAsync(line + "\n").ConfigureAwait(false);
                await _proc.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            finally { _writeLock.Release(); }
        }

        public async Task<List<McpTool>> ListToolsAsync(CancellationToken ct)
        {
            var result = await RequestAsync("tools/list", new { }, ct).ConfigureAwait(false);
            var list = new List<McpTool>();
            if (result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tools.EnumerateArray())
                {
                    list.Add(new McpTool(
                        _name,
                        t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        t.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "",
                        t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                        t.TryGetProperty("inputSchema", out var s) ? s.Clone() : default));
                }
            }
            return list;
        }

        public async Task<string> CallToolAsync(string tool, JsonElement arguments, CancellationToken ct)
        {
            var result = await RequestAsync("tools/call", new
            {
                name = tool,
                arguments = arguments
            }, ct).ConfigureAwait(false);
            return FormatResult(result);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { if (!_proc.HasExited) _proc.Kill(true); } catch { }
            try { _proc.Dispose(); } catch { }
            _cts.Dispose();
            await Task.CompletedTask;
        }
    }

    // ------------------------------------------------------------------- http
    private sealed class HttpMcpClient : IMcpConnection
    {
        private readonly string _name;
        private readonly McpServerDef _def;
        private readonly HttpClient _http;
        private int _nextId = 1;
        private string? _sessionId;

        public HttpMcpClient(string name, McpServerDef def)
        {
            _name = name;
            _def = def;
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        }

        public async Task<List<McpTool>> ListToolsAsync(CancellationToken ct)
        {
            var result = await RequestAsync("tools/list", new { }, ct).ConfigureAwait(false);
            var list = new List<McpTool>();
            if (result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tools.EnumerateArray())
                {
                    list.Add(new McpTool(
                        _name,
                        t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        t.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "",
                        t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                        t.TryGetProperty("inputSchema", out var s) ? s.Clone() : default));
                }
            }
            return list;
        }

        public async Task<string> CallToolAsync(string tool, JsonElement arguments, CancellationToken ct)
        {
            var result = await RequestAsync("tools/call", new { name = tool, arguments = arguments }, ct).ConfigureAwait(false);
            return FormatResult(result);
        }

        private async Task<JsonElement> RequestAsync(string method, object? @params, CancellationToken ct)
        {
            int id = Interlocked.Increment(ref _nextId);
            var payload = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method
            };
            if (@params is not null) payload["params"] = @params;

            using var req = new HttpRequestMessage(HttpMethod.Post, _def.Url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (_sessionId is not null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sid))
                _sessionId = sid.FirstOrDefault();

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");

            // streamable-http may answer with SSE framing; unwrap the first data frame.
            var json = body;
            if (body.Contains("data:", StringComparison.Ordinal))
            {
                var line = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal));
                if (line is not null) json = line[5..].Trim();
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"MCP error: {err.GetRawText()}");

            // The element has to be cloned before the document is disposed. An
            // element that belongs to a disposed document is dead, and touching
            // it throws "Cannot access a disposed object" - which is what the
            // Blender server's tools used to do, because the list came back
            // from a document that was already gone.
            return doc.RootElement.TryGetProperty("result", out var result) ? result.Clone() : default;
        }

        public ValueTask DisposeAsync()
        {
            _http.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static string FormatResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return result.GetRawText();
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return result.GetRawText();

        var sb = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var text)) sb.Append(text.GetString());
            else sb.Append(item.GetRawText());
        }
        if (result.TryGetProperty("isError", out var isErr) && isErr.ValueKind == JsonValueKind.True)
            return "[tool error] " + sb;
        return sb.ToString();
    }
}
