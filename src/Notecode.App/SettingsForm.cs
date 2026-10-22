using System.Text.Json.Nodes;

namespace Notecode.App;

/// <summary>
/// What the settings window needs from the main window: things only the main
/// window can do, handed in so the form does not reach into it.
/// </summary>
internal sealed record SettingsHost(
    Func<Task<List<string>>> LoadToolNames,
    Func<Task> RestartServer,
    Func<Task> OpenHardware,
    Action<string> OpenFile,
    Action<int> ContextChanged,
    Action Applied,
    Action ServerSettingsPending);

/// <summary>
/// Every setting of the app that can be changed while it runs, in one window.
///
/// The pages are the Settings menu: each menu item opens this window on its page.
/// Save writes notecode.config.json (keeping the rest of the file), the provider
/// list and the agent instructions, and applies what can be applied at once. The
/// settings that belong to llama-server take effect when it restarts, and the
/// window offers to do that.
/// </summary>
internal sealed class SettingsForm : Form
{
    public const string ProvidersPage = "Providers";
    public const string AgentPage = "Agent";
    public const string ModelPage = "Model & reasoning";
    public const string PerformancePage = "Performance";
    public const string ChatPage = "Chat & tools";
    public const string McpPage = "MCP servers";
    public const string EditorPage = "Editor & appearance";

    public static readonly string[] Pages =
        { ProvidersPage, AgentPage, ModelPage, PerformancePage, ChatPage, McpPage, EditorPage };

    private readonly AppConfig _cfg;
    private readonly ProviderStore _providers;
    private readonly SettingsHost _host;

    private readonly ListBox _nav = new();
    private readonly Panel _pageHost = new();
    private readonly Dictionary<string, Control> _pages = new();
    private readonly Dictionary<string, Action> _onShow = new();
    private readonly Label _status = new();

    // ---- providers
    private readonly ListBox _providerList = new();
    private readonly Label _providerInfo = new();
    private readonly ComboBox _providerModel = new();
    private string _activeProvider;

    // ---- agent
    private readonly TextBox _agentText = new();
    private readonly ModernCheckBox _agentSend = new();
    private readonly Label _agentCost = new();
    private string _agentLoaded = "";

    // ---- model & reasoning
    private readonly RadioButton _reasonOff = new();
    private readonly RadioButton _reasonAuto = new();
    private readonly RadioButton _reasonOn = new();
    private readonly NumericUpDown _temperature = new();
    private readonly NumericUpDown _maxTokens = new();
    private readonly ModernCheckBox _vision = new();
    private readonly ComboBox _quickProvider = new();
    private readonly ComboBox _quickModel = new();

    // ---- performance
    private readonly NumericUpDown _ctx = new();
    private readonly NumericUpDown _threads = new();
    private readonly NumericUpDown _threadsBatch = new();
    private readonly ComboBox _cacheK = new();
    private readonly ComboBox _cacheV = new();
    private readonly NumericUpDown _cacheReuse = new();
    private readonly ModernCheckBox _mlock = new();
    private readonly Label _kvCost = new();

    // ---- chat & tools
    private readonly CheckedListBox _tools = new();
    private readonly ModernCheckBox _editorContext = new();
    private readonly NumericUpDown _editorChars = new();
    private readonly NumericUpDown _toolResultChars = new();
    private readonly NumericUpDown _historyMessages = new();
    private readonly NumericUpDown _historyChars = new();
    private readonly NumericUpDown _maxToolTurns = new();
    private bool _toolsLoaded;

    // ---- mcp
    private readonly List<(string Name, ModernCheckBox Enabled, ModernCheckBox OnDemand, NumericUpDown Timeout)> _mcpRows = new();
    private readonly ComboBox _reaProvider = new();
    private readonly TextBox _reaModel = new();

    // ---- editor & appearance
    private readonly NumericUpDown _fontScale = new();
    private readonly TextBox _editorFont = new();
    private readonly NumericUpDown _editorSize = new();
    private readonly NumericUpDown _tabSize = new();
    private readonly ModernCheckBox _wrap = new();

    public SettingsForm(AppConfig cfg, ProviderStore providers, SettingsHost host, string page)
    {
        _cfg = cfg;
        _providers = providers;
        _host = host;
        _activeProvider = providers.ActiveProvider().Name;

        Text = "notecode settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        ClientSize = new Size(980, 700);
        MinimumSize = new Size(820, 560);
        KeyPreview = true;
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };

        BuildLayout();
        BuildProvidersPage();
        BuildAgentPage();
        BuildModelPage();
        BuildPerformancePage();
        BuildChatPage();
        BuildMcpPage();
        BuildEditorPage();

