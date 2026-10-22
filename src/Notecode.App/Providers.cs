using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notecode.App;

/// <summary>How a provider expects to be called.</summary>
internal enum ApiStyle
{
    /// <summary>OpenAI-compatible: POST {base}/chat/completions with a bearer token.</summary>
    OpenAi,

    /// <summary>Anthropic Messages: POST {base}/messages with x-api-key and anthropic-version.</summary>
    Anthropic
}

/// <summary>
/// One BYOK endpoint: where to send the turn, how to authenticate, and what to call it.
///
/// The bearer token is never stored in the clear. <see cref="ProtectedToken"/> holds a
/// DPAPI blob that only this Windows user can decrypt, so the file on disk is useless to
/// anyone else and the token never ends up in a backup or a screenshot.
/// </summary>
internal sealed class ProviderDef
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public ApiStyle Style { get; set; } = ApiStyle.OpenAi;
    public string Model { get; set; } = "";
    public List<string> Models { get; set; } = new();

    /// <summary>A chat template lifted out of a GGUF file, for endpoints that need one.</summary>
    public string ChatTemplate { get; set; } = "";

    /// <summary>DPAPI-protected bearer token, base64. Empty means "no token".</summary>
    public string ProtectedToken { get; set; } = "";

    /// <summary>"preset" or "manual" - where this entry came from.</summary>
    public string Source { get; set; } = "preset";

    /// <summary>The local llama-server. It cannot be removed and it owns no token.</summary>
    public bool IsLocal { get; set; }

    /// <summary>
    /// Send the reasoning of an assistant turn back with that turn.
    ///
    /// Not part of the OpenAI chat spec, but the Reflection Beam model answers 400
    /// if an earlier assistant message comes back without the reasoning that
    /// produced it. Only turn it on for a provider that asks for it.
    /// </summary>
    public bool EchoReasoning { get; set; }

    /// <summary>
    /// How hard the model thinks before it answers, sent as <c>reasoning_effort</c>:
    /// low, medium, high, xhigh or max. Empty sends nothing and the provider decides.
    /// MiniMax-M3.1-Flash defaults to max, which makes even "hello" take a long think.
    /// </summary>
    public string ReasoningEffort { get; set; } = "";

    /// <summary>
    /// Requests allowed in <see cref="RateWindowMinutes"/>. Null means not set yet, and
    /// the published figure is filled in (<see cref="KnownLimits"/>); 0 means no limit.
    /// </summary>
    public int? RateLimit { get; set; }

    /// <summary>The window the limit counts in: 1 (a minute), 60, 300 (5 hours) or 1440 (a day).</summary>
    public int RateWindowMinutes { get; set; } = 60;

    /// <summary>Where the limit comes from, shown in Settings > Providers.</summary>
    public string RateLimitNote { get; set; } = "";

    /// <summary>
    /// The most tool rounds in one turn on this provider. 0 uses the chat setting
    /// (chat.maxToolTurns for the local model, chat.cloudMaxToolTurns otherwise).
    /// </summary>
    public int MaxToolTurns { get; set; }

    public DateTimeOffset? LastChecked { get; set; }
    public string DoctorNote { get; set; } = "";

    [JsonIgnore]
    public bool HasToken => ProtectedToken.Length > 0;

    /// <summary>The URL the chat endpoint lives at.</summary>
    public string ChatUrl => Style == ApiStyle.Anthropic
        ? $"{BaseUrl.TrimEnd('/')}/messages"
        : $"{BaseUrl.TrimEnd('/')}/chat/completions";

    /// <summary>The URL the model list lives at.</summary>
    public string ModelsUrl => Style == ApiStyle.Anthropic
        ? $"{BaseUrl.TrimEnd('/')}/models"
        : $"{BaseUrl.TrimEnd('/')}/models";

    public ProviderDef Clone() => (ProviderDef)MemberwiseClone();
}

