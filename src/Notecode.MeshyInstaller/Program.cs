using System.Diagnostics;

namespace Notecode.MeshyInstaller;

/// <summary>
/// The Meshy installer window.
///
/// It says what it is installing and where, asks for the Meshy API key (which
/// can also be added later), and does it. Run again, it updates: it says which
/// version is installed and which one it carries, and it keeps the key.
///
///   meshy_installer.exe                    the window
///   meshy_installer.exe --silent [--key K] install or update without a window
///   meshy_installer.exe --uninstall [--quiet]
///
/// --dir, --overlay and --app-config move the install folder, the user's list
/// of servers and the app's config, so a test does not touch the real ones.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string? Arg(string name)
        {
            var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (Arg("--dir") is { } dir) MeshyInstall.InstallDir = Path.GetFullPath(dir);
        if (Arg("--overlay") is { } overlay) MeshyInstall.OverlayPath = Path.GetFullPath(overlay);
        if (Arg("--app-config") is { } appConfig) MeshyInstall.AppConfigPath = Path.GetFullPath(appConfig);

        if (Has("--uninstall"))
        {
            if (Has("--quiet"))
            {
                MeshyInstall.UnregisterServer();
                return;
            }
            var answer = MessageBox.Show(
                "Remove Meshy from notecode?\n\n" +
                "The server is taken out of your list of servers, so the meshy_* tools stop working.\n\n" +
                $"The installed files, with your API key, are left in:\n{MeshyInstall.InstallDir}\n\n" +
                "They can be deleted by hand.",
                "Meshy for notecode", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes) MeshyInstall.UnregisterServer();
            return;
        }

        // The silent install is for a machine set up without anyone watching,
        // for the notecode installer and install.ps1, and for the tests.
        if (Has("--silent"))
        {
            if (!MeshyInstall.HasPayload)
            {
                Console.Error.WriteLine("the Meshy payload is missing from the installer");
                Environment.ExitCode = 1;
                return;
            }
            var was = MeshyInstall.InstalledVersion;
            MeshyInstall.Extract();
            MeshyInstall.WriteKey(Arg("--key") ?? "");
            var written = MeshyInstall.RegisterServer();
            Console.WriteLine(was is null
                ? $"Meshy {MeshyInstall.PackageVersion} installed in {MeshyInstall.InstallDir}"
                : was == MeshyInstall.PackageVersion
                    ? $"Meshy {was} reinstalled in {MeshyInstall.InstallDir}"
                    : $"Meshy updated from {was} to {MeshyInstall.PackageVersion} in {MeshyInstall.InstallDir}");
            foreach (var w in written) Console.WriteLine($"server registered in {w}");
            if (!MeshyInstall.HasKey) Console.WriteLine($"no API key yet: add MESHY_API_KEY to {MeshyInstall.EnvPath} ({MeshyInstall.KeyPage})");
            return;
        }

        Application.Run(new MeshyInstallForm());
    }
}

/// <summary>The window the installer is.</summary>
internal sealed class MeshyInstallForm : Form
{
    // The same colours as notecode's own installer.
    private static readonly Color AccentColor = ColorTranslator.FromHtml("#8B5CF6");
    private static readonly Color AccentBrightColor = ColorTranslator.FromHtml("#A78BFA");
    private static readonly Color CanvasColor = ColorTranslator.FromHtml("#0B0E17");
    private static readonly Color SurfaceColor = ColorTranslator.FromHtml("#161B27");
    private static readonly Color ForeColorValue = ColorTranslator.FromHtml("#E8ECF5");
    private static readonly Color MutedColor = ColorTranslator.FromHtml("#939DB4");
    private static readonly Color GoodColor = ColorTranslator.FromHtml("#34D399");
    private static readonly Color BadColor = ColorTranslator.FromHtml("#FB7185");

    private readonly Button _install = new() { Width = 130, Height = 34 };
    private readonly Button _cancel = new() { Text = "Close", Width = 100, Height = 34 };
    private readonly TextBox _key = new() { UseSystemPasswordChar = true, Width = 480 };
    private readonly Label _status = new() { AutoSize = false, Height = 40, Dock = DockStyle.Fill };
    private readonly LinkLabel _keyLink = new() { AutoSize = true, LinkColor = AccentBrightColor, ActiveLinkColor = AccentBrightColor };
    private readonly LinkLabel _where = new() { AutoSize = true, LinkColor = AccentBrightColor, ActiveLinkColor = AccentBrightColor };

