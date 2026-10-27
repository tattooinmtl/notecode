using System.Text;
using System.Text.RegularExpressions;

namespace Notecode.App;

/// <summary>What the policy says about a command the agent wants to run.</summary>
internal enum CommandVerdict
{
    /// <summary>It is on the allowlist: it runs.</summary>
    Allow,

    /// <summary>It is neither allowed nor refused: the user has to say yes.</summary>
    Ask,

    /// <summary>It is refused outright. The user is not even asked.</summary>
    Deny
}

/// <summary>The verdict and the reason for it, in words a person can read.</summary>
internal sealed record CommandDecision(CommandVerdict Verdict, string Reason)
{
    public static CommandDecision Allow(string why) => new(CommandVerdict.Allow, why);
    public static CommandDecision Ask(string why) => new(CommandVerdict.Ask, why);
    public static CommandDecision Deny(string why) => new(CommandVerdict.Deny, why);
}

/// <summary>
/// The policy, as it is read from <c>commandPolicy</c> in notecode.config.json.
///
/// The same block is read by runtime/notecode-mcp.mjs, so the headless path and
/// the IDE agree on what is refused. It is the only copy: there are no built-in
/// lists here, because a list in two places is a list that drifts.
/// </summary>
internal sealed class CommandPolicyConfig
{
    /// <summary>Turn the gate off. Everything runs. It is off only if the user says so.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Commands that run without asking.
    ///
    /// An entry is matched as a prefix of the command's words, so "git status"
    /// also covers "git status --short" but not "git statusx".
    /// </summary>
    public List<string> Allow { get; set; } = new();

    /// <summary>
    /// Exact commands the user has said yes to and said yes to again.
    ///
    /// The whole command, compared as text. It is what the "always allow"
    /// button in the approval window writes.
    /// </summary>
    public List<string> AlwaysAllow { get; set; } = new();

    /// <summary>
    /// Commands that are refused rather than offered for approval.
    ///
    /// Each entry is a regular expression matched against the whole command,
    /// case-insensitively. An expression that matches means "no", and the user
    /// is not asked - the idea is that a click on "yes" should not be enough
    /// for the worst commands.
    /// </summary>
    public List<string> Deny { get; set; } = new();

    /// <summary>How long the approval window waits before it gives up and says no.</summary>
    public int AskTimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Decides whether a command the model asked for may run.
///
/// The threat this answers is a prompt injection: a file, a web page or a tool
/// result that says "run this", and the model obeying it because the text
/// looked like an instruction. The control is not a sandbox - a general shell
/// command cannot be sandboxed on Windows without breaking the toolchains that
/// need it - the control is that the user sees the command and says yes.
///
/// Three answers, in the order they are checked:
///
/// Deny  - the command is destructive enough that it is never run, not even on
///         an approval. The user is told why and the model is told no.
/// Ask   - the command is not on the allowlist. The user is shown it.
/// Allow - the command is on the allowlist: a read or a build. It runs.
///
/// The command is split on the shell's own operators before any of this, so
/// "git status &amp;&amp; rm -rf /" is two commands and the second one is judged
/// on its own. Judging the whole string as one command would let an allowed
/// prefix hide anything after it, which is the whole trick.
/// </summary>
internal sealed class CommandPolicy
{
    private readonly CommandPolicyConfig _cfg;
    private readonly List<string[]> _allow;
    private readonly HashSet<string> _alwaysAllow;
    private readonly List<Regex> _deny;

    /// <summary>
    /// The programs that are never run without asking, whatever the allowlist
    /// says.
    ///
    /// A shell is a shell: "bash -c anything" is not a build, it is a way of
    /// asking again. These are the programs whose whole purpose is to run
    /// something else, so no prefix rule about them can be trusted.
    /// </summary>
    private static readonly HashSet<string> NeverAutoAllow = new(StringComparer.Ordinal)
    {
        "cmd", "powershell", "pwsh", "bash", "sh", "zsh", "fish", "wsl",
        "cscript", "wscript", "mshta", "rundll32", "regsvr32",
        "iex", "invoke-expression"
    };

