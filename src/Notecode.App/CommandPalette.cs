using System.Drawing.Drawing2D;

namespace Notecode.App;

/// <summary>
/// The slash-command list that opens under the prompt.
///
/// It is a plain panel that paints itself and never takes the keyboard focus: the
/// prompt keeps the focus the whole time, the arrow keys move the selection and Enter
/// accepts the highlighted command. A control that grabbed the focus would break both
/// typing and the end-to-end test that drives the prompt through UI Automation.
/// </summary>
internal sealed class CommandPalette : Panel
{
    private const int RowHeight = 32;
    private const int FooterHeight = 26;

    private readonly List<SlashCommand> _items = new();
    private int _selected;

    /// <summary>The command the user is on, or null when the list is empty.</summary>
    public SlashCommand? Selected =>
        _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    public int ItemCount => _items.Count;

    public CommandPalette()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Canvas;
        Font = Theme.Ui;
        Visible = false;
    }

    /// <summary>Replaces the list and the highlighted entry.</summary>
    public void SetItems(IReadOnlyList<SlashCommand> items, int selected)
    {
        _items.Clear();
        _items.AddRange(items);
        _selected = items.Count == 0 ? 0 : Math.Clamp(selected, 0, items.Count - 1);
        Invalidate();
    }

    /// <summary>Moves the highlight. Returns false when there is nothing to move to.</summary>
    public bool MoveSelection(int delta)
    {
        if (_items.Count == 0) return false;
        _selected = (_selected + delta + _items.Count) % _items.Count;
        Invalidate();
        return true;
    }

    public int PreferredHeight => Math.Min(_items.Count, 8) * RowHeight + FooterHeight + 12;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // A double-buffered control is not erased by the system, so the background is
        // filled explicitly or the previous contents survive the repaint.
        using (var bg = new SolidBrush(Theme.Canvas))
            g.FillRectangle(bg, ClientRectangle);

        if (_items.Count == 0)
        {
            using var p = new Pen(Theme.Border);
            g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            return;
        }

        int visible = Math.Min(_items.Count, 8);
        int listHeight = visible * RowHeight;

        using (var body = new SolidBrush(Theme.Surface))
            g.FillRectangle(body, new Rectangle(0, 0, Width, listHeight + 6));

        for (int i = 0; i < visible; i++)
        {
            var cmd = _items[i];
            int y = 6 + i * RowHeight;
            bool sel = i == _selected;

            if (sel)
            {
                using var hover = new SolidBrush(Theme.SurfaceHover);
                g.FillRectangle(hover, new Rectangle(4, y, Width - 8, RowHeight - 2));

                // The accent bar marks the entry the next keystroke will take.
                using var accent = new SolidBrush(Theme.Violet);
                g.FillRectangle(accent, new Rectangle(4, y + 6, 2, RowHeight - 14));
            }

            var nameColor = sel ? Theme.VioletBright : Theme.Cyan;
            var nameSize = TextRenderer.MeasureText("/" + cmd.Name, Theme.Code);
            TextRenderer.DrawText(g, "/" + cmd.Name, Theme.Code,
                new Rectangle(16, y, Width - 30, RowHeight - 2), nameColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            // The summary gets the room that is left over after the name, so a long
            // one cannot run back over the command it belongs to.
            int nameWidth = nameSize.Width + 14;
            var summaryWidth = Math.Max(20, Width - 34 - nameWidth);
            TextRenderer.DrawText(g, cmd.Summary, Theme.UiSmall,
                new Rectangle(16 + nameWidth, y, summaryWidth, RowHeight - 2),
                sel ? Theme.TextMuted : Theme.TextDim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        // Footer: the keys that work right now.
        using (var foot = new SolidBrush(Theme.SurfaceRaised))
            g.FillRectangle(foot, new Rectangle(0, listHeight + 6, Width, FooterHeight));

        TextRenderer.DrawText(g, "↑↓ move    Enter accept    Esc close", Theme.UiSmall,
            new Rectangle(12, listHeight + 6, Width - 24, FooterHeight), Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        using var border = new Pen(Theme.BorderStrong);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}
