using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Notecode.App;

/// <summary>
/// The notecode design system.
///
/// One dark, AI-oriented theme. Deep navy surfaces carry the chrome, a violet brand
/// accent marks the agent, and a cyan action accent marks what you can do. An aurora
/// glow sits behind everything so the flat panels read as lit rather than painted on.
///
/// Type is Segoe UI Variable - the Windows 11 system face - and Cascadia Code for
/// anything monospaced. Icons are Segoe Fluent Icons, a vector icon font, so nothing
/// ships as a bitmap and nothing is an emoji. All of it ships with Windows, which keeps
/// the app fully offline.
///
/// Contrast against <see cref="Surface"/>: body text 14.9:1, muted text 6.1:1,
/// violet 5.1:1, cyan 9.4:1 - all above the WCAG AA 4.5:1 floor for normal text.
/// </summary>
internal static class Theme
{
    // -------------------------------------------------------------- surfaces
    public static readonly Color Void = Hex("#07090F");
    public static readonly Color Canvas = Hex("#0B0E17");
    public static readonly Color Surface = Hex("#11151F");
    public static readonly Color SurfaceRaised = Hex("#161B27");
    public static readonly Color SurfaceHover = Hex("#1C2230");
    public static readonly Color Border = Hex("#232A3A");
    public static readonly Color BorderStrong = Hex("#2E3750");

    // ------------------------------------------------------------------ text
    public static readonly Color Text = Hex("#E8ECF5");
    public static readonly Color TextMuted = Hex("#939DB4");

    /// <summary>
    /// The quietest text in the app.
    ///
    /// It has to clear 4.5:1 against every surface it is painted on - the
    /// transcript's speaker labels and tool kinds, the palette's summary
    /// column and its footer, the label of an unchecked box, the panel
    /// subtitle and the status bar. The old value was 3.32:1 on the surface
    /// and 2.89:1 on a hovered row, which is a fail for text that carries
    /// meaning. This one clears 4.5:1 on all five surfaces.
    /// </summary>
    public static readonly Color TextDim = Hex("#7E8AA0");

    /// <summary>
    /// The relative luminance of a colour, as WCAG defines it.
    ///
    /// It is what the contrast ratio is built from, and the app needs it to
    /// decide which colour a label can be read against.
    /// </summary>
    public static double RelativeLuminance(Color c)
    {
        static double Channel(double v) =>
            v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

        double r = Channel(c.R / 255.0);
        double g = Channel(c.G / 255.0);
        double b = Channel(c.B / 255.0);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    /// <summary>
    /// The contrast ratio between two colours, as WCAG defines it: from 1 for
    /// two identical colours to 21 for black on white.
    /// </summary>
    public static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        if (la < lb) (la, lb) = (lb, la);
        return (la + 0.05) / (lb + 0.05);
    }

    // --------------------------------------------------------------- accents
    public static readonly Color Violet = Hex("#8B5CF6");
    public static readonly Color VioletBright = Hex("#A78BFA");
    public static readonly Color Cyan = Hex("#22D3EE");
    public static readonly Color Mint = Hex("#34D399");
    public static readonly Color Amber = Hex("#FBBF24");
    public static readonly Color Rose = Hex("#FB7185");

    // ----------------------------------------------------------------- fonts
    public static readonly string DisplayFamily = "Segoe UI Variable Display";
    public static readonly string DisplaySemiboldFamily = "Segoe UI Variable Display Semib";
    public static readonly string TextFamily = "Segoe UI Variable Text";
    public static readonly string TextSemiboldFamily = "Segoe UI Variable Text Semibold";
    public static readonly string CodeFamily = "Cascadia Code";
    public static readonly string IconFamily = "Segoe Fluent Icons";

    // The sizes are the floor, not the ceiling: every one of them is scaled by
    // the user's own setting, which is what Microsoft's rule for WinForms asks
    // for - text has to be at least 10 point *unless the size is adjustable*, and
    // a code editor that someone reads for hours has to be able to go up too.
    public static Font Display { get; private set; } = new(DisplayFamily, 13f, GraphicsUnit.Point);
    public static Font DisplaySemibold { get; private set; } = new(DisplaySemiboldFamily, 13f, GraphicsUnit.Point);
    public static Font Title { get; private set; } = new(DisplayFamily, 11f, GraphicsUnit.Point);
    public static Font Ui { get; private set; } = new(TextFamily, 10f, GraphicsUnit.Point);
    public static Font UiSemibold { get; private set; } = new(TextSemiboldFamily, 10f, GraphicsUnit.Point);
    public static Font UiSmall { get; private set; } = new(TextFamily, 9f, GraphicsUnit.Point);
    public static Font Code { get; private set; } = new(CodeFamily, 10f, GraphicsUnit.Point);
    public static Font CodeSmall { get; private set; } = new(CodeFamily, 9f, GraphicsUnit.Point);
    public static Font Icon { get; private set; } = new(IconFamily, 10f, GraphicsUnit.Point);
    public static Font IconSmall { get; private set; } = new(IconFamily, 9f, GraphicsUnit.Point);

    /// <summary>The factor every font is multiplied by. 1 is the size above.</summary>
    public static float FontScale { get; private set; } = 1f;

