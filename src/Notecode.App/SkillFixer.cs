using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Notecode.App;

/// <summary>
/// What the skill fixer did with a skill.
/// </summary>
/// <param name="Ok">true when the skill is usable now.</param>
/// <param name="Name">the name the skill is listed under.</param>
/// <param name="Path">where the skill file is.</param>
/// <param name="Summary">what was changed, in the fixer's own words.</param>
/// <param name="Problems">what was wrong with the skill to begin with.</param>
/// <param name="Error">what stopped the fixer, when something did.</param>
/// <param name="FixedByModel">true when the model did the work, false when it was done by hand.</param>
internal sealed record SkillFixResult(
    bool Ok,
    string Name,
    string Path,
    string Summary,
    IReadOnlyList<string> Problems,
    string? Error,
    bool FixedByModel);

/// <summary>
/// The skill fixer: a bounded agent that repairs a skill so it works in notecode.
///
/// A skill is instructions for the model. A skill that is missing the front
/// matter the app reads, or that tells the model to use tools this app does not
/// have, is a skill that does nothing when it is loaded. The fixer looks the
/// skill over, finds what is wrong with it, and hands the file to the model
/// together with the rules of this app, so the model can rewrite it into
/// something that works here.
///
/// The agent is bounded, in the way an agent has to be:
///
///   - it gets a fixed number of turns, and the run ends when they are used up;
///   - every tool call is checked, and a tool that fails is reported back to the
///     model as text rather than ending the run;
///   - every step is written to the app log, so a run that goes wrong can be
///     read back;
///   - when the model cannot be reached at all, the structural problems are
///     still put right, because those do not need a model.
///
/// The fixer runs on whichever backend is answering - the local model or a
/// provider - and it drives the app's own tools, so it can read and write the
/// skill file itself.
/// </summary>
internal static class SkillFixer
{
    /// <summary>How many turns the fixer gets before it is called off.</summary>
    public const int MaxIterations = 6;

    /// <summary>
    /// The tools the fixer is allowed to use.
    ///
    /// Only two. The app has already put the ground truth and the house style
    /// in the prompt, and a tool that lets the model fetch them again is a
    /// tool that spends a turn on 8,000 tokens the model already has. On this
    /// model a turn is minutes, so the two that are left are the only two the
    /// job needs: read the file, write the file.
    /// </summary>
    public static readonly string[] Tools = { "read_skill", "write_skill" };

    /// <summary>
    /// The words that start a tool name. A backticked word in a skill that
    /// starts with one of these is meant to be a tool, so when the tool is not
    /// one notecode has, the skill is asking for something that is not there.
    /// </summary>
    private static readonly string[] ToolWordStarts =
        { "save", "open", "list", "read", "make", "move", "copy", "delete", "write", "run",
          "get", "set", "post", "describe", "edit", "grep", "file", "exec", "apply", "create",
          "search", "fetch", "browse", "web", "git", "send", "download", "install" };

    // ------------------------------------------------------------------- checks

    /// <summary>
    /// Looks a skill over and says what is wrong with it.
    ///
    /// These are the problems that make a skill do nothing: the front matter is
    /// what the app reads, so a skill without it is not listed properly, and a
    /// skill that names a tool notecode does not have sends the model after
    /// something that is not there.
    ///
    /// The tools of the other servers count too: a skill that names a tool of
    /// the Blender server is naming something that is really there.
    /// </summary>
    public static List<string> Check(string text, string path, IReadOnlySet<string>? otherTools = null)
    {
        var problems = new List<string>();
        text ??= "";

        if (!HasFrontMatter(text))
        {
            problems.Add("There is no front matter, so the skill is listed with no name and nothing that says what it is for.");
        }
        else
        {
            var head = FrontMatterLines(text);
            if (!head.Any(l => l.TrimStart().StartsWith("name:", StringComparison.OrdinalIgnoreCase)))
                problems.Add("The front matter has no `name:`.");
            if (!head.Any(l => l.TrimStart().StartsWith("description:", StringComparison.OrdinalIgnoreCase)))
                problems.Add("The front matter has no `description:`, so nothing tells the user what the skill is for.");
        }

        var body = Body(text);
        if (body.Trim().Length == 0)
            problems.Add("The skill has no instructions in it, only the front matter.");

        foreach (var named in MentionedTools(body))
        {
            if (!IsKnownTool(named, otherTools))
                problems.Add($"The skill tells the model to use `{named}`, which notecode does not have.");
        }

        return problems;
    }

