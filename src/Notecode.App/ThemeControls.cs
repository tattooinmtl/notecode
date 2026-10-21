using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms.VisualStyles;

namespace Notecode.App;

/// <summary>
/// Draws the small set of vector marks the chrome needs.
///
/// These are GDI+ paths rather than an icon font or emoji: a consistent set, crisp at
/// any DPI, and nothing extra to ship. Every mark is drawn from the same geometric
/// language - rounded caps, 2px strokes, the theme accents.
/// </summary>
internal static class Marks
{
    /// <summary>Paper plane - the send action.</summary>
    public static void Send(Graphics g, RectangleF box, Color color)
    {
        using var brush = new SolidBrush(color);
        using var path = new GraphicsPath();
        // A stylised plane: nose to the right, swept back to the left.
        path.AddPolygon(new[]
        {
            new PointF(box.Right, box.Top),
            new PointF(box.Left, box.Bottom - box.Height * 0.22f),
            new PointF(box.Left + box.Width * 0.34f, box.Bottom),
            new PointF(box.Left + box.Width * 0.30f, box.Bottom - box.Height * 0.42f),
        });
        g.FillPath(brush, path);
    }

    /// <summary>
    /// The agent mark: three nodes joined by edges. Reads as a small neural graph,
    /// which is the whole point of the product.
    /// </summary>
    public static void AgentMark(Graphics g, RectangleF box, Color primary, Color secondary)
    {
        var c = new[]
        {
            new PointF(box.Left + box.Width * 0.50f, box.Top + box.Height * 0.26f),
            new PointF(box.Left + box.Width * 0.22f, box.Top + box.Height * 0.74f),
            new PointF(box.Left + box.Width * 0.78f, box.Top + box.Height * 0.74f),
        };

        using var edge = new Pen(Color.FromArgb(150, primary), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(edge, c[0], c[1]);
        g.DrawLine(edge, c[0], c[2]);
        g.DrawLine(edge, c[1], c[2]);

        using var b0 = new SolidBrush(primary);
        using var b2 = new SolidBrush(secondary);
        g.FillEllipse(b0, c[0].X - 3.4f, c[0].Y - 3.4f, 6.8f, 6.8f);
        g.FillEllipse(b2, c[1].X - 3.0f, c[1].Y - 3.0f, 6.0f, 6.0f);
        g.FillEllipse(b2, c[2].X - 3.0f, c[2].Y - 3.0f, 6.0f, 6.0f);
    }

    /// <summary>Chevron for a collapsed tree folder.</summary>
    public static void Chevron(Graphics g, PointF center, Color color, bool expanded)
    {
        using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float s = 3.4f;
        if (expanded)
        {
            g.DrawLines(pen, new[]
            {
                new PointF(center.X - s, center.Y - s * 0.55f),
                new PointF(center.X, center.Y + s * 0.55f),
                new PointF(center.X + s, center.Y - s * 0.55f)
            });
        }
        else
        {
            g.DrawLines(pen, new[]
            {
                new PointF(center.X - s * 0.55f, center.Y - s),
                new PointF(center.X + s * 0.55f, center.Y),
                new PointF(center.X - s * 0.55f, center.Y + s)
            });
        }
    }

    /// <summary>A small filled dot, used for file types and status.</summary>
    public static void Dot(Graphics g, PointF center, float radius, Color color)
    {
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, center.X - radius, center.Y - radius, radius * 2, radius * 2);
    }

    /// <summary>Ring around a dot, used for the "waiting" state.</summary>
    public static void Ring(Graphics g, PointF center, float radius, Color color, float width = 1.4f)
    {
        using var pen = new Pen(color, width);
        g.DrawEllipse(pen, center.X - radius, center.Y - radius, radius * 2, radius * 2);
    }

