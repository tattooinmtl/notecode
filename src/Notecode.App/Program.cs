namespace Notecode.App;

internal static class Program
{
    /// <summary>The process that started this one, or -1 for "whoever started me".</summary>
    private const int AttachParentProcess = -1;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // `notecode.exe --print-config [file]` writes the configuration the app
        // resolved, as JSON.
        //
        // It is a diagnostic, and it exists because the configuration is
        // written with %USERPROFILE% in the paths into the user's own folders
        // and has to be expanded when it is read. This is how a user - or a
        // test - sees what those paths turned into on this machine. It runs
        // before anything else, so it answers even while the app is open.
        if (args.Length > 0 && args[0].Equals("--print-config", StringComparison.OrdinalIgnoreCase))
        {
            PrintConfig(args.Length > 1 ? args[1] : null);
            return;
        }

        // One copy at a time. A second copy does not start: it brings the
        // one that is running - usually sitting in the notification area -
        // to the front and leaves.
        using var single = SingleInstance.TryAcquire();
        if (single is null)
        {
            SingleInstance.SignalExisting();
            return;
        }

        AppConfig cfg;
        try
        {
            cfg = AppConfig.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"notecode could not load its configuration.\n\n{ex.Message}\n\n" +
                "Expected runtime\\notecode.config.json next to the executable.",
                "notecode", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // The workspace is the llama-server's working directory, so the model's
        // built-in file tools are scoped to exactly this tree.
        try { Directory.CreateDirectory(cfg.Workspace.Root); }
        catch { /* surfaced later by the explorer */ }

        // The fonts are the user's to scale, so the scale is read before any
        // control is built and every one of them gets the size the user chose.
        Theme.ApplyFontScale(cfg.Ui.FontScale);

        // The saved hardware profiles. They belong to the machine, not to the
        // checkout, so they live under %APPDATA%\notecode.
        var store = HardwareProfileStore.Load();

        // `notecode.exe <file>` opens that file straight into the editor.
        string? initialFile = null;
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            var candidate = Path.GetFullPath(args[0]);
            if (File.Exists(candidate)) initialFile = candidate;
        }

        // The splash scans the machine and decides which llama.cpp build runs
        // here. It closes itself when it is done, which is what ends this
        // Application.Run and lets the real window take over.
        var splash = new SplashForm(cfg, store);
        Application.Run(splash);

        if (splash.ResolvedProfile is { } profile)
        {
            ApplyProfile(cfg, profile);

            if (splash.ResolvedOptions is { } options)
            {
                cfg.RuntimeOptions = options;
            }
        }

        var main = new MainForm(cfg, initialFile, store);

        // The shortcut is clicked while the app is in the notification area:
        // the second copy asks this one to come forward.
        if (single is not null)
        {
            single.ShowRequested += main.ShowFromTray;
        }

        Application.Run(main);
    }

    /// <summary>
    /// Writes the configuration the app resolved, as JSON.
    ///
    /// These are the values the app is using, after the environment variables
    /// in them have been expanded. A path that did not resolve shows up here
    /// as it is - still carrying its %VARIABLE% - instead of quietly turning
    /// into a folder that is named after the variable.
    /// </summary>
    private static void PrintConfig(string? file)
    {
        AppConfig cfg;
        try
        {
            cfg = AppConfig.Load();
        }
        catch (Exception ex)
        {
            WriteOut(file, "{\"error\": " + System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}");
            return;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(cfg, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        WriteOut(file, json);
    }

    /// <summary>
    /// Puts the output where it was asked for: in a file, or on the console.
    /// </summary>
    private static void WriteOut(string? file, string text)
    {
        if (!string.IsNullOrWhiteSpace(file))
        {
            try { File.WriteAllText(file, text); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
            return;
        }

        // The app is a window, so it has no console of its own. The console of
        // whoever started it is borrowed for the length of the write, and a
        // new one is opened if there was none to borrow.
        try
        {
            if (!AttachConsole(AttachParentProcess)) AllocConsole();
            using var stdout = Console.OpenStandardOutput();
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            stdout.Write(bytes, 0, bytes.Length);
        }
        catch
        {
            // There was nowhere to print to. The file form of the command is
            // there for exactly this case.
        }
    }

    /// <summary>
    /// Puts the profile's choices onto the configuration.
    ///
    /// The profile is what the user chose for this machine. The config file is
    /// the shared part - the model, the port, the MCP servers - and the profile
    /// only overrides what is about the machine.
    /// </summary>
    private static void ApplyProfile(AppConfig cfg, HardwareProfile profile)
    {
        // A model that is not on this machine is not applied. A profile can be
        // written on one machine and read on another, and the models live in a
        // folder that is not part of the checkout.
        if (profile.Model.Length > 0 && File.Exists(profile.Model)) cfg.Llama.Model = profile.Model;
        if (profile.Mmproj.Length > 0 && File.Exists(profile.Mmproj)) cfg.Llama.Mmproj = profile.Mmproj;
        if (profile.CtxSize > 0) cfg.Llama.Context.CtxSize = profile.CtxSize;

        if (profile.Threads > 0)
        {
            cfg.Llama.Cpu.Threads = profile.Threads;
            cfg.Llama.Cpu.ThreadsBatch = profile.Threads;
        }
    }
}
