using System.Drawing.Drawing2D;

namespace Notecode.App;

/// <summary>
/// The window notecode shows before it opens.
///
/// It looks like a terminal because that is what it is doing: it is scanning the
/// machine and writing down what it found, one line at a time, before the editor
/// comes up. The scan is the slow part of starting on a new machine - it has to
/// ask Windows how much video memory the display adapter really has, and the
/// answer decides which llama.cpp build can run here - so it is worth watching
/// rather than hiding.
///
/// The scan runs off the UI thread. The lines are written as the answers arrive,
/// not after everything is finished, so the window shows the scan happening.
/// </summary>
internal sealed class SplashForm : Form
{
    // A terminal green that is bright without being harsh on black.
    private static readonly Color Terminal = ColorTranslator.FromHtml("#33FF66");
    private static readonly Color TerminalDim = ColorTranslator.FromHtml("#1F9E44");
    private static readonly Color TerminalBright = ColorTranslator.FromHtml("#7CFF9B");
    private static readonly Color TerminalWarn = ColorTranslator.FromHtml("#FFB000");
    private static readonly Color TerminalBad = ColorTranslator.FromHtml("#FF6060");
    private static readonly Color TerminalCyan = ColorTranslator.FromHtml("#5EE7FF");

    private readonly RichTextBox _console;
    private readonly Font _font;
    private readonly AppConfig _cfg;
    private readonly HardwareProfileStore _store;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The profile this machine is running under, once the scan is done.</summary>
    public HardwareProfile? ResolvedProfile { get; private set; }

    /// <summary>What will actually be run, once the scan is done.</summary>
    public LlamaRuntimeOptions? ResolvedOptions { get; private set; }

    /// <summary>True when the scan finished and the app can carry on.</summary>
    public bool Ready { get; private set; }

    public SplashForm(AppConfig cfg, HardwareProfileStore store)
    {
        _cfg = cfg;
        _store = store;

        Text = "notecode - hardware scan";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        ClientSize = new Size(820, 430);
        KeyPreview = true;

        _font = new Font(Theme.CodeFamily, 11f * Theme.FontScale, GraphicsUnit.Point);

        _console = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            ForeColor = Terminal,
            BorderStyle = BorderStyle.None,
            Font = _font,
            ReadOnly = true,
            DetectUrls = false,
            HideSelection = true,
            ScrollBars = RichTextBoxScrollBars.None,
            WordWrap = false,
            TabStop = true,
            AccessibleName = "Hardware scan output"
        };
        _console.MouseEnter += (s, e) => Cursor = Cursors.Default;

        Controls.Add(_console);