    /// <summary>
    /// The paperclip: the mark for "attach something to the prompt".
    ///
    /// It is drawn, not typed. The shape is three nested hooks on a 45 degree axis,
    /// built from lines and semicircles so it stays crisp at any size.
    /// </summary>
    public static void Paperclip(Graphics g, RectangleF box, Color color)
    {
        // The shape is drawn on a 24 unit grid, and that grid maps onto the box.
        float k = Math.Min(box.Width, box.Height) / 24f;
        if (k <= 0f) return;

        float X(double v) => box.Left + (float)(v * k);
        float Y(double v) => box.Top + (float)(v * k);
        RectangleF Arc(double cx, double cy, double r) =>
            new(X(cx - r), Y(cy - r), (float)(2 * r * k), (float)(2 * r * k));

        using var path = new GraphicsPath();
        path.StartFigure();
        // The tail at the top right, down to the outer hook.
        path.AddLine(X(21.44), Y(11.05), X(12.25), Y(20.24));
        path.AddArc(Arc(8.005, 15.995, 6), 45, 180);
        // Up the long diagonal to the top hook.
        path.AddLine(X(3.76), Y(11.75), X(12.95), Y(2.56));
        path.AddArc(Arc(15.78, 5.39, 4), 225, 180);
        // And back down inside it.
        path.AddLine(X(18.61), Y(8.22), X(9.41), Y(17.41));
        path.AddArc(Arc(7.995, 15.995, 2), 45, 180);
        path.AddLine(X(6.58), Y(14.58), X(15.07), Y(6.10));

        using var pen = new Pen(color, Math.Max(1.1f, 1.8f * k))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// The agent header: a deep gradient with a violet and a cyan glow bleeding in from the
/// top corners, the agent mark, and the two lines of identity text.
///
/// The text is painted here rather than put in child labels on purpose. A WinForms
/// label with a transparent background asks its parent to repaint the region behind
/// it, and if the parent is later repainted the cached copy can be left behind as a
/// ghost of the old contents. Painting the text directly means there is nothing
/// transparent to go wrong.
/// </summary>
internal sealed class AuroraPanel : Panel
{
    public AuroraPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get; set; } = "";

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Subtitle { get; set; } = "";

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SubtitleColor { get; set; } = Theme.TextMuted;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, ClientRectangle);

        using (var fade = new LinearGradientBrush(
                   new Rectangle(0, 0, Width, (int)Math.Max(1, Height)),
                   Color.FromArgb(255, Theme.Violet),
                   Color.FromArgb(0, Theme.Violet),
                   LinearGradientMode.Vertical))
        {
            g.FillRectangle(fade, 0, 0, Width, (int)Math.Max(1, Height * 0.85f));
        }

        Theme.PaintGlow(g, new PointF(Width * 0.10f, -Height * 0.25f), Math.Max(Width, Height) * 0.85f, Theme.Violet, 52);
        Theme.PaintGlow(g, new PointF(Width * 0.95f, Height * 0.10f), Math.Max(Width, Height) * 0.60f, Theme.Cyan, 34);

        // The agent mark, drawn in place of a child control for the same reason.
        var mark = new RectangleF(14, (Height - 30) / 2f, 30, 30);
        Marks.AgentMark(g, mark, Theme.VioletBright, Theme.Cyan);

        float textX = mark.Right + 12f;
        float avail = Math.Max(10f, Width - textX - 14f);

        if (!string.IsNullOrEmpty(Title))
        {
            var size = TextRenderer.MeasureText(Title, Theme.Display);
            TextRenderer.DrawText(g, Title, Theme.Display,
                new Point((int)textX, (int)((Height - size.Height) / 2f - 9)),
                Theme.Text, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        if (!string.IsNullOrEmpty(Subtitle))
        {
            var size = TextRenderer.MeasureText(Subtitle, Theme.UiSmall);
            TextRenderer.DrawText(g, Subtitle, Theme.UiSmall,
                new Point((int)textX, (int)(Height / 2f + 5)),
                SubtitleColor, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>
/// A flat raised field: the composer background, drawn as a surface with a hairline
/// border. Opaque by design, so nothing about it can ghost.
/// </summary>
internal sealed class FieldPanel : Panel
{
    public FieldPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.SurfaceRaised;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var b = new SolidBrush(Theme.SurfaceRaised);
        e.Graphics.FillRectangle(b, ClientRectangle);
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>
/// A raised surface: a soft vertical gradient plus a hairline border and a one-pixel
/// highlight along the top edge, which is what makes a flat rectangle read as glass.
/// </summary>
internal sealed class GlassPanel : Panel
{
    public GlassPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
    }

    [Browsable(false)]

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]

    public Color Fill { get; set; } = Theme.Surface;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Canvas);

        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var path = Theme.RoundedRect(r, Theme.RadiusMd))
        using (var b = new LinearGradientBrush(r, Lighten(Fill, 8), Fill, LinearGradientMode.Vertical))
        {
            g.FillPath(b, path);
        }
        using (var path = Theme.RoundedRect(r, Theme.RadiusMd))
        using (var p = new Pen(Theme.Border))
        {
            g.DrawPath(p, path);
        }

        // Top edge highlight.
        using (var path = Theme.RoundedRect(new RectangleF(1.5f, 1.5f, Width - 3f, Height - 3f), Theme.RadiusMd - 1))
        using (var p = new Pen(Color.FromArgb(38, 255, 255, 255), 1f))
        using (var clip = new Region(path))
        {
            var g2 = g;
            var old = g2.Clip;
            g2.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
            g2.DrawLine(p, 2f, 1.5f, Width - 2f, 1.5f);
            g2.Clip = old;
        }
    }

    private static Color Lighten(Color c, int amount)
    {
        int r = Math.Min(255, c.R + amount);
        int g = Math.Min(255, c.G + amount);
        int b = Math.Min(255, c.B + amount);
        return Color.FromArgb(c.A, r, g, b);
    }
}

/// <summary>
/// A square button that carries a drawn mark instead of a caption.
///
/// It is a plain <see cref="Control"/> and not a <see cref="Button"/>, on purpose: a
/// themed WinForms button paints itself in the system's accent colour and takes
/// <see cref="Control.OnPaint"/> away from the app, so a drawn mark on one is never
/// seen. A plain control paints what the app tells it to.
///
/// It still behaves like a button: it takes the focus, it turns Space and Enter into
/// a click, and it is named for a screen reader.
/// </summary>
internal sealed class IconButton : Control
{
    private bool _hover;
    private bool _pressed;

