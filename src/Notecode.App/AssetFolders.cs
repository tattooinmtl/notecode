namespace Notecode.App;

/// <summary>
/// The asset tree inside the workspace: one folder per kind of thing the tools
/// produce, so a generated model, a Blender file and a REA report each have an
/// obvious home instead of landing wherever the last command was pointed.
///
/// The tree is created when the app starts and again before any command that
/// writes into it, so deleting a folder by hand is never an error.
/// </summary>
internal static class AssetFolders
{
    /// <summary>The folders under assets\, in the order they are created.</summary>
    public static readonly string[] Names =
        { "blender", "unity", "img2threejs", "threejs", "textures", "logos", "REA_outputs" };

    /// <summary>The assets folder of a workspace.</summary>
    public static string Root(string workspaceRoot) => Path.Combine(workspaceRoot, "assets");

    /// <summary>One folder of the tree, for example <c>Folder(root, "img2threejs")</c>.</summary>
    public static string Folder(string workspaceRoot, string name) => Path.Combine(Root(workspaceRoot), name);

    /// <summary>Creates whatever part of the tree is missing. Returns the assets folder.</summary>
    public static string Ensure(string workspaceRoot)
    {
        var root = Root(workspaceRoot);
        foreach (var name in Names)
            Directory.CreateDirectory(Path.Combine(root, name));
        return root;
    }

    /// <summary>
    /// A new folder for one job: <c>name-yyyyMMdd-HHmmss</c> under the given parent.
    /// Every run gets its own, so a second run never overwrites the first.
    /// </summary>
    public static string NewJobFolder(string parent, string name)
    {
        var safe = new string((name ?? "").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray())
            .Trim('-').ToLowerInvariant();
        if (safe.Length == 0) safe = "job";
        if (safe.Length > 48) safe = safe[..48];
        var path = Path.Combine(parent, $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(path);
        return path;
    }
}
