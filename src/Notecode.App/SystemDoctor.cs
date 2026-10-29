using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Notecode.App;

// ===================================================================== recipes

/// <summary>
/// The known-good setup of one MCP server or skill: runtime/doctor/recipes/*.json.
///
/// A recipe is written while the machine is online - by hand, by a cloud model,
/// or by /addtool - and read by the doctor when it is not. It is what the
/// doctor compares the live config and the machine against, so the local model
/// never has to know how a server is meant to be connected: the recipe says.
/// </summary>
internal sealed class DoctorRecipe
{
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public RecipeConfig? Config { get; set; }
    public RecipeNeeds? Needs { get; set; }
    public RecipeApp? App { get; set; }
    public RecipeKey? Key { get; set; }
    public string? Folder { get; set; }

    /// <summary>
    /// The extra pack that installs this server, for a server that is not part of
    /// the app. A pack that is not there is not installed, not broken.
    /// </summary>
    public string? Pack { get; set; }
    public RecipeExpect? Expect { get; set; }
    public List<string> Fixes { get; set; } = new();
    public string HowToUse { get; set; } = "";

    // Skills
    public string? Path { get; set; }
    public List<string> Requires { get; set; } = new();
    public List<string> Optional { get; set; } = new();

    // providers.json
    public Dictionary<string, RecipePages> Pages { get; set; } = new();
}

internal sealed class RecipeConfig
{
    public string Transport { get; set; } = "stdio";
    public string? Command { get; set; }
    public List<string> Args { get; set; } = new();
    public string? Url { get; set; }
}

internal sealed class RecipeNeeds
{
    public List<RecipeCommand> Commands { get; set; } = new();
    public List<RecipeFile> Files { get; set; } = new();
}

internal sealed class RecipeCommand
{
    public string Name { get; set; } = "";
    public string? Min { get; set; }
    public string Get { get; set; } = "";
}

internal sealed class RecipeFile
{
    public string Path { get; set; } = "";
    public string What { get; set; } = "";
    public string? Fix { get; set; }
}

/// <summary>The program a server lives in, for a server that is not started by the app.</summary>
internal sealed class RecipeApp
{
    public string Name { get; set; } = "";
    public string Process { get; set; } = "";
    public List<string> Exe { get; set; } = new();
    public string Enable { get; set; } = "";
}

internal sealed class RecipeKey
{
    public string Label { get; set; } = "";
    public string EnvFile { get; set; } = "";
    public string Var { get; set; } = "";
    public string Placeholder { get; set; } = "";
    public string Test { get; set; } = "";
    public string KeyPage { get; set; } = "";
    public string BillingPage { get; set; } = "";
}

internal sealed class RecipeExpect
{
    public int MinTools { get; set; }
}

internal sealed class RecipePages
{
    public string KeyPage { get; set; } = "";
    public string BillingPage { get; set; } = "";
    public string Host { get; set; } = "";
}

// ==================================================================== findings

internal enum DoctorStatus { Ok, Warn, Fail, Off, Skipped }

/// <summary>
/// What the doctor found about one thing. The code is short and fixed, so the
/// local model can be handed it without the evidence; the message is already
/// plain enough to show as it is.
/// </summary>
internal sealed class DoctorFinding
{
    public string Area { get; set; } = "";      // mcp, skill, provider, local
    public string Item { get; set; } = "";
    public DoctorStatus Status { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Link { get; set; }
    public string Detail { get; set; } = "";
    public List<string> Fixes { get; set; } = new();

    /// <summary>The model's explanation, written in the explain phase.</summary>
    public string? Explanation { get; set; }

    /// <summary>The fixes that were tried, and what came of each.</summary>
    public List<string> RepairLog { get; set; } = new();
    public List<string> FixesTried { get; set; } = new();

    [JsonIgnore] public bool IsProblem => Status is DoctorStatus.Fail or DoctorStatus.Warn;
    [JsonIgnore] public string Key => $"{Area}:{Item}";
}

// ====================================================================== engine

/// <summary>
/// The system doctor: checks the MCP servers, the skills, the cloud keys and the
/// local model, offline, against the recipes.
///
/// Every check is code. The model is not asked to diagnose anything: on the
/// local model the context is 12k tokens, which is not enough to read configs,
/// logs and docs and still reason about them. The doctor does the finding and
/// the model, at most, explains one finding at a time.
/// </summary>
internal sealed class SystemDoctor
{
    private readonly AppConfig _cfg;
    private readonly ProviderStore _providers;
    private readonly McpHub _mcp;
    private readonly Func<CancellationToken, Task<bool>> _llamaHealthy;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public SystemDoctor(AppConfig cfg, ProviderStore providers, McpHub mcp, Func<CancellationToken, Task<bool>> llamaHealthy)
    {
        _cfg = cfg;
        _providers = providers;
        _mcp = mcp;
        _llamaHealthy = llamaHealthy;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("notecode-doctor");
        Recipes = LoadRecipes(RecipeDir);
    }

    public string RecipeDir => System.IO.Path.Combine(_cfg.RuntimeDir, "doctor", "recipes");
    public string FixDir => System.IO.Path.Combine(_cfg.RuntimeDir, "doctor", "fixes");
    public List<DoctorRecipe> Recipes { get; }

