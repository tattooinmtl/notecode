using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Notecode.App;

/// <summary>
/// /doctor: the system doctor, run as a protocol of four phases.
///
///   scan     - code checks every server, skill, key and the local model
///   explain  - the model explains each problem, one at a time
///   repair   - each fix is shown and run only when the user says yes
///   verify   - what was repaired is checked again
///
/// Every phase writes doctor.md after every item, so on the local model, where
/// one pass is many small calls, a stop or a crash costs nothing: /doctor
/// carries on from the first item that is not done.
/// </summary>
internal sealed partial class MainForm
{
    private SystemDoctor? _doctor;
    private bool _doctorRunning;

    private SystemDoctor Doctor() =>
        _doctor ??= new SystemDoctor(_cfg, _providers, _mcp, ct => _llama.IsHealthyAsync(ct));

    private async Task CmdDoctorAsync(string argument)
    {
        var tokens = Tokenize(argument);
        var first = tokens.FirstOrDefault()?.ToLowerInvariant() ?? "";

        // The web endpoint doctor is still here, under its own name.
        if (first is "endpoints" or "web")
        {
            await CmdEndpointDoctorAsync(string.Join(" ", tokens.Skip(1)));
            return;
        }

        if (_doctorRunning)
        {
            _chat.Note("The doctor is already running.", "notecode · doctor");
            return;
        }

        if (first == "status")
        {
            var saved = DoctorReport.Load();
            _chat.Note(saved is null
                ? "The doctor hasn't run yet. `/doctor` runs it."
                : $"**Doctor** - phase `{saved.Phase}`, scope `{saved.Scope}`, started {saved.Started}.\n\n{saved.Summary()}\n" +
                  $"The full notes are in `{DoctorReport.PathFor()}`.", "notecode · doctor");
            return;
        }

        var fresh = first is "new" or "restart";
        var scopeText = fresh ? string.Join(" ", tokens.Skip(1)) : string.Join(" ", tokens);

        // An unfinished run with the same scope is carried on, not started again.
        var report = fresh ? null : DoctorReport.Load();
        if (report is not null && (report.Finished || !report.Scope.Equals(scopeText.Length == 0 ? "all" : scopeText, StringComparison.OrdinalIgnoreCase)))
            report = null;

        if (report is null)
        {
            if (Doctor().ResolveScope(scopeText) is null)
            {
                _chat.Note($"The doctor doesn't know `{scopeText}`. Try `/doctor`, `/doctor mcp`, `/doctor skills`, " +
                           "`/doctor keys`, `/doctor local`, or a server, skill or provider name.", "notecode · doctor");
                return;
            }
            report = DoctorReport.New(scopeText.Length == 0 ? "all" : scopeText);
            report.Save();
        }
        else
        {
            _chat.Log($"the doctor carries on from the {report.Phase} phase of the run started {report.Started}");
        }

        _doctorRunning = true;
        try
        {
            await RunDoctorProtocolAsync(report, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The report is saved after every item, so the next /doctor resumes.
            AppLog.Warn($"the doctor stopped: {ex}");
            _chat.Note($"The doctor stopped in the {report.Phase} phase: {ex.Message}\n\n" +
                       "Everything up to there is saved. `/doctor` carries on from here.", "notecode · doctor");
            return;
        }
        finally { _doctorRunning = false; }

        _chat.Note(DoctorClosingNote(report), "notecode · doctor");
    }

    private async Task RunDoctorProtocolAsync(DoctorReport report, CancellationToken ct)
    {
        var doctor = Doctor();
        var progress = new Progress<string>(s => _chat.Log(s));

        while (!report.Finished)
        {
            switch (report.Phase)
            {
                case "scan":
                {
                    _chat.Log("doctor 1/4: scanning");
                    var scope = doctor.ResolveScope(report.Scope == "all" ? "" : report.Scope) ?? ("all", null);
                    report.Findings = await doctor.ScanAsync(scope.Area, scope.Item, progress, ct);
                    break;
                }

                case "explain":
                    _chat.Log("doctor 2/4: explaining what was found");
                    foreach (var f in report.Findings.Where(f => f.IsProblem && f.Explanation is null))
                    {
                        f.Explanation = await ExplainFindingAsync(f, ct);
                        report.Save();
                    }
                    break;

                case "repair":
                    _chat.Log("doctor 3/4: repairing");
                    foreach (var f in report.Findings.Where(f => f.IsProblem))
                    {
                        var fix = f.Fixes.FirstOrDefault(x => !f.FixesTried.Contains(x));
                        if (fix is null) continue;
                        f.FixesTried.Add(fix);
                        report.Save();
                        f.RepairLog.Add(await ApplyDoctorFixAsync(f, fix, ct));
                        report.Save();
                    }
                    break;

                case "verify":
                {
                    _chat.Log("doctor 4/4: checking the repairs");
                    var repaired = report.Findings.Where(f => f.FixesTried.Count > 0).ToList();
                    foreach (var old in repaired)
                    {
                        // A skill or a whole area is checked through its own scope.
                        var area = old.Area == "local" ? "local" : old.Area;
                        var again = await doctor.ScanAsync(area, old.Area == "local" ? null : old.Item, progress, ct);
                        var now = again.FirstOrDefault(f => f.Key == old.Key);
                        if (now is null) continue;
                        now.RepairLog = old.RepairLog;
                        now.FixesTried = old.FixesTried;
                        now.Explanation = old.Explanation;
                        now.RepairLog.Add(now.IsProblem ? $"checked again: still {DoctorReport.Label(now.Status)}" : "checked again: fixed");
                        report.Findings[report.Findings.IndexOf(old)] = now;
                        report.Save();
                    }
                    break;
                }
            }

            report.Advance();
            report.Save();
        }
    }

    private static string DoctorClosingNote(DoctorReport report)
    {
        var problems = report.Findings.Where(f => f.IsProblem).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(problems.Count == 0
            ? "**Doctor** - everything checked works."
            : $"**Doctor** - {problems.Count} thing{(problems.Count == 1 ? "" : "s")} need{(problems.Count == 1 ? "s" : "")} attention.");
        sb.AppendLine();
        sb.AppendLine(report.Summary());

        foreach (var f in problems)
        {
            sb.AppendLine($"**{f.Item}**: {f.Explanation ?? f.Message}");
            if (f.Link is not null) sb.AppendLine($"Go to: {f.Link}");
            if (f.Detail.Length > 0) sb.AppendLine($"_{f.Detail}_");
            sb.AppendLine();
        }
        sb.AppendLine($"Full notes: `{DoctorReport.PathFor()}`");
        return sb.ToString();
    }

    // ----------------------------------------------------------- explain

    /// <summary>
    /// Asks the model to put one finding into plain words. The prompt is the
    /// finding and the recipe's two lines about the server, a few hundred tokens,
    /// so it fits the local model with room to spare. When no model answers, the
    /// doctor's own message stands: it is already written to be read.
    /// </summary>
    private async Task<string> ExplainFindingAsync(DoctorFinding f, CancellationToken ct)
    {
        var messages = Doctor().ExplainPrompt(f);
        try
        {
            var provider = _providers.ActiveProvider();
            ChatTurn turn;
            if (provider.IsLocal)
            {
                if (!await _llama.IsHealthyAsync(ct)) return f.Message;
                turn = await _llama.StreamChatTurnAsync(messages, null, null, temperature: 0.2, maxTokens: 700, ct: ct);
            }
            else
            {
                turn = await CreateBackend().StreamTurnAsync(messages, null, null, ct);
            }
            var text = turn.Content.Trim();
            return text.Length > 0 ? text : f.Message;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"the doctor could not get an explanation for {f.Item}: {ex.Message}");
            return f.Message;
        }
    }

