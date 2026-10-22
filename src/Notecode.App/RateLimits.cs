using System.Text.Json;

namespace Notecode.App;

/// <summary>
/// The request limits the providers publish, so a provider entry starts with a
/// sensible number instead of none.
///
/// Every one of these depends on the account: the plan, the spend tier, free or
/// paid. The figure here is the entry-level one that was published when this was
/// written (October 2026), with where it came from in the note. The user's own
/// number goes in Settings > Providers and wins: a provider entry whose limit has
/// been set is never touched by this table.
/// </summary>
internal static class KnownLimits
{
    internal sealed record Limit(int Requests, int WindowMinutes, string Note);

    // Matched against the provider's base URL host.
    private static readonly (string Host, Limit Limit)[] Table =
    {
        ("api.minimax.io", new(200, 60,
            "MiniMax: 200 calls an hour on your plan. Token Plan quotas run on rolling 5-hour and weekly windows; " +
            "the console shows what is left. Over the limit it answers HTTP 429.")),
        ("api.moonshot.ai", new(500, 1,
            "Moonshot (Kimi): about 500 requests a minute (third-party figure; tiered by spend).")),
        ("api.anthropic.com", new(1000, 1,
            "Anthropic: 1,000 requests a minute on the Start tier (since June 2026; older Tier 1 was 50).")),
        ("api.openai.com", new(500, 1,
            "OpenAI: about 500 requests a minute on Tier 1; it varies by model.")),
        ("api.x.ai", new(2220, 1,
            "xAI: 37 requests a second on spend tier T0 for Grok 4.3 (more on higher tiers and newer models).")),
        ("generativelanguage.googleapis.com", new(20, 1440,
            "Gemini free tier: about 20 requests a day and 5 a minute on Flash; paid tiers are far higher (see AI Studio).")),
        ("api.deepseek.com", new(0, 60,
            "DeepSeek: no request-per-minute limit; it caps requests in flight and answers 429 under load.")),
        ("integrate.api.nvidia.com", new(40, 1,
            "NVIDIA NIM: 40 requests a minute per model on the developer program.")),
        ("openrouter.ai", new(20, 1,
            "OpenRouter free models: 20 a minute and 50 a day (1,000 a day after $10 of credits). Paid models are not capped this way.")),
        ("api.groq.com", new(30, 1,
            "Groq free tier: 30 requests a minute and 1,000 a day per model.")),
        ("api.mistral.ai", new(60, 1,
            "Mistral: limits are per second and shown in the admin panel, not published; 1 a second is the old free figure.")),
        ("dashscope.aliyuncs.com", new(0, 60,
            "Qwen (DashScope): no public figure found - set yours from the Model Studio console.")),
        ("localhost", new(0, 60, "A local server: no limit.")),
        ("127.0.0.1", new(0, 60, "A local server: no limit.")),
    };

    public static Limit? For(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return null;
        foreach (var (host, limit) in Table)
        {
            if (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))
                return limit;
        }
        return null;
    }

    /// <summary>Gives a provider the published limit when it has none of its own yet.</summary>
    public static void Apply(ProviderDef p)
    {
        if (p.IsLocal || p.RateLimit is not null) return;
        var known = For(p.BaseUrl);
        if (known is null) return;
        p.RateLimit = known.Requests;
        p.RateWindowMinutes = known.WindowMinutes;
        if (string.IsNullOrEmpty(p.RateLimitNote)) p.RateLimitNote = known.Note;
    }

    public static string WindowName(int minutes) => minutes switch
    {
        1 => "minute",
        60 => "hour",
        300 => "5 hours",
        1440 => "day",
        _ => $"{minutes} minutes"
    };
}

/// <summary>
/// Counts the requests sent to each provider in a rolling window, so a turn can stop
/// before the provider starts refusing it.
///
/// The counts are kept in the user's data folder, because the windows are hours long
/// and a restart of the app does not reset the provider's count.
/// </summary>
internal sealed class UsageMeter
{
    private readonly string _path = Path.Combine(AppPaths.DataDir, "usage.json");
    private readonly Dictionary<string, List<DateTimeOffset>> _sent;

    public UsageMeter()
    {
        try
        {
            _sent = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, List<DateTimeOffset>>>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch
        {
            _sent = new();
        }
    }

    /// <summary>How many requests went to the provider in its window.</summary>
    public int Used(ProviderDef p)
    {
        lock (_sent) return Window(p).Count;
    }

    /// <summary>When the oldest request in the window drops out of it.</summary>
    public DateTimeOffset? FreesAt(ProviderDef p)
    {
        lock (_sent)
        {
            var list = Window(p);
            return list.Count == 0 ? null : list[0].AddMinutes(Math.Max(1, p.RateWindowMinutes));
        }
    }

    /// <summary>True when one more request would go over the provider's limit.</summary>
    public bool AtLimit(ProviderDef p) => p.RateLimit is > 0 && Used(p) >= p.RateLimit;

    public void Record(ProviderDef p)
    {
        if (p.IsLocal) return;
        lock (_sent)
        {
            Window(p).Add(DateTimeOffset.Now);
            Save();
        }
    }

    // The provider's timestamps, with the ones older than its window dropped.
    private List<DateTimeOffset> Window(ProviderDef p)
    {
        if (!_sent.TryGetValue(p.Name, out var list)) _sent[p.Name] = list = new();
        var cutoff = DateTimeOffset.Now.AddMinutes(-Math.Max(1, p.RateWindowMinutes));
        list.RemoveAll(t => t < cutoff);
        return list;
    }

    private void Save()
    {
        try
        {
            // Nothing older than a day is worth keeping: no window is longer.
            var cutoff = DateTimeOffset.Now.AddDays(-1);
            foreach (var list in _sent.Values) list.RemoveAll(t => t < cutoff);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_sent));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"usage could not be saved: {ex.Message}");
        }
    }
}
