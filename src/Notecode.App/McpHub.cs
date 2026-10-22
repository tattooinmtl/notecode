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
    // Read from the UI thread and written from the startup task and the retry
    // timer, so every access goes through a lock on the dictionary itself.
    private readonly Dictionary<string, IMcpConnection> _connections = new();
    private readonly Dictionary<string, string> _failed = new();

    // On-demand servers are shared by whoever needs them at the same time (a
    // /rea turn and the REA menu item, say). Each user takes a lease, and the
    // server is stopped when the last lease is given back. The semaphore keeps
    // two users from starting it twice, or one from stopping it under the other.
    private readonly SemaphoreSlim _leaseLock = new(1, 1);
    private readonly Dictionary<string, int> _leases = new();

    public event Action<string, string>? Log;      // (server, message)
    public event Action? ToolsChanged;

    public IReadOnlyCollection<string> ConnectedServers
    {
        get { lock (_connections) return _connections.Keys.ToArray(); }
    }

    /// <summary>The servers that would not start, and why.</summary>
    public IReadOnlyDictionary<string, string> FailedServers
    {
        get { lock (_failed) return new Dictionary<string, string>(_failed); }
    }

    public async Task ConnectAllAsync(AppConfig cfg, CancellationToken ct = default)
    {
        foreach (var (name, def) in cfg.Mcp.Servers)
        {
            // An on-demand server (REA) is started by the command that wants it,
            // not here: it is an npx cold start that publishes 170 tools. A server
            // that is switched off (Settings > MCP servers) is not started at all.
            if (def.OnDemand || !def.Enabled) continue;

            try
            {
                var (conn, tools) = await OpenAsync(name, def, ct).ConfigureAwait(false);
                if (conn is null) continue;
                await RegisterAsync(name, conn).ConfigureAwait(false);
                Log?.Invoke(name, $"connected - {tools.Count} tools");
            }
            catch (Exception e)
            {
                // The server is remembered, so it can be tried again. The Blender
                // server is only listening while Blender is open, and it is not
                // a fault of the app that Blender was not open when it started.
                lock (_failed) _failed[name] = e.Message;
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
        string[] failed;
        lock (_failed) failed = _failed.Keys.ToArray();
        if (failed.Length == 0) return Array.Empty<string>();

        var cameUp = new List<string>();
        foreach (var name in failed)
        {
            if (!cfg.Mcp.Servers.TryGetValue(name, out var def) || !def.Enabled) continue;

            try
            {
                var (conn, tools) = await OpenAsync(name, def, ct).ConfigureAwait(false);
                if (conn is null) continue;
                await RegisterAsync(name, conn).ConfigureAwait(false);
                cameUp.Add(name);
                Log?.Invoke(name, $"connected - {tools.Count} tools");
            }
            catch (Exception e)
            {
                // Still not there. The message is kept, but it is not logged
                // every time, or the app log would fill with the same line.
                lock (_failed) _failed[name] = e.Message;
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
            var (conn, tools) = await OpenAsync(name, def, ct).ConfigureAwait(false);
            if (conn is null)
            {
                Log?.Invoke(name, $"connect failed: unknown transport '{def.Transport}'");
                return -1;
            }

            await RegisterAsync(name, conn).ConfigureAwait(false);
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

    /// <summary>
    /// Starts a server and reads its catalogue. The connection is handed back
    /// only when both worked: a server that starts but does not answer is
    /// stopped here, so a failed attempt never leaves a process behind.
    /// </summary>
    private static async Task<(IMcpConnection? Conn, List<McpTool> Tools)> OpenAsync(
        string name, McpServerDef def, CancellationToken ct)
    {
        IMcpConnection? conn = def.Transport switch
        {
            "stdio" => await StdioMcpClient.ConnectAsync(name, def, ct).ConfigureAwait(false),
            "streamable-http" or "http" => new HttpMcpClient(name, def),
            _ => null
        };
        if (conn is null) return (null, new List<McpTool>());

        try
        {
            return (conn, await conn.ListToolsAsync(ct).ConfigureAwait(false));
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Keeps a connection that answered, and stops the one it replaces.</summary>
    private async Task RegisterAsync(string name, IMcpConnection conn)
    {
        IMcpConnection? old;
        lock (_connections)
        {
            _connections.Remove(name, out old);
            _connections[name] = conn;
        }
        lock (_failed) _failed.Remove(name);
        if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Takes a lease on a server, starting it if nobody is using it. This is how
    /// an on-demand server is started. Every successful call must be matched by
    /// <see cref="ReleaseAsync"/>.
    /// </summary>
    /// <returns>true when the server is connected and the lease is held.</returns>
    public async Task<bool> AcquireAsync(AppConfig cfg, string name, CancellationToken ct = default)
    {
        await _leaseLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool connected;
            lock (_connections) connected = _connections.ContainsKey(name);
            if (!connected)
            {
                if (!cfg.Mcp.Servers.TryGetValue(name, out var def)) return false;
                if (await ConnectOneAsync(name, def, ct).ConfigureAwait(false) < 0) return false;
            }

            _leases[name] = _leases.GetValueOrDefault(name) + 1;
            return true;
        }
        finally { _leaseLock.Release(); }
    }

    /// <summary>
    /// Gives a lease back. The last one out stops the server, so its process
    /// lets go of its memory.
    /// </summary>
    public async Task ReleaseAsync(string name)
    {
        await _leaseLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var left = _leases.GetValueOrDefault(name) - 1;
            if (left > 0) { _leases[name] = left; return; }

            _leases.Remove(name);
            IMcpConnection? conn;
            lock (_connections) _connections.Remove(name, out conn);
            if (conn is null) return;

            try { await conn.DisposeAsync().ConfigureAwait(false); }
            catch (Exception e) { Log?.Invoke(name, $"disconnect failed: {e.Message}"); }
            Log?.Invoke(name, "disconnected");
        }
        finally { _leaseLock.Release(); }
        ToolsChanged?.Invoke();
    }

    private KeyValuePair<string, IMcpConnection>[] Snapshot()
    {
        lock (_connections) return _connections.ToArray();
    }

    private IMcpConnection? Find(string name)
    {
        lock (_connections) return _connections.GetValueOrDefault(name);
    }

    public async Task<List<McpTool>> ListAllToolsAsync(CancellationToken ct = default)
    {
        var all = new List<McpTool>();
        foreach (var (name, conn) in Snapshot())
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
            var conn = Find(name);
            if (conn is null) continue;
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
        var conn = Find(server);
        if (conn is null) return $"[error] unknown MCP server '{server}'";
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
        foreach (var (_, conn) in Snapshot()) await conn.DisposeAsync().ConfigureAwait(false);
        lock (_connections) _connections.Clear();
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
        private readonly TimeSpan _timeout;
        private List<McpTool>? _tools;

        private StdioMcpClient(string name, Process proc, int timeoutMs)
        {
            _name = name;
            _proc = proc;
            _timeout = TimeSpan.FromMilliseconds(timeoutMs > 0 ? timeoutMs : 60000);
            _ = Task.Run(ReadLoopAsync);

            // stderr is redirected, so it has to be read: a server that logs
            // (npx does) fills the pipe and then blocks on its next write,
            // which looks like a tool call that never answers.
            _proc.ErrorDataReceived += (_, _) => { };
            _proc.BeginErrorReadLine();
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
            var client = new StdioMcpClient(name, proc, def.ToolTimeoutMs);

            // A server that starts but does not finish the handshake is stopped
            // here: nobody else holds the client yet, so nobody else could.
            try
            {
                await client.RequestAsync("initialize", new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new { name = "notecode", version = "0.1.0" }
                }, ct).ConfigureAwait(false);

                await client.NotifyAsync("notifications/initialized", new { }, ct).ConfigureAwait(false);
                return client;
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
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

                    if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
                    {
                        // Completed under the lock the timeout takes too, so a
                        // response and a timeout cannot both claim one request.
                        lock (_pending)
                        {
                            if (_pending.Remove(id, out var tcs)) tcs.TrySetResult(root);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            finally
            {
                TaskCompletionSource<JsonElement>[] orphans;
                lock (_pending) { orphans = _pending.Values.ToArray(); _pending.Clear(); }
                foreach (var tcs in orphans) tcs.TrySetException(new IOException($"MCP server '{_name}' exited"));
            }
        }

        private async Task<JsonElement> RequestAsync(string method, object? @params, CancellationToken ct)
        {
            int id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending) _pending[id] = tcs;

            var payload = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method
            };
            if (@params is not null) payload["params"] = @params;

            // A server that never answers must not hold the turn forever. The
            // clock starts before the write: a server that stops reading its
            // stdin blocks the write itself.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeout);
            using var reg = timeout.Token.Register(() =>
            {
                lock (_pending)
                {
                    // Whoever removes the request completes it. If the response
                    // already did, the timeout has nothing left to say.
                    if (!_pending.Remove(id)) return;
                    if (ct.IsCancellationRequested) tcs.TrySetCanceled(ct);
                    else tcs.TrySetException(TimedOut(method));
                }
            });

            JsonElement response;
            try
            {
                await WriteAsync(JsonSerializer.Serialize(payload), timeout.Token).ConfigureAwait(false);
                response = await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw TimedOut(method);
            }
            finally
            {
                // A write that failed never reached the server, so nothing
                // will ever answer it.
                lock (_pending) _pending.Remove(id);
            }

            if (response.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"MCP error: {err.GetRawText()}");
            return response.TryGetProperty("result", out var result) ? result : default;
        }

        private TimeoutException TimedOut(string method) =>
            new($"MCP server '{_name}' did not answer {method} within {_timeout.TotalSeconds:0} s");

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
                // The process pipes are not opened for overlapped I/O, so a
                // cancelled token does not interrupt a write that is blocked on
                // a full pipe. WaitAsync stops waiting for it instead.
                var write = WriteLineAsync(line);
                await write.WaitAsync(ct).ConfigureAwait(false);
            }
            finally { _writeLock.Release(); }
        }

        private async Task WriteLineAsync(string line)
        {
            await _proc.StandardInput.WriteAsync(line + "\n").ConfigureAwait(false);
            await _proc.StandardInput.FlushAsync().ConfigureAwait(false);
        }

        public async Task<List<McpTool>> ListToolsAsync(CancellationToken ct)
        {
            // The catalogue of a running server does not change, and every tool
            // call looks it up, so it is asked for once.
            if (_tools is not null) return _tools;

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
            _tools = list;
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
        private readonly TimeSpan _timeout;
        private int _nextId = 1;
        private string? _sessionId;
        private List<McpTool>? _tools;

        public HttpMcpClient(string name, McpServerDef def)
        {
            _name = name;
            _def = def;
            _timeout = TimeSpan.FromMilliseconds(def.ToolTimeoutMs > 0 ? def.ToolTimeoutMs : 60000);

            // The per-server timeout is applied to each request, so the client
            // itself has none.
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        public async Task<List<McpTool>> ListToolsAsync(CancellationToken ct)
        {
            if (_tools is not null) return _tools;

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
            _tools = list;
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

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeout);

            HttpResponseMessage resp;
            string body;
            try
            {
                resp = await _http.SendAsync(req, timeout.Token).ConfigureAwait(false);
                body = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"MCP server '{_name}' did not answer {method} within {_timeout.TotalSeconds:0} s");
            }

            using var _ = resp;
            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sid))
                _sessionId = sid.FirstOrDefault();

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