    // ----------------------------------------------------------- repair

    /// <summary>
    /// Runs one fix, after the user says yes to it. A fix is either one of the
    /// scripts in runtime/doctor/fixes or something the app does itself; the
    /// model never makes up a command.
    /// </summary>
    private async Task<string> ApplyDoctorFixAsync(DoctorFinding f, string fix, CancellationToken ct)
    {
        var doctor = Doctor();
        var what = doctor.DescribeFix(f, fix);
        var command = doctor.FixCommand(f, fix);

        var (answer, _) = await AskToRunAsync(
            command ?? $"notecode: {fix} {f.Item}",
            _cfg.RuntimeDir,
            $"The doctor wants to fix {f.Item}: {what}");
        if (answer is CommandApproval.Denied or CommandApproval.TimedOut)
            return $"{fix}: skipped - not approved";

        _chat.Log($"doctor: {what}");
        try
        {
            switch (fix)
            {
                case "restore-config":
                {
                    var said = doctor.RestoreConfig(f.Item);
                    if (_cfg.Mcp.Servers.TryGetValue(f.Item, out var def) && !def.OnDemand)
                    {
                        var tools = await _mcp.ConnectOneAsync(f.Item, def, ct);
                        said += tools >= 0 ? $", and it connected ({tools} tools)" : ", but it still doesn't connect";
                    }
                    InvalidateToolCaches();
                    return $"{fix}: {said}";
                }
                case "reconnect":
                {
                    if (!_cfg.Mcp.Servers.TryGetValue(f.Item, out var def)) return $"{fix}: {f.Item} isn't in the config";
                    var tools = await _mcp.ConnectOneAsync(f.Item, def, ct);
                    InvalidateToolCaches();
                    if (def.OnDemand && tools >= 0) await _mcp.ReleaseAsync(f.Item);
                    return tools >= 0 ? $"{fix}: connected ({tools} tools)" : $"{fix}: it still doesn't connect";
                }
                case "start-llama":
                {
                    // StartAsync returns only when the wrapper's output closes,
                    // and the server it starts holds that output open for as long
                    // as it runs. So the doctor starts it and watches /health
                    // instead of waiting for the call.
                    var start = _llama.StartAsync(ct: ct);
                    _ = start.ContinueWith(t => AppLog.Warn($"llama start: {t.Exception?.GetBaseException().Message}"),
                        TaskContinuationOptions.OnlyOnFaulted);
                    for (var i = 0; i < 90; i++)
                    {
                        if (await _llama.IsHealthyAsync(ct)) return $"{fix}: the local model server started";
                        if (start.IsCompleted) break;
                        await Task.Delay(2000, ct);
                    }
                    return $"{fix}: the local model server didn't answer within 3 minutes - see the app log";
                }
            }

            if (command is null) return $"{fix}: there is no script for this on this machine";
            var (code, output) = await SystemDoctor.RunFixScriptAsync(command, ct);
            if (code == 0 && f.Area == "mcp" && _cfg.Mcp.Servers.TryGetValue(f.Item, out var server) && !server.OnDemand)
            {
                // A server that just came up is connected straight away.
                var tools = await _mcp.ConnectOneAsync(f.Item, server, ct);
                InvalidateToolCaches();
                if (tools >= 0) output += $" Connected ({tools} tools).";
            }
            return $"{fix}: {(code == 0 ? "done" : $"failed (exit {code})")} - {output}";
        }
        catch (Exception ex)
        {
            return $"{fix}: failed - {ex.Message}";
        }
    }

