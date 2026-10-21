using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notecode.App;

/// <summary>
/// Typed view over runtime/notecode.config.json. Every component reads the same file
/// so the GUI, the PowerShell wrapper and the CLI cannot drift apart.
/// </summary>
internal sealed class AppConfig
{
    [JsonPropertyName("$comment")] public string? Comment { get; set; }

    public LlamaSection Llama { get; set; } = new();
    public MemorySection Memory { get; set; } = new();
    public McpSection Mcp { get; set; } = new();
    public KimiSection Kimi { get; set; } = new();
    public EditorSection Editor { get; set; } = new();
    public UiSection Ui { get; set; } = new();
    public WorkspaceSection Workspace { get; set; } = new();
    public SearchSection Search { get; set; } = new();
    public SkillsSection Skills { get; set; } = new();

    public static string Locate()
    {
        // Walk up from the executable to find runtime/notecode.config.json.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "runtime", "notecode.config.json");
            if (File.Exists(candidate)) return candidate;
            var flat = Path.Combine(dir, "notecode.config.json");
            if (File.Exists(flat)) return flat;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new FileNotFoundException("notecode.config.json not found");
    }

    public static AppConfig Load(string? path = null)
    {
        path ??= Locate();
        var json = ExpandEnvironmentVariables(File.ReadAllText(path));
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new InvalidDataException("config deserialised to null");

        cfg.SourcePath = path;
        cfg.RuntimeDir = Path.GetDirectoryName(path)!;
        return cfg;
    }

    /// <summary>
    /// Expands %VARIABLE% in every string in the configuration.
    ///
    /// A path to something in the user's own folders - the search key, the
    /// skills, the kimi-code install - is written once and has to work on
    /// every machine, so it is written with %USERPROFILE% in it. A variable
    /// that is not set is left exactly as it is, so a path that has no
    /// variable in it is not damaged.
    /// </summary>
    public static string ExpandEnvironmentVariables(string json)
    {
        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(json);
            Expand(root);
            return root?.ToJsonString() ?? json;
        }
        catch
        {
            // A configuration that cannot be walked is used as it is.
            return json;
        }
    }

    private static void Expand(System.Text.Json.Nodes.JsonNode? node)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                foreach (var key in obj.Select(k => k.Key).ToList())
                {
                    var value = obj[key];
                    if (value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        var expanded = Environment.ExpandEnvironmentVariables(s);
                        if (!string.Equals(expanded, s, StringComparison.Ordinal)) obj[key] = expanded;
                    }
                    else
                    {
                        Expand(value);
                    }
                }
                break;

            case System.Text.Json.Nodes.JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    var value = arr[i];
                    if (value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        var expanded = Environment.ExpandEnvironmentVariables(s);
                        if (!string.Equals(expanded, s, StringComparison.Ordinal)) arr[i] = expanded;
                    }
                    else
                    {
                        Expand(value);
                    }
                }
                break;
        }
    }

    [JsonIgnore] public string SourcePath { get; private set; } = "";
    [JsonIgnore] public string RuntimeDir { get; private set; } = "";

    /// <summary>
    /// The build the local server is started with, resolved by the hardware scan.
    ///
    /// It is not part of the file: it is decided at startup from the machine, and
    /// the profile that says which build to use lives beside the app rather than
    /// inside the shared checkout.
    /// </summary>
    [JsonIgnore] public LlamaRuntimeOptions? RuntimeOptions { get; set; }

    public string WrapperScript => Path.Combine(RuntimeDir, "notecode-llama.ps1");
    public string McpServersConfigPath => Path.Combine(RuntimeDir, Llama.Agent.McpServersConfig);

    /// <summary>Runtime directory to use when no config has been loaded yet.</summary>
    public static string RuntimeDirOrFallback()
    {
        try
        {
            var dir = Locate();
            return Path.GetDirectoryName(dir)!;
        }
        catch
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir is not null; i++)
            {
                if (Directory.Exists(Path.Combine(dir, "runtime"))) return Path.Combine(dir, "runtime");
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return AppContext.BaseDirectory;
        }
    }
}

