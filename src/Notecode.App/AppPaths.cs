namespace Notecode.App;

/// <summary>
/// Where notecode keeps the things that belong to the user rather than to the
/// checkout: the conversations, the saved hardware profiles.
/// </summary>
internal static class AppPaths
{
    /// <summary>
    /// The folder the user's data lives in.
    ///
    /// It is %APPDATA%\notecode by default. NOTECODE_DATA_DIR moves it.
    ///
    /// The override is there for two reasons. A portable install keeps its
    /// data beside the program instead of in the profile. And a test can run
    /// the app against a profile of its own: the app puts the last
    /// conversation back on screen when it starts, so a test that shares the
    /// user's profile reads the previous run's conversation as if it were its
    /// own - and a check that finds a word there is answering a question
    /// about a run that has already finished.
    ///
    /// The name is the whole of the contract: a folder, and the app puts
    /// everything of its own under it.
    /// </summary>
    public static string DataDir
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("NOTECODE_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                try { return Path.GetFullPath(overridden); }
                catch { /* fall through to the default */ }
            }

            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
            return Path.Combine(root, "notecode");
        }
    }
}