    public DoctorRecipe? Recipe(string kind, string name) =>
        Recipes.FirstOrDefault(r => r.Kind == kind && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static List<DoctorRecipe> LoadRecipes(string dir)
    {
        var list = new List<DoctorRecipe>();
        if (!Directory.Exists(dir)) return list;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<DoctorRecipe>(File.ReadAllText(file), JsonOptions);
                if (r is not null && r.Kind.Length > 0) list.Add(r);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"the doctor recipe {file} could not be read: {ex.Message}");
            }
        }
        return list;
    }

    /// <summary>Expands %RUNTIME%, %ROOT% and the environment variables in a recipe path.</summary>
    public string Expand(string text)
    {
        var root = System.IO.Path.GetDirectoryName(_cfg.RuntimeDir.TrimEnd('\\', '/')) ?? _cfg.RuntimeDir;
        return Environment.ExpandEnvironmentVariables(
            text.Replace("%RUNTIME%", _cfg.RuntimeDir, StringComparison.OrdinalIgnoreCase)
                .Replace("%ROOT%", root, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- scan

    /// <summary>
    /// What a scope names: everything, an area (mcp, skills, keys, local), or one
    /// item by name. Answers null when the name matches nothing.
    /// </summary>
    public (string Area, string? Item)? ResolveScope(string scope)
    {
        scope = scope.Trim().ToLowerInvariant();
        switch (scope)
        {
            case "": case "all": return ("all", null);
            case "mcp": case "servers": case "tools": return ("mcp", null);
            case "skills": case "skill": return ("skill", null);
            case "keys": case "providers": case "provider": return ("provider", null);
            case "local": case "model": case "llama": return ("local", null);
        }
        if (_cfg.Mcp.Servers.ContainsKey(scope) || Recipe("mcp", scope) is not null) return ("mcp", scope);
        if (Recipe("skill", scope) is not null || FindSkillFolders().Any(s => s.Name.Equals(scope, StringComparison.OrdinalIgnoreCase)))
            return ("skill", scope);
        // The model says "minimax" for a provider saved as "minimax.io", so a
        // provider is also found by the start of its name or by its host. When
        // several match - the preset and the user's own copy - the one with a key
        // is the one the user means.
        var matches = _providers.Providers.Where(p => !p.IsLocal && (
                p.Name.Equals(scope, StringComparison.OrdinalIgnoreCase) ||
                p.Name.StartsWith(scope, StringComparison.OrdinalIgnoreCase) ||
                (Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var u) && u.Host.Contains(scope, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        var pick = matches.FirstOrDefault(p => p.HasToken)
                   ?? matches.FirstOrDefault(p => p.Name.Equals(scope, StringComparison.OrdinalIgnoreCase))
                   ?? matches.FirstOrDefault();
        return pick is null ? null : ("provider", pick.Name);
    }

    public async Task<List<DoctorFinding>> ScanAsync(string area, string? item, IProgress<string>? progress, CancellationToken ct)
    {
        var findings = new List<DoctorFinding>();

        if (area is "all" or "local" && item is null)
        {
            progress?.Report("checking the local model");
            findings.Add(await CheckLocalAsync(ct));
        }

        // The servers come before the skills, because a skill is only as good as
        // the server it drives.
        var servers = new List<DoctorFinding>();
        if (area is "all" or "mcp" or "skill")
        {
            var names = _cfg.Mcp.Servers.Keys
                .Concat(Recipes.Where(r => r.Kind == "mcp").Select(r => r.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => area != "mcp" || item is null || n.Equals(item, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var name in names)
            {
                // A skill scope checks only the servers its skills need.
                if (area == "skill" && !SkillServers(item).Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                progress?.Report($"checking the {name} server");
                var f = await CheckServerAsync(name, explicitly: item is not null, ct);
                servers.Add(f);
                if (area != "skill") findings.Add(f);
            }
        }

        if (area is "all" or "skill")
        {
            progress?.Report("checking the skills");
            findings.AddRange(CheckSkills(item, servers));
        }

        if (area is "all" or "provider")
        {
            foreach (var p in _providers.Providers.Where(p => !p.IsLocal))
            {
                if (item is not null && !p.Name.Equals(item, StringComparison.OrdinalIgnoreCase)) continue;
                var active = _providers.ActiveProvider() == p;
                // Every provider in the list gets a row, the ones without a key
                // too: the list is what the user picks from.
                progress?.Report($"checking the {p.Label} key");
                findings.Add(await CheckProviderAsync(p, active, ct));
            }
        }

        return findings;
    }

    // ---------------------------------------------------------------- local model

    private async Task<DoctorFinding> CheckLocalAsync(CancellationToken ct)
    {
        var f = new DoctorFinding { Area = "local", Item = "llama-server" };
        var model = _cfg.Llama.Model;
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
        {
            f.Status = DoctorStatus.Fail;
            f.Code = "model-missing";
            f.Message = $"The local model file isn't there ({(model.Length > 0 ? model : "none set")}). " +
                        "Point notecode at a .gguf file in Settings > Model.";
            return f;
        }
        if (!File.Exists(_cfg.Llama.ServerExe))
        {
            f.Status = DoctorStatus.Fail;
            f.Code = "server-missing";
            f.Message = $"llama-server.exe isn't at {_cfg.Llama.ServerExe}. Reinstall notecode to put it back.";
            return f;
        }
        if (!await _llamaHealthy(ct))
        {
            f.Status = DoctorStatus.Fail;
            f.Code = "llama-down";
            f.Message = "The local model server isn't running, so the local model can't answer.";
            f.Fixes.Add("start-llama");
            return f;
        }
        f.Status = DoctorStatus.Ok;
        f.Code = "ok";
        f.Message = $"The local model is running ({System.IO.Path.GetFileName(model)}).";
        return f;
    }

    // ---------------------------------------------------------------- servers

    private async Task<DoctorFinding> CheckServerAsync(string name, bool explicitly, CancellationToken ct)
    {
        var f = new DoctorFinding { Area = "mcp", Item = name };
        var recipe = Recipe("mcp", name);
        _cfg.Mcp.Servers.TryGetValue(name, out var def);
        var title = recipe?.Title.Length > 0 ? recipe.Title : name;

        if (def is null && recipe?.Pack is { } pack)
        {
            f.Status = DoctorStatus.Off;
            f.Code = "not-installed";
            f.Message = $"{title} isn't installed. /install {pack} adds it.";
            return f;
        }
        if (def is null)
        {
            return Fail(f, "config-missing",
                $"{title} isn't set up in notecode.config.json.", fixes: "restore-config");
        }
        if (!def.Enabled)
        {
            f.Status = DoctorStatus.Off;
            f.Code = "disabled";
            f.Message = $"{title} is switched off (Settings > MCP).";
            return f;
        }

        var drift = recipe?.Config is null ? null : Drift(def, recipe.Config);

        // What the server needs on this machine, cheapest first.
        foreach (var cmd in recipe?.Needs?.Commands ?? new())
        {
            var problem = CheckCommand(cmd);
            if (problem is not null) return Fail(f, problem.Value.Code, problem.Value.Message);
        }
        foreach (var file in recipe?.Needs?.Files ?? new())
        {
            var path = Expand(file.Path);
            if (File.Exists(path) || Directory.Exists(path)) continue;
            return Fail(f, "file-missing",
                $"{title} is missing {file.What} ({path}).", fixes: file.Fix is null ? Array.Empty<string>() : new[] { file.Fix });
        }

        if (recipe?.Key is { } key)
        {
            var keyFinding = await CheckEnvKeyAsync(f, key, ct);
            if (keyFinding is not null && keyFinding.Status == DoctorStatus.Fail) return keyFinding;
        }

        // A server that lives inside another program: is the program open, and
        // is its server switched on?
        if (def.Transport is "streamable-http" or "http" && Uri.TryCreate(def.Url, UriKind.Absolute, out var uri))
        {
            if (!await PortOpenAsync(uri.Host, uri.Port, ct))
            {
                if (recipe?.App is { } app)
                {
                    var running = Process.GetProcessesByName(app.Process).Length > 0;
                    if (!running)
                    {
                        var exe = app.Exe.Select(Expand).FirstOrDefault(File.Exists);
                        Fail(f, "app-not-running",
                            $"{Cap(app.Name)} isn't open. Start {app.Name}, then run /doctor {name} again.",
                            fixes: exe is null ? Array.Empty<string>() : new[] { "start-app" });
                        f.Detail = exe is null
                            ? $"{app.Name} was not found in the usual place, so it has to be started by hand. {app.Enable}"
                            : $"{app.Name} is at {exe}. {app.Enable}";
                        return f;
                    }
                    Fail(f, "app-not-listening",
                        $"{Cap(app.Name)} is open, but its MCP server isn't running. {app.Enable}");
                    f.Detail = $"Nothing answers on {uri.Host}:{uri.Port}.";
                    return f;
                }
                return Fail(f, "not-listening",
                    $"Nothing answers on {uri.Host}:{uri.Port}, so {title} can't be reached.",
                    fixes: drift is null ? Array.Empty<string>() : new[] { "restore-config" });
            }
        }

        // The handshake. A server the app already holds is asked through that
        // connection; an on-demand one is only started when it was asked for by
        // name, because REA is an npx cold start of tens of seconds.
        int tools;
        var connected = _mcp.ConnectedServers.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (connected)
        {
            tools = (await _mcp.ListToolsAsync(new[] { name }, ct)).Count;
        }
        else if (def.OnDemand && !explicitly)
        {
            f.Status = DoctorStatus.Ok;
            f.Code = "on-demand";
            f.Message = $"{title} starts only when it is used. Its setup checks out; /doctor {name} starts it to be sure.";
            return f;
        }
        else
        {
            var (count, error) = await McpHub.ProbeAsync(name, def, TimeSpan.FromSeconds(def.OnDemand ? 120 : 30), ct);
            if (error is not null)
            {
                Fail(f, drift is null ? "handshake-failed" : "config-drift",
                    drift is null
                        ? $"{title} starts but doesn't answer as an MCP server."
                        : $"{title} doesn't start, and its config differs from the known-good setup ({drift}).",
                    fixes: drift is null
                        ? (recipe?.Fixes ?? new()).Where(x => x is "reconnect" or "clear-npx-cache" or "reinstall-npm").ToArray()
                        : new[] { "restore-config" });
                f.Detail = Trim(error, 300);
                return f;
            }
            tools = count;
            if (!def.OnDemand)
            {
                f.Status = DoctorStatus.Warn;
                f.Code = "not-connected";
                f.Message = $"{title} works ({tools} tools), but notecode hasn't connected to it since it started.";
                f.Fixes.Add("reconnect");
                return f;
            }
        }

        var min = recipe?.Expect?.MinTools ?? 1;
        if (tools < min)
        {
            f.Status = DoctorStatus.Warn;
            f.Code = "too-few-tools";
            f.Message = $"{title} answers, but lists {tools} tools where {min} or more are expected.";
            f.Fixes.Add("reconnect");
            return f;
        }

        f.Status = DoctorStatus.Ok;
        f.Code = "ok";
        f.Message = $"{title} works ({tools} tools).";
        if (drift is not null) f.Detail = $"Its config differs from the recipe ({drift}), but it works, so it was left alone.";
        return f;
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static DoctorFinding Fail(DoctorFinding f, string code, string message, params string[] fixes)
    {
        f.Status = DoctorStatus.Fail;
        f.Code = code;
        f.Message = message;
        f.Fixes.AddRange(fixes);
        return f;
    }

    /// <summary>How the live config differs from the recipe, or null when it does not.</summary>
    private string? Drift(McpServerDef def, RecipeConfig recipe)
    {
        var want = Expanded(recipe);
        if (!string.Equals(def.Transport, want.Transport, StringComparison.OrdinalIgnoreCase))
            return $"transport is {def.Transport}, expected {want.Transport}";
        if (want.Url is not null && !string.Equals(def.Url, want.Url, StringComparison.OrdinalIgnoreCase))
            return $"url is {def.Url}, expected {want.Url}";
        if (want.Command is not null && !string.Equals(def.Command, want.Command, StringComparison.OrdinalIgnoreCase))
            return $"command is {def.Command}, expected {want.Command}";
        if (want.Args.Count > 0 && !def.Args.SequenceEqual(want.Args, StringComparer.OrdinalIgnoreCase))
            return $"args are {string.Join(" ", def.Args)}, expected {string.Join(" ", want.Args)}";
        return null;
    }

    public RecipeConfig Expanded(RecipeConfig r) => new()
    {
        Transport = r.Transport,
        Command = r.Command is null ? null : Expand(r.Command),
        Args = r.Args.Select(Expand).ToList(),
        Url = r.Url is null ? null : Expand(r.Url)
    };

    private (string Code, string Message)? CheckCommand(RecipeCommand cmd)
    {
        var path = FindOnPath(cmd.Name);
        if (path is null)
            return ("command-missing", $"{cmd.Name} isn't installed or isn't on PATH. Get it from {cmd.Get}.");
        if (cmd.Min is null || !Version.TryParse(cmd.Min, out var min)) return null;

        var output = RunQuick(path, "--version");
        var match = Regex.Match(output ?? "", @"(\d+\.\d+(\.\d+)?)");
        if (match.Success && Version.TryParse(match.Value, out var have) && have < min)
            return ("command-too-old", $"{cmd.Name} {have} is too old; {cmd.Min} or newer is needed. Get it from {cmd.Get}.");
        return null;
    }

    /// <summary>Finds a program the way cmd.exe would: the PATH, with the PATHEXT endings.</summary>
    public static string? FindOnPath(string name)
    {
        if (System.IO.Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        var exts = System.IO.Path.HasExtension(name)
            ? new[] { "" }
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';');
        foreach (var raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            // A relative entry (".", or a bare folder name) resolves against the
            // current folder, which is how a planted node.exe gets picked up. The
            // doctor runs what it finds to read its version, so only absolute
            // folders count.
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0 || !System.IO.Path.IsPathFullyQualified(dir)) continue;
            foreach (var ext in exts)
            {
                try
                {
                    var p = System.IO.Path.Combine(dir, name + ext);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }
        return null;
    }

    private static string? RunQuick(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Trim();
        }
        catch { return null; }
    }

    private static async Task<bool> PortOpenAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(host, port, timeout.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- keys

    /// <summary>Reads a key from a server's .env file and, when it is there, tries it.</summary>
    private async Task<DoctorFinding?> CheckEnvKeyAsync(DoctorFinding f, RecipeKey key, CancellationToken ct)
    {
        var envFile = Expand(key.EnvFile);
        string? value = null;
        if (File.Exists(envFile))
        {
            foreach (var line in File.ReadAllLines(envFile))
            {
                var t = line.Trim();
                if (!t.StartsWith(key.Var + "=", StringComparison.Ordinal)) continue;
                value = t[(key.Var.Length + 1)..].Trim().Trim('"', '\'');
            }
        }

        if (string.IsNullOrEmpty(value) || value == key.Placeholder)
        {
            Fail(f, "key-missing",
                $"There's no {key.Label} API key. Add {key.Var}=<your key> to {envFile}, then run /doctor {f.Item} again. Get a key at {key.KeyPage}");
            f.Link = key.KeyPage;
            return f;
        }

        if (key.Test.Length == 0) return null;
        var (code, message) = await TryKeyAsync(key.Label, key.Test, value, key.KeyPage, key.BillingPage, ct);
        if (code == "offline")
        {
            // Offline is not a key problem: the rest of the server is still checked.
            f.Detail = message;
            return null;
        }
        if (code != "ok")
        {
            Fail(f, code, message);
            f.Link = code == "key-no-credit" && key.BillingPage.Length > 0 ? key.BillingPage : key.KeyPage;
            return f;
        }
        f.Detail = message;
        return null;
    }

    private async Task<DoctorFinding> CheckProviderAsync(ProviderDef p, bool active, CancellationToken ct)
    {
        var f = new DoctorFinding { Area = "provider", Item = p.Name };
        // By name, or by the host of the base URL for a provider saved under
        // another name (minimax.io rather than minimax).
        var all = Recipes.FirstOrDefault(r => r.Kind == "providers")?.Pages ?? new();
        var host = Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var baseUri) ? baseUri.Host : "";
        var pages = all.GetValueOrDefault(p.Name)
                    ?? all.Values.FirstOrDefault(v => v.Host.Length > 0 && v.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        var keyPage = pages?.KeyPage ?? "";
        var billing = pages?.BillingPage is { Length: > 0 } b ? b : keyPage;

        // A server on this machine (Ollama) needs no key: it is asked whether
        // it is running.
        var isLocalHost = host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
        if (!p.HasToken && isLocalHost)
        {
            try
            {
                using var resp = await _http.GetAsync(p.ModelsUrl, ct);
                f.Status = resp.IsSuccessStatusCode ? DoctorStatus.Ok : DoctorStatus.Warn;
                f.Code = resp.IsSuccessStatusCode ? "ok" : "provider-error";
                f.Message = resp.IsSuccessStatusCode
                    ? $"{p.Label} is running (it needs no key)."
                    : $"{p.Label} is running but answered {(int)resp.StatusCode}.";
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                f.Status = DoctorStatus.Off;
                f.Code = "not-running";
                f.Message = $"{p.Label} isn't running on this PC (it needs no key).";
            }
            return f;
        }

        if (!p.HasToken)
        {
            // The active provider without a key is a broken setup; any other is
            // one the user has not set up, which is red too but counted apart.
            Fail(f, active ? "key-missing" : "no-key",
                (active ? $"{p.Label} is the active provider but has no API key." : $"{p.Label} has no API key.") +
                $" Add one with /addprovider {p.Name}." +
                (keyPage.Length > 0 ? $" Get a key at {keyPage}" : ""));
            f.Link = keyPage.Length > 0 ? keyPage : null;
            return f;
        }

        var token = SecretBox.Unprotect(p.ProtectedToken);
        var (code, message) = await TryKeyAsync(p.Label, p.ModelsUrl, token, keyPage, billing, ct,
            anthropic: p.Style == ApiStyle.Anthropic);

        // Some providers (NVIDIA) publish their model list to anyone, so a list
        // that comes back says nothing about the key. A chat request with no
        // messages does: the key is checked first, then the empty request is
        // refused, and no model runs.
        string? modelGone = null;
        if (code == "ok" && p.Style == ApiStyle.OpenAi)
        {
            var (refused, gone) = await KeyRefusedByChatAsync(p, token, p.Model, ct);
            if (gone is not null)
            {
                // The model the provider is set to has been retired. The key is
                // tried again with a model the provider still lists.
                modelGone = gone;
                var other = await FirstListedModelAsync(p, token, ct);
                if (other is not null) (refused, _) = await KeyRefusedByChatAsync(p, token, other, ct);
            }
            if (refused is not null)
            {
                code = "key-rejected";
                message = $"The {p.Label} key was refused ({refused}). It may be expired, revoked or pasted wrong." +
                          (keyPage.Length > 0 ? $" Make a new one at {keyPage}" : "");
            }
            else if (modelGone is not null)
            {
                code = "model-gone";
                message = $"The {p.Label} key works, but the model it is set to ({p.Model}) is no longer available. " +
                          $"Pick another with /model while {p.Name} is the provider.";
            }
        }
        f.Code = code;
        if (modelGone is not null) f.Detail = modelGone;
        f.Message = message;
        f.Status = code switch
        {
            "ok" => DoctorStatus.Ok,
            "offline" => DoctorStatus.Skipped,
            // The key works, but something else is wrong: orange, not red.
            "rate-limited" or "provider-error" or "model-gone" or "key-no-credit" => DoctorStatus.Warn,
            _ => DoctorStatus.Fail
        };
        if (f.IsProblem) f.Link = code == "key-no-credit" ? billing : keyPage;
        if (f.IsProblem && f.Link is { Length: 0 }) f.Link = null;
        return f;
    }

    /// <summary>
    /// Tries a key with one free request and says what the answer means. Nothing
    /// that costs credits is ever called.
    /// </summary>
    private async Task<(string Code, string Message)> TryKeyAsync(
        string label, string url, string key, string keyPage, string billingPage, CancellationToken ct, bool anthropic = false)
    {
        HttpResponseMessage resp;
        string body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (anthropic)
            {
                req.Headers.Add("x-api-key", key);
                req.Headers.Add("anthropic-version", "2023-06-01");
            }
            else req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            resp = await _http.SendAsync(req, ct);
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
            return ("offline", $"Can't reach {label} ({host}). You're probably offline - this isn't a key problem.");
        }

        using (resp)
        {
            var lower = body.ToLowerInvariant();
            var where = keyPage.Length > 0 ? $" Make a new one at {keyPage}" : "";
            var topUp = billingPage.Length > 0 ? $" Top up at {billingPage}" : "";
            var status = (int)resp.StatusCode;

            if (status is 401 or 403 || (resp.IsSuccessStatusCode && Regex.IsMatch(lower, "invalid api.?key|unauthori[sz]ed|authentication fail")))
                return ("key-rejected", $"The {label} key was refused. It may be expired, revoked or pasted wrong.{where}");
            if (status == 402 || Regex.IsMatch(lower, "insufficient (balance|credit|quota)|out of credit|no credit|quota exceeded|exceeded your current quota"))
                return ("key-no-credit", $"The {label} key works, but the account is out of credits.{topUp}");
            if (status == 429)
                return ("rate-limited", $"The {label} key works, but you've hit the rate limit. Wait a minute, or check your plan.");
            if (status >= 500)
                return ("provider-error", $"{label} answered with a server error ({status}). That's on their side; try again later.");

            if (resp.IsSuccessStatusCode || status == 404)
            {
                // Meshy's balance call says how many credits are left.
                var balance = Regex.Match(body, "\"balance\"\\s*:\\s*(-?\\d+)");
                if (balance.Success && int.Parse(balance.Groups[1].Value) <= 0)
                    return ("key-no-credit", $"The {label} key works, but the balance is {balance.Groups[1].Value} credits.{topUp}");
                return ("ok", balance.Success
                    ? $"The {label} key works ({balance.Groups[1].Value} credits left)."
                    : $"The {label} key works.");
            }
            return ("provider-error", $"{label} answered {status}: {Trim(body, 160)}");
        }
    }

    /// <summary>
    /// Sends a chat request with no messages. Answers the status the key was
    /// refused with, and what the provider said when the model itself is gone;
    /// both null when the key was taken.
    /// </summary>
    private async Task<(string? Refused, string? ModelGone)> KeyRefusedByChatAsync(
        ProviderDef p, string token, string model, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, p.ChatUrl)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { model, messages = Array.Empty<object>(), max_tokens = 1 }),
                    Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            var status = (int)resp.StatusCode;
            if (status is 401 or 403) return ($"HTTP {status}", null);
            if (status is 404 or 410)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (body.Contains("model", StringComparison.OrdinalIgnoreCase))
                {
                    var detail = Regex.Match(body, "\"(detail|message)\"\\s*:\\s*\"([^\"]+)\"");
                    return (null, detail.Success ? detail.Groups[2].Value : $"HTTP {status} for {model}");
                }
            }
            return (null, null);
        }
        catch { return (null, null); }
    }

    private async Task<string?> FirstListedModelAsync(ProviderDef p, string token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, p.ModelsUrl);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            var m = Regex.Match(await resp.Content.ReadAsStringAsync(ct), "\"id\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- skills

    private record SkillFolder(string Name, string File);

    private IEnumerable<SkillFolder> FindSkillFolders()
    {
        foreach (var root in _cfg.Skills.Roots.Select(Expand))
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root).ToList(); } catch { continue; }
            foreach (var dir in dirs)
                yield return new SkillFolder(System.IO.Path.GetFileName(dir), System.IO.Path.Combine(dir, "SKILL.md"));
        }
    }

    private IEnumerable<string> SkillServers(string? item) =>
        Recipes.Where(r => r.Kind == "skill" && (item is null || r.Name.Equals(item, StringComparison.OrdinalIgnoreCase)))
               .SelectMany(r => r.Requires.Concat(r.Optional));

    private List<DoctorFinding> CheckSkills(string? item, List<DoctorFinding> servers)
    {
        var findings = new List<DoctorFinding>();

        foreach (var recipe in Recipes.Where(r => r.Kind == "skill"))
        {
            if (item is not null && !recipe.Name.Equals(item, StringComparison.OrdinalIgnoreCase)) continue;
            var f = new DoctorFinding { Area = "skill", Item = recipe.Name };

            // The copy that counts is the one /skill loads: the first one in the
            // skill folders. The recipe's path is where a copy comes from.
            var loaded = FindSkillFolders().FirstOrDefault(s =>
                s.Name.Equals(recipe.Name, StringComparison.OrdinalIgnoreCase) && File.Exists(s.File));
            if (loaded is null)
            {
                var source = recipe.Path is null ? "" : Expand(recipe.Path);
                var roots = string.Join(", ", _cfg.Skills.Roots.Select(Expand));
                if (File.Exists(source))
                {
                    Fail(f, "skill-not-installed",
                        $"The {recipe.Name} skill isn't in a skill folder, so /skill can't load it.", "install-skill");
                    f.Detail = $"A copy is at {source}. The skill folders are {roots}.";
                }
                else
                {
                    Fail(f, "skill-missing", $"The {recipe.Name} skill isn't in any skill folder ({roots}).");
                }
                findings.Add(f);
                continue;
            }
            var path = loaded.File;
            var front = FrontMatterProblem(File.ReadAllText(path));
            if (front is not null)
            {
                findings.Add(Fail(f, "skill-front-matter", $"The {recipe.Name} skill {front}. /addskills \"{path}\" repairs it."));
                continue;
            }
            var down = servers.FirstOrDefault(s =>
                recipe.Requires.Contains(s.Item, StringComparer.OrdinalIgnoreCase) && s.Status is DoctorStatus.Fail or DoctorStatus.Off);
            if (down is not null)
            {
                findings.Add(Fail(f, "server-down",
                    $"The {recipe.Name} skill can't work until the {down.Item} server works: {down.Message}"));
                continue;
            }

            // A server that answers but is not connected, or lists too few tools,
            // is not down - but the skill can't count on it either.
            var degraded = servers.FirstOrDefault(s =>
                recipe.Requires.Contains(s.Item, StringComparer.OrdinalIgnoreCase) && s.Status == DoctorStatus.Warn);
            if (degraded is not null)
            {
                f.Status = DoctorStatus.Warn;
                f.Code = "server-degraded";
                f.Message = $"The {recipe.Name} skill may not work yet: {degraded.Message}";
                findings.Add(f);
                continue;
            }
            var optional = servers.FirstOrDefault(s =>
                recipe.Optional.Contains(s.Item, StringComparer.OrdinalIgnoreCase) && s.Status is DoctorStatus.Fail or DoctorStatus.Off or DoctorStatus.Warn);
            f.Status = optional is null ? DoctorStatus.Ok : DoctorStatus.Warn;
            f.Code = optional is null ? "ok" : "optional-server-down";
            f.Message = optional is null
                ? $"The {recipe.Name} skill is ready."
                : $"The {recipe.Name} skill works, but the {optional.Item} part won't: {optional.Message}";
            findings.Add(f);
        }

        // The skills in the skill folders have no recipe. They get the one check
        // that needs none: the app can read them.
        var broken = new List<string>();
        var count = 0;
        foreach (var skill in FindSkillFolders())
        {
            if (item is not null && !skill.Name.Equals(item, StringComparison.OrdinalIgnoreCase)) continue;
            if (Recipe("skill", skill.Name) is not null) continue;
            // A folder without a SKILL.md is a category of skills, not a skill:
            // the app does not load it either.
            if (!File.Exists(skill.File)) continue;
            count++;
            var problem = FrontMatterProblem(File.ReadAllText(skill.File));
            if (problem is null) continue;
            if (item is not null)
            {
                findings.Add(Fail(new DoctorFinding { Area = "skill", Item = skill.Name }, "skill-front-matter",
                    $"The {skill.Name} skill {problem}. /addskills \"{skill.File}\" repairs it."));
            }
            else broken.Add($"{skill.Name} ({problem})");
        }
        if (item is null && count > 0)
        {
            findings.Add(new DoctorFinding
            {
                Area = "skill",
                Item = "skill folders",
                Status = broken.Count == 0 ? DoctorStatus.Ok : DoctorStatus.Warn,
                Code = broken.Count == 0 ? "ok" : "skill-front-matter",
                Message = broken.Count == 0
                    ? $"All {count} skills in the skill folders can be read."
                    : $"{broken.Count} of {count} skills can't be read by notecode. /addskills on a skill's SKILL.md repairs it.",
                Detail = string.Join("; ", broken.Take(12)) + (broken.Count > 12 ? $" and {broken.Count - 12} more" : "")
            });
        }
        return findings;
    }

    private static string? FrontMatterProblem(string text)
    {
        var head = text.TrimStart();
        if (!head.StartsWith("---")) return "has no front matter (the --- block with name and description)";
        var end = head.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return "has a front matter block that never ends";
        var block = head[..end];
        if (!Regex.IsMatch(block, @"^\s*name\s*:", RegexOptions.Multiline)) return "has no name in its front matter";
        if (!Regex.IsMatch(block, @"^\s*description\s*:", RegexOptions.Multiline)) return "has no description in its front matter";
        return null;
    }

    /// <summary>The doctor as a tool the model can call: notecode_doctor.</summary>
    public static readonly ToolSpec ToolSpec = new(
        "notecode_doctor",
        "Check whether notecode's MCP servers, skills, API keys and local model work, and repair them.",
        JsonSerializer.SerializeToElement(new
        {
            type = "function",
            function = new
            {
                name = "notecode_doctor",
                description =
                    "Checks notecode's MCP servers, skills, API keys and the local model, offline, against their known-good setup, " +
                    "and says what is wrong and what to do. Use it whenever the user asks whether something works, why a tool, " +
                    "server, skill or key fails, or asks to repair or check the setup. With repair=true it offers the fix, " +
                    "which the user approves. Report its answer to the user as it is.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        item = new
                        {
                            type = "string",
                            description = "What to check: a server (blender, meshy, meshy-dcc, global-brain, rea, notecode), " +
                                          "a skill, a provider (minimax, nvidia...), or mcp, skills, keys, local. Empty checks everything."
                        },
                        repair = new { type = "boolean", description = "true to offer the fix for what is broken." }
                    }
                }
            }
        }));

    // ---------------------------------------------------------------- explain

    /// <summary>
    /// The prompt for explaining one finding: the finding and the recipe's two
    /// lines about the item. A few hundred tokens, so it fits the local model's
    /// context many times over, and nothing in it asks the model to work
    /// anything out.
    /// </summary>
    public List<ChatMessage> ExplainPrompt(DoctorFinding f)
    {
        var recipe = Recipes.FirstOrDefault(r => r.Kind != "providers" && r.Name.Equals(f.Item, StringComparison.OrdinalIgnoreCase));
        var facts = new StringBuilder();
        facts.AppendLine($"item: {f.Item} ({f.Area})");
        facts.AppendLine($"problem code: {f.Code}");
        facts.AppendLine($"finding: {f.Message}");
        if (f.Detail.Length > 0) facts.AppendLine($"detail: {f.Detail}");
        if (f.Link is not null) facts.AppendLine($"page: {f.Link}");
        if (recipe is not null)
        {
            facts.AppendLine($"what it is: {recipe.Summary}");
            if (recipe.HowToUse.Length > 0) facts.AppendLine($"how it is used: {recipe.HowToUse}");
        }
        facts.AppendLine(f.Fixes.Count > 0
            ? $"automatic fixes the doctor can run next: {string.Join(", ", f.Fixes.Select(x => DescribeFix(f, x)))}"
            : "automatic fixes: none - the user has to do this");

        return new List<ChatMessage>
        {
            new("system",
                "You are notecode's doctor. Explain the problem below to the user in at most three short sentences: " +
                "what is wrong and exactly what they should do. Use only the facts given. Keep any page address as it is. " +
                "No headings, no lists."),
            new("user", facts.ToString())
        };
    }

    // ---------------------------------------------------------------- fixes

    /// <summary>What a fix will do, in words, for the approval window.</summary>
    public string DescribeFix(DoctorFinding f, string fix)
    {
        var recipe = Recipe("mcp", f.Item);
        return fix switch
        {
            "restore-config" => $"Put the {f.Item} entry in notecode.config.json back to its known-good setup from the recipe.",
            "reconnect" => $"Stop and restart notecode's connection to the {f.Item} server.",
            "start-app" => $"Start {recipe?.App?.Name ?? f.Item}.",
            "reinstall-npm" => $"Reinstall the {f.Item} server's Node packages and rebuild it (npm needs the internet unless its cache has them).",
            "clear-npx-cache" => "Empty npx's package cache, so the server is downloaded fresh on its next start (needs the internet).",
            "start-llama" => "Start the local model server.",
            "install-skill" => $"Copy the {f.Item} skill into {SkillTarget() ?? "the first skill folder"}, where /skill finds it.",
            _ => fix
        };
    }

    private string? SkillTarget() =>
        _cfg.Skills.Roots.Select(Expand).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));

    /// <summary>Copies a skill's folder from its recipe path into the first skill folder.</summary>
    public string InstallSkill(string name)
    {
        var recipe = Recipe("skill", name);
        var source = recipe?.Path is null ? null : System.IO.Path.GetDirectoryName(Expand(recipe.Path));
        var root = SkillTarget();
        if (source is null || !Directory.Exists(source)) return $"there is no copy of {name} to install";
        if (root is null) return "there is no skill folder in notecode.config.json";

        var target = System.IO.Path.Combine(root, name);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(source, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
        return $"copied into {target}";
    }

    /// <summary>The command line a script fix runs, or null for a fix the app does itself.</summary>
    public string? FixCommand(DoctorFinding f, string fix)
    {
        var recipe = Recipe("mcp", f.Item);
        string Script(string name) => System.IO.Path.Combine(FixDir, name);
        string Ps(string script, string args) =>
            $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{Script(script)}\" {args}".TrimEnd();

        switch (fix)
        {
            case "reinstall-npm":
                return recipe?.Folder is null ? null : Ps("reinstall-npm.ps1", $"-Folder \"{Expand(recipe.Folder)}\"");
            case "clear-npx-cache":
                return Ps("clear-npx-cache.ps1", "");
            case "start-app":
                var exe = recipe?.App?.Exe.Select(Expand).FirstOrDefault(File.Exists);
                if (exe is null) return null;
                var port = Uri.TryCreate(recipe?.Config?.Url, UriKind.Absolute, out var u) ? u.Port : 0;
                return Ps("start-app.ps1", $"-Exe \"{exe}\" -Port {port}");
            default:
                return null;
        }
    }

    /// <summary>
    /// Runs a fix script and answers its exit code and its verdict: the last OK
    /// or FAIL line it printed, without the npm noise above it. PowerShell is
    /// started directly, so the quotes in the paths are not left to cmd.exe.
    /// </summary>
    public static async Task<(int Code, string Output)> RunFixScriptAsync(string command, CancellationToken ct)
    {
        const string prefix = "powershell ";
        var psi = command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("powershell.exe", command[prefix.Length..])
            : new ProcessStartInfo("cmd.exe", $"/d /c \"{command}\"");
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("the fix would not start");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A fix that is still running must not be left changing packages
            // while the doctor checks the result: it is stopped, with everything
            // it started, before anything else happens.
            try { p.Kill(entireProcessTree: true); } catch { }
            p.WaitForExit(10000);
            if (ct.IsCancellationRequested) throw;
            return (-1, "FAIL the fix ran for 10 minutes without finishing and was stopped");
        }
        var lines = ((await stdout) + "\n" + (await stderr))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verdict = lines.LastOrDefault(l => l.StartsWith("OK ") || l.StartsWith("FAIL ")) ?? lines.LastOrDefault() ?? "";
        return (p.ExitCode, verdict);
    }

    /// <summary>Writes the recipe's config for a server into notecode.config.json and the live config.</summary>
    public string RestoreConfig(string name)
    {
        var recipe = Recipe("mcp", name);
        if (recipe?.Config is null) return $"there is no recipe for {name}, so there is nothing to restore it to";
        var want = Expanded(recipe.Config);

        if (!_cfg.Mcp.Servers.TryGetValue(name, out var def))
        {
            def = new McpServerDef { Description = recipe.Summary, ToolTimeoutMs = 60000 };
            _cfg.Mcp.Servers[name] = def;
        }
        def.Transport = want.Transport;
        def.Command = want.Command;
        def.Args = want.Args;
        def.Url = want.Url;
        def.Enabled = true;

        var w = new ConfigWriter(_cfg.SourcePath)
            .Set($"mcp.servers.{name}.transport", want.Transport)
            .Set($"mcp.servers.{name}.enabled", true);
        if (want.Command is not null) w.Set($"mcp.servers.{name}.command", want.Command);
        if (want.Args.Count > 0) w.Set($"mcp.servers.{name}.args", want.Args);
        if (want.Url is not null) w.Set($"mcp.servers.{name}.url", want.Url);
        if (def.Description is not null) w.Set($"mcp.servers.{name}.description", def.Description);
        w.Save();
        return "the config was put back to the recipe";
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s.Replace('\n', ' ') : s[..max].Replace('\n', ' ') + "...";
}

