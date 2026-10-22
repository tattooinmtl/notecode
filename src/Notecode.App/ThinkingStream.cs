using System.Text;
using System.Text.Json;

namespace Notecode.App;

/// <summary>
/// Pulls a model's thinking out of a streamed reply, wherever the provider puts it.
///
/// Providers disagree on where the thinking goes:
/// <list type="bullet">
/// <item><c>delta.reasoning_content</c> or <c>delta.reasoning</c>, a string per chunk
/// (llama.cpp, DeepSeek, MiniMax in its documented form);</item>
/// <item><c>delta.reasoning_details</c>, an array of <c>{ text }</c> objects that MiniMax
/// sends with <c>reasoning_split</c> - and that is cumulative: each chunk can carry
/// everything so far, not just the new part;</item>
/// <item>inline in <c>delta.content</c> between <c>&lt;think&gt;</c> and
/// <c>&lt;/think&gt;</c> (older MiniMax models, Qwen-style templates).</item>
/// </list>
/// Without this, a model that thinks for half a minute shows nothing at all until
/// the answer starts, and the app looks frozen.
/// </summary>
internal sealed class ThinkingStream
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    private readonly StringBuilder _pending = new();
    private readonly Dictionary<int, string> _details = new();
    private bool _inThink;

    /// <summary>
    /// Splits one chunk of <c>content</c> into answer text and thinking. A tag cut in
    /// half by the chunk boundary is held back until the next chunk completes it.
    /// </summary>
    public (string Text, string Thinking) FeedContent(string chunk)
    {
        _pending.Append(chunk);
        var s = _pending.ToString();
        _pending.Clear();

        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var i = 0;
        while (i < s.Length)
        {
            if (_inThink)
            {
                var close = s.IndexOf(Close, i, StringComparison.Ordinal);
                if (close < 0) { i = Hold(s, i, thinking); break; }
                thinking.Append(s, i, close - i);
                _inThink = false;
                i = close + Close.Length;
                continue;
            }

            var open = s.IndexOf(Open, i, StringComparison.Ordinal);
            var stray = s.IndexOf(Close, i, StringComparison.Ordinal);

            // MiniMax in split mode sends a lone closing tag in the content just
            // before a tool call. It closes nothing, so it is dropped.
            if (stray >= 0 && (open < 0 || stray < open))
            {
                text.Append(s, i, stray - i);
                i = stray + Close.Length;
                continue;
            }

            if (open < 0) { i = Hold(s, i, text); break; }
            text.Append(s, i, open - i);
            _inThink = true;
            i = open + Open.Length;
        }

        return (text.ToString(), thinking.ToString());
    }

    /// <summary>What is still held back when the stream ends.</summary>
    public (string Text, string Thinking) Flush()
    {
        var rest = _pending.ToString();
        _pending.Clear();
        return _inThink ? ("", rest) : (rest, "");
    }

    /// <summary>
    /// The new part of a <c>reasoning_details</c> array. MiniMax sends it
    /// cumulative, so an entry that starts with what was already seen contributes
    /// only what follows; one that does not is taken as a plain increment.
    /// </summary>
    public string FeedDetails(JsonElement details)
    {
        if (details.ValueKind != JsonValueKind.Array) return "";

        var fresh = new StringBuilder();
        var position = 0;
        foreach (var item in details.EnumerateArray())
        {
            var index = item.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var n) ? n : position;
            position++;
            if (!item.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) continue;

            var text = t.GetString() ?? "";
            var seen = _details.GetValueOrDefault(index, "");
            if (text.StartsWith(seen, StringComparison.Ordinal))
            {
                fresh.Append(text, seen.Length, text.Length - seen.Length);
                _details[index] = text;
            }
            else
            {
                fresh.Append(text);
                _details[index] = seen + text;
            }
        }
        return fresh.ToString();
    }

    /// <summary>
    /// Emits <paramref name="s"/> from <paramref name="from"/> into <paramref name="into"/>,
    /// keeping back a tail that could be the start of a tag.
    /// </summary>
    private int Hold(string s, int from, StringBuilder into)
    {
        var keep = Math.Max(PartialTag(s, from, Open), PartialTag(s, from, Close));
        into.Append(s, from, s.Length - from - keep);
        _pending.Append(s, s.Length - keep, keep);
        return s.Length;
    }

    private static int PartialTag(string s, int from, string tag)
    {
        for (var n = Math.Min(tag.Length - 1, s.Length - from); n > 0; n--)
        {
            if (string.CompareOrdinal(s, s.Length - n, tag, 0, n) == 0) return n;
        }
        return 0;
    }
}