    /// <summary>True when the skill names a tool that is really there.</summary>
    public static bool IsKnownTool(string name, IReadOnlySet<string>? otherTools = null)
    {
        // The tool names notecode ships, plus the ones the local server has of
        // its own. A skill that uses one of these is asking for something real.
        string[] known =
        {
            "save_as", "open_in_editor", "list_files", "read_file", "list_supported_types",
            "make_folder", "move_path", "copy_path", "delete_path", "write_text",
            "run_command", "get_editor_buffer", "set_editor_text", "post_to_chat", "read_conversation",
            "list_skills", "read_skill", "write_skill", "describe_notecode",
            "read_text", "file_glob_search", "grep_search", "write_file", "edit_file",
            "get_info", "exec_shell_command"
        };
        if (known.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;

        // The catalogue publishes notecode's own tools with the server name in
        // front, so `notecode_run_command` is the same tool as `run_command`.
        var bare = name.StartsWith("notecode_", StringComparison.OrdinalIgnoreCase)
            ? name["notecode_".Length..]
            : name;
        if (known.Contains(bare, StringComparer.OrdinalIgnoreCase)) return true;

        if (otherTools is null) return false;

        // The other servers publish their tools with their own name in front:
        // `blender_execute_python`, `rea_get_python_api_docs`.
        return otherTools.Contains(name, StringComparer.OrdinalIgnoreCase) ||
               otherTools.Contains(bare, StringComparer.OrdinalIgnoreCase) ||
               otherTools.Any(t => t.EndsWith("_" + name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The name the skill has to carry: the name of the file the user picked.
    ///
    /// The name is the app's to decide, not the model's. The user picked a file
    /// called `notecode-reach-test-skill.md`; a skill that answers to a
    /// different name is a skill the user cannot find again. The model writes
    /// the description and the shape, and the name is put back if it changed.
    /// </summary>
    public static string NameFromPath(string path)
    {
        var file = Path.GetFileName(path ?? "");
        var stem = Path.GetFileNameWithoutExtension(path ?? "");

        // A skill that is already in a folder is called what the folder is
        // called: the file inside it is always SKILL.md.
        if (file.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(path ?? "") ?? "");
            if (folder.Length > 0) return folder;
        }

        if (stem.Length > 0) return stem;

        return Path.GetFileName(Path.GetDirectoryName(path ?? "") ?? "");
    }

    /// <summary>
    /// Puts the name back into the front matter, if the model changed it.
    ///
    /// A file that has no front matter at all is not touched here: that is the
    /// fixer's job, and it is a job it can do without a model.
    /// </summary>
    public static string WithName(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(name)) return text;
        if (!text.StartsWith("---", StringComparison.Ordinal)) return text;

        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var end = -1;
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].Trim() == "---") { end = i; break; }
        }
        if (end < 0) return text;

        var replaced = false;
        for (var i = 1; i < end; i++)
        {
            if (!lines[i].TrimStart().StartsWith("name:", StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = $"name: {name}";
            replaced = true;
            break;
        }
        if (!replaced) return text;

        return string.Join("\n", lines);
    }

    /// <summary>A name that can be used as a folder.</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > 0 ? slug : "skill";
    }

    // -------------------------------------------------------------- the agent