// ====================================================================== report

/// <summary>
/// doctor.md: the doctor's notes, kept between passes.
///
/// The doctor works in phases - scan, explain, repair, verify - and on the local
/// model each phase is several small model calls. Everything it learns is
/// written here after every item, with the state as JSON in a comment at the
/// top, so a pass that is stopped, crashes or runs out of context is picked up
/// where it was rather than started again.
/// </summary>
internal sealed class DoctorReport
{
    public const string StateStart = "<!-- doctor-state";
    public const string StateEnd = "-->";

    public static readonly string[] Phases = { "scan", "explain", "repair", "verify", "done" };

    public string Started { get; set; } = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");
    public string Scope { get; set; } = "all";
    public string Phase { get; set; } = "scan";
    public List<DoctorFinding> Findings { get; set; } = new();

    [JsonIgnore] public string FilePath { get; private set; } = "";
    [JsonIgnore] public bool Finished => Phase == "done";

    public static string PathFor() => System.IO.Path.Combine(AppPaths.DataDir, "doctor.md");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The report on disk, or null when there is none or it cannot be read.</summary>
    public static DoctorReport? Load(string? path = null)
    {
        path ??= PathFor();
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path);
            var start = text.IndexOf(StateStart, StringComparison.Ordinal);
            if (start < 0) return null;
            start += StateStart.Length;
            var end = text.IndexOf(StateEnd, start, StringComparison.Ordinal);
            if (end < 0) return null;
            var report = JsonSerializer.Deserialize<DoctorReport>(text[start..end].Trim(), JsonOptions);
            if (report is not null) report.FilePath = path;
            return report;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"doctor.md could not be read: {ex.Message}");
            return null;
        }
    }

    public static DoctorReport New(string scope, string? path = null) =>
        new() { Scope = scope, FilePath = path ?? PathFor() };

    public void Advance()
    {
        var i = Array.IndexOf(Phases, Phase);
        Phase = Phases[Math.Min(i + 1, Phases.Length - 1)];
    }

    /// <summary>Writes the file. Called after every item, so nothing learned is lost.</summary>
    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, Render(), new UTF8Encoding(false));
        File.Move(temp, FilePath, overwrite: true);
    }

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# notecode doctor");
        sb.AppendLine();
        sb.AppendLine(StateStart);
        sb.AppendLine(JsonSerializer.Serialize(this, JsonOptions).Replace("-->", "--\\u003E"));
        sb.AppendLine(StateEnd);
        sb.AppendLine();
        var step = Array.IndexOf(Phases, Phase);
        sb.AppendLine(Finished
            ? $"Finished. Started {Started}, scope `{Scope}`."
            : $"**Phase: {Phase}** ({step + 1} of 4). Started {Started}, scope `{Scope}`. `/doctor` carries on from here.");
        sb.AppendLine();
        sb.AppendLine(Summary());

        foreach (var f in Findings.Where(f => (f.IsProblem && f.Code != "no-key") || f.RepairLog.Count > 0))
        {
            sb.AppendLine();
            sb.AppendLine($"## {f.Item} ({f.Area}) - {Label(f.Status)}");
            sb.AppendLine();
            sb.AppendLine($"`{f.Code}` {f.Message}");
            if (f.Detail.Length > 0) { sb.AppendLine(); sb.AppendLine($"Detail: {f.Detail}"); }
            if (f.Link is not null) { sb.AppendLine(); sb.AppendLine($"Go to: {f.Link}"); }
            if (f.Fixes.Count > 0) { sb.AppendLine(); sb.AppendLine($"Fixes: {string.Join(", ", f.Fixes)}"); }
            if (f.Explanation is not null) { sb.AppendLine(); sb.AppendLine($"Explained: {f.Explanation}"); }
            foreach (var line in f.RepairLog) sb.AppendLine($"- {line}");
        }
        return sb.ToString();
    }

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| | Item | Result |");
        sb.AppendLine("|---|---|---|");
        foreach (var f in Findings.OrderBy(f => f.Status switch
                 {
                     DoctorStatus.Fail => 0, DoctorStatus.Warn => 1, DoctorStatus.Skipped => 2, DoctorStatus.Off => 3, _ => 4
                 }))
        {
            sb.AppendLine($"| {Icon(f.Status)} | {f.Item} ({f.Area}) | {f.Message.Replace("|", "/")} |");
        }
        return sb.ToString();
    }

    /// <summary>
    /// The mark in the checks list: green when it works, red when it is broken
    /// (for a provider: the key was refused or there is none), orange when it
    /// works but something about it is wrong (a retired model, no credit).
    /// </summary>
    public static string Icon(DoctorStatus s) => s switch
    {
        DoctorStatus.Ok => "\u2705",          // green check in a box
        DoctorStatus.Warn => "\U0001F7E7",    // orange square
        DoctorStatus.Fail => "\u274C",        // red cross
        DoctorStatus.Off => "\u2796",         // switched off, not installed
        _ => "\u2B1C"                         // not checked
    };

    /// <summary>The same marks as words, for a console that can't show them.</summary>
    public static string IconText(DoctorStatus s) => s switch
    {
        DoctorStatus.Ok => "OK", DoctorStatus.Warn => "WARN", DoctorStatus.Fail => "FAIL", DoctorStatus.Off => "off", _ => "skip"
    };

    public static string Label(DoctorStatus s) => s switch
    {
        DoctorStatus.Ok => "works",
        DoctorStatus.Warn => "needs a look",
        DoctorStatus.Fail => "broken",
        DoctorStatus.Off => "switched off",
        _ => "not checked"
    };
}