internal sealed class LlamaSection
{
    public string ServerExe { get; set; } = "";
    public string ServerDir { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>
    /// The vision projector that goes with the model. MiniCPM-V is a vision model:
    /// the projector turns an image into tokens, and without it the server refuses
    /// any request that carries one. Empty means "this model does not see".
    /// </summary>
    public string Mmproj { get; set; } = "";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8080;
    public string Alias { get; set; } = "notecode-local";
    public CpuSection Cpu { get; set; } = new();
    public ContextSection Context { get; set; } = new();
    public AgentSection Agent { get; set; } = new();
    public ServerFlagsSection Server { get; set; } = new();

    /// <summary>
    /// The llama.cpp builds this machine can use, one per backend.
    ///
    /// Each build is a separate llama-server.exe, because a llama.cpp binary is
    /// compiled for one backend: a CPU build cannot use a GPU and a CUDA build
    /// cannot run without one. The key is the backend - cpu, vulkan, cuda, metal -
    /// and the scan picks the key, the key picks the executable.
    ///
    /// A build whose executable is not on disk is not an error. It is simply not
    /// offered, which is why a machine with no GPU build never sees one.
    /// </summary>
    public Dictionary<string, LlamaBuildDef> Builds { get; set; } = new();

    public string BaseUrl => $"http://{Host}:{Port}";
    public string OpenAiBase => $"{BaseUrl}/v1";
}

internal sealed class CpuSection
{
    public string Device { get; set; } = "none";
    public int NGpuLayers { get; set; }
    public int Threads { get; set; } = 16;
    public int ThreadsBatch { get; set; } = 16;
    public int ThreadsHttp { get; set; } = 8;
}

/// <summary>
/// One compiled llama.cpp server.
///
/// A llama-server.exe is built for exactly one backend, so "the GPU version" and
/// "the CPU version" are two different files. This is the entry that says which
/// file is which and what it has to be told to do.
/// </summary>
internal sealed class LlamaBuildDef
{
    /// <summary>What the build is called in the settings page.</summary>
    public string Label { get; set; } = "";

    /// <summary>The llama-server.exe for this backend.</summary>
    public string Exe { get; set; } = "";

    /// <summary>The --device value: none, vulkan, cuda or metal.</summary>
    public string Device { get; set; } = "none";

    /// <summary>How many model layers go on the GPU. 99 means all of them.</summary>
    public int NGpuLayers { get; set; }

    /// <summary>True when the executable is actually on this machine.</summary>
    [JsonIgnore]
    public bool Exists =>
        !string.IsNullOrWhiteSpace(Exe) && File.Exists(Exe);
}

internal sealed class ContextSection
{
    public int CtxSize { get; set; } = 32768;
    public string CacheTypeK { get; set; } = "q8_0";
    public string CacheTypeV { get; set; } = "q8_0";
    public int Parallel { get; set; } = 1;

    /// <summary>
    /// KV cache bytes per token for this model shape.
    /// qwen35moe: 40 layers x 2 kv heads x 256 head_dim x 2 (K and V).
    /// f16 = 80 KiB/token, q8_0 ~= 42.5 KiB/token.
    /// </summary>
    public double KvBytesPerToken(string cacheType) => cacheType switch
    {
        "q8_0" => 2 * 40 * 2 * 256 * 1.0625,
        "q4_0" => 2 * 40 * 2 * 256 * 0.5625,
        _ => 2 * 40 * 2 * 256 * 2.0
    };

