using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notecode.MeshyInstaller;

/// <summary>
/// The install of the Meshy MCP server.
///
/// The server is Meshy's own (@meshy-ai/meshy-mcp-server, MIT). It is not part
/// of notecode: it is a separate download, so the app stays small and the
/// server can be taken away without touching it. The install is per user, into
/// the user's own profile, and needs no administrator.
///
/// Running the installer again updates the server. The program files are
/// replaced, and the user's .env - which holds the Meshy API key - is kept.
///
/// The server is registered in two places:
///   - the user's own list of servers (%APPDATA%\notecode\mcp.json), which the
///     app reads over the list it ships with, so an update of the app does not
///     remove it;
///   - the installed app's own config, for a notecode that does not read that
///     list yet. Notecode-Setup.exe copies the user's list back into the config
///     on every install, so this entry survives an update of the app too.
/// </summary>
internal static class MeshyInstall
{
    public const string PayloadResource = "meshy-payload.zip";
    public const string KeyPage = "https://www.meshy.ai/developers/keys";

    /// <summary>Where the server is installed. --dir moves it, for a test.</summary>
    public static string InstallDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "notecode", "meshy");

    /// <summary>The user's own list of MCP servers. --overlay moves it, for a test.</summary>
    public static string OverlayPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "notecode", "mcp.json");

    /// <summary>
    /// The installed notecode's config, or null when notecode is not installed.
    /// --app-config sets it, for a test.
    /// </summary>
    public static string? AppConfigPath { get; set; } = FindAppConfig();

    public static string ServerPath => Path.Combine(InstallDir, "server.mjs");
    public static string EnvPath => Path.Combine(InstallDir, ".env");
    private static string VersionPath => Path.Combine(InstallDir, "version.txt");

    public static bool IsInstalled => File.Exists(ServerPath);

    /// <summary>The version that is installed now, or null.</summary>
    public static string? InstalledVersion
    {
        get
        {
            try { return File.Exists(VersionPath) ? File.ReadAllText(VersionPath).Trim() : null; }
            catch { return null; }
        }
    }

    /// <summary>The version this installer carries: the Meshy server's own version.</summary>
    public static string PackageVersion
    {
        get
        {
            var v = typeof(MeshyInstall).Assembly.GetName().Version;
            return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>True when a key is already in the installed .env.</summary>
    public static bool HasKey => ReadKey().Length > 0;

    public static bool HasPayload
    {
        get
        {
            try
            {
                using var s = typeof(MeshyInstall).Assembly.GetManifestResourceStream(PayloadResource);
                return s is not null;
            }
            catch { return false; }
        }
    }

    public static long PayloadSize
    {
        get
        {
            try
            {
                using var s = typeof(MeshyInstall).Assembly.GetManifestResourceStream(PayloadResource);
                return s?.Length ?? 0;
            }
            catch { return 0; }
        }
    }

    /// <summary>
    /// Puts the server in place. Everything already there is removed first -
    /// an update must not leave files of the old version behind for node to
    /// load - except the .env, which is the user's.
    /// </summary>
    public static void Extract(Action<string>? progress = null)
    {
        var root = Path.GetFullPath(InstallDir);
        Directory.CreateDirectory(root);

        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (string.Equals(Path.GetFileName(entry), ".env", StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, true);
            else File.Delete(entry);
        }

        using var stream = typeof(MeshyInstall).Assembly.GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException("the Meshy payload is missing from the installer");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length == 0) continue;

            // A zip entry can try to escape the folder it is unzipped into.
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"the payload tried to write outside the install folder: {entry.FullName}");

            progress?.Invoke(entry.FullName);
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }

            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            entry.ExtractToFile(target, true);
        }

        File.WriteAllText(VersionPath, PackageVersion);
    }

    /// <summary>
    /// Writes the key into the .env. An empty key leaves the file as it is, so
    /// an update without a key keeps the one that is there.
    /// </summary>
    public static void WriteKey(string key)
    {
        key = key.Trim();
        var lines = File.Exists(EnvPath) ? File.ReadAllLines(EnvPath).ToList() : new List<string>();

        if (key.Length > 0)
        {
            lines.RemoveAll(l => l.TrimStart().StartsWith("MESHY_API_KEY=", StringComparison.Ordinal));
            lines.Insert(0, $"MESHY_API_KEY={key}");
        }
        else if (!lines.Any(l => l.TrimStart().StartsWith("MESHY_API_KEY=", StringComparison.Ordinal)))
        {
            // No key yet: the line is there to fill in, and the doctor says so.
            lines.Insert(0, "MESHY_API_KEY=");
        }

        // notecode talks to the server over stdio.
        if (!lines.Any(l => l.TrimStart().StartsWith("TRANSPORT=", StringComparison.Ordinal)))
            lines.Add("TRANSPORT=stdio");

        File.WriteAllLines(EnvPath, lines);
    }

    private static string ReadKey()
    {
        try
        {
            if (!File.Exists(EnvPath)) return "";
            var line = File.ReadAllLines(EnvPath)
                .FirstOrDefault(l => l.TrimStart().StartsWith("MESHY_API_KEY=", StringComparison.Ordinal));
            return line is null ? "" : line.Split('=', 2)[1].Trim().Trim('"', '\'');
        }
        catch { return ""; }
    }

    /// <summary>The server's entry, the same in both places it is written.</summary>
    private static JsonObject Entry() => new()
    {
        ["transport"] = "stdio",
        ["command"] = "node",
        ["args"] = new JsonArray(ServerPath),
        ["toolTimeoutMs"] = 300000,
        ["enabled"] = true,
        ["onDemand"] = false,
        ["description"] = "Meshy AI 3D generation (28 tools): text/image to 3D, texture, rig, animate, remesh, convert, " +
                          $"printability. Installed by meshy_installer.exe; the API key is in {EnvPath}."
    };

    /// <summary>Registers the server in the user's list and, when notecode is installed, in its config.</summary>
    public static List<string> RegisterServer()
    {
        var written = new List<string>();

        var overlay = ReadJson(OverlayPath);
        if (overlay is null)
        {
            // The list is there but cannot be read. It is kept beside the new
            // one rather than lost, with the other servers in it.
            File.Copy(OverlayPath, OverlayPath + ".unreadable.bak", overwrite: true);
            overlay = new JsonObject();
        }
        if (overlay["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            overlay["mcpServers"] = servers;
        }
        servers["meshy"] = Entry();
        SaveJson(OverlayPath, overlay);
        written.Add(OverlayPath);

        // The app's config is only written when it was read: a config that
        // cannot be parsed is the app's problem, and replacing it would lose
        // every setting in it.
        if (AppConfigPath is not null && File.Exists(AppConfigPath) && ReadJson(AppConfigPath) is { } cfg)
        {
            if (cfg["mcp"] is not JsonObject mcp) { mcp = new JsonObject(); cfg["mcp"] = mcp; }
            if (mcp["servers"] is not JsonObject appServers) { appServers = new JsonObject(); mcp["servers"] = appServers; }
            appServers["meshy"] = Entry();
            SaveJson(AppConfigPath, cfg);
            written.Add(AppConfigPath);
        }
        return written;
    }

    /// <summary>
    /// Takes the server out of both lists. The installed files are left, with
    /// the user's key in them: taking them away is a separate decision.
    /// </summary>
    public static void UnregisterServer()
    {
        var overlay = ReadJson(OverlayPath);
        if (overlay?["mcpServers"] is JsonObject servers && servers.Remove("meshy"))
        {
            if (servers.Count == 0) overlay.Remove("mcpServers");
            SaveJson(OverlayPath, overlay);
        }

        if (AppConfigPath is not null && File.Exists(AppConfigPath) && ReadJson(AppConfigPath) is { } cfg)
        {
            if (cfg["mcp"]?["servers"] is JsonObject appServers && appServers.Remove("meshy"))
                SaveJson(AppConfigPath, cfg);
        }
    }

    /// <summary>The installed notecode's config: where its uninstall entry says it is, or the default folder.</summary>
    private static string? FindAppConfig()
    {
        var dirs = new List<string>();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\notecode");
            if (key?.GetValue("InstallLocation") is string location && location.Length > 0) dirs.Add(location);
        }
        catch { }
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "notecode"));

        return dirs.Select(d => Path.Combine(d, "runtime", "notecode.config.json")).FirstOrDefault(File.Exists);
    }

    /// <summary>The file as a tree: empty when it is not there, null when it is there but cannot be read.</summary>
    private static JsonObject? ReadJson(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                as JsonObject;
        }
        catch { return null; }
    }

    private static void SaveJson(string path, JsonObject root)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Written to a temporary name and moved into place, so a write that is
        // cut short does not leave a half-written file where the real one was.
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}
