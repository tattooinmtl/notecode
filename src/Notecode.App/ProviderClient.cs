using System.Text;
using System.Text.Json;

namespace Notecode.App;

/// <summary>
/// Something that can run an assistant turn and dispatch the tools the model asks for.
///
/// The local llama-server and a BYOK provider both implement it, so the conversation
/// loop does not care which one is answering.
/// </summary>
internal interface IChatBackend
{
    /// <summary>A label for the status bar and the transcript.</summary>
    string DisplayName { get; }

    /// <summary>Streams one assistant turn. Tool calls come back with the turn.</summary>
    Task<ChatTurn> StreamTurnAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSpec>? tools,
        Action<string>? onDelta,
        CancellationToken ct = default,
        Action<string>? onReasoning = null);

    /// <summary>Dispatches one tool call and returns the text the model will see.</summary>
    Task<string> InvokeToolAsync(string toolName, string argumentsJson, CancellationToken ct = default);
}

/// <summary>
/// Talks to a BYOK provider.
///
/// Two wire formats are supported. Most providers speak the OpenAI chat-completions
/// format, so that is the default. Anthropic has its own messages format with a
/// different message shape and a different streaming protocol, and it is handled
/// separately rather than bent into the OpenAI shape.
/// </summary>
internal sealed class ProviderClient : IChatBackend
{
    private readonly ProviderDef _provider;
    private readonly HttpClient _http;
    private readonly Func<string, string, CancellationToken, Task<string>> _dispatch;

    public ProviderClient(
        ProviderDef provider,
        Func<string, string, CancellationToken, Task<string>> dispatch)
    {
        _provider = provider;
        _dispatch = dispatch;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    /// <summary>Sampling temperature for the turn (llama.agent.temperature).</summary>
    public double Temperature { get; init; } = 0.7;

    public string DisplayName =>
        $"{_provider.Label} ({Model})";

    /// <summary>
    /// The model a turn is sent to. A provider saved before the form required a
    /// model can have an empty one, and the endpoint refuses "model": "", so the
    /// first model of its list stands in.
    /// </summary>
    private string Model =>
        _provider.Model.Length > 0 ? _provider.Model : _provider.Models.FirstOrDefault() ?? "";

    public async Task<ChatTurn> StreamTurnAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSpec>? tools,
        Action<string>? onDelta,
        CancellationToken ct = default,
        Action<string>? onReasoning = null)
    {
        return _provider.Style == ApiStyle.Anthropic
            ? await StreamAnthropicAsync(messages, tools, onDelta, onReasoning, ct).ConfigureAwait(false)
            : await StreamOpenAiAsync(messages, tools, onDelta, onReasoning, ct).ConfigureAwait(false);
    }

    public async Task<string> InvokeToolAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        var token = SecretBox.Unprotect(_provider.ProtectedToken);
        try
        {
            return await _dispatch(toolName, argumentsJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"[tool error] {ex.Message}";
        }
    }

    // --------------------------------------------------------------- OpenAI shape