    /// <summary>
    /// The words that separate one command from the next.
    ///
    /// A shell runs what comes after these as a command of its own, so they
    /// are where a command stops being one command.
    /// </summary>
    private static readonly char[] Separators = { '|', ';', '&', '\n', '\r' };

    public CommandPolicy(CommandPolicyConfig cfg)
    {
        _cfg = cfg ?? new CommandPolicyConfig();
        _allow = _cfg.Allow
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            .Where(a => a.Length > 0)
            .ToList();
        _alwaysAllow = new HashSet<string>(_cfg.AlwaysAllow, StringComparer.OrdinalIgnoreCase);
        _deny = _cfg.Deny
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => new Regex(d, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
    }

    /// <summary>The policy as it is configured, for the window that asks.</summary>
    public CommandPolicyConfig Config => _cfg;

    /// <summary>
    /// The verdict for one command.
    ///
    /// The command is judged as the user would see it: the whole string, not
    /// the first word of it.
    /// </summary>
    public CommandDecision Classify(string? command)
    {
        var trimmed = (command ?? "").Trim();
        if (trimmed.Length == 0) return CommandDecision.Deny("there was no command to run.");
        if (!_cfg.Enabled) return CommandDecision.Allow("the command policy is switched off.");

        // The refusals come first and they are over the whole string, because
        // the dangerous shape is a pipeline - a download poured into a shell -
        // and that shape only exists across the whole command.
        foreach (var pattern in _deny)
        {
            if (pattern.IsMatch(trimmed))
                return CommandDecision.Deny(
                    "notecode refuses to run this one. It matches a rule in commandPolicy.deny in " +
                    "notecode.config.json - the command is destructive enough that an approval is not " +
                    "enough. If you meant it, run it yourself in a terminal.");
        }

        var segments = Split(trimmed);
        if (segments.Count == 0) return CommandDecision.Deny("there was no command to run.");

        // A command the user has already said yes to, in this exact form.
        if (_alwaysAllow.Contains(trimmed))
            return CommandDecision.Allow("you have allowed this exact command before.");

        // Everything after this is judged one command at a time.
        foreach (var segment in segments)
        {
            if (segment.Words.Count == 0) continue;

            var program = Normalise(segment.Words[0]);

            if (NeverAutoAllow.Contains(program))
                return CommandDecision.Ask(
                    $"\"{segment.Text}\" runs another program, so notecode asks before it runs it.");

            if (!MatchesAllow(segment))
                return CommandDecision.Ask(
                    $"\"{segment.Text}\" is not on the allowlist in notecode.config.json, so notecode asks " +
                    "before it runs it.");
        }

        return CommandDecision.Allow("it is on the allowlist.");
    }

    /// <summary>True when the command's words start with one of the allowlist entries.</summary>
    private bool MatchesAllow(Segment segment)
    {
        foreach (var rule in _allow)
        {
            if (segment.Words.Count < rule.Length) continue;

            var matches = true;
            for (int i = 0; i < rule.Length; i++)
            {
                if (!string.Equals(segment.Words[i], rule[i], StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }
            if (matches) return true;
        }
        return false;
    }

    /// <summary>
    /// The program name, without the path and without the extension.
    ///
    /// "C:\Program Files\Git\cmd\git.exe" is "git", so a rule about git is a
    /// rule about every git on the machine and not just the one on the path.
    /// </summary>
    internal static string Normalise(string program)
    {
        var p = program.Trim().Trim('"');
        if (p.Length == 0) return "";
        var name = Path.GetFileNameWithoutExtension(p);
        if (string.IsNullOrWhiteSpace(name)) name = p;
        return name.ToLowerInvariant();
    }

    // ----------------------------------------------------------------- splitting

    /// <summary>One command out of a line, with the words it was made of.</summary>
    internal sealed record Segment(List<string> Words, List<string> Redirects)
    {
        /// <summary>The command as it was written, for showing to the user.</summary>
        public string Text => string.Join(" ", Words);

        /// <summary>The program, as it was written.</summary>
        public string Program => Words.Count > 0 ? Words[0] : "";
    }

    /// <summary>
    /// Splits a command line into the commands it actually runs.
    ///
    /// The shell's own operators end one command and start the next, so
    /// "git status &amp;&amp; rm -rf /" is two commands and each is judged on its
    /// own. Quoted text is left alone: an ampersand inside quotes is a letter.
    /// A redirection - "2&gt;&amp;1", "&gt; out.txt" - is not a command boundary;
    /// the target is kept aside so it can be checked on its own.
    /// </summary>
    internal static List<Segment> Split(string command)
    {
        var segments = new List<Segment>();
        var words = new List<string>();
        var redirects = new List<string>();
        var word = new StringBuilder();

        void FlushWord()
        {
            if (word.Length == 0) return;
            words.Add(word.ToString());
            word.Clear();
        }

        void FlushSegment()
        {
            FlushWord();
            if (words.Count > 0)
            {
                segments.Add(new Segment(new List<string>(words), new List<string>(redirects)));
                words.Clear();
                redirects.Clear();
            }
            else
            {
                redirects.Clear();
            }
        }

        bool inSingle = false, inDouble = false;

        for (int i = 0; i < command.Length; i++)
        {
            var c = command[i];

            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                else word.Append(c);
                continue;
            }
            if (inDouble)
            {
                if (c == '"') inDouble = false;
                else word.Append(c);
                continue;
            }
            if (c == '\'') { inSingle = true; continue; }
            if (c == '"') { inDouble = true; continue; }

            if (char.IsWhiteSpace(c))
            {
                if (c == '\n' || c == '\r') FlushSegment();
                else FlushWord();
                continue;
            }

            if (c == ';') { FlushSegment(); continue; }

            if (c == '|')
            {
                if (i + 1 < command.Length && command[i + 1] == '|') i++;
                FlushSegment();
                continue;
            }

            if (c == '&')
            {
                // "&>" redirects both streams. Anything else with an ampersand
                // is an operator, and "2>&1" is caught by the redirection below
                // because the ">" comes first.
                if (i + 1 < command.Length && command[i + 1] == '>')
                {
                    i++;                      // past the "&"
                    i++;                      // past the ">"
                    FlushWord();
                    var both = ReadRedirectTarget(command, ref i);
                    if (both.Length > 0) redirects.Add(both);
                    continue;
                }
                if (i + 1 < command.Length && command[i + 1] == '&') i++;
                FlushSegment();
                continue;
            }

            if (c == '>' || c == '<')
            {
                // A number in front of the operator is the file descriptor and
                // not a word: "2>&1" is a redirection, not the command "2".
                if (word.Length > 0 && word.ToString().All(char.IsDigit)) word.Clear();
                else FlushWord();

                i++;                        // past the operator
                if (c == '>' && i < command.Length && command[i] == '>') i++;   // ">>"

                var target = ReadRedirectTarget(command, ref i);
                if (target.Length > 0) redirects.Add(target);
                continue;
            }

            word.Append(c);
        }

        FlushSegment();
        return segments;
    }

    /// <summary>
    /// Reads the file name a redirection points at.
    ///
    /// The ampersand in "2&gt;&amp;1" is part of the target, not an operator, so
    /// it is read here rather than splitting the command in half.
    /// </summary>
    private static string ReadRedirectTarget(string command, ref int i)
    {
        while (i < command.Length && char.IsWhiteSpace(command[i])) i++;

        var target = new StringBuilder();
        bool inSingle = false, inDouble = false;

        while (i < command.Length)
        {
            var c = command[i];
            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                else target.Append(c);
                i++;
                continue;
            }
            if (inDouble)
            {
                if (c == '"') inDouble = false;
                else target.Append(c);
                i++;
                continue;
            }
            if (c == '\'' || c == '"')
            {
                inSingle = c == '\'';
                inDouble = c == '"';
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c)) break;
            if (c == ';' || c == '|' || c == '<' || c == '>') break;
            target.Append(c);
            i++;
        }

        return target.ToString();
    }

    /// <summary>The operators that separate one command from the next, for the tests.</summary>
    internal static char[] OperatorCharacters => Separators;
}
