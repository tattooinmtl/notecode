using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Notecode.App;

/// <summary>
/// Owns the local llama-server lifecycle and speaks its OpenAI-compatible API.
///
/// The server is started through runtime/notecode-llama.ps1 so the exact argv the GUI
/// uses is the same argv the CLI produces - there is no second, divergent launcher.
///
/// Tool handling lives here too, because llama-server does not run tools for us: the client
/// must advertise the schemas, notice the tool calls in the reply, dispatch them and feed the
/// results back. llama-server exposes the catalogue on <c>GET /tools</c> and dispatches a
/// single tool on <c>POST /tools</c> - both built-in tools and MCP tools come from the same
/// place.
/// </summary>
internal sealed class LlamaServer : IChatBackend, IDisposable
{
    private readonly AppConfig _cfg;
    private readonly HttpClient _http;
    private readonly string _logPath;

    public LlamaServer(AppConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _logPath = Path.Combine(cfg.RuntimeDir, "notecode-server.log");
    }

    /// <summary>The local model, as it is shown in the status bar.</summary>
    public string DisplayName => $"local ({_cfg.Llama.Alias})";

    /// <summary>
    /// Which build to run, resolved by the hardware scan.
    ///
    /// A llama.cpp server is compiled for one backend, so the build that runs here
    /// is not a setting of the app but a property of the machine. The scan fills
    /// this in before the server is started, and the wrapper is told what to run.
    /// </summary>
    public LlamaRuntimeOptions? Options { get; set; }

    /// <summary>
    /// Where a tool call goes when the app has a client of its own for it.
    ///
    /// The delegate answers with whether it handled the call. When it did not,
    /// the call goes to llama-server as usual.
    /// </summary>
    public Func<string, string, CancellationToken, Task<(bool Handled, string Output)>>? Dispatch { get; set; }