    private async Task<ChatTurn> StreamOpenAiAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSpec>? tools,
        Action<string>? onDelta,
        Action<string>? onReasoning = null,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = Model,
            ["messages"] = messages.Select(ToOpenAiMessage).ToArray(),
            ["stream"] = true,
            ["temperature"] = Temperature
        };
        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(t => t.Definition).ToArray();
            payload["tool_choice"] = "auto";
        }

        var text = new StringBuilder();
        var calls = new ToolCallAccumulator();
        var reasoning = new StringBuilder();
        string finish = "";

        using var req = new HttpRequestMessage(HttpMethod.Post, _provider.ChatUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        var token = SecretBox.Unprotect(_provider.ProtectedToken);
        if (!string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

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
                        if (!string.IsNullOrEmpty(t)) { text.Append(t); onDelta?.Invoke(t); }
                    }

                    // The thinking behind the answer. Some providers send it under
                    // "reasoning_content" and some under "reasoning".
                    foreach (var name in new[] { "reasoning_content", "reasoning" })
                    {
                        if (d.TryGetProperty(name, out var r) && r.ValueKind == JsonValueKind.String)
                        {
                            var t = r.GetString();
                            if (!string.IsNullOrEmpty(t)) { reasoning.Append(t); onReasoning?.Invoke(t); }
                        }
                    }

                    if (d.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                        foreach (var tc in tcs.EnumerateArray()) calls.Add(tc);
                }
            }
            catch (JsonException) { /* keep-alive or partial frame */ }
        }

        return new ChatTurn(text.ToString(), calls.Build(), finish, reasoning.ToString());
    }

    private object ToOpenAiMessage(ChatMessage m)
    {
        if (m.Role == "tool")
            return new { role = "tool", tool_call_id = m.ToolCallId ?? "", content = m.Content ?? "" };

        var message = new Dictionary<string, object?>
        {
            ["role"] = m.Role,
            ["content"] = m.Content ?? "",
        };

        if (m.Role == "assistant")
        {
            if (m.ToolCalls is { Count: > 0 })
            {
                message["tool_calls"] = m.ToolCalls.Select(c => new
                {
                    id = c.Id,
                    type = "function",
                    function = new { name = c.Name, arguments = c.ArgumentsJson }
                }).ToArray();
            }

            // The reasoning goes back with the assistant turn that produced it, but
            // only for a provider that asks for it: it is not part of the chat spec.
            if (_provider.EchoReasoning && !string.IsNullOrEmpty(m.Reasoning))
                message["reasoning_content"] = m.Reasoning;
        }

        // A picture the user attached turns the content into the parts array that
        // the chat API uses for a multimodal turn.
        if (!string.IsNullOrEmpty(m.Image))
        {
            message["content"] = new object[]
            {
                new { type = "text", text = m.Content ?? "" },
                new { type = "image_url", image_url = new { url = m.Image } }
            };
        }

        return message;
    }

    // ------------------------------------------------------------- Anthropic shape

    private async Task<ChatTurn> StreamAnthropicAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSpec>? tools,
        Action<string>? onDelta,
        Action<string>? onReasoning = null,
        CancellationToken ct = default)
    {
        var system = new List<string>();
        var conversation = new List<object>();

        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "system":
                    system.Add(m.Content ?? "");
                    break;

                case "tool":
                    conversation.Add(new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                type = "tool_result",
                                tool_use_id = m.ToolCallId ?? "",
                                content = m.Content ?? ""
                            }
                        }
                    });
                    break;

                case "assistant" when m.ToolCalls is { Count: > 0 }:
                    var blocks = new List<object>();
                    if (!string.IsNullOrEmpty(m.Content)) blocks.Add(new { type = "text", text = m.Content });
                    foreach (var c in m.ToolCalls)
                    {
                        blocks.Add(new
                        {
                            type = "tool_use",
                            id = c.Id,
                            name = c.Name,
                            input = ParseArguments(c.ArgumentsJson)
                        });
                    }
                    conversation.Add(new { role = "assistant", content = blocks });
                    break;

                default:
                    conversation.Add(new { role = m.Role, content = m.Content ?? "" });
                    break;
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = Model,
            ["messages"] = conversation,
            ["stream"] = true,
            ["max_tokens"] = 4096
        };
        if (system.Count > 0) payload["system"] = string.Join("\n\n", system);
        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(t =>
            {
                var def = t.Definition;
                var fn = def.TryGetProperty("function", out var f) ? f : def;
                object schema = fn.TryGetProperty("parameters", out var p) && p.ValueKind != JsonValueKind.Undefined
                    ? p
                    : new { type = "object" };
                return new
                {
                    name = t.Name,
                    description = t.Description,
                    input_schema = schema
                };
            }).ToArray();
        }

        var text = new StringBuilder();
        var calls = new ToolCallAccumulator();
        var partialTools = new Dictionary<int, (string Name, StringBuilder Args)>();
        var toolIndexes = new Dictionary<int, string>();
        var reasoning = new StringBuilder();
        string finish = "";

        using var req = new HttpRequestMessage(HttpMethod.Post, _provider.ChatUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        var token = SecretBox.Unprotect(_provider.ProtectedToken);
        if (!string.IsNullOrEmpty(token)) req.Headers.TryAddWithoutValidation("x-api-key", token);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

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
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            try
            {
                using var evt = JsonDocument.Parse(data);
                var root = evt.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";

                switch (type)
                {
                    case "content_block_start":
                    {
                        int index = root.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0;
                        if (root.TryGetProperty("content_block", out var block) &&
                            block.TryGetProperty("type", out var bt) && bt.GetString() == "tool_use")
                        {
                            var id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                            var nm = block.TryGetProperty("name", out var nmEl) ? nmEl.GetString() ?? "" : "";
                            partialTools[index] = (nm, new StringBuilder());
                            toolIndexes[index] = id;
                        }
                        break;
                    }

                    case "content_block_delta":
                    {
                        int index = root.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0;
                        if (!root.TryGetProperty("delta", out var delta)) break;
                        var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : "";

                        if (deltaType == "text_delta" && delta.TryGetProperty("text", out var txt))
                        {
                            var value = txt.GetString() ?? "";
                            if (value.Length > 0) { text.Append(value); onDelta?.Invoke(value); }
                        }
                        else if (deltaType == "thinking_delta" && delta.TryGetProperty("thinking", out var th))
                        {
                            // The thinking behind the answer.
                            var value = th.GetString() ?? "";
                            if (value.Length > 0) { reasoning.Append(value); onReasoning?.Invoke(value); }
                        }
                        else if (deltaType == "input_json_delta" && delta.TryGetProperty("partial_json", out var pj))
                        {
                            if (partialTools.TryGetValue(index, out var entry))
                                entry.Args.Append(pj.GetString() ?? "");
                        }
                        break;
                    }

                    case "message_delta":
                        if (root.TryGetProperty("delta", out var md) &&
                            md.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                            finish = sr.GetString() ?? "";
                        break;
                }
            }
            catch (JsonException) { /* keep-alive or partial frame */ }
        }

        // Turn the accumulated tool blocks into the same call list the local backend
        // produces, so the conversation loop does not have to care which one answered.
        var built = new List<ToolCall>();
        foreach (var pair in partialTools.OrderBy(k => k.Key))
        {
            if (string.IsNullOrEmpty(pair.Value.Name)) continue;
            var id = toolIndexes.TryGetValue(pair.Key, out var tid) ? tid : "call_" + pair.Key;
            built.Add(new ToolCall(id, pair.Value.Name,
                pair.Value.Args.Length == 0 ? "{}" : pair.Value.Args.ToString()));
        }

        return new ChatTurn(text.ToString(), built, finish, reasoning.ToString());
    }

    private static object ParseArguments(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }
}