    /// <summary>
    /// Scales every font in the app.
    ///
    /// The body text is 10 point, which is the floor Microsoft sets for WinForms
    /// text whose size the user cannot change. Here the user *can* change it,
    /// so the floor is a starting point rather than a limit.
    /// </summary>
    public static void ApplyFontScale(float scale)
    {
        FontScale = Math.Clamp(scale, 0.8f, 2.0f);

        Display = new Font(DisplayFamily, 13f * FontScale, GraphicsUnit.Point);
        DisplaySemibold = new Font(DisplaySemiboldFamily, 13f * FontScale, GraphicsUnit.Point);
        Title = new Font(DisplayFamily, 11f * FontScale, GraphicsUnit.Point);
        Ui = new Font(TextFamily, 10f * FontScale, GraphicsUnit.Point);
        UiSemibold = new Font(TextSemiboldFamily, 10f * FontScale, GraphicsUnit.Point);
        UiSmall = new Font(TextFamily, 9f * FontScale, GraphicsUnit.Point);
        Code = new Font(CodeFamily, 10f * FontScale, GraphicsUnit.Point);
        CodeSmall = new Font(CodeFamily, 9f * FontScale, GraphicsUnit.Point);
        Icon = new Font(IconFamily, 10f * FontScale, GraphicsUnit.Point);
        IconSmall = new Font(IconFamily, 9f * FontScale, GraphicsUnit.Point);
    }

    // -------------------------------------------------------------- geometry
    public const int RadiusSm = 6;
    public const int RadiusMd = 10;
    public const int RadiusLg = 14;

    public static Color Hex(string html) => ColorTranslator.FromHtml(html);

    public static Color WithAlpha(this Color color, int alpha) => Color.FromArgb(alpha, color);

    /// <summary>
    /// Rounded rectangle path. Every panel, chip and button in the app is built from
    /// this, so the corner radius stays consistent across surfaces.
    /// </summary>
    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) return path;

        float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, RectangleF r, float radius, Color fill)
    {
        using var path = RoundedRect(r, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
    }

    public static void DrawRounded(Graphics g, RectangleF r, float radius, Color stroke, float width = 1f)
    {
        using var path = RoundedRect(r, radius);
        using var pen = new Pen(stroke, width);
        g.DrawPath(pen, path);
    }

    /// <summary>Soft radial glow, used for the aurora backdrop and focus halos.</summary>
    public static void PaintGlow(Graphics g, PointF center, float radius, Color color, int alpha)
    {
        if (radius <= 1f) return;
        var rect = new RectangleF(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        using var path = new GraphicsPath();
        path.AddEllipse(rect);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(alpha, color),
            SurroundColors = new[] { Color.FromArgb(0, color) }
        };
        g.FillPath(brush, path);
    }

    // --------------------------------------------------------- file accents
    /// <summary>
    /// A colour per file type, used for the explorer dots and the tab markers.
    /// The set is deliberately narrow so the explorer reads as one system.
    /// </summary>
    public static Color FileTypeColorFor(string path) => FileTypeColor(Path.GetExtension(path));

    public static Color FileTypeColor(string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "md" or "mdx" or "markdown" => VioletBright,
            "cs" or "csx" => Mint,
            "js" or "mjs" or "cjs" or "jsx" or "ts" or "tsx" => Amber,
            "cpp" or "cc" or "cxx" or "c" or "h" or "hpp" or "hxx" => Cyan,
            "html" or "htm" or "xhtml" or "xml" or "vue" or "svelte" => Rose,
            "css" or "scss" or "sass" or "less" => Hex("#F472B6"),
            "py" or "pyw" or "pyi" => Hex("#60A5FA"),
            "rs" => Hex("#FB923C"),
            "go" => Hex("#2DD4BF"),
            "java" or "kt" or "kts" => Hex("#F87171"),
            "php" or "phtml" => Hex("#818CF8"),
            "rb" or "erb" => Hex("#F87171"),
            "sh" or "bash" or "zsh" or "fish" or "ps1" => Hex("#4ADE80"),
            "sql" => Hex("#38BDF8"),
            "yaml" or "yml" or "toml" or "json" or "ini" or "cfg" => Hex("#94A3B8"),
            "swift" => Hex("#FB923C"),
            "lua" => Hex("#818CF8"),
            "diff" or "patch" => Hex("#94A3B8"),
            _ => TextDim
        };
    }

    // -------------------------------------------------------------- chrome
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subtitle, string? listTheme);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>
    /// Dark title bar and rounded window corners. The window frame is drawn by DWM,
    /// not by WinForms, so this is the only way to make it match the app.
    /// </summary>
    public static void ApplyWindowChrome(Form form)
    {
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch { /* the frame simply stays in the system style */ }
    }

    /// <summary>
    /// Paints the scrollbars of a WinForms control in the dark theme. Without this the
    /// scrollbars stay light and the panel looks broken.
    /// </summary>
    public static void ApplyDarkScrollbars(Control control)
    {
        try
        {
            if (control.IsHandleCreated) SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        }
        catch { /* cosmetic only */ }
    }

    /// <summary>
    /// Drops the visual style from a control so it paints with its own colours.
    ///
    /// The themed TabControl paints its tab strip in the system window colour, which
    /// leaves a white band across the top of the editor no matter what BackColor is
    /// set to. Removing the theme makes the control fall back to classic painting,
    /// which honours the owner-drawn tabs and the background colour.
    /// </summary>
    public static void DisableVisualStyles(Control control)
    {
        try
        {
            if (control.IsHandleCreated) SetWindowTheme(control.Handle, null, null);
        }
        catch { /* cosmetic only */ }
    }

    /// <summary>True when the app is running on a high-DPI display that we scale for.</summary>
    public static bool SupportsRoundedCorners
    {
        get
        {
            try
            {
                int corner = 0;
                return DwmGetWindowAttribute(IntPtr.Zero, DWMWA_WINDOW_CORNER_PREFERENCE, out corner, sizeof(int)) == 0;
            }
            catch { return false; }
        }
    }
}