    /// <summary>True when the tool belongs to one of the configured MCP servers.</summary>
    private bool IsMcpTool(string toolName)
    {
        foreach (var server in _cfg.Mcp.Servers.Keys)
        {
            if (toolName.StartsWith(server + "_", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// The backend interface. The local server keeps its own tuning knobs on
    /// <see cref="StreamChatTurnAsync"/>, so the interface method forwards to it.
    /// </summary>
    Task<ChatTurn> IChatBackend.StreamTurnAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSpec>? tools,
        Action<string>? onDelta,
        CancellationToken ct = default,
        Action<string>? onReasoning = null) =>
        StreamChatTurnAsync(messages, tools, onDelta, onReasoning, ct: ct);

    /// <summary>
    /// Whether the server answers /health within three seconds. The app awaits
    /// this: a blocking check on the UI thread froze the window for up to three
    /// seconds at the start of every turn.
    /// </summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var resp = await _http.GetAsync($"{_cfg.Llama.BaseUrl}/health", cts.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>The blocking form, for the console probe. Not for the UI thread.</summary>
    public bool IsHealthy => IsHealthyAsync().GetAwaiter().GetResult();

    /// <summary>Server root, where /health and /tools live.</summary>
    public string BaseUrl => _cfg.Llama.BaseUrl;

    /// <summary>OpenAI-compatible root, where /chat/completions lives.</summary>
    public string OpenAiBase => _cfg.Llama.OpenAiBase;

    /// <summary>Runs the shared PowerShell wrapper. Returns the wrapper's exit code.</summary>
    public async Task<(int ExitCode, string Output)> RunWrapperAsync(
        string command, int? ctx = null, int? threads = null, CancellationToken ct = default)
    {
        var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", _cfg.WrapperScript, command };
        if (ctx is > 0) { args.Add("-Ctx"); args.Add(ctx.Value.ToString()); }
        if (threads is > 0) { args.Add("-Threads"); args.Add(threads.Value.ToString()); }

        // The build that is actually run. The wrapper takes these so that the
        // command line on screen is the same one the server was started with.
        var options = Options;
        if (options is not null)
        {
            if (!string.IsNullOrWhiteSpace(options.Exe)) { args.Add("-Exe"); args.Add(options.Exe); }
            if (!string.IsNullOrWhiteSpace(options.Device)) { args.Add("-Device"); args.Add(options.Device); }
            args.Add("-NGpuLayers"); args.Add(options.NGpuLayers.ToString());
            if (options.Ctx is > 0) { args.Add("-Ctx"); args.Add(options.Ctx.Value.ToString()); }
        }

        var psi = new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _cfg.RuntimeDir
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        var combined = stdout + (string.IsNullOrWhiteSpace(stderr) ? "" : Environment.NewLine + stderr);
        try { File.AppendAllText(_logPath, combined + Environment.NewLine); } catch { }
        return (proc.ExitCode, combined);
    }

    public async Task<bool> StartAsync(int? ctx = null, int? threads = null, CancellationToken ct = default)
    {
        if (await IsHealthyAsync(ct).ConfigureAwait(false)) return true;

        // The wrapper starts llama-server with Start-Process, which takes it out
        // of the app's job, so it would outlive the app. The wrapper writes the
        // server's pid to llama-server.pid; the new pid is put back in the job.
        var pidFile = Path.Combine(_cfg.RuntimeDir, "llama-server.pid");
        var before = File.Exists(pidFile) ? File.GetLastWriteTimeUtc(pidFile) : DateTime.MinValue;
        var run = RunWrapperAsync("start", ctx, threads, ct);
        _ = AdoptServerWhenStartedAsync(pidFile, before, run);

        var (code, output) = await run.ConfigureAwait(false);
        return code == 0 && await IsHealthyAsync(ct).ConfigureAwait(false);
    }

    private static async Task AdoptServerWhenStartedAsync(string pidFile, DateTime before, Task run)
    {
        for (var i = 0; i < 600 && !run.IsCompleted; i++)
        {
            try
            {
                if (File.Exists(pidFile) && File.GetLastWriteTimeUtc(pidFile) > before &&
                    int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
                {
                    ProcessJob.Adopt(pid);
                    return;
                }
            }
            catch (IOException) { /* the wrapper is still writing it */ }
            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await RunWrapperAsync("stop", null, null, ct).ConfigureAwait(false);
    }

    /// <summary>Lists the models the server currently exposes.</summary>
    public async Task<string[]> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{OpenAiBase}/models", ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            var list = new List<string>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id)) list.Add(id.GetString() ?? "");
            }
            return list.ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Fetches the tool catalogue llama-server advertises.
    ///
    /// Built-in tools and the notecode MCP tools are always included. The 170 REA tools are
    /// opt-in: their schemas alone are large enough to swallow most of a 32k context window on
    /// a CPU-sized model, and on this machine REA has no analysis provider installed anyway.
    /// </summary>
    public async Task<List<ToolSpec>> GetToolsAsync(bool includeRea, CancellationToken ct = default)
    {
        var specs = new List<ToolSpec>();
        foreach (var item in await FetchCatalogueAsync(ct).ConfigureAwait(false))
        {
            var name = item.TryGetProperty("tool", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;

            bool isBuiltin = item.TryGetProperty("type", out var ty) && ty.GetString() == "server";
            bool isNotecode = name.StartsWith("notecode_", StringComparison.Ordinal);
            bool isRea = name.StartsWith("rea_", StringComparison.Ordinal);
            if (!isBuiltin && !isNotecode && !(includeRea && isRea)) continue;

            var spec = ToSpec(name!, item);
            if (spec is not null) specs.Add(spec);
        }
        return specs;
    }

    /// <summary>
    /// The tool catalogue, narrowed to the tools named.
    ///
    /// The names are matched without the server prefix, so the caller can ask for
    /// "read_skill" and get "notecode_read_skill" when that is how the server
    /// publishes it. This is how the skill fixer gets a handful of tools instead
    /// of the whole catalogue: on a CPU-sized model the schemas of 190 tools
    /// cost more than the work they would do.
    /// </summary>
    public async Task<List<ToolSpec>> GetToolsByNameAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        var wanted = new HashSet<string>(names.Select(StripServerPrefix), StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return new List<ToolSpec>();

        var specs = new List<ToolSpec>();
        foreach (var item in await FetchCatalogueAsync(ct).ConfigureAwait(false))
        {
            var name = item.TryGetProperty("tool", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;
            if (!wanted.Contains(StripServerPrefix(name!))) continue;

            var spec = ToSpec(name!, item);
            if (spec is not null) specs.Add(spec);
        }
        return specs;
    }

    /// <summary>The server publishes its MCP tools with the server name in front.</summary>
    public static string StripServerPrefix(string name) =>
        name.StartsWith("notecode_", StringComparison.Ordinal) ? name["notecode_".Length..] : name;

    private async Task<JsonElement[]> FetchCatalogueAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{BaseUrl}/tools", ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<JsonElement>();

            var items = new List<JsonElement>();
            foreach (var item in doc.RootElement.EnumerateArray()) items.Add(item.Clone());
            return items.ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"tool catalogue fetch failed: {ex.Message}");
            return Array.Empty<JsonElement>();
        }
    }

    private static ToolSpec? ToSpec(string name, JsonElement item)
    {
        if (!item.TryGetProperty("definition", out var def)) return null;

        string desc = "";
        if (def.TryGetProperty("function", out var fn) &&
            fn.TryGetProperty("description", out var d) &&
            d.ValueKind == JsonValueKind.String)
        {
            desc = d.GetString() ?? "";
        }

        // Clone so the element survives the JsonDocument being disposed.
        return new ToolSpec(name, desc, def.Clone());
    }

    /// <summary>
    /// Dispatches one tool call. llama-server routes it to the owning MCP server or to its own
    /// built-in implementation, and returns the result as text for the model.
    /// </summary>
    /// <remarks>
    /// The MCP servers are called through the app's own client when it has one
    /// (see <see cref="Dispatch"/>). llama.cpp's MCP client cannot carry a
    /// large tool argument: the call never reaches the server, the client
    /// stops answering, and every later call queues behind it. A Blender
    /// script or a file of a few thousand characters is over that limit, so a
    /// call that went through llama.cpp would wedge the local backend for the
    /// rest of the session.
    /// </remarks>
    public async Task<string> InvokeToolAsync(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        // The tools of an MCP server are published with the server name in
        // front, so the name says which server owns it. The app's own client
        // is asked first, and llama.cpp's is the fallback.
        var dispatch = Dispatch;
        if (dispatch is not null && IsMcpTool(toolName))
        {
            try
            {
                var (handled, output) = await dispatch(toolName, argumentsJson, ct).ConfigureAwait(false);
                if (handled) return output;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"tool {toolName}: the app's own client failed: {ex.Message}");
            }
        }

        object args = new Dictionary<string, object?>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            args = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            AppLog.Warn($"tool {toolName}: arguments were not valid JSON, sending an empty object");
        }

        var payload = new Dictionary<string, object?>
        {
            ["tool"] = toolName,
            ["params"] = args,
            ["stream"] = false
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/tools")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return $"[tool error {(int)resp.StatusCode}] {body}";

            // MCP tools answer with a normalised plain-text field; built-ins answer with their
            // own JSON, which is handed to the model as-is.
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("plain_text_response", out var plain) &&
                    plain.ValueKind == JsonValueKind.String)
                {
                    return plain.GetString() ?? "";
                }
            }
            catch (JsonException) { /* fall through to the raw body */ }

            return body;
        }
        catch (Exception ex)
        {
            return $"[tool error] {ex.Message}";
        }
    }

    /// <summary>
    /// Streams one assistant turn. Text deltas are handed to <paramref name="onDelta"/> as they
    /// arrive; tool calls are stitched back together from their partial deltas and returned
    /// with the turn.
    /// </summary>
    public async Task<ChatTurn> StreamChatTurnAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSpec>? tools,
        Action<string>? onDelta,
        Action<string>? onReasoning = null,
        double temperature = 0.7,
        int maxTokens = 0,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _cfg.Llama.Alias,
            ["messages"] = messages.Select(ToRequestJson).ToArray(),
            ["stream"] = true,
            ["temperature"] = temperature
        };
        if (maxTokens > 0) payload["max_tokens"] = maxTokens;
        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(t => t.Definition).ToArray();
            payload["tool_choice"] = "auto";
        }

        var text = new StringBuilder();
        var calls = new ToolCallAccumulator();
        string finish = "";

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{OpenAiBase}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new ChatTurn($"[error {(int)resp.StatusCode}] {err}", new List<ToolCall>(), "error");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            if (data == "[DONE]") break;

            try
            {
                using var chunk = JsonDocument.Parse(data);
                if (!chunk.RootElement.TryGetProperty("choices", out var choices)) continue;

                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                        finish = fr.GetString() ?? "";

                    if (!choice.TryGetProperty("delta", out var d)) continue;

                    if (d.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        var t = content.GetString();
                        if (!string.IsNullOrEmpty(t))
                        {
                            text.Append(t);
                            onDelta?.Invoke(t);
                        }
                    }

                    // The thinking behind the answer, when the server splits it out.
                    // MiniCPM-V does not produce any, but a model that does should
                    // not be swallowed.
                    foreach (var name in new[] { "reasoning_content", "reasoning" })
                    {
                        if (!d.TryGetProperty(name, out var r) || r.ValueKind != JsonValueKind.String) continue;
                        var t = r.GetString();
                        if (string.IsNullOrEmpty(t)) continue;
                        onReasoning?.Invoke(t);
                    }

                    if (d.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in tcs.EnumerateArray()) calls.Add(tc);
                    }
                }
            }
            catch (JsonException) { /* keep-alive or partial frame */ }
        }

        return new ChatTurn(text.ToString(), calls.Build(), finish);
    }

    /// <summary>
    /// Shapes a message the way the chat template expects. Assistant messages carry their tool
    /// calls, and tool results are sent back as role=tool with the originating call id.
    /// </summary>
    private static object ToRequestJson(ChatMessage m)
    {
        if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
        {
            return new
            {
                role = "assistant",
                content = m.Content ?? "",
                tool_calls = m.ToolCalls.Select(c => new
                {
                    id = c.Id,
                    type = "function",
                    function = new { name = c.Name, arguments = c.ArgumentsJson }
                }).ToArray()
            };
        }

        if (m.Role == "tool")
        {
            return new { role = "tool", tool_call_id = m.ToolCallId ?? "", content = m.Content ?? "" };
        }

        // A picture the user attached turns the content into the parts array that
        // the chat API uses for a multimodal turn: a text part and an image part.
        if (!string.IsNullOrEmpty(m.Image))
        {
            return new
            {
                role = m.Role,
                content = new object[]
                {
                    new { type = "text", text = m.Content ?? "" },
                    new { type = "image_url", image_url = new { url = m.Image } }
                }
            };
        }

        return new { role = m.Role, content = m.Content ?? "" };
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// One message in the conversation. <see cref="ToolCalls"/> is set on assistant turns that asked
/// for tools; <see cref="ToolCallId"/> is set on the tool results that answer them.
///
/// <see cref="Reasoning"/> carries the thinking an assistant turn produced. It is not
/// part of the OpenAI chat spec, but some providers require it to be sent back: the
/// Reflection Beam model answers 400 if an earlier assistant message comes back
/// without the reasoning that produced it.
///
/// <see cref="Image"/> is a data URL for a picture the user attached. It turns the
/// content into the parts array the chat API uses for a multimodal turn:
/// a text part and an image part.
/// </summary>
internal sealed record ChatMessage(
    string Role,
    string Content,
    string? ToolCallId = null,
    List<ToolCall>? ToolCalls = null,
    string? Reasoning = null,
    string? Image = null);
