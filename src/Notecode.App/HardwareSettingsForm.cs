using System.Diagnostics;

namespace Notecode.App;

/// <summary>
/// The hardware half of the settings.
///
/// It answers three questions: what this machine is, which llama.cpp build should
/// run on it, and what the choice did to the numbers. The scan button re-reads the
/// hardware; the checkboxes pick the build; Apply saves the profile and restarts
/// the app, because a llama-server is compiled for one backend and changing the
/// backend means running a different program.
///
/// Every backend can be ticked, including ones this machine cannot run. That is
/// deliberate: the point of the page is to try a configuration, and a refused
/// checkbox cannot be tried. The cost of a bad choice is stated instead of hidden.
/// </summary>
internal sealed class HardwareSettingsForm : Form
{
    private readonly AppConfig _cfg;
    private readonly HardwareProfileStore _store;
    private HardwareProfile _profile;
    private HardwareFacts _facts;
    private bool _loading;

    private readonly Label _status = new();
    private readonly TextBox _factsBox = new();
    private readonly ModernCheckBox _cpu = new();
    private readonly ModernCheckBox _vulkan = new();
    private readonly ModernCheckBox _cuda = new();
    private readonly ModernCheckBox _metal = new();
    private readonly Label _cpuNote = new();
    private readonly Label _vulkanNote = new();
    private readonly Label _cudaNote = new();
    private readonly Label _metalNote = new();
    private readonly TextBox _exe = new();
    private readonly TextBox _model = new();
    private readonly TextBox _mmproj = new();
    private readonly NumericUpDown _ctx = new();
    private readonly NumericUpDown _layers = new();
    private readonly Label _verdict = new();
    private readonly TableLayoutPanel _profileList = new();

    /// <summary>The profile to save, once Apply is pressed.</summary>
    public HardwareProfile? Result { get; private set; }

    /// <summary>True when the user asked for the app to restart under the new profile.</summary>
    public bool RestartRequested { get; private set; }