    public IconButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Canvas;
        Font = Theme.Ui;
        Cursor = Cursors.Hand;
        TabStop = true;
        Size = new Size(26, 26);
        AccessibleRole = AccessibleRole.PushButton;
    }

    /// <summary>The mark to draw, in the box the button gives it.</summary>
    [Browsable(false)]

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]

    public Action<Graphics, RectangleF, Color>? Icon { get; set; }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override bool IsInputKey(Keys keyData) =>
        keyData == Keys.Space || keyData == Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // A control that is not a Button does not turn the keyboard into a click by
        // itself, so this does it.
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

        if (_hover || _pressed || Focused)
        {
            using (var path = Theme.RoundedRect(r, 6f))
            using (var b = new SolidBrush(_pressed ? Theme.SurfaceHover : Theme.SurfaceRaised))
                g.FillPath(b, path);
        }

        var color = !Enabled ? Theme.TextDim : (_hover ? Theme.Cyan : Theme.TextMuted);
        var box = new RectangleF(6f, 6f, Width - 12f, Height - 12f);
        Icon?.Invoke(g, box, color);

        // The keyboard has to be able to see where it is.
        if (Focused)
        {
            using var ring = new Pen(Theme.Cyan, 1.2f);
            g.DrawRectangle(ring, 1f, 1f, Width - 2f, Height - 2f);
        }
    }
}

/// <summary>
/// The primary action button: a violet-to-cyan gradient on a rounded pill, with a soft
/// glow on hover. It stays a real <see cref="Button"/> so keyboard focus, accessibility
/// and UI Automation keep working.
/// </summary>
internal sealed class AccentButton : Button
{
    private bool _hover;
    private bool _pressed;

    public AccentButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Theme.SurfaceRaised;
        ForeColor = Theme.Text;
        Font = Theme.UiSemibold;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    [Browsable(false)]

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]

    public Color From { get; set; } = Theme.Violet;
    [Browsable(false)]

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]

    public Color To { get; set; } = Theme.Cyan;

    /// <summary>
    /// A secondary action: a flat surface with a hairline border instead of the
    /// gradient. A violet pill next to the prompt would shout at something that is
    /// only a helper.
    /// </summary>
    [Browsable(false)]

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]

    public bool Subtle { get; set; }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var r = new RectangleF(1f, 1f, Width - 2f, Height - 2f);
        bool on = Enabled;

        if (Subtle)
        {
            // A flat surface with a hairline border. The border warms up on hover.
            using (var path = Theme.RoundedRect(r, r.Height / 2f))
            {
                using var b = new SolidBrush(on ? Theme.SurfaceRaised : Theme.Surface);
                g.FillPath(b, path);
                using var p = new Pen(_hover && on ? Theme.Violet : Theme.Border);
                g.DrawPath(p, path);
            }
            var subtleFg = on ? Theme.Text : Theme.TextDim;
            TextRenderer.DrawText(g, Text, Font,
                new Rectangle(0, 0, Width, Height),
                subtleFg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            return;
        }

        if (_hover && on)
        {
            Theme.PaintGlow(g, new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f), r.Height * 1.5f, From, 70);
        }

        using (var path = Theme.RoundedRect(r, r.Height / 2f))
        {
            if (!on)
            {
                using var b = new SolidBrush(Theme.SurfaceRaised);
                g.FillPath(b, path);
            }
            else
            {
                var a = _pressed ? From : (_hover ? From : Theme.VioletBright);
                var bcol = _pressed ? To : (_hover ? Theme.Cyan : Theme.Violet);
                using var b = new LinearGradientBrush(r, a, bcol, LinearGradientMode.Horizontal);
                g.FillPath(b, path);
            }
        }

        var fg = on ? LabelOn(From, To) : Theme.TextDim;
        // SingleLine pairs with VerticalCenter; see ModernCheckBox for why it matters.
        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(0, 0, Width, Height),
            fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    /// <summary>
    /// The colour a label on this button can be read against.
    ///
    /// The accent button is filled either with the bright gradient or with a
    /// flat dark surface, and one label colour does not work on both. White on
    /// the violet gradient is 2.7:1 at the bright end, which is a fail for the
    /// primary action of the app; a dark label on a flat dark surface would be
    /// invisible. So the label follows the fill.
    ///
    /// The threshold is where the two swap: a dark label clears 4.5:1 from a
    /// background of luminance 0.177 and above, and a light one clears it from
    /// 0.183 and below.
    /// </summary>
    private static Color LabelOn(Color a, Color b)
    {
        double lightest = Math.Max(Theme.RelativeLuminance(a), Theme.RelativeLuminance(b));
        return lightest >= 0.177 ? Theme.Void : Color.White;
    }
}