    /// <summary>The tool lists are cached per backend; a server that changed has to be read again.</summary>
    private void InvalidateToolCaches()
    {
        _byokToolSpecs = null;
        _toolSpecsLoaded = false;
    }

    // ----------------------------------------------------------- the tool

    /// <summary>
    /// The doctor as a tool, so a plain question - "why won't blender connect?",
    /// "repair the mcp servers" - is answered by the model calling it. It is one
    /// small schema, and what it returns is a few lines per item, so it costs the
    /// local model next to nothing.
    /// </summary>
    private static ToolSpec DoctorToolSpec => SystemDoctor.ToolSpec;

    private async Task<string> RunDoctorToolAsync(string argumentsJson, CancellationToken ct)
    {
        string item = "";
        bool repair = false;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (doc.RootElement.TryGetProperty("item", out var i) && i.ValueKind == JsonValueKind.String) item = i.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("repair", out var r) && r.ValueKind == JsonValueKind.True) repair = true;
        }
        catch { }

        var doctor = Doctor();
        var scope = doctor.ResolveScope(item);
        if (scope is null)
            return $"There is nothing called '{item}'. Servers: {string.Join(", ", _cfg.Mcp.Servers.Keys)}.";

        var findings = await doctor.ScanAsync(scope.Value.Area, scope.Value.Item, null, ct);
        var sb = new StringBuilder();
        foreach (var f in findings)
        {
            if (!f.IsProblem && scope.Value.Item is null) continue;
            sb.AppendLine($"{f.Item}: {DoctorReport.Label(f.Status)} - {f.Message}");
            if (f.Link is not null) sb.AppendLine($"  page: {f.Link}");
            if (f.Detail.Length > 0) sb.AppendLine($"  detail: {f.Detail}");

            if (repair && f.IsProblem && f.Fixes.Count > 0)
            {
                var result = await ApplyDoctorFixAsync(f, f.Fixes[0], ct);
                sb.AppendLine($"  repair: {result}");
            }
            else if (f.Fixes.Count > 0)
            {
                sb.AppendLine($"  can repair: {string.Join(", ", f.Fixes.Select(x => doctor.DescribeFix(f, x)))}");
            }
        }

        var okCount = findings.Count(f => !f.IsProblem);
        if (sb.Length == 0) return $"Everything checked works ({okCount} items).";
        if (scope.Value.Item is null) sb.AppendLine($"{okCount} other items work.");
        return sb.ToString().TrimEnd();
    }
}
