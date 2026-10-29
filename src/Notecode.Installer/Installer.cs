using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notecode.Installer;

/// <summary>
/// The install itself: the files, the configuration, the shortcuts and the
/// entry in the list of installed programs.
///
/// It installs per user, into %LOCALAPPDATA%\Programs. That is the right
/// place for an app that runs a local model: it needs no administrator, it
/// does not fight another version of itself, and uninstalling is a folder
/// and a registry key.
/// </summary>
internal static class Installer
{
    /// <summary>Where notecode is installed by default.</summary>
    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "notecode");

    /// <summary>The name of the embedded payload.</summary>
    public const string PayloadResource = "payload.zip";

    /// <summary>True when the payload was built into the installer.</summary>
    public static bool HasPayload
    {
        get
        {
            try
            {
                using var s = typeof(Installer).Assembly.GetManifestResourceStream(PayloadResource);
                return s is not null;
            }
            catch { return false; }
        }
    }

    /// <summary>How big the payload is, in bytes.</summary>
    public static long PayloadSize
    {
        get
        {
            try
            {
                using var s = typeof(Installer).Assembly.GetManifestResourceStream(PayloadResource);
                return s?.Length ?? 0;
            }
            catch { return 0; }
        }
    }

    /// <summary>Unpacks the payload into the install folder.</summary>
    public static void Extract(string installDir, Action<string>? progress = null)
    {
        using var stream = typeof(Installer).Assembly.GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException("the payload is missing from the installer");

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length == 0) continue;

            // A zip entry can try to escape the folder it is unzipped into.
            // The name is checked before anything is written.
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(installDir, relative));
            var root = Path.GetFullPath(installDir);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"the payload tried to write outside the install folder: {entry.FullName}");

            progress?.Invoke(entry.FullName);

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            entry.ExtractToFile(target, true);
        }
    }

    /// <summary>
    /// Points the installed configuration at the installed files.
    ///
    /// The configuration that ships in the payload points at the build
    /// machine: the llama-server in C:\Llama and the model in C:\models. The
    /// llama-server is installed alongside the app, so that path is rewritten.
    /// The model is not installed - the weights are far too large to ship -
    /// so the model path is left alone and the settings page picks it.
    /// </summary>
    public static void WriteConfig(string installDir)
    {
        var configPath = Path.Combine(installDir, "runtime", "notecode.config.json");
        if (!File.Exists(configPath)) return;

        var json = File.ReadAllText(configPath);
        var root = JsonNode.Parse(json) as JsonObject;
        if (root is null) return;

        var llama = root["llama"] as JsonObject;
        if (llama is null) return;

        var llamaDir = Path.Combine(installDir, "llama");
        var serverExe = Path.Combine(llamaDir, "llama-server.exe");

        llama["serverExe"] = serverExe;
        llama["serverDir"] = llamaDir;

        // Each backend is a different executable. The CPU one is the one that
        // is installed; the GPU ones are built on the machine that has the
        // card, and they land in their own folders beside it.
        if (llama["builds"] is JsonObject builds)
        {
            foreach (var (key, value) in builds)
            {
                if (value is not JsonObject build) continue;
                build["exe"] = key == "cpu"
                    ? serverExe
                    : Path.Combine(llamaDir, key, "llama-server.exe");
            }
        }

        // The workspace is where the agent's file tools are allowed to work.
        if (root["workspace"] is JsonObject workspace)
            workspace["root"] = Path.Combine(installDir, "workspace");

        if (root["mcp"] is JsonObject mcp && mcp["servers"] is JsonObject servers)
        {
            // The config in the payload points the servers at the build
            // machine. notecode's own file tools are installed with the app...
            if (servers["notecode"] is JsonObject notecode)
                notecode["args"] = new JsonArray(Path.Combine(installDir, "runtime", "notecode-mcp.mjs"));

            // ...and Meshy is an extra pack, which meshy_installer.exe puts in
            // the user's profile. Until it is installed, the doctor says where
            // to get it.
            if (servers["meshy"] is JsonObject meshy)
            {
                meshy["command"] = "node";
                meshy["args"] = new JsonArray(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "notecode", "meshy", "server.mjs"));
            }

            // The servers the user installed - the extra packs - are kept in
            // the user's own list, and an install of the app replaces this
            // file. They are copied back in, so an update keeps them.
            foreach (var (name, entry) in ReadUserServers())
                servers[name] = entry;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(configPath, root.ToJsonString(options));
    }

    /// <summary>The servers in the user's own list, %APPDATA%\notecode\mcp.json.</summary>
    private static List<(string Name, JsonNode Entry)> ReadUserServers()
    {
        var list = new List<(string, JsonNode)>();
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "notecode", "mcp.json");
            if (!File.Exists(path)) return list;
            if (JsonNode.Parse(File.ReadAllText(path))?["mcpServers"] is JsonObject user)
                foreach (var (name, entry) in user)
                    if (entry is not null) list.Add((name, entry.DeepClone()));
        }
        catch
        {
            // A list that cannot be read adds nothing; the app's own servers stand.
        }
        return list;
    }

    /// <summary>Creates the shortcuts on the desktop and in the start menu.</summary>
    public static void CreateShortcuts(string installDir, bool desktop, bool startMenu)
    {
        var exe = Path.Combine(installDir, "notecode.exe");
        var icon = Path.Combine(installDir, "notecode.ico");

        if (desktop)
        {
            var link = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "notecode.lnk");
            CreateShortcut(link, exe, installDir, icon);
        }

        if (startMenu)
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs");
            Directory.CreateDirectory(folder);
            CreateShortcut(Path.Combine(folder, "notecode.lnk"), exe, installDir, icon);
        }
    }

    private static void CreateShortcut(string linkPath, string exe, string workingDir, string icon)
    {
        var dir = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // The Windows Script Host makes the .lnk. It is part of Windows, but
        // it is not guaranteed to be there, so a machine without it gets no
        // shortcut rather than a failed install.
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) return;

        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type);
            if (instance is null) return;

            dynamic shell = instance;
            var link = shell.CreateShortcut(linkPath);
            link.TargetPath = exe;
            link.WorkingDirectory = workingDir;
            link.Description = "notecode - a dual-pane IDE over a local llama.cpp runtime";
            if (File.Exists(icon)) link.IconLocation = icon + ",0";
            link.Save();
        }
        catch
        {
            // A shortcut that could not be made is not a failed install.
        }
        finally
        {
            if (instance is IDisposable d) d.Dispose();
        }
    }

    /// <summary>Removes the shortcuts the installer made.</summary>
    public static void RemoveShortcuts()
    {
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "notecode.lnk"));
        TryDelete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", "notecode.lnk"));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // -------------------------------------------------------------- uninstall

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\notecode";

    /// <summary>Adds the entry that makes notecode appear in the list of installed programs.</summary>
    public static void RegisterUninstaller(string installDir, string version, string keyName)
    {
        try
        {
            var keyPath = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{keyName}";
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue("DisplayName", "notecode");
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "notecode");
            key.SetValue("DisplayIcon", Path.Combine(installDir, "notecode.exe"));
            key.SetValue("InstallLocation", installDir);
            key.SetValue("UninstallString", $"\"{Path.Combine(installDir, "Uninstall.exe")}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{Path.Combine(installDir, "Uninstall.exe")}\" --uninstall --quiet");
            key.SetValue("NoModify", 1);
            key.SetValue("NoRepair", 1);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"could not register the uninstaller: {ex.Message}");
        }
    }

    /// <summary>Removes the entry from the list of installed programs.</summary>
    public static void UnregisterUninstaller(string? keyName = null)
    {
        try
        {
            var keyPath = keyName is null
                ? UninstallKey
                : $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{keyName}";
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
        }
        catch { }
    }

    /// <summary>
    /// Removes the installation.
    ///
    /// The uninstaller cannot delete the folder it is running from, so it
    /// leaves itself behind and a detached process removes the folder a
    /// moment later.
    /// </summary>
    public static void Uninstall(string installDir, bool quiet)
    {
        // The shortcuts are only the real ones when this is the real
        // installation. An install somewhere else is a test, and it must not
        // take the desktop shortcut with it.
        var isTheRealOne = string.Equals(
            Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(DefaultInstallDir).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        if (isTheRealOne) RemoveShortcuts();

        UnregisterUninstaller(Path.GetFileName(installDir.TrimEnd(Path.DirectorySeparatorChar)));

        var self = Path.Combine(installDir, "Uninstall.exe");
        var exe = Path.Combine(installDir, "notecode.exe");

        // The app has to be gone before its files can be.
        try
        {
            foreach (var p in Process.GetProcessesByName("notecode"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }

        foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(file, self, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch { }
        }
        foreach (var dir in Directory.EnumerateDirectories(installDir, "*", SearchOption.AllDirectories)
                               .OrderByDescending(d => d.Length))
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        // The folder and the uninstaller itself.
        StartDetachedRemoval(installDir, self);
    }

    /// <summary>Removes the install folder once this process has let go of it.</summary>
    private static void StartDetachedRemoval(string installDir, string self)
    {
        try
        {
            var cmd = $"/c ping 127.0.0.1 -n 3 > nul & del /f /q \"{self}\" & rmdir /s /q \"{installDir}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmd,
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch { }
    }
}