    /// <summary>
    /// Runs the fixer over a skill file.
    ///
    /// The model does the work when it can be reached. When it cannot, the
    /// front matter is still put right by hand, because that is the part the app
    /// actually reads.
    /// </summary>
    public static async Task<SkillFixResult> RunAsync(
        IChatBackend backend,
        IReadOnlyList<ToolSpec> tools,
        string path,
        string text,
        string groundTruth,
        string installedSkills,
        string otherTools,
        Action<string>? onProgress,
        Func<string, string, CancellationToken, Task<string>>? writeSkill,
        CancellationToken ct)
    {
        var name = NameFromPath(path);
        var otherToolNames = ParseToolNames(otherTools);
        var problems = Check(text, path, otherToolNames);

        var messages = new List<ChatMessage>
        {
            new("system", BuildSystemPrompt(tools, groundTruth, installedSkills, otherTools)),
            new("user", BuildTask(path, text, problems))
        };

        for (int turn = 1; turn <= MaxIterations; turn++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke($"skill fixer, turn {turn} of {MaxIterations}");

            ChatTurn result;
            try
            {
                result = await backend.StreamTurnAsync(messages, tools, null, ct, null);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"skill fixer: the model failed on turn {turn}: {ex.Message}");
                return RepairByHand(path, text, problems, $"the model could not be reached: {ex.Message}");
            }

            // No tool call means the fixer has finished and is telling us what it did.
            if (result.ToolCalls.Count == 0)
            {
                var summary = result.Content.Trim();
                if (summary.Length == 0) summary = "The skill was looked over and left as it was.";
                AppLog.Write("skill-fixer", $"finished on turn {turn}: {summary}");
                return new SkillFixResult(true, name, path, summary, problems, null, true);
            }

            messages.Add(new ChatMessage("assistant", result.Content, null, result.ToolCalls, result.Reasoning));

            foreach (var call in result.ToolCalls)
            {
                // Every step goes in the log, so a run that goes wrong can be
                // read back afterwards.
                AppLog.Write("skill-fixer", $"turn {turn}: {call.Name}");

                string output;

                if (IsWrite(call.Name) && writeSkill is not null)
                {
                    // The model writes the content; the app puts it on the disk.
                    //
                    // The app does the writing, not the model's backend, and that
                    // is not a detail: llama.cpp's MCP client cannot carry a
                    // large tool argument. The call never reaches the server,
                    // the client stops answering, and every later call queues
                    // behind it. A skill is bigger than that limit, so a fixer
                    // that wrote through the model would never finish.
                    var target = ArgOf(call.ArgumentsJson, "path");
                    var content = ArgOf(call.ArgumentsJson, "content");

                    if (target.Length == 0)
                    {
                        output = "[error] the call had no path. Write the file with the same path it was given.";
                    }
                    else if (!IsAcceptable(text, content, otherToolNames, out var why))
                    {
                        // The rewrite is checked before it is kept. A model that
                        // loses the front matter, or most of the instructions,
                        // must not be able to replace a skill that was working.
                        output = $"[rejected] the rewrite was not kept: {why}. " +
                                 "The file is exactly as it was before. Write it again, keeping everything that was in it.";
                        AppLog.Warn($"skill fixer: the rewrite was rejected: {why}");
                    }
                    else
                    {
                        output = await writeSkill(target, content, ct);
                        AppLog.Write("skill-fixer", $"wrote {target} ({content.Length} characters)");
                    }
                }
                else
                {
                    try
                    {
                        output = await backend.InvokeToolAsync(call.Name, call.ArgumentsJson, ct);
                    }
                    catch (Exception ex)
                    {
                        // A tool that fails is information for the model, not the end
                        // of the run: it can try a different way.
                        output = $"[error] the tool {call.Name} failed: {ex.Message}";
                        AppLog.Warn($"skill fixer: tool {call.Name} failed: {ex.Message}");
                    }
                }

                messages.Add(new ChatMessage("tool", output, call.Id));
            }
        }

