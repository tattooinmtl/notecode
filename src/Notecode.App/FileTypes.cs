using System.Text.Json;

namespace Notecode.App;

/// <summary>
/// File typing, loaded from runtime/file-types.json.
///
/// The same registry is read by runtime/notecode-mcp.mjs, so the AI's notion of
/// "save as markdown" and the editor's notion of "how do I highlight .md" can
/// never drift apart.
/// </summary>
internal sealed class FileTypes
{
    private readonly Dictionary<string, string> _modes;
    private readonly Dictionary<string, string> _aliases;
    private readonly HashSet<string> _binary;
    private readonly List<SniffRule> _sniff;
    private readonly string _defaultMode;

    private sealed record SniffRule(string? StartsWith, string? Contains, string Ext);

    public int SupportedExtensionCount => _modes.Count;

    public FileTypes()
    {
        _modes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _binary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _sniff = new List<SniffRule>();
        _defaultMode = "text/plain";
    }

    public static FileTypes Load(string runtimeDir)
    {
        var path = Path.Combine(runtimeDir, "file-types.json");
        if (!File.Exists(path)) return new FileTypes();

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var modes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("modes", out var modesEl))
        {
            foreach (var p in modesEl.EnumerateObject())
            {
                if (p.Name.StartsWith('$')) continue;
                if (p.Value.ValueKind == JsonValueKind.String)
                    modes[p.Name] = p.Value.GetString()!;
            }
        }

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("aliases", out var aliasesEl))
        {
            foreach (var p in aliasesEl.EnumerateObject())
            {
                if (p.Name.StartsWith('$')) continue;
                if (p.Value.ValueKind == JsonValueKind.String)
                    aliases[p.Name] = p.Value.GetString()!;
            }
        }

        var binary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("binary", out var binaryEl))
        {
            foreach (var e in binaryEl.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) binary.Add(e.GetString()!);
        }

        var sniff = new List<SniffRule>();
        if (root.TryGetProperty("sniff", out var sniffEl) &&
            sniffEl.TryGetProperty("rules", out var rulesEl))
        {
            foreach (var r in rulesEl.EnumerateArray())
            {
                string? starts = r.TryGetProperty("startsWith", out var s) ? s.GetString() : null;
                string? contains = r.TryGetProperty("contains", out var c) ? c.GetString() : null;
                string ext = r.TryGetProperty("ext", out var e) ? e.GetString() ?? "" : "";
                if (ext.Length > 0) sniff.Add(new SniffRule(starts, contains, ext));
            }
        }

        var defaultMode = root.TryGetProperty("defaultMode", out var d) ? d.GetString() ?? "text/plain" : "text/plain";
        return new FileTypes(modes, aliases, binary, sniff, defaultMode);
    }

    private FileTypes(
        Dictionary<string, string> modes,
        Dictionary<string, string> aliases,
        HashSet<string> binary,
        List<SniffRule> sniff,
        string defaultMode)
    {
        _modes = modes;
        _aliases = aliases;
        _binary = binary;
        _sniff = sniff;
        _defaultMode = defaultMode;
    }

    public string ExtensionOf(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public bool IsBinary(string path) => _binary.Contains(ExtensionOf(path));

    /// <summary>Canonical extension for a type name the user or model may use.</summary>
    public string? ResolveExtension(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        var raw = type.Trim().ToLowerInvariant();
        if (_aliases.TryGetValue(raw, out var aliased)) return aliased;
        if (_modes.ContainsKey(raw)) return raw;
        var ext = ExtensionOf(raw);
        if (ext.Length > 0 && (_aliases.TryGetValue(ext, out var a2) || _modes.ContainsKey(ext)))
            return _aliases.TryGetValue(ext, out var a3) ? a3 : ext;
        return null;
    }

    /// <summary>
    /// The CodeMirror mode for a file. The extension wins; content sniffing is
    /// only a fallback for unknown or missing extensions.
    /// </summary>
    public string ModeFor(string path, string? content = null)
    {
        var ext = ExtensionOf(path);
        if (ext.Length > 0 && _modes.TryGetValue(ext, out var mode)) return mode;

        if (content is not null)
        {
            foreach (var rule in _sniff)
            {
                if (rule.StartsWith is not null && content.StartsWith(rule.StartsWith, StringComparison.Ordinal))
                    return _modes.TryGetValue(rule.Ext, out var m1) ? m1 : _defaultMode;
                if (rule.Contains is not null && content.Contains(rule.Contains, StringComparison.Ordinal))
                    return _modes.TryGetValue(rule.Ext, out var m2) ? m2 : _defaultMode;
            }
        }

        return _defaultMode;
    }

    /// <summary>Human-readable name for a mode, for the status bar.</summary>
    public string Describe(string path)
    {
        var ext = ExtensionOf(path);
        if (ext.Length == 0) return "plain text";
        return _modes.TryGetValue(ext, out var mode) ? $"{ext} - {mode}" : ext;
    }
}