    public double KvGiB => KvBytesPerToken(CacheTypeK) * CtxSize / 1024.0 / 1024.0 / 1024.0;
}

internal sealed class AgentSection
{
    public List<string> Tools { get; set; } = new();
    public string McpServersConfig { get; set; } = "mcp-servers.json";
    public bool WebuiMcpProxy { get; set; } = true;
    public string Reasoning { get; set; } = "off";
    public bool Jinja { get; set; } = true;
}

internal sealed class ServerFlagsSection
{
    public bool Jinja { get; set; } = true;
    public bool Slots { get; set; } = true;
    public string CorsOrigins { get; set; } = "localhost";
    public int TimeoutSeconds { get; set; } = 3600;
    public bool NoWarmup { get; set; }
    public string LogLevel { get; set; } = "info";
}

internal sealed class MemorySection
{
    public double BudgetGiB { get; set; } = 24;
    public double ModelGiB { get; set; } = 19.71;
    public double ReserveGiB { get; set; } = 1.0;
    public double WarnFreeSystemGiB { get; set; } = 6.0;
}

internal sealed class McpSection
{
    public Dictionary<string, McpServerDef> Servers { get; set; } = new();
}

internal sealed class McpServerDef
{
    public string Transport { get; set; } = "stdio";
    public string? Command { get; set; }
    public List<string> Args { get; set; } = new();
    public string? Url { get; set; }
    public int ToolTimeoutMs { get; set; } = 60000;
    public string? Description { get; set; }
}

internal sealed class KimiSection
{
    public string Exe { get; set; } = "";
    public string Config { get; set; } = "";
    public string McpConfig { get; set; } = "";
    public LocalProvider? LocalProvider { get; set; }
}

internal sealed class LocalProvider
{
    public string Name { get; set; } = "notecode-local";
    public string Type { get; set; } = "openai";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public int MaxContextSize { get; set; } = 32768;
    public List<string> Capabilities { get; set; } = new();
}

internal sealed class EditorSection
{
    public string Mode { get; set; } = "text/x-csrc";
    public string FontFamily { get; set; } = "Cascadia Code, Consolas, monospace";
    public int FontSize { get; set; } = 13;
    public int TabSize { get; set; } = 4;
    public bool LineWrapping { get; set; }
}

/// <summary>
/// How the app looks. The font scale is here because Microsoft's rule for
/// WinForms text is 10 point unless the size is adjustable by the person using
/// it - so it is adjustable, and this is where the choice is kept.
/// </summary>
internal sealed class UiSection
{
    /// <summary>The factor every font in the app is multiplied by. 1 is the built-in size.</summary>
    [JsonPropertyName("fontScale")] public float FontScale { get; set; } = 1f;
}

internal sealed class WorkspaceSection
{
    public string Root { get; set; } = "";
}

/// <summary>
/// The private search gateway the endpoint doctor uses to keep the provider
/// catalogue current. It is a plain MCP server that also runs as a command, and the
/// doctor calls it as a command.
/// </summary>
internal sealed class SearchSection
{
    /// <summary>Path to server.mjs.</summary>
    public string Server { get; set; } = "";

    /// <summary>The key file the gateway reads. Empty means "let the server decide".</summary>
    public string KeyFile { get; set; } = "";

    /// <summary>Search category: general, it, science, news.</summary>
    public string Category { get; set; } = "it";

    /// <summary>
    /// The category the endpoint doctor searches in. It is separate from
    /// <see cref="Category"/> on purpose: "it" is a programming corpus (MDN, Stack
    /// Overflow) and answers programming questions, but a vendor's base URL lives on
    /// the open web. Searching "MiniMax API base URL" in "it" returns HTML
    /// documentation about the base URL element; in "general" it returns the vendor
    /// docs. The doctor asks for the general web, so this defaults to "general".
    /// </summary>
    public string DoctorCategory { get; set; } = "general";

    /// <summary>How many results to ask for.</summary>
    public int Limit { get; set; } = 5;
}

/// <summary>
/// Where the skills live. A skill is a folder with a SKILL.md in it; the list is what
/// `/skills` shows and `/skill` loads from.
/// </summary>
internal sealed class SkillsSection
{
    public List<string> Roots { get; set; } = new();
}
