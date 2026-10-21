using System.Text;
using System.Text.Json;

namespace Notecode.App;

/// <summary>
/// A tool advertised by llama-server. The schema is kept exactly as the server published it
/// (in <c>/tools</c> every entry carries an OpenAI <c>definition</c> object) so the model sees
/// the same contract the server will dispatch against.
/// </summary>
internal sealed record ToolSpec(string Name, string Description, JsonElement Definition);

/// <summary>A single tool invocation the model asked for.</summary>
internal sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// One assistant turn: the text it produced plus any tool calls it wants executed before it
/// can continue.
///
/// <see cref="Reasoning"/> is the thinking behind the answer, when the provider sends it.
/// It is kept so it can be handed back with the message on the next turn - see
/// <see cref="ChatMessage.Reasoning"/>.
/// </summary>
internal sealed record ChatTurn(
    string Content,
    List<ToolCall> ToolCalls,
    string FinishReason,
    string? Reasoning = null);

/// <summary>
/// Rebuilds a tool call from the partial deltas llama-server streams. The server sends the
/// function name once and the arguments in fragments, so the pieces have to be stitched back
/// together by index before they can be dispatched.
/// </summary>
internal sealed class ToolCallAccumulator
{
    private sealed class Partial
    {
        public string Id = "";
        public string Name = "";
        public StringBuilder Arguments = new();
    }

    private readonly Dictionary<int, Partial> _partials = new();

    public void Add(JsonElement call)
    {
        int index = ReadIndex(call);
        if (!_partials.TryGetValue(index, out var p))
        {
            p = new Partial();
            _partials[index] = p;
        }

        if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            var value = id.GetString();
            if (!string.IsNullOrEmpty(value)) p.Id = value;
        }

        if (call.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
        {
            if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                var value = name.GetString();
                if (!string.IsNullOrEmpty(value)) p.Name = value;
            }
            if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
            {
                var value = args.GetString();
                if (!string.IsNullOrEmpty(value)) p.Arguments.Append(value);
            }
        }
    }

    public List<ToolCall> Build()
    {
        var calls = new List<ToolCall>();
        foreach (var pair in _partials.OrderBy(k => k.Key))
        {
            var p = pair.Value;
            if (string.IsNullOrEmpty(p.Name)) continue;
            calls.Add(new ToolCall(
                string.IsNullOrEmpty(p.Id) ? "call_" + pair.Key : p.Id,
                p.Name,
                p.Arguments.Length == 0 ? "{}" : p.Arguments.ToString()));
        }
        return calls;
    }

    private static int ReadIndex(JsonElement call)
    {
        if (!call.TryGetProperty("index", out var idx)) return 0;
        // llama-server writes the index as a JSON string; the OpenAI spec says number.
        return idx.ValueKind switch
        {
            JsonValueKind.Number => idx.TryGetInt32(out var n) ? n : 0,
            JsonValueKind.String => int.TryParse(idx.GetString(), out var s) ? s : 0,
            _ => 0
        };
    }
}
