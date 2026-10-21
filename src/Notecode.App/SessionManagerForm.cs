namespace Notecode.App;

/// <summary>
/// The list of sessions from the past.
///
/// This is the window the tray icon's "Open session" opens. It shows every
/// conversation the app has kept, pinned ones first, and it is the control
/// panel for them: open one to carry on where it left off, pin the ones worth
/// keeping in sight, delete the ones that were noise.
///
/// It is deliberately a separate window rather than a tab in the editor: a
/// session is not a file, and the app keeps running in the notification area
/// while this is open.
/// </summary>
internal sealed class SessionManagerForm : Form
{
    private readonly SessionStore _store;
    private readonly ModernTreeView _list = new();
    private readonly Label _hint = new();
    private readonly AccentButton _open = new();
    private readonly AccentButton _pin = new();
    private readonly AccentButton _delete = new();
    private string? _selectedId;

    /// <summary>Raised with the identifier of the session the user opened.</summary>
    public event Action<string>? SessionOpened;

    /// <summary>Raised when the user asked for a new, empty session.</summary>
    public event Action? NewSessionRequested;

    public SessionManagerForm(SessionStore store)
    {
        _store = store;

        Text = "notecode - sessions";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        ClientSize = new Size(760, 540);
        MinimumSize = new Size(620, 420);
        KeyPreview = true;

        BuildLayout();
        Reload();

        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            if (e.KeyCode == Keys.Enter) OpenSelected();
            if (e.KeyCode == Keys.Delete) DeleteSelected();
        };
    }

    // ------------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Canvas,
            Padding = new Padding(18, 14, 18, 12)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));

        // ---- header
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Canvas, Margin = Padding.Empty };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160f));
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var title = new Label
        {
            Text = "The conversations you have had. Open one to carry on where it left off.",
            ForeColor = Theme.TextMuted,
            BackColor = Theme.Canvas,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        top.Controls.Add(title, 0, 0);

        var fresh = new AccentButton { Text = "New session", Height = 30, Dock = DockStyle.Fill, From = Theme.SurfaceRaised, To = Theme.SurfaceRaised };
        fresh.Click += (s, e) =>
        {
            NewSessionRequested?.Invoke();
            Close();
        };
        top.Controls.Add(fresh, 1, 0);
        root.Controls.Add(top, 0, 0);

        // ---- the list
        _list.Dock = DockStyle.Fill;
        _list.AccessibleName = "Sessions";
        _list.HideSelection = false;
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = Theme.Surface;
        _list.ForeColor = Theme.Text;
        _list.AfterSelect += (s, e) => OnSelected(e.Node);
        root.Controls.Add(_list, 0, 1);

        // ---- the control panel
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, BackColor = Theme.Canvas, Margin = Padding.Empty };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120f));  // open
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120f));  // pin
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120f));  // delete
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));  // the hint
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120f));  // close
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _open.Text = "Open";
        _open.Height = 32;
        _open.Dock = DockStyle.Fill;
        _open.Click += (s, e) => OpenSelected();
        buttons.Controls.Add(_open, 0, 0);

        _pin.Text = "Pin";
        _pin.Height = 32;
        _pin.Dock = DockStyle.Fill;
        _pin.From = Theme.SurfaceRaised;
        _pin.To = Theme.SurfaceRaised;
        _pin.Click += (s, e) => TogglePin();
        buttons.Controls.Add(_pin, 1, 0);

        _delete.Text = "Delete";
        _delete.Height = 32;
        _delete.Dock = DockStyle.Fill;
        _delete.From = Theme.SurfaceRaised;
        _delete.To = Theme.SurfaceRaised;
        _delete.Click += (s, e) => DeleteSelected();
        buttons.Controls.Add(_delete, 2, 0);

        _hint.Text = "";
        _hint.ForeColor = Theme.TextDim;
        _hint.BackColor = Theme.Canvas;
        _hint.Dock = DockStyle.Fill;
        _hint.TextAlign = ContentAlignment.MiddleLeft;
        _hint.Padding = new Padding(10, 0, 0, 0);
        buttons.Controls.Add(_hint, 3, 0);

        var close = new AccentButton { Text = "Close", Height = 32, Dock = DockStyle.Fill, From = Theme.SurfaceRaised, To = Theme.SurfaceRaised };
        close.Click += (s, e) => Close();
        buttons.Controls.Add(close, 4, 0);

        root.Controls.Add(buttons, 0, 2);

        foreach (Control c in root.Controls) c.Margin = new Padding(0, 0, 0, 0);
        Controls.Add(root);
    }

    // ------------------------------------------------------------------- state

    /// <summary>Rebuilds the list from the store, keeping the selection.</summary>
    public void Reload()
    {
        var keep = _selectedId;
        _list.BeginUpdate();
        _list.Nodes.Clear();

        foreach (var s in _store.All())
        {
            var node = new TreeNode(s.DisplayTitle) { Tag = s.Id };
            node.Nodes.Add(new TreeNode(
                $"{s.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  ·  {s.Messages.Count} messages" +
                (s.Pinned ? "  ·  pinned" : "")));
            _list.Nodes.Add(node);
        }

        _list.EndUpdate();

        if (keep is not null)
        {
            foreach (TreeNode n in _list.Nodes)
            {
                if ((string?)n.Tag == keep) { n.EnsureVisible(); _list.SelectedNode = n; break; }
            }
        }
        else if (_list.Nodes.Count > 0)
        {
            _list.SelectedNode = _list.Nodes[0];
        }

        UpdateButtons();
    }

    private void OnSelected(TreeNode? node)
    {
        while (node is not null && node.Parent is not null) node = node.Parent;
        _selectedId = node?.Tag as string;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var s = _selectedId is null ? null : _store.Find(_selectedId);
        _open.Enabled = s is not null;
        _pin.Enabled = s is not null;
        _delete.Enabled = s is not null;
        _pin.Text = s is { Pinned: true } ? "Unpin" : "Pin";
        _hint.Text = s is null
            ? "No session selected."
            : $"{s.Messages.Count} messages  ·  {s.Workspace}";
    }

    // ----------------------------------------------------------------- actions

    private void OpenSelected()
    {
        if (_selectedId is null) return;
        SessionOpened?.Invoke(_selectedId);
        Close();
    }

    private void TogglePin()
    {
        if (_selectedId is null) return;
        var s = _store.Find(_selectedId);
        if (s is null) return;
        _store.SetPinned(s.Id, !s.Pinned);
        Reload();
    }

    private void DeleteSelected()
    {
        if (_selectedId is null) return;
        var s = _store.Find(_selectedId);
        if (s is null) return;

        var answer = MessageBox.Show(this,
            $"Delete \"{s.DisplayTitle}\"?\n\nThe conversation is removed and cannot be brought back.",
            "notecode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        _store.Delete(s.Id);
        _selectedId = null;
        Reload();
    }
}
