using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Notecode.App;

/// <summary>
/// The agent conversation surface.
///
/// The transcript lives in a WebView2 rather than a RichTextBox because an agent
/// harness is not a log: it has user turns, assistant turns, tool cards with their
/// arguments and their results, and a streaming caret. Rich HTML is the only sane way
/// to render that, and it is the same surface the editor and the terminal already use.
///
/// Messages that arrive before the page has booted are queued and replayed, so the
/// first line of the transcript never gets lost to a race.
/// </summary>
internal sealed class ChatView : IDisposable
{
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly List<string> _pending = new();
    private bool _ready;

    public Control Control => _view;

    /// <summary>Raised when the user clicks one of the suggested prompts.</summary>
    public event Action<string>? PromptRequested;

    public async Task InitAsync(string webroot)
    {
        var env = await CoreWebView2Environment.CreateAsync(
            null, Path.Combine(Path.GetTempPath(), "notecode-webview"));
        await _view.EnsureCoreWebView2Async(env);

        _view.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "notecode.local", webroot, CoreWebView2HostResourceAccessKind.DenyCors);
        _view.CoreWebView2.Settings.AreDevToolsEnabled = true;
        _view.CoreWebView2.Settings.AreHostObjectsAllowed = false;

        _view.CoreWebView2.WebMessageReceived += OnMessage;
        _view.Source = new Uri("https://notecode.local/chat.html");
    }

    /// <summary>
    /// Shows the transcript once the layout has settled.
    ///
    /// The control is kept hidden while the panel is being laid out. A WebView2 that
    /// paints at one size and is then resized can leave a stale tile of the old
    /// rendering on screen, and the only reliable cure is to let it paint once at
    /// its final size.
    /// </summary>
    public void Reveal()
    {
        if (_view.InvokeRequired) { _view.BeginInvoke(Reveal); return; }
        _view.Visible = true;
        _view.Refresh();
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(e.WebMessageAsJson ?? "{}"); }
        catch { return; }
        if (root is null) return;

        switch (root["type"]?.GetValue<string>())
        {
            case "ready":
                _ready = true;
                lock (_pending)
                {
                    foreach (var json in _pending) SendRaw(json);
                    _pending.Clear();
                }
                break;

            case "prompt":
                var text = root["text"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text)) PromptRequested?.Invoke(text);
                break;
        }
    }

    /// <summary>
    /// Posts a message into the page. PostWebMessageAsJson has to run on the UI
    /// thread, and the page may not have booted yet, so both are handled here.
    /// </summary>
    private void Post(object payload)
    {
        string json;
        try { json = JsonSerializer.Serialize(payload); }
        catch { return; }

        if (!_ready)
        {
            lock (_pending) _pending.Add(json);
            return;
        }

        SendRaw(json);
    }

    /// <summary>Hands a serialized message to the page, on the UI thread.</summary>
    private void SendRaw(string json)
    {
        void Send()
        {
            try
            {
                if (_view.CoreWebView2 is not null) _view.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex) { AppLog.Write("chat", "post failed", ex); }
        }

        try
        {
            if (_view.InvokeRequired) _view.BeginInvoke(Send);
            else Send();
        }
        catch (Exception ex) { AppLog.Write("chat", "dispatch failed", ex); }
    }

    public void Log(string text, string level = "") => Post(new { type = "log", text, level });

    public void User(string text, string? image = null, string? file = null) =>
        Post(new { type = "user", text, image, file });

    public void Thinking() => Post(new { type = "thinking" });

    /// <summary>What the agent is doing right now: an icon and a line of text.</summary>
    public void Activity(string kind, string label) => Post(new { type = "activity", kind, label });

    /// <summary>A piece of the agent's thinking, on the line under the blinker.</summary>
    public void Reasoning(string text) => Post(new { type = "reasoning", text });

    public void Delta(string text) => Post(new { type = "delta", text });

    public void Assistant(string text) => Post(new { type = "assistant", text });

    public void Tool(string name, string args, string output) => Post(new { type = "tool", name, args, output });

    /// <summary>Output from a slash command - the IDE answering, not the model.</summary>
    public void Note(string text, string kind = "notecode") => Post(new { type = "note", text, kind });

    public void Clear() => Post(new { type = "clear" });

    public void Dispose()
    {
        try { _view.Dispose(); } catch { /* teardown */ }
    }
}