        KeyDown += OnKeyDown;
    }

    // ------------------------------------------------------------------ paint

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        // A one-pixel frame, the way a terminal draws its border.
        using var pen = new Pen(ColorTranslator.FromHtml("#12351F"));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    // ------------------------------------------------------------------- scan

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            await WriteAsync("notecode", "hardware scan", TerminalCyan);
            await WriteAsync(new string('-', 74), "", TerminalDim);

            var facts = await Task.Run(() => HardwareScanner.Scan(_cts.Token), _cts.Token);

            await WritePairAsync("os", $"{facts.OsName}  {facts.Architecture}", Terminal);
            await WritePairAsync("cpu", $"{facts.CpuName}  {facts.PhysicalCores} cores / {facts.LogicalProcessors} threads", Terminal);
            await WritePairAsync("memory", $"{facts.RamGiB:0.0} GiB", Terminal);

            var gpu = facts.Gpu;
            if (gpu is null)
            {
                await WritePairAsync("gpu", "none found", TerminalWarn);
            }
            else
            {
                var vram = gpu.SharedMemory
                    ? $"{gpu.VramGiB:0.0} GiB shared"
                    : $"{gpu.VramGiB:0.0} GiB";
                await WritePairAsync("gpu", $"{gpu.FullName}  {vram}  ({gpu.Source})", Terminal);
            }

            if (facts.HasNpu)
            {
                await WritePairAsync("npu", facts.NpuName, TerminalWarn);
                await WriteAsync("note", "llama.cpp has no NPU backend, so this is not used to pick a build", TerminalDim);
            }
            else
            {
                await WritePairAsync("npu", "none", TerminalDim);
            }

            // The profile decides the build. A saved profile for this exact machine
            // wins, because it carries the choices the user already made.
            var profile = _store.FindBySignature(facts.Signature);
            var isNew = profile is null;
            if (profile is null) profile = _store.FindByHostSignature(facts.HostSignature);
            var fromSeed = "";
            if (profile is null)
            {
                profile = new HardwareProfile
                {
                    Name = MakeName(facts),
                    MachineLabel = facts.CpuName,
                    Facts = facts,
                    Backend = LlamaBackend.Auto,
                    Model = _cfg.Llama.Model,
                    Mmproj = _cfg.Llama.Mmproj,
                    CtxSize = _cfg.Llama.Context.CtxSize
                };

                // A machine that has never run notecode can still be described in
                // the seed file, so the setup can be written on one machine and
                // used on another.
                var seeds = HardwareProfileSeed.Load(Path.Combine(_cfg.RuntimeDir, "hardware-profiles.seed.json"));
                var seed = seeds.FirstOrDefault(s => s.Matches(facts));
                if (seed is not null)
                {
                    seed.ApplyTo(profile);
                    fromSeed = ", from the seed";
                }
            }

            profile.Facts = facts;
            profile.Signature = facts.Signature;
            profile.HostSignature = facts.HostSignature;
            profile.MachineLabel = facts.CpuName;
            if (profile.Name.Length == 0) profile.Name = MakeName(facts);

            var options = LlamaRuntimeOptions.Resolve(profile, _cfg.Llama.Builds, _cfg.Llama.ServerExe);
            var backend = BackendSelector.FromKey(options.BackendKey);

            await WritePairAsync("backend",
                $"{options.BackendKey}  -  {BackendSelector.Reason(facts, backend)}", TerminalBright);

            await WritePairAsync("server", options.Exe,
                File.Exists(options.Exe) ? Terminal : TerminalBad);
            if (!File.Exists(options.Exe))
            {
                await WriteAsync("error", "that llama-server is not on this machine", TerminalBad);
            }

            await WritePairAsync("model", $"{Path.GetFileName(profile.Model)}  ({(profile.Model.Length > 0 ? GiB(profile.Model) : "not set")})", Terminal);

            var verdict = MemoryPlanner.Assess(facts, ModelBytes(profile.Model), profile.CtxSize,
                backend, _cfg.Llama.Context.KvBytesPerToken(_cfg.Llama.Context.CacheTypeK));
            await WritePairAsync("memory check", verdict.Summary,
                verdict.Fits ? (verdict.Tight ? TerminalWarn : Terminal) : TerminalBad);
            await WriteAsync(" ", verdict.Detail, TerminalDim);

            // A model that is not on this machine is not applied: a profile can be
            // written on one machine and read on another.
            if (profile.Model.Length > 0 && File.Exists(profile.Model)) _cfg.Llama.Model = profile.Model;
            if (profile.Mmproj.Length > 0 && File.Exists(profile.Mmproj)) _cfg.Llama.Mmproj = profile.Mmproj;

            // Save the profile so the next start on this machine knows what it is.
            profile.IsActive = true;
            _store.Upsert(profile);
            _store.ActiveId = profile.Id;
            foreach (var other in _store.Profiles) other.IsActive = other.Id == profile.Id;
            _store.Save();

            await WritePairAsync("profile", $"{profile.Name}  ({(isNew ? "saved" : "matched")}{fromSeed})", TerminalBright);
            await WriteAsync(new string('-', 74), "", TerminalDim);
            await WriteAsync("starting notecode", "...", Terminal);

            ResolvedProfile = profile;
            ResolvedOptions = options;
            Ready = true;
        }
        catch (OperationCanceledException)
        {
            // The user pressed escape. Carry on with whatever the store knows.
            ResolvedProfile = _store.Active;
            Ready = true;
        }
        catch (Exception ex)
        {
            await WriteAsync("error", ex.Message, TerminalBad);
            await WriteAsync("", "continuing without a hardware profile", TerminalDim);
            ResolvedProfile = _store.Active;
            Ready = true;
        }

        // A beat so the last line is readable before the window goes away.
        await Task.Delay(450);
        Close();
    }

    // ----------------------------------------------------------------- output

    private async Task WritePairAsync(string key, string value, Color valueColor)
    {
        await WriteAsync($"[{key.PadRight(11)}]", "", TerminalDim);
        await WriteAsync(value, "", valueColor);
    }

    /// <summary>
    /// Writes a line the way a terminal prints it: the text appears a few
    /// characters at a time instead of all at once.
    /// </summary>
    private async Task WriteAsync(string left, string right, Color color)
    {
        var text = right.Length > 0 ? left + " " + right : left;
        var chunk = Math.Max(1, text.Length / 12);

        for (int i = 0; i < text.Length; i += chunk)
        {
            var take = Math.Min(chunk, text.Length - i);
            _console.SelectionStart = _console.TextLength;
            _console.SelectionLength = 0;
            _console.SelectionColor = color;
            _console.AppendText(text.Substring(i, take));
            _console.SelectionStart = _console.TextLength;
            _console.SelectionLength = 0;
            _console.ScrollToCaret();
            await Task.Delay(6);
        }

        _console.SelectionStart = _console.TextLength;
        _console.SelectionLength = 0;
        _console.SelectionColor = color;
        _console.AppendText(Environment.NewLine);
        await Task.Delay(18);
    }

    // ------------------------------------------------------------------ input

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Escape skips the scan and uses the profile that is already stored.
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            _cts.Cancel();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
            _font.Dispose();
        }
        base.Dispose(disposing);
    }

    // ----------------------------------------------------------------- helpers

    private static string MakeName(HardwareFacts facts)
    {
        var gpu = facts.Gpu;
        if (gpu is not null && !gpu.SharedMemory && gpu.Name.Length > 0)
            return gpu.Name.Replace(" ", "-").ToLowerInvariant();
        return facts.CpuName.Replace(" ", "-").ToLowerInvariant();
    }

    private static long ModelBytes(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;
            return new FileInfo(path).Length;
        }
        catch { return 0; }
    }

    private static string GiB(string path)
    {
        var bytes = ModelBytes(path);
        if (bytes == 0) return "missing";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.0} GiB";
    }
}