/// <summary>
/// A dropdown that looks like the rest of the app.
///
/// The stock WinForms ComboBox paints its button with the system theme, so on a
/// dark surface it shows up as a white box with a pale arrow - the one control
/// that refused to join the design. This one paints itself and opens a themed
/// menu instead of the system list.
///
/// It stays a real focusable control so the keyboard, the screen reader and UI
/// Automation keep working: the arrow keys move the selection, Enter opens the
/// list, and the control reports itself as a combo box.
/// </summary>
internal sealed class ModernComboBox : Control
{
    private readonly List<string> _items = new();
    private int _selectedIndex = -1;
    private bool _hover;
    private bool _open;

    public ModernComboBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
        AccessibleRole = AccessibleRole.ComboBox;
    }

    /// <summary>The choices. Adding to it repaints the control.</summary>
    public List<string> Items => _items;

    [Browsable(false)]

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var v = value;
            if (v < -1) v = -1;
            if (v >= _items.Count) v = _items.Count - 1;
            if (v == _selectedIndex) return;
            _selectedIndex = v;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? SelectedItem =>
        _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;

    public event EventHandler? SelectedIndexChanged;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Enabled) OpenMenu();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up:
                SelectedIndex = Math.Max(0, _selectedIndex - 1);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            case Keys.Down:
                SelectedIndex = Math.Min(_items.Count - 1, _selectedIndex + 1);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            case Keys.Enter:
            case Keys.Space:
                OpenMenu();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    private void OpenMenu()
    {
        if (_items.Count == 0) return;

        var menu = new ContextMenuStrip
        {
            Renderer = new FlatMenuRenderer(),
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
            ShowImageMargin = false,
            ShowCheckMargin = false,
        };

        for (int i = 0; i < _items.Count; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(_items[i])
            {
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                Font = Theme.Ui,
                Checked = i == _selectedIndex,
            };
            item.Click += (s, e) => SelectedIndex = index;
            menu.Items.Add(item);
        }

        _open = true;
        menu.Closed += (s, e) => { _open = false; Invalidate(); };
        menu.Show(this, new Point(0, Height + 2));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var path = Theme.RoundedRect(r, Theme.RadiusMd))
        using (var b = new SolidBrush(Theme.SurfaceRaised))
        {
            g.FillPath(b, path);
        }
        using (var path = Theme.RoundedRect(r, Theme.RadiusMd))
        using (var p = new Pen(Focused || _hover ? Theme.Violet : Theme.Border))
        {
            g.DrawPath(p, path);
        }

        var value = SelectedItem ?? string.Empty;
        // SingleLine pairs with VerticalCenter; see ModernCheckBox for why it matters.
        TextRenderer.DrawText(g, value, Font,
            new Rectangle(10, 0, Width - 34, Height),
            Enabled ? Theme.Text : Theme.TextDim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        // The chevron points down while the list is closed and up while it is open.
        var c = new PointF(Width - 15f, Height / 2f);
        using var pen = new Pen(Theme.TextDim, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        const float s = 3.6f;
        float dir = _open ? -1f : 1f;
        g.DrawLines(pen, new[]
        {
            new PointF(c.X - s, c.Y - s * 0.5f * dir),
            new PointF(c.X, c.Y + s * 0.5f * dir),
            new PointF(c.X + s, c.Y - s * 0.5f * dir)
        });
    }
}

/// <summary>Flat menu strip: no borders, a tinted hover pill, and an accent underline.</summary>
internal sealed class FlatMenuRenderer : ToolStripProfessionalRenderer
{
    public FlatMenuRenderer() : base(new FlatMenuColors()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Enabled) return;
        if (e.Item.Selected)
        {
            Theme.FillRounded(e.Graphics, new RectangleF(2, 1, e.Item.Width - 4, e.Item.Height - 2), Theme.RadiusSm, Theme.SurfaceHover);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Selected ? Theme.Text : Theme.TextMuted;
        e.TextFont = Theme.Ui;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawLine(p, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Theme.Canvas);
        e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.ToolStrip.Size));
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawLine(p, 4, e.Item.Height / 2, e.Item.Width - 4, e.Item.Height / 2);
    }

    private sealed class FlatMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color MenuItemSelected => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => Theme.SurfaceHover;
        public override Color MenuItemBorder => Theme.Border;
        public override Color MenuBorder => Theme.Border;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color ToolStripGradientBegin => Theme.Canvas;
        public override Color ToolStripGradientMiddle => Theme.Canvas;
        public override Color ToolStripGradientEnd => Theme.Canvas;
    }
}