        _nav.SelectedIndex = Math.Max(0, Array.IndexOf(Pages, page));
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Theme.Canvas };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200f));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f));

        _nav.Dock = DockStyle.Fill;
        _nav.BorderStyle = BorderStyle.None;
        _nav.BackColor = Theme.Surface;
        _nav.ForeColor = Theme.Text;
        _nav.Font = Theme.Ui;
        _nav.IntegralHeight = false;
        _nav.DrawMode = DrawMode.OwnerDrawFixed;
        _nav.ItemHeight = 34;
        _nav.AccessibleName = "Settings pages";
        _nav.Items.AddRange(Pages);
        _nav.DrawItem += (s, e) => DrawListItem(_nav, e, Pages[e.Index], null);
        _nav.SelectedIndexChanged += (s, e) => ShowPage(Pages[Math.Max(0, _nav.SelectedIndex)]);
        root.Controls.Add(_nav, 0, 0);

        _pageHost.Dock = DockStyle.Fill;
        _pageHost.BackColor = Theme.Canvas;
        _pageHost.Padding = new Padding(22, 16, 22, 8);
        root.Controls.Add(_pageHost, 1, 0);

        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Theme.Surface, Padding = new Padding(12, 9, 12, 9) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100f));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200f));
        _status.Dock = DockStyle.Fill;
        _status.ForeColor = Theme.TextMuted;
        _status.Font = Theme.UiSmall;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;
        bar.Controls.Add(_status, 0, 0);

        var cancel = Button("Cancel", secondary: true);
        cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        bar.Controls.Add(cancel, 1, 0);

        var save = Button("Save");
        save.Click += async (s, e) => await SaveAsync(restart: false);
        bar.Controls.Add(save, 2, 0);

        var restart = Button("Save and restart server");
        restart.Click += async (s, e) => await SaveAsync(restart: true);
        bar.Controls.Add(restart, 3, 0);

        root.Controls.Add(bar, 0, 1);
        root.SetColumnSpan(bar, 2);
        Controls.Add(root);
    }

    private void ShowPage(string name)
    {
        _pageHost.SuspendLayout();
        _pageHost.Controls.Clear();
        if (_pages.TryGetValue(name, out var page)) _pageHost.Controls.Add(page);
        _pageHost.ResumeLayout();
        if (_onShow.TryGetValue(name, out var refresh)) refresh();
    }

    // ------------------------------------------------------------------ pages

    private void BuildProvidersPage()
    {
        var page = new PageBuilder("Providers",
            "Where a chat turn goes. The active provider answers every turn; /rea needs a cloud one. " +
            "API keys are encrypted with Windows DPAPI and never shown here.");

        _providerList.BorderStyle = BorderStyle.None;
        _providerList.BackColor = Theme.Surface;
        _providerList.ForeColor = Theme.Text;
        _providerList.DrawMode = DrawMode.OwnerDrawFixed;
        _providerList.ItemHeight = 30;
        _providerList.IntegralHeight = false;
        _providerList.AccessibleName = "Providers";
        _providerList.DrawItem += (s, e) =>
        {
            if (e.Index < 0 || e.Index >= _providers.Providers.Count) return;
            var p = _providers.Providers[e.Index];
            var tags = new List<string>();
            if (p.Name == _activeProvider) tags.Add("active");
            if (!p.IsLocal) tags.Add(p.HasToken ? "key set" : "no key");
            DrawListItem(_providerList, e, p.Label, string.Join(" · ", tags));
        };
        _providerList.SelectedIndexChanged += (s, e) => ShowProvider();
        page.Full(_providerList, 190);

        _providerInfo.ForeColor = Theme.TextMuted;
        _providerInfo.Font = Theme.UiSmall;
        _providerInfo.AutoSize = false;
        page.Full(_providerInfo, 44);

        StyleCombo(_providerModel, editable: true);
        _providerModel.AccessibleName = "Model of the selected provider";
        _providerModel.TextChanged += (s, e) =>
        {
            var p = SelectedProvider();
            if (p is not null && !p.IsLocal && _providerModel.Text.Trim().Length > 0) p.Model = _providerModel.Text.Trim();
        };
        page.Row("Model", _providerModel, "Pick one, or type a model id the provider offers.");

        var buttons = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Canvas, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        var makeActive = Button("Make active", width: 120);
        makeActive.Click += (s, e) =>
        {
            var p = SelectedProvider();
            if (p is null) return;
            if (!p.IsLocal && !p.HasToken) { Say($"{p.Label} has no API key yet. Use Edit / API key first.", warn: true); return; }
            _activeProvider = p.Name;
            _providerList.Invalidate();
            Say($"{p.Label} will answer the next turn once you save.");
        };
        var edit = Button("Edit / API key...", secondary: true, width: 150);
        edit.Click += (s, e) => EditProvider(SelectedProvider());
        var add = Button("Add...", secondary: true, width: 90);
        add.Click += (s, e) => EditProvider(null);
        var remove = Button("Remove", secondary: true, width: 90);
        remove.Click += (s, e) => RemoveProvider();
        buttons.Controls.AddRange(new Control[] { makeActive, edit, add, remove });
        page.Full(buttons, 44);

        _pages[ProvidersPage] = page.Root;
        _onShow[ProvidersPage] = () => RefreshProviderList(SelectedProvider()?.Name ?? _activeProvider);
    }

    private void BuildAgentPage()
    {
        var file = AgentFilePath();
        var page = new PageBuilder("Agent",
            $"The instructions the model gets on every turn, from {file}. Keep them short: every token here " +
            "is read again for every token the model writes.");

        _agentSend.Text = "Send these instructions with every turn";
        _agentSend.Checked = _cfg.Chat.SendInstructions;
        _agentSend.AutoSize = true;
        page.Full(_agentSend, 30);

        _agentText.Multiline = true;
        _agentText.AcceptsReturn = true;
        _agentText.AcceptsTab = true;
        _agentText.ScrollBars = ScrollBars.Vertical;
        _agentText.BorderStyle = BorderStyle.None;
        _agentText.BackColor = Theme.Surface;
        _agentText.ForeColor = Theme.Text;
        _agentText.Font = Theme.Code;
        _agentText.AccessibleName = "Agent instructions";
        _agentLoaded = File.Exists(file) ? File.ReadAllText(file) : "";
        _agentText.Text = _agentLoaded;
        _agentText.TextChanged += (s, e) => UpdateAgentCost();
        page.Full(_agentText, 330);

        _agentCost.Font = Theme.UiSmall;
        _agentCost.AutoSize = false;
        page.Full(_agentCost, 22);

        var buttons = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Canvas };
        var reload = Button("Reload from disk", secondary: true, width: 150);
        reload.Click += (s, e) =>
        {
            _agentLoaded = File.Exists(file) ? File.ReadAllText(file) : "";
            _agentText.Text = _agentLoaded;
        };
        var open = Button("Open in editor", secondary: true, width: 140);
        open.Click += (s, e) => { if (File.Exists(file)) _host.OpenFile(file); else Say("Save once to create the file first.", warn: true); };
        buttons.Controls.AddRange(new Control[] { reload, open });
        page.Full(buttons, 44);

        UpdateAgentCost();
        _pages[AgentPage] = page.Root;
    }

    private void BuildModelPage()
    {
        var page = new PageBuilder("Model & reasoning", "Which model answers, and how it thinks.");

        page.Section("Model");
        StyleCombo(_quickProvider, editable: false);
        _quickProvider.AccessibleName = "Active provider";
        _quickProvider.SelectedIndexChanged += (s, e) =>
        {
            if (_quickProvider.SelectedIndex < 0) return;
            var p = _providers.Providers[_quickProvider.SelectedIndex];
            if (!p.IsLocal && !p.HasToken)
            {
                Say($"{p.Label} has no API key yet. Add it on the Providers page.", warn: true);
                return;
            }
            _activeProvider = p.Name;
            FillModels(_quickModel, p);
        };
        page.Row("Provider", _quickProvider, "The provider that answers every turn.");

        StyleCombo(_quickModel, editable: true);
        _quickModel.AccessibleName = "Model";
        _quickModel.TextChanged += (s, e) =>
        {
            var p = _providers.Find(_activeProvider);
            if (p is not null && !p.IsLocal && _quickModel.Text.Trim().Length > 0) p.Model = _quickModel.Text.Trim();
        };
        page.Row("Model", _quickModel, "The local model file itself is chosen on Settings > Hardware.");

        page.Section("Reasoning (local model)");
        var reasoning = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Canvas, Margin = Padding.Empty };
        StyleRadio(_reasonOff, "Off - fastest");
        StyleRadio(_reasonAuto, "Auto - the model decides");
        StyleRadio(_reasonOn, "On - thinks first");
        reasoning.Controls.AddRange(new Control[] { _reasonOff, _reasonAuto, _reasonOn });
        var mode = _cfg.Llama.Agent.Reasoning;
        _reasonOn.Checked = mode == "on";
        _reasonAuto.Checked = mode == "auto";
        _reasonOff.Checked = !_reasonOn.Checked && !_reasonAuto.Checked;
        page.Row("Thinking", reasoning,
            "Thinking writes hundreds of tokens before the answer: at ~10 tok/s that is tens of seconds a turn. " +
            "Applies when the server restarts.");

        page.Section("Sampling");
        StyleNumber(_temperature, 0, 2, 0.1m, decimals: 2);
        _temperature.Value = (decimal)Math.Clamp(_cfg.Llama.Agent.Temperature, 0, 2);
        page.Row("Temperature", _temperature, "Lower is steadier, higher is more varied. 0.2-0.4 suits code.");

        StyleNumber(_maxTokens, 0, 32768, 256);
        _maxTokens.Value = Math.Clamp(_cfg.Llama.Agent.MaxTokens, 0, 32768);
        page.Row("Answer cap (tokens)", _maxTokens,
            "Longest local answer. 0 is no cap. Under ~2048, a save_as of a whole file can be cut off.");

        page.Section("Vision");
        _vision.Text = "Load the vision projector, so the local model can see attached pictures";
        _vision.Checked = _cfg.Llama.LoadMmproj;
        _vision.AutoSize = true;
        page.Row("Pictures", _vision, "Costs 1.1 GiB of RAM, which this machine needs for the model. Applies on restart.");

        _pages[ModelPage] = page.Root;
        _onShow[ModelPage] = () =>
        {
            _quickProvider.Items.Clear();
            foreach (var p in _providers.Providers) _quickProvider.Items.Add(p.Label);
            var index = _providers.Providers.FindIndex(p => p.Name == _activeProvider);
            _quickProvider.SelectedIndex = Math.Max(0, index);
            var active = _providers.Find(_activeProvider);
            if (active is not null) FillModels(_quickModel, active);
        };
    }

    private void BuildPerformancePage()
    {
        var page = new PageBuilder("Performance",
            "How llama-server runs the local model. These apply when the server restarts - use Save and restart server.");

        page.Section("Context");
        StyleNumber(_ctx, 2048, 131072, 1024);
        _ctx.Value = Math.Clamp(_cfg.Llama.Context.CtxSize, 2048, 131072);
        _ctx.ValueChanged += (s, e) => UpdateKvCost();
        page.Row("Context size (tokens)", _ctx,
            "The most the model can hold at once. A normal turn is ~2-4k; bigger only costs RAM.");

        StyleCombo(_cacheK, editable: false);
        StyleCombo(_cacheV, editable: false);
        foreach (var c in new[] { _cacheK, _cacheV }) c.Items.AddRange(new object[] { "f16", "q8_0", "q4_0" });
        _cacheK.SelectedItem = _cfg.Llama.Context.CacheTypeK;
        _cacheV.SelectedItem = _cfg.Llama.Context.CacheTypeV;
        _cacheK.SelectedIndexChanged += (s, e) => UpdateKvCost();
        page.Row("KV cache, keys", _cacheK, "q8_0 halves the cache against f16; q4_0 halves it again and costs some quality.");
        page.Row("KV cache, values", _cacheV, null);

        _kvCost.Font = Theme.UiSmall;
        _kvCost.ForeColor = Theme.TextMuted;
        _kvCost.AutoSize = false;
        page.Full(_kvCost, 22);

        page.Section("Threads");
        var cores = Environment.ProcessorCount;
        StyleNumber(_threads, 1, cores, 1);
        _threads.Value = Math.Clamp(_cfg.Llama.Cpu.Threads, 1, cores);
        page.Row("Decode threads", _threads, "One per physical core is usually fastest for writing tokens.");
        StyleNumber(_threadsBatch, 1, cores, 1);
        _threadsBatch.Value = Math.Clamp(_cfg.Llama.Cpu.ThreadsBatch, 1, cores);
        page.Row("Prompt threads", _threadsBatch, "Reading the prompt is batched work; every logical core helps.");

        page.Section("Cache and memory");
        StyleNumber(_cacheReuse, 0, 4096, 64);
        _cacheReuse.Value = Math.Clamp(_cfg.Llama.Server.CacheReuse, 0, 4096);
        page.Row("Cache reuse (tokens)", _cacheReuse, "Reuse cached chunks this big when part of the prompt changed. 0 turns it off.");

        _mlock.Text = "Keep the model locked in RAM (mlock)";
        _mlock.Checked = _cfg.Llama.Server.Mlock;
        _mlock.AutoSize = true;
        page.Row("Memory", _mlock, "Stops paging, but only turn it on with ~23 GiB free, or the rest of the system pages instead.");

        var hardware = Button("Hardware settings...", secondary: true, width: 180);
        hardware.Click += async (s, e) => await _host.OpenHardware();
        page.Row("Build and model file", hardware, "Which llama.cpp build runs, and the model file it loads.");

        UpdateKvCost();
        _pages[PerformancePage] = page.Root;
    }

    private void BuildChatPage()
    {
        var page = new PageBuilder("Chat & tools",
            "What goes with each turn. All of it is prompt: smaller is faster on the local model.");

        page.Section("Tools the local model is given");
        _tools.BorderStyle = BorderStyle.None;
        _tools.BackColor = Theme.Surface;
        _tools.ForeColor = Theme.Text;
        _tools.CheckOnClick = true;
        _tools.IntegralHeight = false;
        _tools.AccessibleName = "Tools offered to the local model";
        foreach (var name in _cfg.Llama.Agent.ChatTools) _tools.Items.Add(name, true);
        page.Full(_tools, 170);
        page.Hint("Each tool's schema costs ~100-300 tokens on every turn. Ticked tools are offered; the list " +
                  "fills in from the running server.");

        page.Section("Context");
        _editorContext.Text = "Send the open editor file with each turn";
        _editorContext.Checked = _cfg.Chat.EditorContext;
        _editorContext.AutoSize = true;
        page.Row("Editor file", _editorContext, null);
        StyleNumber(_editorChars, 0, 200_000, 1000);
        _editorChars.Value = Math.Clamp(_cfg.Chat.EditorContextChars, 0, 200_000);
        page.Row("Editor file, characters", _editorChars, "~4 characters a token. The model can ask for the rest with a tool.");
        StyleNumber(_toolResultChars, 500, 200_000, 500);
        _toolResultChars.Value = Math.Clamp(_cfg.Chat.ToolResultChars, 500, 200_000);
        page.Row("Tool result, characters", _toolResultChars, "How much of a tool's answer the model reads. The chat shows all of it.");

        page.Section("History");
        StyleNumber(_historyMessages, 0, 100, 2);
        _historyMessages.Value = Math.Clamp(_cfg.Chat.HistoryMessages, 0, 100);
        page.Row("Messages kept", _historyMessages, "Earlier questions and answers sent back. 0 forgets every turn.");
        StyleNumber(_historyChars, 0, 200_000, 1000);
        _historyChars.Value = Math.Clamp(_cfg.Chat.HistoryChars, 0, 200_000);
        page.Row("History, characters", _historyChars, "The oldest exchanges go first when this is full.");
        StyleNumber(_maxToolTurns, 1, 50, 1);
        _maxToolTurns.Value = Math.Clamp(_cfg.Chat.MaxToolTurns, 1, 50);
        page.Row("Tool rounds per turn", _maxToolTurns, "How many times the model may call tools before it has to answer.");

        _pages[ChatPage] = page.Root;
        _onShow[ChatPage] = async () => await LoadToolsAsync();
    }

    private void BuildMcpPage()
    {
        var page = new PageBuilder("MCP servers",
            "The tool servers the app connects to. Changes apply the next time the app starts.");

        foreach (var (name, def) in _cfg.Mcp.Servers)
        {
            page.Section(name);
            var enabled = new ModernCheckBox { Text = "Enabled", Checked = def.Enabled, AutoSize = true };
            var onDemand = new ModernCheckBox { Text = "On demand (started only by the command that needs it)", Checked = def.OnDemand, AutoSize = true };
            var timeout = new NumericUpDown();
            StyleNumber(timeout, 5, 3600, 5);
            timeout.Value = Math.Clamp(def.ToolTimeoutMs / 1000, 5, 3600);

            var flags = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Canvas, Margin = Padding.Empty };
            flags.Controls.AddRange(new Control[] { enabled, onDemand });
            page.Row("Server", flags, def.Description);
            page.Row("Tool timeout (seconds)", timeout, null);
            _mcpRows.Add((name, enabled, onDemand, timeout));

            if (name == "rea")
            {
                StyleCombo(_reaProvider, editable: false);
                foreach (var p in _providers.Providers.Where(p => !p.IsLocal)) _reaProvider.Items.Add(p.Name);
                _reaProvider.SelectedItem = def.Provider;
                page.Row("Suggested provider", _reaProvider, "REA is cloud only. /rea on the local model points here.");
                StyleText(_reaModel);
                _reaModel.Text = def.Model;
                page.Row("Suggested model", _reaModel, null);
            }
        }

        _pages[McpPage] = page.Root;
    }

    private void BuildEditorPage()
    {
        var page = new PageBuilder("Editor & appearance", "How the app and the editor look. These apply the next time the app starts.");

        StyleNumber(_fontScale, 0.8m, 2.0m, 0.05m, decimals: 2);
        _fontScale.Value = (decimal)Math.Clamp(_cfg.Ui.FontScale, 0.8f, 2.0f);
        page.Row("Text size", _fontScale, "1.00 is the built-in size. Same as /fontsize.");

        StyleText(_editorFont);
        _editorFont.Text = _cfg.Editor.FontFamily;
        page.Row("Editor font", _editorFont, "A CSS font list, first one installed wins.");
        StyleNumber(_editorSize, 8, 40, 1);
        _editorSize.Value = Math.Clamp(_cfg.Editor.FontSize, 8, 40);
        page.Row("Editor font size", _editorSize, null);
        StyleNumber(_tabSize, 1, 16, 1);
        _tabSize.Value = Math.Clamp(_cfg.Editor.TabSize, 1, 16);
        page.Row("Tab size", _tabSize, null);
        _wrap.Text = "Wrap long lines";
        _wrap.Checked = _cfg.Editor.LineWrapping;
        _wrap.AutoSize = true;
        page.Row("Wrapping", _wrap, null);

        var open = Button("Open the config file", secondary: true, width: 180);
        open.Click += (s, e) => _host.OpenFile(_cfg.SourcePath);
        page.Row("Everything else", open, "Every setting lives in notecode.config.json, with a note on each.");

        _pages[EditorPage] = page.Root;
    }

    // ------------------------------------------------------------------ providers

    private ProviderDef? SelectedProvider() =>
        _providerList.SelectedIndex >= 0 && _providerList.SelectedIndex < _providers.Providers.Count
            ? _providers.Providers[_providerList.SelectedIndex]
            : null;

    private void RefreshProviderList(string? select)
    {
        _providerList.BeginUpdate();
        _providerList.Items.Clear();
        foreach (var p in _providers.Providers) _providerList.Items.Add(p.Label);
        _providerList.EndUpdate();
        var index = _providers.Providers.FindIndex(p => p.Name == select);
        _providerList.SelectedIndex = Math.Max(0, index);
        ShowProvider();
    }

    private void ShowProvider()
    {
        var p = SelectedProvider();
        if (p is null) { _providerInfo.Text = ""; return; }

        _providerInfo.Text = p.IsLocal
            ? "The local llama-server. Its model file is chosen on Settings > Hardware."
            : $"{p.BaseUrl}  ·  {p.Style}  ·  {(p.HasToken ? "API key set" : "no API key yet - use Edit / API key")}" +
              (string.IsNullOrEmpty(p.DoctorNote) ? "" : $"\n{p.DoctorNote}");
        FillModels(_providerModel, p);
    }

    private static void FillModels(ComboBox box, ProviderDef p)
    {
        box.BeginUpdate();
        box.Items.Clear();
        foreach (var m in p.Models) box.Items.Add(m);
        if (p.Model.Length > 0 && !p.Models.Contains(p.Model)) box.Items.Add(p.Model);
        box.EndUpdate();
        box.Text = p.Model;
        box.Enabled = !p.IsLocal;
    }

    private void EditProvider(ProviderDef? existing)
    {
        using var form = new AddProviderForm(_cfg, _providers, existing, existing?.Name);
        if (form.ShowDialog(this) != DialogResult.OK || form.Result is null) return;

        // The provider form keeps the store in memory; the key is written now, so
        // a key entered here is not lost if this window is cancelled.
        _providers.Save(_cfg.RuntimeDir);
        RefreshProviderList(form.Result.Name);
        Say($"Saved {form.Result.Label}.");
    }

    private void RemoveProvider()
    {
        var p = SelectedProvider();
        if (p is null) return;
        if (p.IsLocal || p.Source == "preset" || p.Source == "builtin")
        {
            Say($"{p.Label} is built in and cannot be removed. Edit it instead.", warn: true);
            return;
        }
        _providers.Providers.Remove(p);
        if (_activeProvider == p.Name) _activeProvider = "local";
        _providers.Save(_cfg.RuntimeDir);
        RefreshProviderList(_activeProvider);
        Say($"Removed {p.Label}.");
    }

    // ------------------------------------------------------------------ helpers

    private string AgentFilePath()
    {
        var file = _cfg.Chat.InstructionsFile;
        return Path.IsPathRooted(file) ? file : Path.Combine(_cfg.Workspace.Root, file);
    }

    private void UpdateAgentCost()
    {
        var tokens = _agentText.Text.Length / 4;
        var capped = _agentText.Text.Length > _cfg.Chat.InstructionsChars;
        _agentCost.Text = $"{_agentText.Text.Length:N0} characters, about {tokens:N0} tokens on every turn" +
                          (capped ? $" - over the {_cfg.Chat.InstructionsChars:N0}-character cap, the rest is cut off." : ".");
        _agentCost.ForeColor = tokens > 1000 || capped ? Theme.Amber : Theme.TextMuted;
    }

    private void UpdateKvCost()
    {
        var type = _cacheK.SelectedItem as string ?? "q8_0";
        var gib = _cfg.Llama.Context.KvBytesPerToken(type) * (double)_ctx.Value / 1024 / 1024 / 1024;
        _kvCost.Text = $"KV cache at this size: about {gib:0.00} GiB of RAM.";
    }

    private async Task LoadToolsAsync()
    {
        if (_toolsLoaded) return;
        _toolsLoaded = true;
        try
        {
            var names = await _host.LoadToolNames();
            var ticked = new HashSet<string>(_tools.CheckedItems.Cast<string>());
            foreach (var name in names)
            {
                if (_tools.Items.Contains(name)) continue;
                _tools.Items.Add(name, ticked.Contains(name));
            }
        }
        catch (Exception ex)
        {
            Say($"The tool list could not be read from the server: {ex.Message}", warn: true);
        }
    }

    private void Say(string text, bool warn = false)
    {
        _status.Text = text;
        _status.ForeColor = warn ? Theme.Amber : Theme.TextMuted;
    }

    // ------------------------------------------------------------------ save

    private async Task SaveAsync(bool restart)
    {
        var reasoning = _reasonOn.Checked ? "on" : _reasonAuto.Checked ? "auto" : "off";
        var server = (
            _cfg.Llama.Context.CtxSize, _cfg.Llama.Context.CacheTypeK, _cfg.Llama.Context.CacheTypeV,
            _cfg.Llama.Cpu.Threads, _cfg.Llama.Cpu.ThreadsBatch, _cfg.Llama.Server.CacheReuse,
            _cfg.Llama.Server.Mlock, _cfg.Llama.Agent.Reasoning, _cfg.Llama.LoadMmproj);

        try
        {
            var w = new ConfigWriter(_cfg.SourcePath);

            // Model & reasoning
            _cfg.Llama.Agent.Reasoning = reasoning;
            _cfg.Llama.Agent.Temperature = (double)_temperature.Value;
            _cfg.Llama.Agent.MaxTokens = (int)_maxTokens.Value;
            _cfg.Llama.LoadMmproj = _vision.Checked;
            w.Set("llama.agent.reasoning", reasoning)
             .Set("llama.agent.temperature", _cfg.Llama.Agent.Temperature)
             .Set("llama.agent.maxTokens", _cfg.Llama.Agent.MaxTokens)
             .Set("llama.loadMmproj", _cfg.Llama.LoadMmproj);

            // Performance
            _cfg.Llama.Context.CtxSize = (int)_ctx.Value;
            _cfg.Llama.Context.CacheTypeK = _cacheK.SelectedItem as string ?? _cfg.Llama.Context.CacheTypeK;
            _cfg.Llama.Context.CacheTypeV = _cacheV.SelectedItem as string ?? _cfg.Llama.Context.CacheTypeV;
            _cfg.Llama.Cpu.Threads = (int)_threads.Value;
            _cfg.Llama.Cpu.ThreadsBatch = (int)_threadsBatch.Value;
            _cfg.Llama.Server.CacheReuse = (int)_cacheReuse.Value;
            _cfg.Llama.Server.Mlock = _mlock.Checked;
            w.Set("llama.context.ctxSize", _cfg.Llama.Context.CtxSize)
             .Set("llama.context.cacheTypeK", _cfg.Llama.Context.CacheTypeK)
             .Set("llama.context.cacheTypeV", _cfg.Llama.Context.CacheTypeV)
             .Set("llama.cpu.threads", _cfg.Llama.Cpu.Threads)
             .Set("llama.cpu.threadsBatch", _cfg.Llama.Cpu.ThreadsBatch)
             .Set("llama.server.cacheReuse", _cfg.Llama.Server.CacheReuse)
             .Set("llama.server.mlock", _cfg.Llama.Server.Mlock);

            // Chat & tools. Ticked tools keep the order they are listed in, which
            // is the order the prompt gets them in.
            _cfg.Llama.Agent.ChatTools = _tools.CheckedItems.Cast<string>().ToList();
            _cfg.Chat.EditorContext = _editorContext.Checked;
            _cfg.Chat.EditorContextChars = (int)_editorChars.Value;
            _cfg.Chat.ToolResultChars = (int)_toolResultChars.Value;
            _cfg.Chat.HistoryMessages = (int)_historyMessages.Value;
            _cfg.Chat.HistoryChars = (int)_historyChars.Value;
            _cfg.Chat.MaxToolTurns = (int)_maxToolTurns.Value;
            _cfg.Chat.SendInstructions = _agentSend.Checked;
            w.Set("llama.agent.chatTools", _cfg.Llama.Agent.ChatTools)
             .Set("chat.editorContext", _cfg.Chat.EditorContext)
             .Set("chat.editorContextChars", _cfg.Chat.EditorContextChars)
             .Set("chat.toolResultChars", _cfg.Chat.ToolResultChars)
             .Set("chat.historyMessages", _cfg.Chat.HistoryMessages)
             .Set("chat.historyChars", _cfg.Chat.HistoryChars)
             .Set("chat.maxToolTurns", _cfg.Chat.MaxToolTurns)
             .Set("chat.sendInstructions", _cfg.Chat.SendInstructions);

            // MCP servers
            foreach (var (name, enabled, onDemand, timeout) in _mcpRows)
            {
                var def = _cfg.Mcp.Servers[name];
                def.Enabled = enabled.Checked;
                def.OnDemand = onDemand.Checked;
                def.ToolTimeoutMs = (int)timeout.Value * 1000;
                w.Set($"mcp.servers.{name}.enabled", def.Enabled)
                 .Set($"mcp.servers.{name}.onDemand", def.OnDemand)
                 .Set($"mcp.servers.{name}.toolTimeoutMs", def.ToolTimeoutMs);
                if (name == "rea")
                {
                    def.Provider = _reaProvider.SelectedItem as string ?? def.Provider;
                    def.Model = _reaModel.Text.Trim();
                    w.Set("mcp.servers.rea.provider", def.Provider).Set("mcp.servers.rea.model", def.Model);
                }
            }

            // Editor & appearance
            _cfg.Ui.FontScale = (float)_fontScale.Value;
            _cfg.Editor.FontFamily = _editorFont.Text.Trim();
            _cfg.Editor.FontSize = (int)_editorSize.Value;
            _cfg.Editor.TabSize = (int)_tabSize.Value;
            _cfg.Editor.LineWrapping = _wrap.Checked;
            w.Set("ui.fontScale", _cfg.Ui.FontScale)
             .Set("editor.fontFamily", _cfg.Editor.FontFamily)
             .Set("editor.fontSize", _cfg.Editor.FontSize)
             .Set("editor.tabSize", _cfg.Editor.TabSize)
             .Set("editor.lineWrapping", _cfg.Editor.LineWrapping);

            w.Save();

            // Agent instructions
            if (_agentText.Text != _agentLoaded)
            {
                var file = AgentFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, _agentText.Text);
                _agentLoaded = _agentText.Text;
            }

            // Providers
            _providers.SetActive(_activeProvider);
            _providers.Save(_cfg.RuntimeDir);
        }
        catch (Exception ex)
        {
            Say($"Not saved: {ex.Message}", warn: true);
            return;
        }

        if (server.CtxSize != _cfg.Llama.Context.CtxSize) _host.ContextChanged(_cfg.Llama.Context.CtxSize);
        _host.Applied();

        var serverChanged = server != (
            _cfg.Llama.Context.CtxSize, _cfg.Llama.Context.CacheTypeK, _cfg.Llama.Context.CacheTypeV,
            _cfg.Llama.Cpu.Threads, _cfg.Llama.Cpu.ThreadsBatch, _cfg.Llama.Server.CacheReuse,
            _cfg.Llama.Server.Mlock, _cfg.Llama.Agent.Reasoning, _cfg.Llama.LoadMmproj);

        DialogResult = DialogResult.OK;
        Close();

        if (restart) await _host.RestartServer();
        else if (serverChanged) _host.ServerSettingsPending();
    }

    // ------------------------------------------------------------------ styling

    private static void DrawListItem(ListBox list, DrawItemEventArgs e, string text, string? tag)
    {
        if (e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var back = new SolidBrush(selected ? Theme.SurfaceRaised : list.BackColor);
        e.Graphics.FillRectangle(back, e.Bounds);
        if (selected)
        {
            using var bar = new SolidBrush(Theme.Violet);
            e.Graphics.FillRectangle(bar, e.Bounds.X, e.Bounds.Y + 6, 3, e.Bounds.Height - 12);
        }

        var textBox = new Rectangle(e.Bounds.X + 14, e.Bounds.Y, e.Bounds.Width - 20, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, Theme.Ui, textBox, selected ? Theme.Text : Theme.TextMuted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (!string.IsNullOrEmpty(tag))
        {
            TextRenderer.DrawText(e.Graphics, tag, Theme.UiSmall, textBox, Theme.TextDim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }
    }

    private static AccentButton Button(string text, bool secondary = false, int width = 0)
    {
        var b = new AccentButton { Text = text, Height = 32, Dock = width == 0 ? DockStyle.Fill : DockStyle.None, Margin = new Padding(4, 2, 4, 2) };
        if (width > 0) b.Width = width;
        if (secondary) { b.From = Theme.SurfaceRaised; b.To = Theme.SurfaceRaised; }
        return b;
    }

    private static void StyleNumber(NumericUpDown n, decimal min, decimal max, decimal step, int decimals = 0)
    {
        n.Minimum = min;
        n.Maximum = max;
        n.Increment = step;
        n.DecimalPlaces = decimals;
        n.ThousandsSeparator = decimals == 0;
        n.BackColor = Theme.SurfaceRaised;
        n.ForeColor = Theme.Text;
        n.BorderStyle = BorderStyle.FixedSingle;
        n.Font = Theme.Ui;
        n.Width = 140;
    }

    private static void StyleCombo(ComboBox c, bool editable)
    {
        c.DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList;
        c.FlatStyle = FlatStyle.Flat;
        c.BackColor = Theme.SurfaceRaised;
        c.ForeColor = Theme.Text;
        c.Font = Theme.Ui;
        c.Width = 320;
    }

    private static void StyleText(TextBox t)
    {
        t.BorderStyle = BorderStyle.FixedSingle;
        t.BackColor = Theme.SurfaceRaised;
        t.ForeColor = Theme.Text;
        t.Font = Theme.Ui;
        t.Width = 320;
    }

    private static void StyleRadio(RadioButton r, string text)
    {
        r.Text = text;
        r.AutoSize = true;
        r.FlatStyle = FlatStyle.Flat;
        r.ForeColor = Theme.Text;
        r.BackColor = Theme.Canvas;
        r.Font = Theme.Ui;
        r.Margin = new Padding(0, 4, 18, 4);
    }

    /// <summary>
    /// Lays one page out: a title, a line on what the page is for, then rows of a
    /// label, a control and a hint under it. The page scrolls when it is taller
    /// than the window.
    /// </summary>
    private sealed class PageBuilder
    {
        private readonly TableLayoutPanel _grid;
        public Panel Root { get; }

        public PageBuilder(string title, string intro)
        {
            Root = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Canvas };
            _grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                BackColor = Theme.Canvas,
                Padding = new Padding(0, 0, 12, 12)
            };
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190f));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            Root.Controls.Add(_grid);

            AddSpanning(new Label { Text = title, Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
            AddSpanning(new Label { Text = intro, Font = Theme.UiSmall, ForeColor = Theme.TextMuted, AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(0, 0, 0, 10) });
        }

        public void Section(string text) =>
            AddSpanning(new Label { Text = text, Font = Theme.UiSemibold, ForeColor = Theme.VioletBright, AutoSize = true, Margin = new Padding(0, 12, 0, 6) });

        public void Hint(string text) =>
            AddSpanning(new Label { Text = text, Font = Theme.UiSmall, ForeColor = Theme.TextDim, AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(0, 2, 0, 6) });

        public void Row(string label, Control control, string? hint)
        {
            var row = _grid.RowCount++;
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _grid.Controls.Add(new Label
            {
                Text = label,
                ForeColor = Theme.TextMuted,
                AutoSize = true,
                Margin = new Padding(0, 7, 8, 0)
            }, 0, row);

            var cell = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                BackColor = Theme.Canvas,
                Margin = new Padding(0, 2, 0, 4)
            };
            control.Margin = new Padding(0, 2, 0, 0);
            cell.Controls.Add(control);
            if (!string.IsNullOrEmpty(hint))
            {
                cell.Controls.Add(new Label
                {
                    Text = hint,
                    Font = Theme.UiSmall,
                    ForeColor = Theme.TextDim,
                    AutoSize = true,
                    MaximumSize = new Size(560, 0),
                    Margin = new Padding(0, 3, 0, 0)
                });
            }
            _grid.Controls.Add(cell, 1, row);
        }

        public void Full(Control control, int height)
        {
            control.Height = height;
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 2, 0, 4);
            var row = _grid.RowCount++;
            _grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height + 6));
            _grid.Controls.Add(control, 0, row);
            _grid.SetColumnSpan(control, 2);
        }

        private void AddSpanning(Control control)
        {
            var row = _grid.RowCount++;
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _grid.Controls.Add(control, 0, row);
            _grid.SetColumnSpan(control, 2);
        }
    }
}
