using System.Diagnostics;

namespace Notecode.Installer;

/// <summary>
/// The installer window.
///
/// It says what is going to happen, where it is going to happen, and then it
/// happens. The models are not part of it: the weights are far too large to
/// ship, so the app is installed with a model to be pointed at afterwards.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
        var uninstall = args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        var silent = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));

        // The folder to install into. It is only there so the check can
        // install somewhere else and prove the installer works without
        // touching the real installation.
        var dirIndex = Array.FindIndex(args, a => a.Equals("--dir", StringComparison.OrdinalIgnoreCase));
        var installDir = Installer.DefaultInstallDir;
        if (dirIndex >= 0 && dirIndex + 1 < args.Length) installDir = Path.GetFullPath(args[dirIndex + 1]);

        if (uninstall)
        {
            if (quiet)
            {
                Installer.Uninstall(installDir, true);
                return;
            }

            var answer = MessageBox.Show(
                "Remove notecode?\n\nThe app, the local llama.cpp runtime and the shortcuts are removed. " +
                "The conversations in %APPDATA%\\notecode are left alone.",
                "notecode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes) Installer.Uninstall(installDir, false);
            return;
        }

        // The silent install is for machines that are being set up without
        // anyone watching: the build pipeline, and a deployment.
        if (silent)
        {
            if (!Installer.HasPayload)
            {
                Console.Error.WriteLine("the payload is missing from the installer");
                return;
            }
            Directory.CreateDirectory(installDir);
            Installer.Extract(installDir);
            Installer.WriteConfig(installDir);
            var self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self))
                File.Copy(self, Path.Combine(installDir, "Uninstall.exe"), true);
            // A test install must not take over the real desktop shortcut.
            if (!args.Any(a => a.Equals("--no-shortcuts", StringComparison.OrdinalIgnoreCase)))
                Installer.CreateShortcuts(installDir, true, true);

            // The entry is filed under the name of the folder it was
            // installed into, so the uninstaller finds the same one.
            Installer.RegisterUninstaller(installDir, Version(), Path.GetFileName(installDir.TrimEnd(Path.DirectorySeparatorChar)));
            Console.WriteLine($"notecode installed in {installDir}");
            return;
        }

        Application.Run(new InstallForm(installDir));
    }

    private static string Version()
    {
        var v = typeof(Program).Assembly.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}

/// <summary>The window the installer is.</summary>
internal sealed class InstallForm : Form
{
    // The colours are named with a suffix, because a field called Text would
    // shadow the Text property of every control set in an object initialiser.
    private static readonly Color AccentColor = ColorTranslator.FromHtml("#8B5CF6");
    private static readonly Color AccentBrightColor = ColorTranslator.FromHtml("#A78BFA");
    private static readonly Color CanvasColor = ColorTranslator.FromHtml("#0B0E17");
    private static readonly Color SurfaceColor = ColorTranslator.FromHtml("#161B27");
    private static readonly Color ForeColorValue = ColorTranslator.FromHtml("#E8ECF5");
    private static readonly Color MutedColor = ColorTranslator.FromHtml("#939DB4");
    private static readonly Color GoodColor = ColorTranslator.FromHtml("#34D399");
    private static readonly Color BadColor = ColorTranslator.FromHtml("#FB7185");

