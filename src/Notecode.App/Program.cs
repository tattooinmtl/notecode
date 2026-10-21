namespace Notecode.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

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
