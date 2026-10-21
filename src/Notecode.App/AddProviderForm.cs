using System.Drawing.Drawing2D;

namespace Notecode.App;

/// <summary>
/// The form behind `/addprovider`.
///
/// It edits one provider: the name, the base URL, the bearer token, the API style, the
/// model list and the chat template. **Scan GGUF** reads the chat template straight out
/// of a model file, and **Auto update endpoints** runs the doctor for this provider.
///
/// The token is encrypted with DPAPI before it is written, so the form never hands a
/// plain token to the store.
/// </summary>
internal sealed class AddProviderForm : Form
{
    private readonly AppConfig _cfg;
    private readonly ProviderStore _store;
    private readonly ProviderDef _original;
    private readonly bool _isNew;

    private readonly TextBox _name = new();
    private readonly TextBox _label = new();
    private readonly TextBox _baseUrl = new();
    private readonly TextBox _token = new() { UseSystemPasswordChar = true };
    private readonly ModernComboBox _style = new();
    private readonly TextBox _model = new();
    private readonly TextBox _models = new() { Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _template = new() { Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label _status = new();
    private readonly Label _tokenState = new();
    private readonly ModernCheckBox _echoReasoning = new();

    public ProviderDef? Result { get; private set; }

    public AddProviderForm(AppConfig cfg, ProviderStore store, ProviderDef? existing, string? suggestedName = null)
    {
        _cfg = cfg;
        _store = store;
        _original = existing ?? new ProviderDef { Name = suggestedName ?? "", Label = suggestedName ?? "" };
        _isNew = existing is null;

        Text = existing is null ? "Add provider" : $"Edit provider - {existing.Label}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        ClientSize = new Size(560, 620);
        KeyPreview = true;

        BuildLayout();
        LoadValues();

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
            RowCount = 10,
            BackColor = Theme.Canvas,
            Padding = new Padding(18, 16, 18, 14)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));  // title
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));  // name
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));  // label
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));  // base url
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));  // token
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));  // style + model
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));  // echo reasoning
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // models + template
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));  // status
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));  // buttons

        var title = new Label
        {
            Text = "Bring your own key. The token is encrypted for your Windows account before it is written.",
            ForeColor = Theme.TextMuted,
            BackColor = Theme.Canvas,
            Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft
        };
        root.Controls.Add(title, 0, 0);

        root.Controls.Add(Field("Name", _name, "minimax"), 0, 1);
        root.Controls.Add(Field("Display name", _label, "Moonshot AI"), 0, 2);
        root.Controls.Add(Field("Base URL", _baseUrl, "https://api.example.com/v1"), 0, 3);

        var tokenRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Canvas, Margin = Padding.Empty };
        tokenRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));
        tokenRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        tokenRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        var tokenLabel = Caption("Bearer token");
        tokenRow.Controls.Add(tokenLabel, 0, 0);
        var tokenBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Canvas, Margin = Padding.Empty };
        tokenBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        tokenBox.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108f));
        tokenBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        StyleInput(_token);
        tokenBox.Controls.Add(_token, 0, 0);
        _tokenState.ForeColor = Theme.TextDim;
        _tokenState.BackColor = Theme.Canvas;
        _tokenState.Font = Theme.UiSmall;
        _tokenState.Dock = DockStyle.Fill;
        _tokenState.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
        tokenBox.Controls.Add(_tokenState, 1, 0);
        tokenRow.Controls.Add(tokenBox, 1, 0);
        root.Controls.Add(tokenRow, 0, 4);

        // API style and model on one row.
        var styleRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Theme.Canvas, Margin = Padding.Empty };
        styleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));
        styleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160f));
        styleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72f));
        styleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        styleRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        styleRow.Controls.Add(Caption("API style"), 0, 0);
        _style.BackColor = Theme.Canvas;
        _style.ForeColor = Theme.Text;
        _style.Font = Theme.Ui;
        _style.Dock = DockStyle.Fill;
        _style.Margin = new Padding(0, 3, 10, 3);
        _style.Items.Add("OpenAI-compatible");
        _style.Items.Add("Anthropic messages");
        styleRow.Controls.Add(_style, 1, 0);
        styleRow.Controls.Add(Caption("Model"), 2, 0);
        StyleInput(_model);
        _model.Margin = new Padding(0, 3, 0, 3);
        styleRow.Controls.Add(_model, 3, 0);
        root.Controls.Add(styleRow, 0, 5);

        // A provider that reasons may also want the reasoning sent back with every
        // turn. It is not part of the OpenAI chat spec, so it is opt-in.
        _echoReasoning.Text = "Send the reasoning back with every turn";
        _echoReasoning.Checked = false;
        _echoReasoning.Dock = DockStyle.Fill;
        _echoReasoning.Margin = new Padding(0, 4, 0, 0);
        root.Controls.Add(_echoReasoning, 0, 6);

        // Model list and chat template.
        var lower = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Canvas, Margin = Padding.Empty };
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        lower.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var modelsBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Canvas, Margin = new Padding(0, 4, 8, 0) };
        modelsBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        modelsBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
        modelsBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        modelsBox.Controls.Add(Caption("Models (one per line)"), 0, 0);
        StyleInput(_models);
        modelsBox.Controls.Add(_models, 0, 1);
        lower.Controls.Add(modelsBox, 0, 0);

        var templateBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Canvas, Margin = new Padding(8, 4, 0, 0) };
        templateBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        templateBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
        templateBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
        templateBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        templateBox.Controls.Add(Caption("Chat template (optional)"), 0, 0);

        var scanRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Canvas, Margin = Padding.Empty };
        scanRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        scanRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f));
        scanRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        var scanHint = new Label
        {
            Text = "A GGUF carries its own template.",
            ForeColor = Theme.TextDim,
            BackColor = Theme.Canvas,
            Font = Theme.UiSmall,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft
        };
        scanRow.Controls.Add(scanHint, 0, 0);
        var scanButton = new AccentButton { Text = "Scan GGUF", Height = 24, Width = 100, Dock = DockStyle.Fill, From = Theme.SurfaceRaised, To = Theme.SurfaceRaised };
        scanButton.Click += (s, e) => ScanGguf();
        scanRow.Controls.Add(scanButton, 1, 0);
        templateBox.Controls.Add(scanRow, 0, 1);

        StyleInput(_template);
        _template.Font = Theme.CodeSmall;
        templateBox.Controls.Add(_template, 0, 2);
        lower.Controls.Add(templateBox, 1, 0);
        root.Controls.Add(lower, 0, 7);

        _status.ForeColor = Theme.TextMuted;
        _status.BackColor = Theme.Canvas;
        _status.Font = Theme.UiSmall;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;
        root.Controls.Add(_status, 0, 8);

        // The doctor button carries the longest caption in the form, so its column
        // is wide enough for the whole text: a caption that runs past the edge of
        // its button is cut in half, because the button paints the text centred.
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Theme.Canvas, Margin = Padding.Empty };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 178f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var doctorButton = new AccentButton { Text = "Auto update endpoints", Height = 32, Dock = DockStyle.Fill, From = Theme.SurfaceRaised, To = Theme.SurfaceRaised };
        doctorButton.Click += async (s, e) => await RunDoctorAsync();
        buttons.Controls.Add(doctorButton, 0, 0);

        var save = new AccentButton { Text = "Save", Height = 32, Dock = DockStyle.Fill };
        save.Click += (s, e) => Save();
        buttons.Controls.Add(save, 2, 0);
        var cancel = new AccentButton { Text = "Cancel", Height = 32, Dock = DockStyle.Fill, From = Theme.SurfaceRaised, To = Theme.SurfaceRaised };
        cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(cancel, 3, 0);
        root.Controls.Add(buttons, 0, 9);

        // TableLayoutPanel gives every child a 3px margin by default, which would
        // leave a hairline gap around each row.
        foreach (Control c in root.Controls) c.Margin = Padding.Empty;

        Controls.Add(root);
    }

    // ------------------------------------------------------------------ helpers

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
        box.Margin = new Padding(0, 3, 0, 3);
    }

    /// <summary>A caption and an input on one row.</summary>
    private static Control Field(string caption, TextBox input, string placeholder)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Canvas, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        row.Controls.Add(Caption(caption), 0, 0);

        StyleInput(input);
        input.Tag = placeholder;
        var shell = new FieldPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3) };
        shell.Controls.Add(input);
        row.Controls.Add(shell, 1, 0);
        return row;
    }

    private void LoadValues()
    {
        _name.Text = _original.Name;
        _label.Text = _original.Label;
        _baseUrl.Text = _original.BaseUrl;
        _model.Text = _original.Model;
        _models.Text = string.Join(Environment.NewLine, _original.Models);
        _template.Text = _original.ChatTemplate;
        _style.SelectedIndex = _original.Style == ApiStyle.Anthropic ? 1 : 0;
        _echoReasoning.Checked = _original.EchoReasoning;
        _tokenState.Text = _original.HasToken ? "a token is stored" : "no token";
    }

    // ------------------------------------------------------------------ actions

    private void ScanGguf()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Pick a GGUF model file",
            Filter = "GGUF models (*.gguf)|*.gguf|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var template = Gguf.ChatTemplate(dlg.FileName);
            var name = Gguf.ModelName(dlg.FileName);

            if (string.IsNullOrEmpty(template))
            {
                _status.ForeColor = Theme.Amber;
                _status.Text = "that file has no chat template in its header";
                return;
            }

            _template.Text = template;
            if (string.IsNullOrEmpty(_model.Text) && !string.IsNullOrEmpty(name)) _model.Text = name;
            _status.ForeColor = Theme.Mint;
            _status.Text = $"read the chat template from {Path.GetFileName(dlg.FileName)} ({template.Length} characters)";
        }
        catch (Exception ex)
        {
            _status.ForeColor = Theme.Rose;
            _status.Text = ex.Message;
        }
    }

    private async Task RunDoctorAsync()
    {
        var probe = BuildProvider(out _);
        _status.ForeColor = Theme.TextMuted;
        _status.Text = "checking the endpoint and searching for the current one...";

        try
        {
            var doctor = new EndpointDoctor(_cfg);
            var result = await doctor.CheckAsync(probe);

            var parts = new List<string> { result.Status };
            if (result.Models.Count > 0) parts.Add($"{result.Models.Count} models");
            if (result.SuggestedBaseUrl is not null) parts.Add($"the web suggests {result.SuggestedBaseUrl}");
            if (result.SuggestedModels.Count > 0) parts.Add($"the web mentions {string.Join(", ", result.SuggestedModels.Take(3))}");
            if (!string.IsNullOrEmpty(result.Note)) parts.Add(result.Note);

            _status.ForeColor = result.Reachable ? Theme.Mint : Theme.Amber;
            _status.Text = string.Join(" - ", parts);

            if (result.SuggestedBaseUrl is not null &&
                !EndpointDoctor.SameEndpoint(result.SuggestedBaseUrl, probe.BaseUrl))
            {
                var answer = MessageBox.Show(this,
                    $"The web suggests a different base URL:\n\n  current : {probe.BaseUrl}\n  suggested: {result.SuggestedBaseUrl}\n\nUse the suggested one?",
                    "notecode", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer == DialogResult.Yes) _baseUrl.Text = result.SuggestedBaseUrl;
            }

            if (result.Models.Count > 0 && string.IsNullOrEmpty(_models.Text))
                _models.Text = string.Join(Environment.NewLine, result.Models);
        }
        catch (Exception ex)
        {
            _status.ForeColor = Theme.Rose;
            _status.Text = ex.Message;
        }
    }

    /// <summary>Assembles a provider from the form, without saving it.</summary>
    private ProviderDef BuildProvider(out string error)
    {
        error = "";
        var name = _name.Text.Trim().ToLowerInvariant().Replace(' ', '-');
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "the provider needs a name";
            return new ProviderDef();
        }

        var baseUrl = _baseUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            error = "the provider needs a base URL";
            return new ProviderDef();
        }
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            error = "the base URL must start with http:// or https://";
            return new ProviderDef();
        }

        var models = _models.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.Trim())
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var token = _token.Text.Trim();
        var protectedToken = token.Length > 0
            ? SecretBox.Protect(token)
            : _original.ProtectedToken;

        if (token.Length > 0 && protectedToken.Length == 0)
        {
            error = "the token could not be encrypted, so it was not saved";
            return new ProviderDef();
        }

        return new ProviderDef
        {
            Name = name,
            Label = string.IsNullOrWhiteSpace(_label.Text) ? name : _label.Text.Trim(),
            BaseUrl = baseUrl.TrimEnd('/'),
            Style = _style.SelectedIndex == 1 ? ApiStyle.Anthropic : ApiStyle.OpenAi,
            EchoReasoning = _echoReasoning.Checked,
            Model = _model.Text.Trim(),
            Models = models,
            ChatTemplate = _template.Text,
            ProtectedToken = protectedToken,
            Source = "manual",
            IsLocal = false,
            LastChecked = _original.LastChecked,
            DoctorNote = _original.DoctorNote
        };
    }

    private void Save()
    {
        var provider = BuildProvider(out var error);
        if (error.Length > 0)
        {
            _status.ForeColor = Theme.Rose;
            _status.Text = error;
            return;
        }

        // Replace an entry with the same name rather than adding a second one.
        var existing = _store.Providers.FirstOrDefault(p =>
            string.Equals(p.Name, provider.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !_isNew)
        {
            var index = _store.Providers.IndexOf(existing);
            _store.Providers[index] = provider;
        }
        else
        {
            if (existing is not null) _store.Providers.Remove(existing);
            _store.Providers.Add(provider);
        }

        Result = provider;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Theme.ApplyWindowChrome(this);
        _name.Focus();
    }
}
