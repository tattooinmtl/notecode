using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Notecode.App;

/// <summary>What the doctor found for one provider.</summary>
internal sealed class DoctorResult
{
    public string Provider { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>True when the endpoint answered a models request.</summary>
    public bool Reachable { get; set; }

    /// <summary>What the endpoint said when it was asked.</summary>
    public string Status { get; set; } = "";

    /// <summary>The models the endpoint lists right now.</summary>
    public List<string> Models { get; set; } = new();

    /// <summary>A base URL the web suggests, when it differs from the configured one.</summary>
    public string? SuggestedBaseUrl { get; set; }

    /// <summary>Model names the web suggests that the endpoint does not list.</summary>
    public List<string> SuggestedModels { get; set; } = new();

    /// <summary>Anything worth telling the user about this provider.</summary>
    public string Note { get; set; } = "";
}

/// <summary>
/// The endpoint doctor.
///
/// Two halves: it asks the endpoint itself what models it has, and it searches the web
/// for the provider's current base URL and model names. Providers move their endpoints
/// and rename their models, and a stale base URL is the difference between a working
/// agent and a 404.
///
/// The web half runs through the private GWN search gateway
/// (`SEARCH_GWN_MCP/server.mjs`) as a plain command, not as an MCP server: the doctor
/// needs one answer, and giving the model a search tool would spend context on
/// something the app can do by itself.
/// </summary>
internal sealed class EndpointDoctor
{
    private readonly AppConfig _cfg;
    private readonly HttpClient _http;

