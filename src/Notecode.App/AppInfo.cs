using System.Reflection;

namespace Notecode.App;

/// <summary>
/// The version of the app, and the icon it wears.
///
/// The version is read from the assembly rather than written down twice, so the
/// About box, the title bar and the built executable cannot disagree with each
/// other. The number itself lives in the project file, and the build stamps it
/// into the assembly.
/// </summary>
internal static class AppInfo
{
    private static string? _version;

    /// <summary>The version, for example "0.2.0".</summary>
    public static string Version => _version ??= ResolveVersion();

    /// <summary>The name and the version, for a title bar and an About box.</summary>
    public static string Display => $"notecode {Version}";

    /// <summary>Where the app is installed, for the About box.</summary>
    public static string InstallPath => AppContext.BaseDirectory;

    private static string ResolveVersion()
    {
        Version? v;
        try { v = typeof(AppInfo).Assembly.GetName().Version; }
        catch { v = null; }
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}

/// <summary>
/// When this copy of the app was built.
///
/// The date comes from the executable on disk, which is the honest answer:
/// it is when this copy was put together, whether that was on the build
/// machine or on the machine that installed it.
/// </summary>
internal static class BuildInfo
{
    /// <summary>The build date, for example "2026-10-21 12:31".</summary>
    public static string BuildStamp
    {
        get
        {
            try
            {
                var exe = Path.Combine(AppContext.BaseDirectory, "notecode.exe");
                if (File.Exists(exe))
                    return new FileInfo(exe).LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            }
            catch { }
            return "unknown";
        }
    }
}

/// <summary>
/// The app icon, loaded from the .ico that is embedded in the assembly.
///
/// The file is built by <c>scripts/build-icon.ps1</c> and carries every size
/// Windows asks for, from 16 pixels in the notification area to 256 in the
/// large icons view. One icon for the window, the task bar, the notification
/// area and the shortcut, so there is only one icon to keep right.
///
/// It is embedded rather than shipped beside the executable because a single
/// file publish has no loose files to find at run time.
/// </summary>
internal static class AppIcon
{
    private static Icon? _main;

    /// <summary>The name of the embedded resource.</summary>
    public const string ResourceName = "notecode.ico";

    /// <summary>True when the icon is in the assembly.</summary>
    public static bool Exists
    {
        get
        {
            try
            {
                using var s = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);
                return s is not null;
            }
            catch { return false; }
        }
    }

    /// <summary>The icon for the window and the task bar.</summary>
    public static Icon? Main => _main ??= Load(32);

    /// <summary>
    /// The icon at a given size.
    ///
    /// The notification area asks for 16, 20 or 24 pixels depending on the
    /// display scale, and it is given the frame that matches rather than a
    /// stretched one.
    /// </summary>
    public static Icon? Load(int size)
    {
        try
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return null;
            using var icon = new Icon(stream, new Size(size, size));
            return (Icon)icon.Clone();
        }
        catch
        {
            return null;
        }
    }
}
