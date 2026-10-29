using System.Diagnostics;

namespace Notecode.Installer;

/// <summary>An extra pack: an installer on the download site that installs per user and takes --silent.</summary>
/// <param name="AsksUser">
/// The pack's own installer has something to ask (Meshy's asks for the API key),
/// so in the window it is run with its window, not with --silent.
/// </param>
internal sealed record Pack(string Key, string Title, string Detail, string Url, string File, Version? Node, bool AsksUser = false);

/// <summary>
/// The extra packs the installer can add after the app.
///
/// They are not in the payload. REA alone is 75 MB, and most machines never
/// use it, so a pack is downloaded from the site only when its box is ticked.
/// This list matches the one in install.ps1 on the site, so the window and the
/// one-liner offer the same packs from the same addresses.
/// </summary>
internal static class Packs
{
    public const string Site = "https://notecode.globalwarningnetworks.com";

    public static readonly Pack[] All =
    {
        new("rea",
            "REA reverse-engineering tools (170 MCP tools)",
            "A 75 MB download. The tools run on Node 22.19 or newer.",
            $"{Site}/downloads/mcp/rea_installer.exe",
            "rea_installer.exe",
            new Version(22, 19, 0)),
        new("meshy",
            "Meshy AI 3D generation (28 MCP tools)",
            "A 55 MB download. Needs Node 18 or newer and a Meshy API key, which it asks for.",
            $"{Site}/downloads/mcp/meshy_installer.exe",
            "meshy_installer.exe",
            new Version(18, 0, 0),
            AsksUser: true),
    };

    public static Pack? Find(string key) =>
        All.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Says why the pack will not run yet, or null when it will. A missing
    /// Node is said, not refused: the pack installs fine and works once Node
    /// is there.
    /// </summary>
    public static string? NodeProblem(Pack pack)
    {
        if (pack.Node is null) return null;

        string? output = null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is not null)
            {
                output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);
            }
        }
        catch { }

        if (string.IsNullOrEmpty(output))
            return $"Node.js was not found. {pack.Key} needs Node {pack.Node} or newer to run (https://nodejs.org).";
        if (Version.TryParse(output.TrimStart('v'), out var have) && have < pack.Node)
            return $"Node {output} is too old. {pack.Key} needs Node {pack.Node} or newer.";
        return null;
    }

    /// <summary>
    /// Downloads the pack's installer into the temporary folder, checks that it
    /// is a Windows program and runs it with --silent. Throws with a message a
    /// person can read when any of that fails.
    /// </summary>
    public static async Task InstallAsync(Pack pack, IProgress<string>? progress = null, CancellationToken ct = default, bool silent = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), "notecode-install");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, pack.File);

        progress?.Report($"Downloading {pack.File}");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("notecode-install");
            using var response = await http.GetAsync(pack.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"the download answered {(int)response.StatusCode} {response.ReasonPhrase}");

            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = System.IO.File.Create(file);

            // The file is written as it arrives rather than held in memory.
            var buffer = new byte[1 << 16];
            long done = 0;
            var lastPercent = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total is > 0)
                {
                    var percent = (int)(done * 100 / total.Value);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report($"Downloading {pack.File}  {percent}%  ({done / 1048576.0:N1} of {total.Value / 1048576.0:N1} MB)");
                    }
                }
            }
        }

        // A page that answers with an error is not run as a program: the file
        // has to be big enough to be the installer and start with "MZ".
        var info = new FileInfo(file);
        if (info.Length < 1048576)
            throw new InvalidDataException($"the downloaded file is {info.Length} bytes, which is too small to be the installer");
        using (var check = System.IO.File.OpenRead(file))
        {
            if (check.ReadByte() != 0x4D || check.ReadByte() != 0x5A)
                throw new InvalidDataException("the downloaded file is not a Windows program");
        }

        progress?.Report($"Installing {pack.Key}");
        using var run = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            // A silent install of notecode installs the packs silently too; in
            // the window, a pack that has a question gets its own window.
            Arguments = silent || !pack.AsksUser ? "--silent" : "",
            UseShellExecute = true,
            WorkingDirectory = folder
        }) ?? throw new InvalidOperationException($"the {pack.Key} installer would not start");
        await run.WaitForExitAsync(ct);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"the {pack.Key} installer stopped with code {run.ExitCode}");
    }
}
