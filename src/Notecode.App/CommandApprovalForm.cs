namespace Notecode.App;

/// <summary>What the user said in the approval window.</summary>
internal enum CommandApproval
{
    /// <summary>Do not run it.</summary>
    Denied,

    /// <summary>Run it this once.</summary>
    Approved,

    /// <summary>Run it this once, with the whole environment.</summary>
    ApprovedWithEnvironment,

    /// <summary>Run it this once, and run this exact command again without asking.</summary>
    AlwaysApproved,

    /// <summary>Nobody answered in time, so it did not run.</summary>
    TimedOut
}

/// <summary>
/// The window that asks before a command runs.
///
/// The agent wants to run a command and the command is not on the allowlist, so
/// the user is shown it and asked. The command is shown in full, in a font that
/// is meant to be read rather than skimmed, because the whole point of the
/// window is that the user gets to see what is about to happen.
///
/// There are three answers: run it once, run it once and never ask about this
/// exact command again, or do not run it. Escape is "do not run it", and so is
/// the countdown running out - a window that is left alone says no, because a
/// command that runs while nobody is looking is the case this exists for.
///
/// The window is modal on purpose. The agent is waiting for the answer, so
/// nothing else should happen while the question is on screen.
/// </summary>
internal sealed class CommandApprovalForm : Form
{
    private readonly Label _countdown = new();
    private readonly CheckBox _fullEnvironment = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private int _secondsLeft;

    /// <summary>What the user said.</summary>
    public CommandApproval Result { get; private set; } = CommandApproval.Denied;

    /// <summary>
    /// True when the user asked for the command to see the whole environment.
    ///
    /// A command normally runs with a small environment, so that a command
    /// invented by the model cannot read an API key out of it and send it
    /// somewhere. A build that genuinely needs a token needs the real
    /// environment, so the window offers it - one command at a time, and off
    /// by default.
    /// </summary>
    public bool UseFullEnvironment { get; private set; }

    /// <summary>The command, as it will be run.</summary>
    public string Command { get; }

    /// <summary>Where it will run.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Why notecode is asking rather than just running it.</summary>
    public string Reason { get; }

    public CommandApprovalForm(string command, string workingDirectory, string reason, int timeoutSeconds)
    {
        Command = command;
        WorkingDirectory = workingDirectory;
        Reason = reason;

        Text = "notecode - run this command?";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        ClientSize = new Size(720, 360);
        KeyPreview = true;

        BuildLayout();

        _secondsLeft = Math.Max(15, timeoutSeconds);
        _countdown.Text = $"No answer in {_secondsLeft / 60}:{_secondsLeft % 60:00} means no.";
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Finish(CommandApproval.Denied);
            }
        };
    }

    // ------------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Theme.Canvas,
            Padding = new Padding(20, 16, 20, 14)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));

        // ---- the warning
        var head = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Canvas, Margin = Padding.Empty };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        head.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        head.Controls.Add(new Label
        {
            Text = "The agent wants to run a command.",
            Font = Theme.Display,
            ForeColor = Theme.Amber,
            BackColor = Theme.Canvas,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        head.Controls.Add(new Label
        {
            Text = Reason,
            ForeColor = Theme.TextMuted,
            BackColor = Theme.Canvas,
            Dock = DockStyle.Fill,
            AutoEllipsis = true
        }, 0, 1);

        root.Controls.Add(head, 0, 0);

        // ---- the command itself
        var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Canvas, Margin = new Padding(0, 6, 0, 6) };
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        middle.RowStyles.Add(new RowStyle(SizeType.Absolute, 18f));
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        middle.Controls.Add(new Label
        {
            Text = "Command",
            ForeColor = Theme.TextDim,
            BackColor = Theme.Canvas,
            Dock = DockStyle.Fill
        }, 0, 0);

        var commandBox = new TextBox
        {
            Text = Command,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.Code,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            AccessibleName = "The command that will run"
        };
        middle.Controls.Add(commandBox, 0, 1);

        root.Controls.Add(middle, 0, 1);

        // ---- where it runs
        root.Controls.Add(new Label
        {
            Text = "in " + WorkingDirectory,
            ForeColor = Theme.TextDim,
            BackColor = Theme.Canvas,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 2);

        // ---- the environment
        _fullEnvironment.Text = "let it see my environment variables too (it can read my API keys)";
        _fullEnvironment.ForeColor = Theme.TextDim;
        _fullEnvironment.BackColor = Theme.Canvas;
        _fullEnvironment.Dock = DockStyle.Fill;
        _fullEnvironment.AutoSize = false;
        _fullEnvironment.TextAlign = ContentAlignment.MiddleLeft;
        _fullEnvironment.AccessibleName = "Give this command the whole environment";
        root.Controls.Add(_fullEnvironment, 0, 3);

        // ---- the buttons
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Theme.Canvas, Margin = Padding.Empty };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));   // the countdown
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150f)); // always allow
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f)); // deny
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130f)); // run once
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _countdown.ForeColor = Theme.TextDim;
        _countdown.BackColor = Theme.Canvas;
        _countdown.Dock = DockStyle.Fill;
        _countdown.TextAlign = ContentAlignment.MiddleLeft;
        buttons.Controls.Add(_countdown, 0, 0);

        var always = new AccentButton { Text = "Always allow", Height = 32, Dock = DockStyle.Fill, From = Theme.SurfaceRaised, To = Theme.SurfaceRaised };
        always.Click += (s, e) => Finish(CommandApproval.AlwaysApproved);
        buttons.Controls.Add(always, 1, 0);

        var deny = new AccentButton { Text = "Do not run", Height = 32, Dock = DockStyle.Fill, From = Theme.SurfaceRaised, To = Theme.SurfaceRaised };
        deny.Click += (s, e) => Finish(CommandApproval.Denied);
        buttons.Controls.Add(deny, 2, 0);

        var run = new AccentButton { Text = "Run it", Height = 32, Dock = DockStyle.Fill };
        run.Click += (s, e) => Finish(_fullEnvironment.Checked
            ? CommandApproval.ApprovedWithEnvironment
            : CommandApproval.Approved);
        buttons.Controls.Add(run, 3, 0);

        root.Controls.Add(buttons, 0, 4);

        foreach (Control c in root.Controls) c.Margin = new Padding(0, 0, 0, 0);
        Controls.Add(root);

        AcceptButton = run;
        CancelButton = null;
    }

    // ------------------------------------------------------------------ actions

    private void Tick()
    {
        _secondsLeft--;
        if (_secondsLeft <= 0)
        {
            Finish(CommandApproval.TimedOut);
            return;
        }
        _countdown.Text = $"No answer in {_secondsLeft / 60}:{_secondsLeft % 60:00} means no.";
    }

    private void Finish(CommandApproval answer)
    {
        _timer.Stop();
        Result = answer;
        UseFullEnvironment = answer == CommandApproval.ApprovedWithEnvironment;
        DialogResult = answer is CommandApproval.Approved
                       or CommandApproval.ApprovedWithEnvironment
                       or CommandApproval.AlwaysApproved
            ? DialogResult.OK
            : DialogResult.Cancel;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