/// <summary>Flat status bar: no 3D edges, dim labels, no grip.</summary>
internal sealed class FlatStatusRenderer : ToolStripProfessionalRenderer
{
    public FlatStatusRenderer() : base(new FlatStatusColors()) { }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawLine(p, 0, 0, e.ToolStrip.Width, 0);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Theme.Canvas);
        e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.ToolStrip.Size));
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.ForeColor == SystemColors.ControlText ? Theme.TextMuted : e.Item.ForeColor;
        base.OnRenderItemText(e);
    }

    private sealed class FlatStatusColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Canvas;
        public override Color ImageMarginGradientMiddle => Theme.Canvas;
        public override Color ImageMarginGradientEnd => Theme.Canvas;
        public override Color MenuItemSelected => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => Theme.SurfaceHover;
        public override Color MenuItemBorder => Theme.Border;
        public override Color MenuBorder => Theme.Border;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color ToolStripGradientBegin => Theme.Canvas;
        public override Color ToolStripGradientMiddle => Theme.Canvas;
        public override Color ToolStripGradientEnd => Theme.Canvas;
        public override Color StatusStripGradientBegin => Theme.Canvas;
        public override Color StatusStripGradientEnd => Theme.Canvas;
    }
}

/// <summary>
/// Owner-drawn tab strip. The active tab is a rounded card with an accent underline;
/// inactive tabs are flat. A file-type dot carries the language colour, and unsaved
/// work is a filled dot rather than a leading asterisk.
/// </summary>
internal sealed class ModernTabControl : TabControl
{
    public ModernTabControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        DrawMode = TabDrawMode.OwnerDrawFixed;
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(160, 34);
        // The control's own height has to match the strip, otherwise the leftover client
        // area is painted in the system window colour and shows as a white band.
        Height = 34;
        BackColor = Theme.Surface;
        Font = Theme.Ui;
        // The themed control paints its own strip in the system window colour, which
        // leaves a white band across the top of the editor.
        HandleCreated += (s, e) => Theme.DisableVisualStyles(this);
    }

    /// <summary>True when the tab has unsaved edits. Set by the host before invalidating.</summary>
    [Browsable(false)]

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]

    public Func<TabPage, bool> IsDirty { get; set; } = _ => false;

    /// <summary>The file path behind a tab, used to pick its type colour.</summary>
    [Browsable(false)]

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]

    public Func<TabPage, string?> PathOf { get; set; } = _ => null;

    /// <summary>
    /// Raised when the cross on a tab is clicked. The host decides what closing
    /// means - a tab with unsaved work may want to ask first.
    /// </summary>
    public event EventHandler<TabCloseEventArgs>? TabCloseRequested;

    /// <summary>
    /// The cross on a tab.
    ///
    /// It is a real control rather than a painted mark, so it can be reached with the
    /// keyboard, named for a screen reader, and clicked by a test. The strip is
    /// owner-drawn, so the native tab control cannot describe its tabs to assistive
    /// technology at all - a painted cross would be invisible to everyone but the
    /// mouse.
    /// </summary>
    private sealed class TabCloseButton : Control
    {
        private bool _hot;
        private Color _backdrop = Theme.Surface;

        public TabCloseButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(14, 14);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
        }

        /// <summary>The colour behind the cross, so it does not show as a patch.</summary>
        [Browsable(false)]

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]

        public Color Backdrop
        {
            get => _backdrop;
            set { if (_backdrop != value) { _backdrop = value; Invalidate(); } }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hot = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hot = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData == Keys.Space || keyData == Keys.Enter || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            // A control that is not a Button does not turn the keyboard into a
            // click by itself, so the cross does it here.
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var b = new SolidBrush(_backdrop)) g.FillRectangle(b, ClientRectangle);

            var color = _hot || Focused ? Theme.Text : Theme.TextDim;
            var c = new PointF(Width / 2f, Height / 2f);
            const float h = 3.6f;
            using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(c.X - h, c.Y - h),
                new PointF(c.X + h, c.Y + h)
            });
            g.DrawLines(pen, new[]
            {
                new PointF(c.X + h, c.Y - h),
                new PointF(c.X - h, c.Y + h)
            });

            // The keyboard has to be able to see where it is.
            if (Focused)
            {
                using var ring = new Pen(Theme.Cyan, 1.2f);
                g.DrawEllipse(ring, 1.2f, 1.2f, Width - 2.4f, Height - 2.4f);
            }
        }
    }

    /// <summary>Where the cross of a tab sits.</summary>
    private Rectangle CloseRectOf(int index)
    {
        var r = GetTabRect(index);
        const int size = 14;
        return new Rectangle(r.Right - size - 8, r.Y + (r.Height - size) / 2, size, size);
    }

    /// <summary>
    /// The bounds of a tab, in screen coordinates.
    ///
    /// <see cref="TabControl.GetTabRect"/> is protected, so the accessibility object
    /// reaches it through here.
    /// </summary>
    internal Rectangle TabBounds(int index) =>
        index >= 0 && index < TabCount ? RectangleToScreen(GetTabRect(index)) : Rectangle.Empty;

    /// <summary>One close cross per tab.</summary>
    private readonly List<TabCloseButton> _closeButtons = new();

    /// <summary>
    /// Keeps the crosses in step with the tabs: one for each, over the tab it
    /// belongs to, named after the file it closes.
    ///
    /// The crosses live in the panel that holds the strip, not in the tab control
    /// itself: WinForms refuses any child of a TabControl that is not a TabPage.
    /// </summary>
    private void SyncCloseButtons()
    {
        var host = Parent;
        if (host is null) return;

        try
        {
            while (_closeButtons.Count > TabCount)
            {
                var b = _closeButtons[^1];
                _closeButtons.RemoveAt(_closeButtons.Count - 1);
                b.Click -= OnCloseButtonClick;
                host.Controls.Remove(b);
                b.Dispose();
            }

            while (_closeButtons.Count < TabCount)
            {
                var b = new TabCloseButton();
                b.Click += OnCloseButtonClick;
                host.Controls.Add(b);
                _closeButtons.Add(b);
            }

            for (int i = 0; i < _closeButtons.Count; i++)
            {
                var b = _closeButtons[i];
                var page = TabPages[i];
                var rect = CloseRectOf(i);

                // The tab rectangle is in the strip's own coordinates, so it has to
                // travel through the screen to reach the panel that holds the cross.
                var at = host.PointToClient(PointToScreen(rect.Location));

                b.Location = at;
                b.Size = new Size(rect.Width, rect.Height);
                b.Backdrop = SelectedIndex == i ? Theme.SurfaceRaised : Theme.Surface;
                b.AccessibleName = "Close " + page.Text;
                b.BringToFront();
            }
        }
        catch (Exception ex)
        {
            // The crosses are decoration on top of the tab strip. If they cannot be
            // placed, the strip still works - it must never take the app down.
            AppLog.Write("tabs", "the close crosses could not be placed", ex);
        }
    }

    private void OnCloseButtonClick(object? sender, EventArgs e)
    {
        if (sender is not TabCloseButton b) return;
        int index = _closeButtons.IndexOf(b);
        if (index < 0 || index >= TabCount) return;

        TabCloseRequested?.Invoke(this, new TabCloseEventArgs(TabPages[index], index));
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        SyncCloseButtons();
    }

    protected override void OnControlRemoved(ControlEventArgs e)
    {
        base.OnControlRemoved(e);
        SyncCloseButtons();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        SyncCloseButtons();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // The tab count changes before the removal is reported, so the crosses are
        // brought in step here as well: a cross without a tab is a cross that stays
        // on screen after the tab is gone.
        SyncCloseButtons();

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // Clear the whole surface: a double-buffered control is not erased by the
        // system, so stale pixels from a previous layout would otherwise show through.
        using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, ClientRectangle);

        var strip = new Rectangle(0, 0, Width, ItemSize.Height);

        using (var b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, strip);

        for (int i = 0; i < TabCount; i++)
        {
            var page = TabPages[i];
            var r = GetTabRect(i);
            bool selected = SelectedIndex == i;

            var rect = new RectangleF(r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6);

            if (selected)
            {
                Theme.FillRounded(g, rect, Theme.RadiusSm, Theme.SurfaceRaised);
                using (var p = new Pen(Theme.Border))
                using (var path = Theme.RoundedRect(rect, Theme.RadiusSm))
                {
                    g.DrawPath(p, path);
                }
                // Accent underline on the active tab.
                using (var accent = new SolidBrush(Theme.VioletBright))
                using (var path = Theme.RoundedRect(new RectangleF(rect.X + 10, rect.Bottom - 2.5f, rect.Width - 20, 2.5f), 1.5f))
                {
                    g.FillPath(accent, path);
                }
            }

            var path2 = PathOf(page);
            var color = path2 is null ? Theme.TextDim : Theme.FileTypeColorFor(path2);

            float dotX = rect.X + 12f;
            if (selected || path2 is not null)
            {
                Marks.Dot(g, new PointF(dotX, rect.Y + rect.Height / 2f), 3.2f, color);
            }
            dotX += 12f;

            bool dirty = IsDirty(page);
            var text = dirty ? (page.Text.TrimStart('*')) : page.Text;
            var fg = selected ? Theme.Text : Theme.TextMuted;

            // The cross takes the right end of the tab, so the caption stops before it
            // and the unsaved dot moves left of the cross.
            var close = CloseRectOf(i);
            var textRect = new RectangleF(dotX, rect.Y, close.X - dotX - 6f, rect.Height);
            TextRenderer.DrawText(g, text, Font,
                new Rectangle((int)textRect.X, (int)textRect.Y, (int)textRect.Width, (int)textRect.Height),
                fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            if (dirty)
            {
                Marks.Dot(g, new PointF(close.X - 9f, rect.Y + rect.Height / 2f), 3f, Theme.Cyan);
            }
        }
    }

    /// <summary>
    /// The accessibility object behind the tab strip.
    ///
    /// The strip is painted by hand, so the stock TabControl accessibility has
    /// nothing to describe it with: a screen reader finds no tabs at all. Each tab
    /// is published here as a page-tab child with its name, its bounds and its
    /// selected state, which is also what lets a test reach the close cross.
    /// </summary>
    protected override AccessibleObject CreateAccessibilityInstance() => new TabStripAccessibleObject(this);

    private sealed class TabStripAccessibleObject : ControlAccessibleObject
    {
        private readonly ModernTabControl _owner;
        private readonly List<TabAccessibleObject> _tabs = new();

        public TabStripAccessibleObject(ModernTabControl owner) : base(owner) => _owner = owner;

        public override AccessibleRole Role => AccessibleRole.PageTabList;

        public override int GetChildCount()
        {
            if (_tabs.Count != _owner.TabCount)
            {
                _tabs.Clear();
                for (int i = 0; i < _owner.TabCount; i++) _tabs.Add(new TabAccessibleObject(_owner, i));
            }
            return _tabs.Count;
        }

        public override AccessibleObject? GetChild(int childId) =>
            childId >= 0 && childId < _tabs.Count ? _tabs[childId] : null;

        public override AccessibleObject? GetSelected()
        {
            int index = _owner.SelectedIndex;
            return index >= 0 && index < _tabs.Count ? _tabs[index] : null;
        }
    }

    /// <summary>One tab, as an accessible object.</summary>
    private sealed class TabAccessibleObject : AccessibleObject
    {
        private readonly ModernTabControl _owner;

        public TabAccessibleObject(ModernTabControl owner, int index)
        {
            _owner = owner;
            Index = index;
        }

        public int Index { get; }

        public override AccessibleRole Role => AccessibleRole.PageTab;

        public override string? Name =>
            Index < _owner.TabCount ? _owner.TabPages[Index].Text : null;

        public override string? DefaultAction => "Switch";

        public override AccessibleStates State =>
            Index < _owner.TabCount && _owner.SelectedIndex == Index
                ? AccessibleStates.Selected | AccessibleStates.Focusable
                : AccessibleStates.Focusable;

        public override Rectangle Bounds => _owner.TabBounds(Index);

        public override void DoDefaultAction()
        {
            if (Index < _owner.TabCount) _owner.SelectedIndex = Index;
        }
    }
}