    public EndpointDoctor(AppConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    /// <summary>
    /// Checks one provider. The search step is best-effort: when the gateway is down
    /// the endpoint check still runs and the result says the search was skipped.
    /// </summary>
    public async Task<DoctorResult> CheckAsync(ProviderDef provider, CancellationToken ct = default)
    {
        var result = new DoctorResult { Provider = provider.Name, Label = provider.Label };

        await CheckEndpointAsync(provider, result, ct);

        try
        {
            await CheckAgainstTheWebAsync(provider, result, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result.Note = Append(result.Note, $"the web check did not run ({ex.Message})");
        }

        return result;
    }

    /// <summary>Checks every provider in the store.</summary>
    public async Task<List<DoctorResult>> CheckAllAsync(
        ProviderStore store, string? only = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var results = new List<DoctorResult>();
        foreach (var p in store.Providers)
        {
            if (only is not null && !string.Equals(p.Name, only, StringComparison.OrdinalIgnoreCase)) continue;
            progress?.Report($"checking {p.Label} ({p.BaseUrl})");
            var r = await CheckAsync(p, ct);
            results.Add(r);
            progress?.Report($"{p.Name}: {r.Status}");
        }
        return results;
    }

    // --------------------------------------------------------------- the endpoint

    private async Task CheckEndpointAsync(ProviderDef provider, DoctorResult result, CancellationToken ct)
    {
        if (provider.IsLocal)
        {
            // The local server has its own doctor in runtime/notecode-llama.ps1.
            result.Reachable = true;
            result.Status = "local - managed by notecode-llama.ps1";
            result.Models.Add(provider.Model);
            return;
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            result.Status = "no base URL";
            return;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, provider.ModelsUrl);
            AddAuth(req, provider);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            result.Reachable = resp.IsSuccessStatusCode;
            if (!resp.IsSuccessStatusCode)
            {
                // Not every OpenAI-compatible endpoint lists its models. Google's
                // generativelanguage v1beta/openai layer answers 404 on /models and
                // works fine on /chat/completions, so a missing model list is not
                // proof that the base URL is wrong. The chat path is asked instead.
                if (resp.StatusCode == HttpStatusCode.NotFound &&
                    await ChatPathExistsAsync(provider, ct).ConfigureAwait(false))
                {
                    result.Reachable = true;
                    result.Status = "reachable, no model list";
                    return;
                }

                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                result.Status = $"HTTP {(int)resp.StatusCode}";
                result.Note = Append(result.Note, Trim(body, 200));
                return;
            }

            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            result.Models = ParseModels(text);
            result.Status = result.Models.Count > 0
                ? $"reachable, {result.Models.Count} models"
                : "reachable";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result.Reachable = false;
            result.Status = "unreachable";
            result.Note = Append(result.Note, ex.Message);
        }
    }

    /// <summary>
    /// Asks the chat endpoint whether the base URL points at something real. A
    /// response that is not 404 means the path exists - a missing API key answers
    /// 400 or 401, which still proves the endpoint is there.
    /// </summary>
    private async Task<bool> ChatPathExistsAsync(ProviderDef provider, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, provider.ChatUrl);
            AddAuth(req, provider);
            req.Content = new StringContent(
                "{\"model\":\"probe\",\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}",
                Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.StatusCode != HttpStatusCode.NotFound;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static void AddAuth(HttpRequestMessage req, ProviderDef provider)
    {
        var token = SecretBox.Unprotect(provider.ProtectedToken);
        if (string.IsNullOrEmpty(token)) return;
        if (provider.Style == ApiStyle.Anthropic)
        {
            req.Headers.TryAddWithoutValidation("x-api-key", token);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }
    }

    /// <summary>Reads the model ids out of an OpenAI-style models response.</summary>
    private static List<string> ParseModels(string body)
    {
        var models = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return models;
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    var value = id.GetString();
                    if (!string.IsNullOrEmpty(value)) models.Add(value);
                }
            }
        }
        catch (JsonException)
        {
            // Anthropic's models endpoint has a different shape; the ids still sit
            // under "data", but a plain array is accepted too.
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            models.Add(id.GetString() ?? "");
                    }
                }
            }
            catch (JsonException) { /* not a model list */ }
        }
        return models;
    }

    // ----------------------------------------------------------------- the web

    private async Task CheckAgainstTheWebAsync(ProviderDef provider, DoctorResult result, CancellationToken ct)
    {
        var query = $"{provider.Label} API base URL chat completions endpoint";
        var text = await SearchAsync(query, ct).ConfigureAwait(false);
        if (text is null)
        {
            result.Note = Append(result.Note, "the search gateway did not answer");
            return;
        }

        foreach (var url in ExtractUrls(text))
        {
            if (!LooksLikeApiBase(url)) continue;
            if (SameEndpoint(url, provider.BaseUrl)) continue;
            if (result.SuggestedBaseUrl is null) result.SuggestedBaseUrl = Normalise(url);
        }

        // A second, narrower question: what is this provider called now.
        var modelText = await SearchAsync($"{provider.Label} API model names list", ct).ConfigureAwait(false);
        if (modelText is not null)
        {
            foreach (var candidate in ExtractModelNames(modelText))
            {
                if (result.Models.Contains(candidate)) continue;
                if (!result.SuggestedModels.Contains(candidate)) result.SuggestedModels.Add(candidate);
            }
        }
    }

    /// <summary>
    /// Runs the GWN search gateway as a command and returns its text output, or null
    /// when the gateway cannot be reached.
    /// </summary>
    private async Task<string?> SearchAsync(string query, CancellationToken ct)
    {
        var server = _cfg.Search.Server;
        if (string.IsNullOrWhiteSpace(server) || !File.Exists(server)) return null;

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(server) ?? _cfg.RuntimeDir
        };
        psi.ArgumentList.Add(server);
        psi.ArgumentList.Add("--search");
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(word);
        psi.ArgumentList.Add("--category");
        // The doctor wants the open web, not the programming corpus. Searching a
        // vendor's base URL in "it" returns articles about the URL API; in
        // "general" it returns the vendor documentation.
        var category = string.IsNullOrWhiteSpace(_cfg.Search.DoctorCategory)
            ? "general"
            : _cfg.Search.DoctorCategory;
        psi.ArgumentList.Add(category);
        psi.ArgumentList.Add("--limit");
        psi.ArgumentList.Add(_cfg.Search.Limit.ToString());

        var keyFile = _cfg.Search.KeyFile;
        if (!string.IsNullOrWhiteSpace(keyFile) && File.Exists(keyFile))
        {
            psi.Environment["SEARCH_GWN_KEY_FILE"] = keyFile;
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0) return null;

            return stdout;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static readonly Regex UrlRegex = new(@"https?://[^\s\)\]\""']+", RegexOptions.Compiled);

    /// <summary>Every URL in the search output.</summary>
    public static List<string> ExtractUrls(string text)
    {
        var urls = new List<string>();
        foreach (Match m in UrlRegex.Matches(text ?? ""))
        {
            var url = m.Value.TrimEnd('.', ',', ':', ';');
            if (!urls.Contains(url)) urls.Add(url);
        }
        return urls;
    }

    /// <summary>
    /// Model-shaped words in the search output: a token that looks like a model id
    /// (letters, digits and dashes) that is not a common English word.
    /// </summary>
    public static List<string> ExtractModelNames(string text)
    {
        var names = new List<string>();
        var rx = new Regex(@"\b([a-z][a-z0-9]*(?:[-.][a-z0-9]+){1,4})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "api-key", "api-keys", "chat-completions", "openai-compatible", "base-url",
            "e.g", "i.e", "web-search", "read-only", "up-to-date"
        };

        // Words that show up in a page path rather than in a model id.
        var junk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "and", "the", "for", "with", "using", "how", "what", "get", "use", "api", "apis",
            "docs", "doc", "guide", "guides", "reference", "overview", "including",
            "com", "www", "http", "https", "html", "pdf", "models", "model", "endpoint",
            "endpoints", "quickstart", "introduction", "tutorial", "example", "examples",
            "sample", "samples", "blog", "news", "pricing", "features", "index", "page"
        };

        foreach (var line in (text ?? "").Split('\n'))
        {
            // A result's URL line is a slug, not a model name.
            var trimmed = line.Trim();
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (Match m in rx.Matches(line))
            {
                var value = m.Groups[1].Value;
                if (value.Length < 6 || value.Length > 40) continue;
                if (noise.Contains(value)) continue;
                if (!value.Any(char.IsDigit)) continue;   // a model id usually carries a version

                // Slugs such as "and-api-including-grok-4" or
                // "deepseek-models-overview-00381" are page paths, not model ids.
                var segments = value.Split('-', '.');
                if (segments.Any(s => junk.Contains(s))) continue;

                if (!names.Contains(value)) names.Add(value);
            }
        }
        return names;
    }

    /// <summary>True when the URL looks like an API root rather than a documentation page.</summary>
    public static bool LooksLikeApiBase(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var lower = url.ToLowerInvariant();
        if (lower.Contains("/docs") || lower.Contains("/blog") || lower.Contains("/pricing") ||
            lower.Contains("/features") || lower.Contains("github.com") || lower.Contains("wikipedia"))
            return false;
        return lower.Contains("/v1") || lower.EndsWith(".ai") || lower.Contains("api.");
    }

    /// <summary>True when the two URLs point at the same endpoint.</summary>
    public static bool SameEndpoint(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        // The version segment is optional. api.deepseek.com and api.deepseek.com/v1
        // are the same endpoint, and a search result that drops the segment is not
        // a different provider - treating them as different made the doctor offer
        // to "fix" a base URL that was already right.
        if (Uri.TryCreate(a, UriKind.Absolute, out var ua) &&
            Uri.TryCreate(b, UriKind.Absolute, out var ub))
        {
            if (!ua.Host.Equals(ub.Host, StringComparison.OrdinalIgnoreCase)) return false;
            if (!ua.Scheme.Equals(ub.Scheme, StringComparison.OrdinalIgnoreCase)) return false;

            var va = VersionSegment(ua.AbsolutePath);
            var vb = VersionSegment(ub.AbsolutePath);
            // Both name a version: they have to be the same one.
            if (va.Length > 0 && vb.Length > 0) return va == vb;
            return true;
        }

        return string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The "v1" part of a path, lower cased, or an empty string.</summary>
    private static string VersionSegment(string path)
    {
        var m = Regex.Match(path ?? "", @"/(v\d+[a-z0-9]*)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "";
    }

    /// <summary>Trims a URL down to its API root, without a trailing slash.</summary>
    public static string Normalise(string url)
    {
        var value = (url ?? "").Trim().TrimEnd('/');
        // Cut a documentation path off the end, keep the version segment.
        var match = Regex.Match(value, @"^(https?://[^/]+(?:/v\d+)?)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : value;
    }

    private static string Append(string note, string extra) =>
        string.IsNullOrWhiteSpace(note) ? extra : note + "; " + extra;

    private static string Trim(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");
}
