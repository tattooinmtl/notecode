using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notecode.App;

/// <summary>One turn in a session.</summary>
internal sealed class SessionMessage
{
    /// <summary>user, assistant, note or tool.</summary>
    public string Role { get; set; } = "";

    /// <summary>What was said.</summary>
    public string Text { get; set; } = "";

    /// <summary>When it was said.</summary>
    public DateTime AtUtc { get; set; }
}

/// <summary>
/// A conversation, kept between runs.
///
/// A session is what the tray menu calls a session: one conversation, in one
/// workspace, with the turns that made it up. It is written to disk as it
/// grows, so closing the window does not lose the conversation, and so the
/// session window has something to list.
/// </summary>
internal sealed class SessionRecord
{
    /// <summary>The identifier, which is also the file name.</summary>
    public string Id { get; set; } = "";

    /// <summary>What the conversation is called.</summary>
    public string Title { get; set; } = "";

    /// <summary>When the session started.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>When the session was last touched.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Pinned sessions stay at the top of the list.</summary>
    public bool Pinned { get; set; }

    /// <summary>The workspace the session belongs to.</summary>
    public string Workspace { get; set; } = "";

    /// <summary>The turns.</summary>
    public List<SessionMessage> Messages { get; set; } = new();

    /// <summary>How long ago it was touched, in words.</summary>
    public string LastActive
    {
        get
        {
            var span = DateTime.UtcNow - UpdatedUtc;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
            if (span.TotalDays < 7) return $"{(int)span.TotalDays} d ago";
            return UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd");
        }
    }

    /// <summary>The title to show, which is the first thing that was said.</summary>
    public string DisplayTitle
    {
        get
        {
            if (Title.Length > 0 && Title != "New session") return Title;

            // The first thing the person said, if they said anything. If the
            // session is only the app talking to itself, the first thing that
            // is not one of the app's own lines is used instead, so the list
            // does not say "New session" twenty times.
            string? firstUser = null;
            string? firstOther = null;
            foreach (var m in Messages)
            {
                var text = m.Text.Trim();
                if (text.Length == 0) continue;
                if (m.Role == "user") { firstUser = text; break; }
                if (firstOther is null && !IsAppLine(text)) firstOther = text;
            }

            var line = FirstLine(firstUser ?? firstOther);
            if (line is null) return "New session";
            return line.Length <= 60 ? line : line[..60] + "...";
        }
    }

    /// <summary>
    /// True for a line the app wrote about itself rather than something in
    /// the conversation: the server starting, an MCP server connecting.
    /// </summary>
    private static bool IsAppLine(string text) =>
        text.StartsWith("[notecode]", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("[mcp:", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first line of a piece of text, trimmed.</summary>
    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Split('\n')[0].Trim();
        return line.Length == 0 ? null : line;
    }
}

/// <summary>
/// The sessions of this machine, on disk.
///
/// They live under %APPDATA%\notecode\sessions, one JSON file per session,
/// because a conversation is a property of the person using it and not of the
/// checkout it happened in.
/// </summary>
internal sealed class SessionStore
{
    /// <summary>How many turns a session keeps. Older ones are dropped.</summary>
    public const int MaxMessages = 400;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Dictionary<string, SessionRecord> _sessions = new(StringComparer.OrdinalIgnoreCase);

    private SessionStore() { }

    /// <summary>Where the session files are.</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "notecode", "sessions");

    /// <summary>Reads every session on disk. A file that cannot be read is skipped.</summary>
    public static SessionStore Load()
    {
        var store = new SessionStore();
        try
        {
            if (!Directory.Exists(Root)) return store;
            foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
            {
                try
                {
                    var s = JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(file), Options);
                    if (s is null || s.Id.Length == 0) continue;
                    store._sessions[s.Id] = s;
                }
                catch
                {
                    // One unreadable session is not a reason to lose the others.
                }
            }
        }
        catch { }
        return store;
    }

    /// <summary>Every session: the pinned ones first, then the most recent.</summary>
    public IReadOnlyList<SessionRecord> All() =>
        _sessions.Values
            .OrderByDescending(s => s.Pinned)
            .ThenByDescending(s => s.UpdatedUtc)
            .ToList();

    /// <summary>One session by its identifier.</summary>
    public SessionRecord? Find(string id) =>
        _sessions.TryGetValue(id, out var s) ? s : null;

    /// <summary>
    /// Starts a new session.
    ///
    /// It is not written out yet. A session that never says anything is not a
    /// conversation, and a list full of empty ones is worse than no list, so
    /// the file appears the first time there is something in it.
    /// </summary>
    public SessionRecord New(string workspace)
    {
        var now = DateTime.UtcNow;
        return new SessionRecord
        {
            Id = now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6],
            Title = "New session",
            CreatedUtc = now,
            UpdatedUtc = now,
            Workspace = workspace
        };
    }

    /// <summary>
    /// The most recent session that has something in it, for this workspace.
    ///
    /// This is what the app carries on with when it starts, so closing the
    /// window and opening it again does not lose the thread.
    /// </summary>
    public SessionRecord? Latest(string workspace)
    {
        SessionRecord? best = null;
        foreach (var s in _sessions.Values)
        {
            if (s.Messages.Count == 0) continue;
            if (workspace.Length > 0 && s.Workspace.Length > 0 &&
                !s.Workspace.Equals(workspace, StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null || s.UpdatedUtc > best.UpdatedUtc) best = s;
        }
        return best;
    }

    /// <summary>Writes a session out. The file name is the identifier.</summary>
    public void Save(SessionRecord s)
    {
        if (s.Id.Length == 0) return;
        s.UpdatedUtc = DateTime.UtcNow;
        if (s.Messages.Count > MaxMessages)
            s.Messages.RemoveRange(0, s.Messages.Count - MaxMessages);
        _sessions[s.Id] = s;
        try
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, s.Id + ".json");
            var json = JsonSerializer.Serialize(s, Options);
            // Written to a temporary file and moved into place, so a crash
            // halfway through cannot leave a half written session behind.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, true);
        }
        catch { }
    }

    /// <summary>Removes a session from the disk and from the list.</summary>
    public void Delete(string id)
    {
        _sessions.Remove(id);
        try { File.Delete(Path.Combine(Root, id + ".json")); }
        catch { }
    }

    /// <summary>Pins or unpins a session.</summary>
    public void SetPinned(string id, bool pinned)
    {
        var s = Find(id);
        if (s is null) return;
        s.Pinned = pinned;
        Save(s);
    }
}