    public MeshyInstallForm()
    {
        var updating = MeshyInstall.IsInstalled;
        Text = updating ? "Update Meshy for notecode" : "Install Meshy for notecode";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = CanvasColor;
        ForeColor = ForeColorValue;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(560, 430);

        var title = new Label
        {
            Text = "Meshy AI - 3D generation",
            Font = new Font("Segoe UI", 16f),
            ForeColor = ForeColorValue,
            AutoSize = true
        };

        var blurb = new Label
        {
            Text =
                "Meshy's MCP server: text or image to 3D, texture, rig, animate, remesh, convert and " +
                "check a model for printing, from the notecode chat. It needs Node.js 18 or newer, " +
                "a Meshy account and its API key, and the internet to generate.\n\n" +
                "It is a separate download from notecode, so an update of notecode leaves it alone. " +
                "Run this installer again to update it; your key is kept.",
            ForeColor = MutedColor,
            AutoSize = false,
            Height = 110,
            Dock = DockStyle.Fill
        };

        var keyLabel = new Label
        {
            Text = MeshyInstall.HasKey
                ? "Meshy API key - one is already saved; leave this empty to keep it"
                : "Meshy API key (optional - it can be added later)",
            ForeColor = ForeColorValue,
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 4)
        };
        _key.BackColor = SurfaceColor;
        _key.ForeColor = ForeColorValue;
        _key.BorderStyle = BorderStyle.FixedSingle;
        _keyLink.Text = $"Get a key: {MeshyInstall.KeyPage}";
        _keyLink.LinkClicked += (s, e) => Open(MeshyInstall.KeyPage);

        _where.Text = $"Installed into: {MeshyInstall.InstallDir}";
        _where.LinkClicked += (s, e) => Open(MeshyInstall.InstallDir);

        StyleButton(_install, AccentColor, Color.Black);
        StyleButton(_cancel, SurfaceColor, ForeColorValue);
        _install.Text = updating ? "Update" : "Install";
        _install.Click += async (s, e) => await InstallAsync();
        _cancel.Click += (s, e) => Close();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            BackColor = CanvasColor
        };
        buttons.Controls.Add(_install);
        buttons.Controls.Add(_cancel);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(28, 22, 28, 18),
            BackColor = CanvasColor
        };
        // One column the width of the window: a long path wraps instead of
        // widening the layout and pushing the buttons out of sight.
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        foreach (var link in new[] { _keyLink, _where }) link.MaximumSize = new Size(500, 0);
        foreach (var c in new Control[] { title, blurb, keyLabel, _key, _keyLink, _where, _status, buttons })
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(c);
        }
        Controls.Add(root);

        _status.ForeColor = MutedColor;
        var size = MeshyInstall.PayloadSize;
        if (!MeshyInstall.HasPayload)
        {
            _install.Enabled = false;
            _status.ForeColor = BadColor;
            _status.Text = "This installer has no server in it, so it cannot install anything.";
        }
        else if (updating)
        {
            var was = MeshyInstall.InstalledVersion ?? "an earlier version";
            _status.Text = was == MeshyInstall.PackageVersion
                ? $"Meshy {was} is installed. Updating puts the same version back, which repairs a broken install."
                : $"Meshy {was} is installed. Updating installs {MeshyInstall.PackageVersion}.";
        }
        else
        {
            _status.Text = $"Meshy {MeshyInstall.PackageVersion}, about {size / 1024.0 / 1024.0:N0} MB.";
        }
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* not there yet, or no program for it */ }
    }

    private static void StyleButton(Button b, Color back, Color fore)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = back;
        b.ForeColor = fore;
        b.Cursor = Cursors.Hand;
        b.UseVisualStyleBackColor = false;
    }

    private async Task InstallAsync()
    {
        _install.Enabled = false;
        _cancel.Enabled = false;
        _key.Enabled = false;
        _status.ForeColor = MutedColor;
        var was = MeshyInstall.InstalledVersion;
        var key = _key.Text;

        try
        {
            _status.Text = "Putting the server in place...";
            await Task.Run(() => MeshyInstall.Extract());
            _status.Text = "Saving the key and registering the server...";
            await Task.Run(() =>
            {
                MeshyInstall.WriteKey(key);
                MeshyInstall.RegisterServer();
            });

            _status.ForeColor = GoodColor;
            _status.Text = (was is null ? $"Meshy {MeshyInstall.PackageVersion} is installed." : $"Meshy is updated to {MeshyInstall.PackageVersion}.") +
                           (MeshyInstall.HasKey
                               ? " Restart notecode to use it."
                               : " Add your API key (run this again, or edit the .env) before generating.");
            _install.Visible = false;
            _cancel.Enabled = true;
        }
        catch (Exception ex)
        {
            _status.ForeColor = BadColor;
            _status.Text = "The install failed: " + ex.Message;
            _install.Enabled = true;
            _cancel.Enabled = true;
            _key.Enabled = true;
        }
    }
}
