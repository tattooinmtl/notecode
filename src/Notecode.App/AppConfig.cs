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
    public ChatSection Chat { get; set; } = new();
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
        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new ExpandingStringConverter() }
        }) ?? throw new InvalidDataException("config deserialised to null");

        cfg.SourcePath = path;
        cfg.RuntimeDir = Path.GetDirectoryName(path)!;
        return cfg;
    }

    /// <summary>
    /// Expands %VARIABLE% in a string as the configuration is read.
    ///
    /// A path to something in the user's own folders - the search key, the
    /// skills, the kimi-code install - is written once and has to work on
    /// every machine, so it is written with %USERPROFILE% in it. A variable
    /// that is not set is left exactly as it is, so a path that has no
    /// variable in it is not damaged.
    ///
    /// The expansion happens on the way in, while the file is being read, and
    /// not on the text of the file before it is parsed. Two reasons, and both
    /// of them were learned the hard way.
    ///
    /// The first is that a configuration file with the same key twice is legal
    /// JSON but not a legal node tree: parsing it into a tree throws "an item
    /// with the same key has already been added", and a pass that expands the
    /// file by rewriting it fails on a file that is otherwise perfectly good.
    /// The reader here does not mind a repeated key - it takes the last one -
    /// so the file is read the same way by every other reader of it.
    ///
    /// The second is that a converter sees every string, in every object and
    /// every array, without a list of the fields that happen to hold a path.
    /// A path field added later is expanded too, instead of being the one
    /// field that was forgotten.
    /// </summary>
    private sealed class ExpandingStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Null ? null : Expand(reader.GetString() ?? string.Empty);

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);

        private static string Expand(string text) => Environment.ExpandEnvironmentVariables(text);
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

    /// <summary>
    /// Whether the server loads <see cref="Mmproj"/>. The projector is 1.1 GiB, and
    /// on a machine where the model barely fits that RAM is what keeps the model
    /// from paging, so vision is turned on only when it is wanted.
    /// </summary>
    public bool LoadMmproj { get; set; }

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
    public int Threads { get; set; } = 8;
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
    public int CtxSize { get; set; } = 12288;
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

    /// <summary>
    /// The tools an ordinary chat turn with the local model is given. Every schema is
    /// prompt the CPU reads and attends over on every token, so this is a short list
    /// rather than the whole catalogue. Empty means the whole catalogue.
    /// </summary>
    public List<string> ChatTools { get; set; } = new();

    /// <summary>Cap on one assistant turn of the local model. 0 means no cap.</summary>
    public int MaxTokens { get; set; } = 2048;

    /// <summary>Sampling temperature for chat turns, on the local model and on providers.</summary>
    public double Temperature { get; set; } = 0.7;

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
    public int CacheReuse { get; set; } = 256;
    public bool Mlock { get; set; }
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

    /// <summary>
    /// Not started with the app. The server is connected only when a command asks
    /// for it, and let go again when that command is done.
    /// </summary>
    public bool OnDemand { get; set; }

    /// <summary>A server that is switched off is not started and its tools are not offered.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The cloud provider and model to suggest when this server's tools cannot run
    /// on the local model. REA's catalogue is 111,434 tokens, so /rea refuses the
    /// local model and names these instead. Only a suggestion: the turn runs on the
    /// active provider.
    /// </summary>
    public string Provider { get; set; } = "";

    /// <summary>The model on <see cref="Provider"/> to suggest.</summary>
    public string Model { get; set; } = "";
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

/// <summary>
/// How a chat turn is put together: what context rides along and how much of it.
///
/// Everything here is prompt, and on a CPU every token of prompt is read again for
/// every token the model writes. The defaults keep an ordinary local turn near 2k
/// tokens; the settings page says what each one costs.
/// </summary>
internal sealed class ChatSection
{
    /// <summary>Send the open editor buffer with each turn.</summary>
    public bool EditorContext { get; set; } = true;

    /// <summary>How much of the editor buffer is sent.</summary>
    public int EditorContextChars { get; set; } = 6_000;

    /// <summary>How much of one tool result the model is given back.</summary>
    public int ToolResultChars { get; set; } = 4_000;

    /// <summary>How many messages of history (user and assistant) are sent back.</summary>
    public int HistoryMessages { get; set; } = 8;

    /// <summary>The most characters of history sent back, all messages together.</summary>
    public int HistoryChars { get; set; } = 6_000;

    /// <summary>How much of any one message is kept in the history.</summary>
    public int HistoryMessageChars { get; set; } = 1_500;

    /// <summary>The most model/tool round trips in one turn.</summary>
    public int MaxToolTurns { get; set; } = 8;

    /// <summary>
    /// The agent instructions file, relative to the workspace root. Its text is
    /// added to the system prompt when <see cref="SendInstructions"/> is on.
    /// </summary>
    public string InstructionsFile { get; set; } = "agents.md";

    /// <summary>Add the instructions file to the system prompt of every turn.</summary>
    public bool SendInstructions { get; set; } = true;

    /// <summary>The instructions are cut at this many characters, so a long file cannot swamp the context.</summary>
    public int InstructionsChars { get; set; } = 8_000;
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