        AppLog.Warn($"skill fixer: used all {MaxIterations} turns without finishing");
        return new SkillFixResult(
            false, name, path,
            $"The fixer used all {MaxIterations} turns and did not finish.",
            problems, $"the skill fixer reached its limit of {MaxIterations} turns", true);
    }

    /// <summary>
    /// One string out of a tool call's arguments.
    /// </summary>
    private static string ArgOf(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            if (!doc.RootElement.TryGetProperty(name, out var value)) return "";
            return value.ValueKind == JsonValueKind.String
                ? (value.GetString() ?? "")
                : value.ToString();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// True when the tool call writes the skill file.
    ///
    /// The name comes in two spellings: the bare one, and the one the
    /// catalogue publishes, with the server name in front.
    /// </summary>
    private static bool IsWrite(string toolName)
    {
        var name = toolName ?? "";
        return name.EndsWith("write_skill", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decides whether the rewrite the model made is one to keep.
    ///
    /// The model is given the whole file and asked to write it back. On a slow
    /// model that is a long way to lose something: the front matter, a table
    /// of tools, half the instructions. So the new text is checked against the
    /// old one, and a rewrite that is worse is put back.
    ///
    /// A skill that was already broken is allowed to change shape - that is
    /// the fixer's job. A skill that was already well formed is not allowed to
    /// lose most of what it said.
    /// </summary>
    private static bool IsAcceptable(string original, string updated, IReadOnlySet<string>? otherTools, out string reason)
    {
        reason = "";

        if (string.IsNullOrWhiteSpace(updated))
        {
            reason = "the rewrite was empty";
            return false;
        }

        // The shape has to be right: the app reads the front matter to list the
        // skill, and a skill without it cannot be picked at all.
        var structural = Check(updated, "", otherTools)
            .Where(p => !p.Contains("does not have", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (structural.Count > 0)
        {
            reason = structural[0];
            return false;
        }

        // A tool that is not there is worse than the problem it was sent to fix.
        var invented = Check(updated, "", otherTools)
            .FirstOrDefault(p => p.Contains("does not have", StringComparison.OrdinalIgnoreCase));
        if (invented != null)
        {
            reason = invented;
            return false;
        }

        var before = Body(original);
        var after = Body(updated);
        if (before.Length > 200 && after.Length * 3 < before.Length)
        {
            reason = $"the rewrite kept {after.Length} characters of the {before.Length} it started with";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Puts the front matter right without a model.
    ///
    /// This is the part of the repair that does not need one: the app reads the
    /// front matter to list the skill and to describe it, so a skill that has
    /// none is a skill the user cannot pick.
    /// </summary>
    private static SkillFixResult RepairByHand(string path, string text, List<string> problems, string error)
    {
        var repaired = RepairFrontMatter(text, path);
        var name = NameFromPath(path);

        if (repaired == text)
        {
            return new SkillFixResult(false, name, path, "", problems, error, false);
        }

        try
        {
            File.WriteAllText(path, repaired);
            AppLog.Write("skill-fixer", $"repaired the front matter by hand: {path}");
            return new SkillFixResult(
                false, name, path,
                "The front matter was put right by hand, because the model could not be reached.",
                problems, error, false);
        }
        catch (Exception ex)
        {
            return new SkillFixResult(
                false, name, path, "",
                problems, $"could not write the repaired skill: {ex.Message}", false);
        }
    }

    /// <summary>
    /// Builds the front matter the app expects, keeping what was already there.
    /// </summary>
    public static string RepairFrontMatter(string text, string path)
    {
        text ??= "";
        var body = Body(text);
        var head = FrontMatterLines(text);
        var bodyFirstLine = FirstSentence(body);

        var keys = new List<string>();
        foreach (var line in head)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            keys.Add(trimmed);
        }

        bool hasName = keys.Any(l => l.StartsWith("name:", StringComparison.OrdinalIgnoreCase));
        bool hasDescription = keys.Any(l => l.StartsWith("description:", StringComparison.OrdinalIgnoreCase));

        if (!hasName) keys.Insert(0, $"name: {Slug(NameFromPath(path))}");
        if (!hasDescription)
        {
            var description = bodyFirstLine.Length > 0 ? bodyFirstLine : "A skill for notecode.";
            if (description.Length > 140) description = description[..140] + "...";
            keys.Add($"description: {description}");
        }

        return "---\n" + string.Join("\n", keys) + "\n---\n" + body;
    }

    // -------------------------------------------------------------- the prompt

    /// <summary>
    /// The names out of a list of tools of the other servers.
    ///
    /// The list arrives as markdown lines of the form
    /// `- **blender**: blender_execute_python, blender_scene_info`, so the names
    /// are picked out of it.
    /// </summary>
    private static HashSet<string> ParseToolNames(string otherTools)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(otherTools)) return names;

        foreach (var line in otherTools.Replace("\r\n", "\n").Split('\n'))
        {
            var after = line.IndexOf("**", StringComparison.Ordinal);
            if (after < 0) continue;
            var rest = line[(after + 2)..];
            var end = rest.IndexOf("**", StringComparison.Ordinal);
            if (end < 0) continue;
            var server = line.Substring(after + 2, end).Trim();
            rest = rest[end..].TrimStart(':', ' ', '\t');

            foreach (var part in rest.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = part.Trim().Trim('`', ' ');
                if (name.Length == 0) continue;
                names.Add(name);

                // The catalogue publishes the tools with the server name in
                // front, and a skill may name either spelling. Both have to be
                // known, or the fixer would take a real tool out of a skill.
                if (server.Length > 0 &&
                    !name.StartsWith(server + "_", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(server + "_" + name);
                }
            }
        }
        return names;
    }

    private static string BuildSystemPrompt(IReadOnlyList<ToolSpec> tools, string groundTruth, string installedSkills)
    {
        return BuildSystemPrompt(tools, groundTruth, installedSkills, "");
    }

    private static string BuildSystemPrompt(IReadOnlyList<ToolSpec> tools, string groundTruth, string installedSkills, string otherTools)
    {
        var names = tools.Count > 0
            ? string.Join(", ", tools.Select(t => t.Name))
            : string.Join(", ", Tools);

        var sb = new StringBuilder();
        sb.AppendLine("You are the skill fixer in notecode, a desktop coding IDE. " +
                      "A skill is a markdown file of instructions for the model. Your job is to " +
                      "make a skill work in this app.");
        sb.AppendLine();
        sb.AppendLine("What a skill has to look like:");
        sb.AppendLine("- It starts with front matter, and the front matter has a `name:` and a `description:`.");
        sb.AppendLine("  The name is the folder the skill lives in. The description is one line saying what the");
        sb.AppendLine("  skill is for and when to use it.");
        sb.AppendLine("- The body is the instruction the model follows. Write it as instructions to the model:");
        sb.AppendLine("  what to do, in what order, and what to check when it is done.");
        sb.AppendLine("- Keep what the author wrote. Fix what is broken, do not replace the skill with your own.");
        sb.AppendLine();
        sb.AppendLine("What makes a skill not work here:");
        sb.AppendLine("- It names a tool notecode does not have.");
        sb.AppendLine("- It asks for a thing the app cannot do: there is no browser, no web search, no way to");
        sb.AppendLine("  reach a file outside the workspace except through a tool, and no tool that commits to git.");
        sb.AppendLine("- It has no front matter, so the user cannot pick it from `/skills`.");
        sb.AppendLine();

        if (groundTruth.Trim().Length > 0)
        {
            sb.AppendLine("What this app can really do:");
            sb.AppendLine();
            sb.AppendLine(groundTruth.Trim());
            sb.AppendLine();
        }

        if (installedSkills.Trim().Length > 0)
        {
            sb.AppendLine("The skills that are already installed, so you can match their shape:");
            sb.AppendLine();
            sb.AppendLine(installedSkills.Trim());
            sb.AppendLine();
        }

        if (otherTools.Trim().Length > 0)
        {
            sb.AppendLine("The other tool servers that are connected. A skill may name their tools too -");
            sb.AppendLine("they are really there. Do not change a tool of another server.");
            sb.AppendLine();
            sb.AppendLine(otherTools.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("How to work:");
        sb.AppendLine("1. Read the skill that is given to you. Everything you need is in this message.");
        sb.AppendLine("2. Call `write_skill` with the whole new content of the file, using the same path.");
        sb.AppendLine("3. Finish with a short answer saying what you changed and why.");
        sb.AppendLine();
        sb.AppendLine($"The tools you have: {names}.");
        sb.AppendLine("Use `read_skill` only if you need the file again. Do not ask for the list of installed");
        sb.AppendLine("skills or for a description of the app: both are already in this message, and fetching");
        sb.AppendLine("them again costs a whole turn for nothing.");
        return sb.ToString();
    }

    private static string BuildTask(string path, string text, IReadOnlyList<string> problems)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"The skill to fix is at `{path}`.");
        sb.AppendLine();
        sb.AppendLine($"Its name is `{NameFromPath(path)}` - the name of the file, without the .md. The name in");
        sb.AppendLine("the front matter has to be that. You write the description and the shape; the name");
        sb.AppendLine("belongs to the file, and changing it would leave the user unable to find the skill.");
        sb.AppendLine();

        if (problems.Count > 0)
        {
            sb.AppendLine("What is wrong with it:");
            foreach (var p in problems) sb.AppendLine($"- {p}");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("Nothing obvious is wrong with its shape, so look at what it asks the model to do " +
                          "and check that notecode can do all of it.");
            sb.AppendLine();
        }

        sb.AppendLine("Here it is:");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine(text);
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Fix it now. Write the whole file with `write_skill`, using the same path.");
        return sb.ToString();
    }

    // ----------------------------------------------------------------- helpers

    private static bool HasFrontMatter(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        return lines.Length > 0 && lines[0].Trim() == "---";
    }

    /// <summary>The lines between the two fences of the front matter.</summary>
    private static List<string> FrontMatterLines(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return new List<string>();

        var head = new List<string>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") break;
            head.Add(lines[i]);
        }
        return head;
    }

    /// <summary>Everything under the front matter.</summary>
    private static string Body(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return text ?? "";

        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") return string.Join("\n", lines.Skip(i + 1));
        }
        return "";
    }

    /// <summary>The first line of the body that says something.</summary>
    private static string FirstSentence(string body)
    {
        foreach (var line in (body ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('#', ' ', '\t');
            if (trimmed.Length == 0) continue;
            trimmed = Regex.Replace(trimmed, "[*_`]", "");
            return trimmed;
        }
        return "";
    }

    /// <summary>
    /// The backticked words in a body that are meant to be tool names.
    /// </summary>
    private static IEnumerable<string> MentionedTools(string body)
    {
        var found = new List<string>();
        foreach (Match m in Regex.Matches(body ?? "", "`([^`\r\n]{2,40})`"))
        {
            var word = m.Groups[1].Value.Trim();
            if (!Regex.IsMatch(word, "^[a-z][a-z0-9_]*$")) continue;
            if (!word.Contains('_')) continue;

            var head = word[..word.IndexOf('_')];
            if (!ToolWordStarts.Contains(head, StringComparer.OrdinalIgnoreCase)) continue;

            found.Add(word);
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