    public HardwareSettingsForm(AppConfig cfg, HardwareProfileStore store, HardwareProfile profile, HardwareFacts facts)
    {
        _cfg = cfg;
        _store = store;
        _profile = profile;
        _facts = facts;

        Text = "notecode settings - hardware";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        ClientSize = new Size(920, 780);
        MinimumSize = new Size(820, 620);
        KeyPreview = true;

        BuildLayout();
        LoadProfile(_profile);

        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        };
    }

    // ------------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = Theme.Canvas,
            Padding = new Padding(18, 14, 18, 12)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));   // title + scan
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 168f));  // facts
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 138f));  // builds
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150f));  // paths
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // profiles
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));   // buttons

        // ---- title row
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Canvas, Margin = Padding.Empty };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150f));
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        var title = new Label
        {
            Text = "This machine, and the build that should run on it.",
            ForeColor = Theme.TextMuted,
            BackColor = Theme.Canvas,
            Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft
        };
        top.Controls.Add(title, 0, 0);
        var scan = new AccentButton { Text = "Scan hardware", Height = 30, Dock = DockStyle.Fill, From = Theme.SurfaceRaised, To = Theme.SurfaceRaised };
        scan.Click += async (s, e) => await ScanAsync();
        top.Controls.Add(scan, 1, 0);
        root.Controls.Add(top, 0, 0);

        // ---- facts
        var factsPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Canvas, Margin = new Padding(0, 4, 0, 8) };
        factsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        factsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
        factsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        factsPanel.Controls.Add(Caption("This machine"), 0, 0);

        _factsBox.Multiline = true;
        _factsBox.ReadOnly = true;
        _factsBox.BorderStyle = BorderStyle.None;
        _factsBox.BackColor = Theme.Surface;
        _factsBox.ForeColor = Theme.Text;
        _factsBox.Font = Theme.Code;
        _factsBox.Dock = DockStyle.Fill;
        _factsBox.ScrollBars = ScrollBars.Vertical;
        _factsBox.TabStop = true;
        _factsBox.AccessibleName = "Detected hardware";
        factsPanel.Controls.Add(_factsBox, 0, 1);
        root.Controls.Add(factsPanel, 0, 1);

        // ---- builds
        var builds = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = Theme.Canvas, Margin = new Padding(0, 0, 0, 8) };
        builds.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        builds.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
        builds.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
        builds.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
        builds.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
        builds.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
        builds.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
        builds.Controls.Add(Caption("Build - one of these runs the local model"), 0, 0);

        AddBuildRow(builds, 1, _cpu, "CPU only", _cpuNote);
        AddBuildRow(builds, 2, _vulkan, "Vulkan", _vulkanNote);
        AddBuildRow(builds, 3, _cuda, "CUDA (NVIDIA)", _cudaNote);
        AddBuildRow(builds, 4, _metal, "Metal (macOS)", _metalNote);

        _verdict.ForeColor = Theme.TextMuted;
        _verdict.BackColor = Theme.Canvas;
        _verdict.Font = Theme.UiSmall;
        _verdict.Dock = DockStyle.Fill;
        _verdict.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _verdict.AutoEllipsis = true;
        builds.Controls.Add(_verdict, 0, 5);
        root.Controls.Add(builds, 0, 2);

        // ---- paths
        var paths = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 5, BackColor = Theme.Canvas, Margin = new Padding(0, 0, 0, 8) };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132f));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));
        paths.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
        paths.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        paths.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        paths.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        paths.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        paths.Controls.Add(Caption("Model files and context"), 0, 0);

        paths.Controls.Add(Caption("llama-server"), 0, 1);
        StyleInput(_exe);
        _exe.ReadOnly = true;
        _exe.BackColor = Theme.Surface;
        _exe.ForeColor = Theme.TextDim;
        paths.Controls.Add(_exe, 1, 1);

        paths.Controls.Add(Caption("model"), 0, 2);
        StyleInput(_model);
        paths.Controls.Add(_model, 1, 2);
        var modelBrowse = new AccentButton { Text = "Browse", Dock = DockStyle.Fill, From = Theme.SurfaceRaised, To = Theme.SurfaceRaised, Margin = new Padding(6, 3, 0, 3) };
        modelBrowse.Click += (s, e) => BrowseModel(_model);
        paths.Controls.Add(modelBrowse, 2, 2);

        paths.Controls.Add(Caption("projector"), 0, 3);
        StyleInput(_mmproj);
        paths.Controls.Add(_mmproj, 1, 3);
        var mmBrowse = new AccentButton { Text = "Browse", Dock = DockStyle.Fill, From = Theme.SurfaceRaised, To = Theme.SurfaceRaised, Margin = new Padding(6, 3, 0, 3) };
        mmBrowse.Click += (s, e) => BrowseModel(_mmproj);
        paths.Controls.Add(mmBrowse, 2, 3);

        paths.Controls.Add(Caption("context"), 0, 4);
        _ctx.Minimum = 1024;
        _ctx.Maximum = 262144;
        _ctx.Increment = 1024;
        _ctx.ThousandsSeparator = true;
        _ctx.BackColor = Theme.SurfaceRaised;
        _cfg.Llama.Context.CtxSize = _cfg.Llama.Context.CtxSize;
        _ctx.Value = Math.Clamp(_cfg.Llama.Context.CtxSize, 1024, 262144);
        _ctx.Font = Theme.Ui;
        _ctx.Dock = DockStyle.Fill;
        _ctx.Margin = new Padding(0, 3, 6, 3);
        _ctx.ValueChanged += (s, e) => UpdateVerdict();
        paths.Controls.Add(_ctx, 1, 4);

        paths.Controls.Add(Caption("gpu layers"), 2, 4);
        _layers.Minimum = 0;
        _layers.Maximum = 999;
        _layers.BackColor = Theme.SurfaceRaised;
        _layers.Font = Theme.Ui;
        _layers.Dock = DockStyle.Fill;
        _layers.Margin = new Padding(0, 3, 0, 3);
        _layers.ValueChanged += (s, e) => UpdateVerdict();
        paths.Controls.Add(_layers, 3, 4);
        root.Controls.Add(paths, 0, 3);

        // ---- saved profiles
        var profiles = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Canvas, Margin = new Padding(0, 0, 0, 8) };
        profiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        profiles.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
        profiles.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        profiles.Controls.Add(Caption("Saved profiles - click one to load it"), 0, 0);

        _profileList.Dock = DockStyle.Fill;
        _profileList.AutoScroll = true;
        _profileList.BackColor = Theme.Canvas;
        _profileList.ColumnCount = 1;
        _profileList.RowCount = 1;
        _profileList.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _profileList.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _profileList.Margin = Padding.Empty;
        profiles.Controls.Add(_profileList, 0, 1);
        root.Controls.Add(profiles, 0, 4);

        // ---- buttons
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Theme.Canvas, Margin = Padding.Empty };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170f));
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _status.ForeColor = Theme.TextMuted;
        _status.BackColor = Theme.Canvas;
        _status.Font = Theme.UiSmall;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;
        buttons.Controls.Add(_status, 0, 0);

        var cancel = new AccentButton { Text = "Cancel", Height = 32, Dock = DockStyle.Fill, From = Theme.SurfaceRaised, To = Theme.SurfaceRaised };
        cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(cancel, 1, 0);

        var apply = new AccentButton { Text = "Apply and restart", Height = 32, Dock = DockStyle.Fill };
        apply.Click += (s, e) => Apply();
        buttons.Controls.Add(apply, 2, 0);
        root.Controls.Add(buttons, 0, 5);

        foreach (Control c in root.Controls) c.Margin = new Padding(0, 0, 0, 0);

        Controls.Add(root);
    }

    private void AddBuildRow(TableLayoutPanel parent, int row, ModernCheckBox box, string text, Label note)
    {
        var line = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Canvas, Margin = Padding.Empty };
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210f));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        line.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        box.Text = text;
        box.Dock = DockStyle.Fill;
        box.Margin = Padding.Empty;
        box.CheckedChanged += BuildCheckedChanged;
        line.Controls.Add(box, 0, 0);

        note.ForeColor = Theme.TextDim;
        note.BackColor = Theme.Canvas;
        note.Font = Theme.UiSmall;
        note.Dock = DockStyle.Fill;
        note.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        note.AutoEllipsis = true;
        line.Controls.Add(note, 1, 0);

        parent.Controls.Add(line, 0, row);
    }

    private static Label Caption(string text) => new()
    {
        Text = text,
        ForeColor = Theme.TextMuted,
        BackColor = Theme.Canvas,
        Dock = DockStyle.Fill,
        TextAlign = System.Drawing.ContentAlignment.MiddleLeft
    };

    private static void StyleInput(TextBox box)
    {
        box.BorderStyle = BorderStyle.None;
        box.BackColor = Theme.SurfaceRaised;
        box.ForeColor = Theme.Text;
        box.Font = Theme.Ui;
        box.Dock = DockStyle.Fill;
        box.Margin = new Padding(0, 3, 6, 3);
    }

    // ------------------------------------------------------------------- state

    /// <summary>Loads a profile into the page.</summary>
    private void LoadProfile(HardwareProfile profile)
    {
        _loading = true;

        _profile = profile;
        _model.Text = profile.Model;
        _mmproj.Text = profile.Mmproj;
        _ctx.Value = Math.Clamp(profile.CtxSize > 0 ? profile.CtxSize : _cfg.Llama.Context.CtxSize, 1024, 262144);
        _layers.Value = Math.Clamp(profile.NGpuLayers, 0, 999);

        SetBackendCheckboxes(profile.Backend);
        RefreshFacts();
        RefreshBuildNotes();
        UpdateVerdict();
        RefreshProfileList();

        _loading = false;
    }

    private void SetBackendCheckboxes(LlamaBackend backend)
    {
        _cpu.Checked = backend is LlamaBackend.Cpu or LlamaBackend.Auto;
        _vulkan.Checked = backend == LlamaBackend.Vulkan;
        _cuda.Checked = backend == LlamaBackend.Cuda;
        _metal.Checked = backend == LlamaBackend.Metal;
    }

    private void BuildCheckedChanged(object? sender, EventArgs e)
    {
        if (_loading) return;

        // They are checkboxes because that is what was asked for, but only one can
        // be true: the machine runs one build.
        var checkedBox = sender as ModernCheckBox;
        if (checkedBox is { Checked: true })
        {
            _loading = true;
            foreach (var other in new[] { _cpu, _vulkan, _cuda, _metal })
            {
                if (!ReferenceEquals(other, checkedBox)) other.Checked = false;
            }
            _loading = false;
        }

        _profile.Backend = SelectedBackend();
        RefreshBuildNotes();
        UpdateVerdict();
    }

    private LlamaBackend SelectedBackend()
    {
        if (_vulkan.Checked) return LlamaBackend.Vulkan;
        if (_cuda.Checked) return LlamaBackend.Cuda;
        if (_metal.Checked) return LlamaBackend.Metal;
        return LlamaBackend.Cpu;
    }

    private void RefreshFacts()
    {
        var f = _facts;
        var gpu = f.Gpu;
        var lines = new List<string>
        {
            $"cpu        {f.CpuName}",
            $"cores      {f.PhysicalCores} physical / {f.LogicalProcessors} logical",
            $"memory     {f.RamGiB:0.0} GiB",
            $"os         {f.OsName} {f.Architecture}",
            $"gpu        {(gpu is null ? "none found" : gpu.FullName)}",
            $"vram       {(gpu is null ? "-" : gpu.VramGiB.ToString("0.0") + " GiB" + (gpu.SharedMemory ? " shared" : ""))}",
            $"source     {(gpu is null ? "-" : gpu.Source)}",
            $"npu        {(f.HasNpu ? f.NpuName : "none")}"
        };
        _factsBox.Lines = lines.ToArray();
    }

    private void RefreshBuildNotes()
    {
        NoteBuild(_cpu, _cpuNote, LlamaBackend.Cpu);
        NoteBuild(_vulkan, _vulkanNote, LlamaBackend.Vulkan);
        NoteBuild(_cuda, _cudaNote, LlamaBackend.Cuda);
        NoteBuild(_metal, _metalNote, LlamaBackend.Metal);
    }

    private void NoteBuild(ModernCheckBox box, Label note, LlamaBackend backend)
    {
        var reason = BackendSelector.UnavailableReason(backend, _facts, _cfg.Llama.Builds);
        if (reason is null)
        {
            note.ForeColor = Theme.TextDim;
            note.Text = "available";
        }
        else
        {
            note.ForeColor = Theme.Amber;
            note.Text = reason.Replace("\n", " ");
        }

        // The executable this build would run, so the page says which file is which.
        if (_cfg.Llama.Builds.TryGetValue(BackendSelector.Key(backend), out var build))
        {
            // The accessible name is the label the user reads, not the key of the
            // build: "CPU only" tells a screen reader user more than "cpu build".
            box.AccessibleName = box.Text;
            note.Text = note.Text == "available"
                ? $"available  -  {Path.GetFileName(build.Exe)}"
                : note.Text;
        }
        else
        {
            box.AccessibleName = box.Text;
        }
    }

    private void UpdateVerdict()
    {
        var backend = SelectedBackend();
        var modelPath = _model.Text.Trim();
        var modelBytes = 0L;
        try { if (File.Exists(modelPath)) modelBytes = new FileInfo(modelPath).Length; } catch { }

        var verdict = MemoryPlanner.Assess(_facts, modelBytes, (int)_ctx.Value, backend,
            _cfg.Llama.Context.KvBytesPerToken(_cfg.Llama.Context.CacheTypeK));

        _verdict.Text = $"memory check: {verdict.Summary}  -  {verdict.Detail}";
        _verdict.ForeColor = verdict.Fits ? (verdict.Tight ? Theme.Amber : Theme.Mint) : Theme.Rose;

        // Show the executable the chosen build would run.
        var key = BackendSelector.Key(backend);
        _exe.Text = _cfg.Llama.Builds.TryGetValue(key, out var build)
            ? build.Exe
            : _cfg.Llama.ServerExe;

        if (_loading) return;
        _profile.Backend = backend;
        _profile.CtxSize = (int)_ctx.Value;
        _profile.NGpuLayers = (int)_layers.Value;
    }

    private void RefreshProfileList()
    {
        _profileList.Controls.Clear();
        _profileList.RowCount = 0;
        _profileList.RowStyles.Clear();

        if (_store.Profiles.Count == 0)
        {
            _profileList.RowCount = 1;
            _profileList.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
            var empty = new Label
            {
                Text = "No profiles saved yet. Press Scan hardware, then Apply.",
                ForeColor = Theme.TextDim,
                BackColor = Theme.Canvas,
                Font = Theme.UiSmall,
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            };
            _profileList.Controls.Add(empty, 0, 0);
            return;
        }

        foreach (var profile in _store.Profiles)
        {
            var row = new ProfileRow(profile, _profile.Id == profile.Id);
            row.Picked += (s, e) => LoadProfile(profile);
            _profileList.RowCount++;
            _profileList.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            _profileList.Controls.Add(row, 0, _profileList.RowCount - 1);
        }

        // A filler row so a couple of profiles do not stretch over the whole panel.
        _profileList.RowCount++;
        _profileList.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _profileList.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas }, 0, _profileList.RowCount - 1);
    }

    // ----------------------------------------------------------------- actions

    private async Task ScanAsync()
    {
        _status.ForeColor = Theme.Amber;
        _status.Text = "scanning...";
        _cpu.Enabled = _vulkan.Enabled = _cuda.Enabled = _metal.Enabled = false;

        try
        {
            _facts = await Task.Run(() => HardwareScanner.Scan());

            // A scan of a different machine is a different profile.
            var match = _store.FindBySignature(_facts.Signature);
            if (match is not null && match.Id != _profile.Id)
            {
                _profile = match.Clone();
                _profile.Backend = SelectedBackend();
            }
            else
            {
                _profile.Facts = _facts;
                _profile.Signature = _facts.Signature;
                _profile.HostSignature = _facts.HostSignature;
                _profile.MachineLabel = _facts.CpuName;
                if (_profile.Name.Length == 0) _profile.Name = _facts.CpuName.Replace(" ", "-").ToLowerInvariant();
            }

            RefreshFacts();
            RefreshBuildNotes();
            UpdateVerdict();

            var suggestion = BackendSelector.Choose(_facts, _cfg.Llama.Builds);
            _status.ForeColor = Theme.Mint;
            _status.Text = $"scan complete - the scan would pick {BackendSelector.Key(suggestion)}";
        }
        catch (Exception ex)
        {
            _status.ForeColor = Theme.Rose;
            _status.Text = ex.Message;
        }
        finally
        {
            _cpu.Enabled = _vulkan.Enabled = _cuda.Enabled = _metal.Enabled = true;
        }
    }

    private void BrowseModel(TextBox target)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Pick a model file",
            Filter = "GGUF models (*.gguf)|*.gguf|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (Directory.Exists(@"C:\models")) dlg.InitialDirectory = @"C:\models";
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        target.Text = dlg.FileName;
        UpdateVerdict();
    }

    private void Apply()
    {
        var backend = SelectedBackend();
        var key = BackendSelector.Key(backend);

        // Say what is going to happen before it happens: a build that is not
        // compiled, or a backend the hardware cannot run, is a choice the user
        // may be making on purpose to test something.
        var reason = BackendSelector.UnavailableReason(backend, _facts, _cfg.Llama.Builds);
        if (reason is not null)
        {
            var answer = MessageBox.Show(this,
                $"{reason}\n\nApply it anyway? The server will not start if the build is missing.",
                "notecode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }

        _profile.Backend = backend;
        _profile.Model = _model.Text.Trim();
        _profile.Mmproj = _mmproj.Text.Trim();
        _profile.CtxSize = (int)_ctx.Value;
        _profile.NGpuLayers = (int)_layers.Value;
        _profile.Facts = _facts;
        _profile.Signature = _facts.Signature;
        _profile.HostSignature = _facts.HostSignature;
        _profile.MachineLabel = _facts.CpuName;
        if (_profile.Name.Length == 0) _profile.Name = _facts.CpuName.Replace(" ", "-").ToLowerInvariant();

        Result = _profile;
        RestartRequested = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Theme.ApplyWindowChrome(this);
    }
}

/// <summary>One saved profile, as a row in the settings page.</summary>
internal sealed class ProfileRow : Panel
{
    public event EventHandler? Picked;

    private readonly bool _current;

    public ProfileRow(HardwareProfile profile, bool current)
    {
        _current = current;
        Dock = DockStyle.Fill;
        Height = 34;
        Margin = Padding.Empty;
        BackColor = current ? Theme.SurfaceRaised : Theme.Canvas;
        Cursor = Cursors.Hand;
        AccessibleName = $"Profile {profile.Name}";
        AccessibleRole = AccessibleRole.PushButton;

        var name = new Label
        {
            Text = profile.Name,
            ForeColor = current ? Theme.VioletBright : Theme.Text,
            BackColor = BackColor,
            Font = Theme.UiSemibold,
            Dock = DockStyle.Left,
            Width = 190,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            Margin = new Padding(8, 0, 0, 0)
        };

        var backend = new Label
        {
            Text = BackendSelector.Key(profile.Backend),
            ForeColor = Theme.Cyan,
            BackColor = BackColor,
            Font = Theme.CodeSmall,
            Dock = DockStyle.Left,
            Width = 80,
            AutoSize = false,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft
        };

        var machine = new Label
        {
            Text = profile.MachineLabel,
            ForeColor = Theme.TextMuted,
            BackColor = BackColor,
            Font = Theme.UiSmall,
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        Controls.Add(machine);
        Controls.Add(backend);
        Controls.Add(name);

        Click += (s, e) => Picked?.Invoke(this, EventArgs.Empty);
        name.Click += (s, e) => Picked?.Invoke(this, EventArgs.Empty);
        backend.Click += (s, e) => Picked?.Invoke(this, EventArgs.Empty);
        machine.Click += (s, e) => Picked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!_current) BackColor = Theme.Surface;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_current) BackColor = Theme.Canvas;
    }
}