/// <summary>Which tab was closed, and where it was in the strip.</summary>
internal sealed class TabCloseEventArgs : EventArgs
{
    public TabCloseEventArgs(TabPage page, int index)
    {
        Page = page;
        Index = index;
    }

    public TabPage Page { get; }
    public int Index { get; }
}

/// <summary>
/// A checkbox drawn in the theme: a rounded square that fills with the brand gradient
/// when checked. The stock WinForms check mark is invisible on a dark surface.
/// </summary>
internal sealed class ModernCheckBox : CheckBox
{
    private bool _hover;

    public ModernCheckBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.Canvas;
        ForeColor = Theme.TextMuted;
        Font = Theme.Ui;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // A double-buffered control is not erased by the system, so whatever was in the
        // buffer survives the repaint. Without this the row keeps a ghost of whatever
        // was drawn there before the layout settled.
        using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);

        var box = new RectangleF(3f, (Height - 15f) / 2f, 15f, 15f);
        bool on = Checked;
        bool hot = Enabled && (_hover || Focused);

        using (var path = Theme.RoundedRect(box, 4.5f))
        {
            if (on)
            {
                using var b = new LinearGradientBrush(box, Theme.Violet, Theme.VioletBright, LinearGradientMode.Horizontal);
                g.FillPath(b, path);
            }
            else
            {
                using var b = new SolidBrush(Theme.SurfaceRaised);
                g.FillPath(b, path);
                using var p = new Pen(hot ? Theme.VioletBright : Theme.BorderStrong);
                g.DrawPath(p, path);
            }
        }

        if (on)
        {
            using var p = new Pen(Color.White, 1.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(p, new[]
            {
                new PointF(box.X + 3.6f, box.Y + box.Height / 2f),
                new PointF(box.X + box.Width * 0.44f, box.Bottom - 4.4f),
                new PointF(box.X + box.Width - 3.6f, box.Y + 4.4f)
            });
        }

        var fg = !Enabled ? Theme.TextDim : on ? Theme.Text : Theme.TextMuted;
        // SingleLine is not optional next to VerticalCenter. Without it GDI ignores the
        // vertical centring and measures the string against a wrapped rectangle, so the
        // caption lands below the control and a sliver of it bleeds into the next row.
        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(26, 0, Math.Max(10, Width - 30), Height),
            fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// A section caption: an uppercase, letter-spaced title on a raised strip with a
/// hairline under it, and an optional right-aligned hint. It is what turns a wall of
/// panes into named sections.
/// </summary>
internal sealed class SectionHeader : Panel
{
    private readonly Label _title;
    private readonly Label _hint;

    public SectionHeader(string text, string hint = "")
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        Height = 30;

        _title = new Label
        {
            Dock = DockStyle.Fill,
            Text = text.ToUpperInvariant(),
            ForeColor = Theme.TextMuted,
            BackColor = Theme.Surface,
            Font = Theme.UiSemibold,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 0, 1)
        };
        _hint = new Label
        {
            Dock = DockStyle.Right,
            Width = 120,
            Text = hint,
            ForeColor = Theme.TextDim,
            BackColor = Theme.Surface,
            Font = Theme.CodeSmall,
            TextAlign = System.Drawing.ContentAlignment.MiddleRight,
            Padding = new Padding(0, 0, 14, 1)
        };

        Controls.Add(_title);
        Controls.Add(_hint);
    }

    public void SetHint(string hint) => _hint.Text = hint;

    protected override void OnPaint(PaintEventArgs e)
    {
        using var b = new SolidBrush(Theme.Surface);
        e.Graphics.FillRectangle(b, ClientRectangle);
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
    }
}

