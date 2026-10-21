namespace Notecode.App;

/// <summary>
/// Tiny append-only diagnostics log. The GUI has no console, so anything that
/// fails silently in the app (WebView2 messages, shell startup, MCP connects)
/// would otherwise be invisible.
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string Path
    {
        get
        {
            if (_path is null)
            {
                var dir = AppConfig.RuntimeDirOrFallback();
                _path = System.IO.Path.Combine(dir, "notecode-app.log");
            }
            return _path;
        }
    }

    public static void Write(string component, string message)
    {
        try
        {
            lock (Gate)
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{component}] {message}{Environment.NewLine}";
                File.AppendAllText(Path, line);
            }
        }
        catch { /* logging must never throw */ }
    }

    public static void Write(string component, string message, Exception ex)
        => Write(component, $"{message} :: {ex.GetType().Name}: {ex.Message}");

    /// <summary>Records a recoverable problem that the user should know about.</summary>
    public static void Warn(string message) => Write("warn", message);
}