/// <summary>
/// The on-disk provider list: runtime/providers.json.
///
/// The file holds endpoints, model names and the encrypted tokens. It is created on
/// first run with the preset catalogue, and it is the only thing the app writes when
/// the user adds or edits a provider.
/// </summary>
internal sealed class ProviderStore
{
    public string Active { get; set; } = "local";
    public List<ProviderDef> Providers { get; set; } = new();

    public static string FileName => "providers.json";

    public static string PathFor(string runtimeDir) => Path.Combine(runtimeDir, FileName);

    public static ProviderStore Load(string runtimeDir) => LoadFile(PathFor(runtimeDir));

    /// <summary>
    /// Reads a provider store from an explicit path. A file that is not there is not
    /// an error - the presets are the baseline and the file is the user's edits on
    /// top of them.
    /// </summary>
    public static ProviderStore LoadFile(string path)
    {
        ProviderStore? store = null;
        if (File.Exists(path))
        {
            try
            {
                store = JsonSerializer.Deserialize<ProviderStore>(File.ReadAllText(path), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
            }
            catch (Exception ex)
            {
                AppLog.Warn($"providers.json could not be read ({ex.Message}); starting from the presets");
            }
        }

        store ??= new ProviderStore();
        store.MergePresets();

        // A provider without a limit of its own starts with the published one,
        // whether it is a preset or one added by hand for the same endpoint.
        foreach (var p in store.Providers) KnownLimits.Apply(p);
        return store;
    }

    /// <summary>
    /// Makes sure every preset exists. A preset the user edited keeps its edits; a
    /// preset that is missing is added back, because the catalogue is the baseline.
    /// </summary>
    private void MergePresets()
    {
        Providers.RemoveAll(p => p is null);
        foreach (var preset in ProviderPresets.All)
        {
            var existing = Providers.FirstOrDefault(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                Providers.Add(preset.Clone());
                continue;
            }
            // A preset that was never touched stays in sync with the catalogue.
            if (existing.Source == "preset")
            {
                existing.BaseUrl = preset.BaseUrl;
                existing.Style = preset.Style;
                existing.Label = preset.Label;
                existing.Model = string.IsNullOrEmpty(existing.Model) ? preset.Model : existing.Model;
                existing.EchoReasoning = preset.EchoReasoning;
                foreach (var m in preset.Models) if (!existing.Models.Contains(m)) existing.Models.Add(m);
            }
        }
    }

    public void Save(string runtimeDir)
    {
        var path = PathFor(runtimeDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    public ProviderDef? Find(string name) =>
        Providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public ProviderDef ActiveProvider() =>
        Find(Active) ?? Providers.FirstOrDefault(p => p.IsLocal) ?? Providers.First();

    /// <summary>Sets the active provider by name. Returns false when there is no such provider.</summary>
    public bool SetActive(string name)
    {
        var p = Find(name);
        if (p is null) return false;
        Active = p.Name;
        return true;
    }
}

/// <summary>
/// The preset catalogue: the endpoints the user asked for, pre-filled.
///
/// These are starting points, not gospel - the endpoint doctor refreshes them against
/// the live web, and every field is editable in the Add Provider form.
/// </summary>
internal static class ProviderPresets
{
    public static IReadOnlyList<ProviderDef> All { get; } = new List<ProviderDef>
    {
        new()
        {
            Name = "local", Label = "Local model (llama-server)", IsLocal = true, Source = "builtin",
            BaseUrl = "http://127.0.0.1:8080/v1", Style = ApiStyle.OpenAi,
            Model = "notecode-minicpm", Models = { "notecode-minicpm" },
            DoctorNote = "the local llama-server; the model is whatever notecode.config.json points at"
        },
        new()
        {
            Name = "minimax", Label = "MiniMax", Source = "preset",
            BaseUrl = "https://api.minimax.io/v1", Style = ApiStyle.OpenAi,
            Model = "MiniMax-M3.1-Flash", Models = { "MiniMax-M3.1-Flash", "MiniMax-M2", "MiniMax-M2.1" },
            DoctorNote = "Token Plan and M Plan both work against api.minimax.io/v1"
        },
        new()
        {
            Name = "moonshot", Label = "Moonshot AI (Kimi)", Source = "preset",
            BaseUrl = "https://api.moonshot.ai/v1", Style = ApiStyle.OpenAi,
            Model = "kimi-k2-turbo-preview", Models = { "kimi-k2-turbo-preview", "kimi-latest" }
        },
        new()
        {
            Name = "anthropic", Label = "Anthropic (Claude)", Source = "preset",
            BaseUrl = "https://api.anthropic.com/v1", Style = ApiStyle.Anthropic,
            Model = "claude-sonnet-4-5", Models = { "claude-sonnet-4-5", "claude-opus-4-1", "claude-haiku-4-5" }
        },
        new()
        {
            Name = "openai", Label = "OpenAI", Source = "preset",
            BaseUrl = "https://api.openai.com/v1", Style = ApiStyle.OpenAi,
            Model = "gpt-4.1", Models = { "gpt-4.1", "gpt-4.1-mini", "o3" }
        },
        new()
        {
            Name = "grok", Label = "xAI (Grok)", Source = "preset",
            BaseUrl = "https://api.x.ai/v1", Style = ApiStyle.OpenAi,
            Model = "grok-4", Models = { "grok-4", "grok-3", "grok-code-fast-1" }
        },
        new()
        {
            Name = "antigravity", Label = "Antigravity (Google)", Source = "preset",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai", Style = ApiStyle.OpenAi,
            Model = "gemini-2.5-pro", Models = { "gemini-2.5-pro", "gemini-2.5-flash" },
            DoctorNote = "Antigravity drives Gemini; this is the OpenAI-compatible Gemini endpoint"
        },
        new()
        {
            Name = "deepseek", Label = "DeepSeek", Source = "preset",
            BaseUrl = "https://api.deepseek.com/v1", Style = ApiStyle.OpenAi,
            Model = "deepseek-chat", Models = { "deepseek-chat", "deepseek-reasoner" }
        },
        new()
        {
            Name = "nvidia", Label = "NVIDIA NIM", Source = "preset",
            BaseUrl = "https://integrate.api.nvidia.com/v1", Style = ApiStyle.OpenAi,
            Model = "meta/llama-3.3-70b-instruct", Models = { "meta/llama-3.3-70b-instruct", "nvidia/llama-3.1-nemotron-70b-instruct" }
        },
        new()
        {
            Name = "qwen", Label = "Qwen (DashScope)", Source = "preset",
            BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1", Style = ApiStyle.OpenAi,
            Model = "qwen-max", Models = { "qwen-max", "qwen-plus", "qwen3-coder-plus" }
        },

        // The rest of the catalogue, from the same endpoint list omni ships with.

        new()
        {
            Name = "openrouter", Label = "OpenRouter", Source = "preset",
            BaseUrl = "https://openrouter.ai/api/v1", Style = ApiStyle.OpenAi,
            Model = "anthropic/claude-3.5-sonnet",
            Models = { "anthropic/claude-3.5-sonnet", "meta-llama/llama-3-8b-instruct:free", "mistralai/mistral-7b-instruct:free" },
            DoctorNote = "one key, many vendors; the model id carries the vendor, for example anthropic/claude-3.5-sonnet"
        },
        new()
        {
            Name = "groq", Label = "Groq", Source = "preset",
            BaseUrl = "https://api.groq.com/openai/v1", Style = ApiStyle.OpenAi,
            Model = "llama-3.3-70b-versatile",
            Models = { "llama-3.3-70b-versatile", "llama-3.1-8b-instant" },
            DoctorNote = "very fast tokens per second on a free tier"
        },
        new()
        {
            Name = "mistral", Label = "Mistral", Source = "preset",
            BaseUrl = "https://api.mistral.ai/v1", Style = ApiStyle.OpenAi,
            Model = "mistral-large-latest", Models = { "mistral-large-latest", "codestral-latest" }
        },
        new()
        {
            Name = "ollama", Label = "Ollama (local)", Source = "preset",
            BaseUrl = "http://localhost:11434/v1", Style = ApiStyle.OpenAi,
            Model = "qwen2.5-coder", Models = { "qwen2.5-coder", "llama3.1" },
            DoctorNote = "a local Ollama server; it needs no key, so leave the bearer token empty"
        },
        new()
        {
            Name = "agnes", Label = "Agnes AI", Source = "preset",
            BaseUrl = "https://apihub.agnes-ai.com/v1", Style = ApiStyle.OpenAi,
            Model = "agnes-2.5-flash", Models = { "agnes-2.5-flash", "agnes-2.5-pro-alpha", "agnes-2.0-flash" },
            DoctorNote = "keys come from https://apihub.agnes-ai.com"
        },
        new()
        {
            Name = "xkiro", Label = "xKiro", Source = "preset",
            BaseUrl = "https://api.xkiro.com/v1", Style = ApiStyle.OpenAi,
            Model = "mistralai/mistral-large-2512",
            Models = { "mistralai/mistral-large-2512", "mistralai/devstral-medium", "openai/gpt-5.3-codex-spark" },
            DoctorNote = "a free tier of 5M tokens a day across eleven free-priced models"
        },
        new()
        {
            Name = "atria", Label = "Atria ASI", Source = "preset",
            BaseUrl = "https://api.atria-asi.ai/v1", Style = ApiStyle.OpenAi,
            Model = "Atria-Dawn-Preview", Models = { "Atria-Dawn-Preview" },
            DoctorNote = "the keys look like atr_..."
        },
        new()
        {
            Name = "reflection", Label = "Reflection AI (Beam)", Source = "preset",
            BaseUrl = "https://api.reflection.ai/openai/v1", Style = ApiStyle.OpenAi,
            Model = "Beam-1.0-501B-A23B", Models = { "Beam-1.0-501B-A23B" },
            EchoReasoning = true,
            DoctorNote = "Beam has a 1M context and reasons on every request; it needs the reasoning sent back with every turn"
        }
    };
}

/// <summary>
/// Windows DPAPI, used so a bearer token is never written to disk in the clear.
///
/// CryptProtectData with CRYPTPROTECT_UI_FORBIDDEN encrypts for the current Windows
/// user only. Anything that reads the file on another account, or on another machine,
/// gets nothing back.
/// </summary>
internal static class SecretBox
{
    private const int CryptprotectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>Encrypts a secret for the current user. Returns an empty string on failure.</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = Encoding.UTF8.GetBytes(plain);
        var blob = default(DataBlob);
        try
        {
            blob.cbData = bytes.Length;
            blob.pbData = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blob.pbData, bytes.Length);

            if (!CryptProtectData(ref blob, "notecode provider token", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptprotectUiForbidden, out var encrypted))
            {
                throw new InvalidOperationException($"CryptProtectData failed: {Marshal.GetLastWin32Error()}");
            }

            var result = new byte[encrypted.cbData];
            Marshal.Copy(encrypted.pbData, result, 0, encrypted.cbData);
            LocalFree(encrypted.pbData);
            return Convert.ToBase64String(result);
        }
        finally
        {
            if (blob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(blob.pbData);
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    /// <summary>Decrypts a blob. Returns an empty string when it cannot be read.</summary>
    public static string Unprotect(string blob)
    {
        if (string.IsNullOrEmpty(blob)) return "";
        try
        {
            var raw = Convert.FromBase64String(blob);
            var data = default(DataBlob);
            try
            {
                data.cbData = raw.Length;
                data.pbData = Marshal.AllocHGlobal(raw.Length);
                Marshal.Copy(raw, 0, data.pbData, raw.Length);

                if (!CryptUnprotectData(ref data, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CryptprotectUiForbidden, out var plain))
                {
                    return "";
                }

                var result = new byte[plain.cbData];
                Marshal.Copy(plain.pbData, result, 0, plain.cbData);
                LocalFree(plain.pbData);
                return Encoding.UTF8.GetString(result);
            }
            finally
            {
                if (data.pbData != IntPtr.Zero) Marshal.FreeHGlobal(data.pbData);
            }
        }
        catch
        {
            return "";
        }
    }
}