/// <summary>
/// Owner-drawn file tree. Each file carries a type-coloured dot, directories a chevron,
/// and the selection is a rounded highlight instead of the system rectangle.
/// </summary>
internal sealed class ModernTreeView : TreeView
{
    public ModernTreeView()
    {
        DrawMode = TreeViewDrawMode.OwnerDrawText;
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        BorderStyle = BorderStyle.None;
        HideSelection = false;
        ItemHeight = 22;
        ShowLines = false;
        ShowRootLines = false;
        ShowPlusMinus = false;
        Font = Theme.Ui;
        Indent = 16;
    }

    protected override void OnDrawNode(DrawTreeNodeEventArgs e)
    {
        if (e.Node is null) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        bool selected = (e.State & TreeNodeStates.Selected) != 0;
        var textColor = selected ? Theme.Text : (e.Node.Tag is string ? Theme.TextMuted : Theme.Text);
        var text = e.Node.Text;

        var indent = e.Node.Level * Indent;
        var bounds = e.Bounds;

        if (selected)
        {
            Theme.FillRounded(g, new RectangleF(bounds.X - 2, bounds.Y + 1, bounds.Width + 2, bounds.Height - 2),
                Theme.RadiusSm, Theme.SurfaceHover);
        }

        float x = bounds.X + 4 + indent;
        var centerY = bounds.Y + bounds.Height / 2f;

        bool isDir = e.Node.Nodes.Count > 0 || (e.Node.Tag is string dp && Directory.Exists(dp));

        if (isDir)
        {
            Marks.Chevron(g, new PointF(x + 5f, centerY), selected ? Theme.VioletBright : Theme.TextDim, e.Node.IsExpanded);
            Marks.Dot(g, new PointF(x + 20f, centerY), 3.4f, Theme.Violet);
            x += 32f;
        }
        else
        {
            var path = e.Node.Tag as string;
            var color = path is null ? Theme.TextDim : Theme.FileTypeColorFor(path);
            Marks.Dot(g, new PointF(x + 5f, centerY), 3.4f, color);
            x += 16f;
        }

        // e.Bounds is the node's own label rectangle, so it is only as wide as the text.
        // The draw rectangle has to run to the edge of the tree, otherwise the label is
        // ellipsised to nothing.
        TextRenderer.DrawText(g, text, Font,
            new Rectangle((int)x, bounds.Y, Math.Max(10, ClientSize.Width - (int)x - 6), bounds.Height),
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}