    private readonly string _installDir;
    private readonly CheckBox _desktop = new() { Checked = true, AutoSize = true };
    private readonly CheckBox _startMenu = new() { Checked = true, AutoSize = true };
    private readonly Button _install = new() { Text = "Install", Width = 130, Height = 34 };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 100, Height = 34 };
    private readonly Label _status = new() { AutoSize = false, Height = 20, Dock = DockStyle.Fill };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Height = 8, Dock = DockStyle.Fill };
    private readonly LinkLabel _where = new() { AutoSize = true, LinkColor = AccentBrightColor, ActiveLinkColor = AccentBrightColor };
    private readonly TableLayoutPanel _root = new();

    public InstallForm(string? installDir = null)
    {
        _installDir = installDir ?? Installer.DefaultInstallDir;

        Text = "Install notecode";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = CanvasColor;
        ForeColor = ForeColorValue;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(560, 400);

        BuildLayout();

        _install.Click += async (s, e) => await InstallAsync();
        _cancel.Click += (s, e) => Close();
        _where.LinkClicked += (s, e) => { try { Process.Start("explorer.exe", _installDir); } catch { } };
    }

    private void BuildLayout()
    {
        _root.Dock = DockStyle.Fill;
        _root.ColumnCount = 1;
        _root.RowCount = 6;
        _root.BackColor = CanvasColor;
        _root.Padding = new Padding(26, 20, 26, 14);
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));   // title
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 98f));   // what
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58f));   // options
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));   // where
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // status
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));   // buttons

        var title = new Label
        {
            Text = "notecode",
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            ForeColor = ForeColorValue,
            BackColor = CanvasColor,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _root.Controls.Add(title, 0, 0);

        var what = new Label
        {
            Text =
                "A dual-pane IDE over a local llama.cpp runtime.\n\n" +
                "This installs the app and the llama.cpp server it runs the model with. " +
                "The model itself is not installed: the weights are tens of gigabytes, so the " +
                "app is pointed at one afterwards, in the settings.",
            ForeColor = MutedColor,
            BackColor = CanvasColor,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft
        };
        _root.Controls.Add(what, 0, 1);

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            BackColor = CanvasColor,
            WrapContents = false
        };
        foreach (var box in new[] { _desktop, _startMenu })
        {
            box.Text = box == _desktop ? "Create a shortcut on the desktop" : "Create a shortcut in the start menu";
            box.ForeColor = ForeColorValue;
            box.BackColor = CanvasColor;
            box.Margin = new Padding(0, 2, 0, 6);
            options.Controls.Add(box);
        }
        _root.Controls.Add(options, 0, 2);

        var whereRow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = CanvasColor, WrapContents = false };
        var whereLabel = new Label
        {
            Text = "Install to ",
            AutoSize = true,
            ForeColor = MutedColor,
            BackColor = CanvasColor,
            Margin = new Padding(0, 3, 0, 0)
        };
        _where.Text = _installDir;
        _where.Margin = new Padding(0, 0, 0, 0);
        whereRow.Controls.Add(whereLabel);
        whereRow.Controls.Add(_where);
        _root.Controls.Add(whereRow, 0, 3);

        var statusRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = CanvasColor,
            Margin = Padding.Empty
        };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        statusRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
        statusRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 12f));
        _status.ForeColor = MutedColor;
        _status.BackColor = CanvasColor;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        statusRow.Controls.Add(_status, 0, 0);
        _progress.Margin = new Padding(0, 2, 0, 0);
        statusRow.Controls.Add(_progress, 0, 1);
        _root.Controls.Add(statusRow, 0, 4);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = CanvasColor,
            Margin = Padding.Empty
        };
        StyleButton(_install, AccentColor, Color.Black);
        StyleButton(_cancel, SurfaceColor, ForeColorValue);
        _install.Margin = new Padding(10, 4, 0, 0);
        _cancel.Margin = new Padding(0, 4, 10, 0);
        buttons.Controls.Add(_install);
        buttons.Controls.Add(_cancel);
        _root.Controls.Add(buttons, 0, 5);

        Controls.Add(_root);
    }

    private static void StyleButton(Button b, Color back, Color fore)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.12f);
        b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(back, 0.08f);
        b.BackColor = back;
        b.ForeColor = fore;
        b.Cursor = Cursors.Hand;
        b.UseVisualStyleBackColor = false;
    }

    private async Task InstallAsync()
    {
        if (!Installer.HasPayload)
        {
            MessageBox.Show(this,
                "This installer has no payload in it.\n\n" +
                "The payload is built by scripts/build-installer.ps1. Run that first.",
                "notecode", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _install.Enabled = false;
        _cancel.Enabled = false;
        _progress.Visible = true;
        _progress.Style = ProgressBarStyle.Marquee;
        _status.Text = "Unpacking...";
        await Task.Delay(50);

        try
        {
            Directory.CreateDirectory(_installDir);

            await Task.Run(() =>
            {
                Installer.Extract(_installDir, name => _status.Text = $"Unpacking {name}");
                Installer.WriteConfig(_installDir);
            });

            // The uninstaller is the installer itself with a different flag.
            var self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self))
            {
                File.Copy(self, Path.Combine(_installDir, "Uninstall.exe"), true);
            }

            Installer.CreateShortcuts(_installDir, _desktop.Checked, _startMenu.Checked);
            Installer.RegisterUninstaller(_installDir, Version(), _installDir);

            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Value = 100;
            _status.ForeColor = GoodColor;
            _status.Text = "notecode is installed.";

            var start = new Button { Text = "Start notecode", Width = 140, Height = 34 };
            StyleButton(start, AccentColor, Color.Black);
            start.Click += (s, e) =>
            {
                try { Process.Start(Path.Combine(_installDir, "notecode.exe")); } catch { }
                Close();
            };
            _install.Visible = false;
            _cancel.Text = "Close";
            _cancel.Enabled = true;
            _cancel.Click += (s, e) => Close();
            _root.Controls.Add(start, 0, 5);
            start.BringToFront();
        }
        catch (Exception ex)
        {
            _progress.Visible = false;
            _status.ForeColor = BadColor;
            _status.Text = "The install failed.";
            MessageBox.Show(this,
                $"notecode could not be installed.\n\n{ex.Message}",
                "notecode", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _install.Enabled = true;
            _cancel.Enabled = true;
        }
    }

    private static string Version()
    {
        var v = typeof(InstallForm).Assembly.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
