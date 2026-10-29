using System.Text;

namespace Notecode.App;

/// <summary>
/// One command the prompt understands.
///
/// The registry is the single place that knows what a command is called, what it does
/// and how to use it, so the palette, the help text and the dispatcher can never
/// disagree about what exists.
/// </summary>
internal sealed record SlashCommand(
    string Name,
    string Summary,
    string Usage,
    string Help,
    string[]? Aliases = null)
{
    /// <summary>The name plus every alias, lower-cased.</summary>
    public IEnumerable<string> Keys
    {
        get
        {
            yield return Name.ToLowerInvariant();
            foreach (var a in Aliases ?? Array.Empty<string>()) yield return a.ToLowerInvariant();
        }
    }
}

/// <summary>A command that was typed, with its arguments split off.</summary>
internal sealed record ParsedCommand(string Name, string Arguments, SlashCommand Definition);

/// <summary>
/// The catalogue of slash commands and the parser that recognises them.
///
/// A command is a slash followed by a name at the start of the prompt or after
/// whitespace. Everything after the name is its arguments, kept as raw text because
/// different commands want different shapes.
/// </summary>
internal static class SlashCommands
{
    public static IReadOnlyList<SlashCommand> All { get; } = new List<SlashCommand>
    {
        new(
            "help",
            "Show every command with full instructions",
            "/help",
            """
            **/help** - this text.

            Lists every command, what it does and how to use it. `/help <command>` shows
            the long form for one command, for example `/help rea`.
            """),

        new(
            "rea",
            "Start a reverse-engineering session on a project",
            "/rea <project> <output destination> <action requests>",
            """
            **/rea** - run a REA (reverse engineering agent) session.

            ```
            /rea <project> <output destination> <action requests>
            ```

            | Part | What it means | Example |
            |---|---|---|
            | project | the binary, folder or file to analyse | `kimi.exe`, `C:\build\app.exe` |
            | output destination | where the findings are written | `rea/findings/kimi` |
            | action requests | what you want done, in plain words | `map the imports and find the licence check` |

            Full example:

            ```
            /rea C:\Llama\llama.cpp\build-notecode\bin\Release\llama-server.exe rea/findings/llama-server list the exports and explain the startup path
            ```

            This turns on the 170 REA tools for this turn only - there is no checkbox
            anymore - and hands the model the project, the destination and the actions.
            The model then drives the REA tools and writes the results to the
            destination.

            An output destination of `auto` makes a new folder for the job under
            `assets\REA_outputs` in the workspace, for example
            `/rea kimi.exe auto list the exports`.

            The output destination is created if it does not exist. If you leave the
            actions empty the model asks what to look for instead of guessing.
            """,
            new[] { "reverse" }),

        new(
            "img2threejs",
            "Turn the attached picture into a Three.js model",
            "/img2threejs [--out <folder>] [--name <name>] [what you want]",
            """
            **/img2threejs** - run the img2threejs pipeline on a picture.

            Attach the picture with the paperclip first, then type:

            ```
            /img2threejs
            /img2threejs --name "notecode logo"
            /img2threejs --out D:\models make the nodes glossy and the plate matte
            ```

            | Part | What it means |
            |---|---|
            | `--out <folder>` | where the job folder goes. Without it you are asked, and the suggested folder is `assets\img2threejs` in the workspace |
            | `--name <name>` | the model's name (default: the picture's file name) |
            | `--image <path>` | a picture on disk, instead of the attachment |
            | `--no-open` | do not open the viewer when the job is done |
            | what you want | optional. When it is there, the model carries the job on from the files the pipeline wrote |

            Every run gets a new folder, `<name>-<date>-<time>`, with:

            - `reference.png`, `intake\probe.json`, `assessment.json`, `object-sculpt-spec.json`
              and `.img2threejs\state.json` - the img2threejs intake and starter spec;
            - `src\createObjectModel.js` - the model as procedural Three.js (an ES module);
            - `model.glb` - the same mesh for Blender and Unity;
            - `viewer.html` - opens offline in a browser, three.js is copied beside it;
            - `job.json` - every step and its result.

            The first model is a 2.5D relief: the outline becomes a solid plate and the
            parts that stand out in colour are raised from it. That is the right shape
            for a logo, an icon or a badge. One picture cannot show depth or the back,
            so for anything else it is a blockout to refine.

            The pipeline is bundled in `runtime\img2threejs` and needs Python 3.10 or
            newer. The workspace also gets an `assets` tree with `blender`, `unity`,
            `img2threejs`, `threejs`, `textures`, `logos` and `REA_outputs`.
            """,
            new[] { "i2t", "3d" }),

        new(
            "model",
            "Show or switch the model",
            "/model [model name]",
            """
            **/model** - show the model in use, or switch to another one.

            ```
            /model
            /model MiniMax-M3.1-Flash
            ```

            With no argument it lists the models the active provider offers and marks
            the one in use. With an argument it switches to that model, for the local
            provider that is the model alias in `notecode.config.json`.
            """,
            new[] { "models" }),

        new(
            "provider",
            "Show or switch the provider",
            "/provider [name]",
            """
            **/provider** - show the endpoint in use, or switch to another one.

            ```
            /provider
            /provider minimax
            ```

            With no argument it lists every configured provider, the one in use first,
            and whether it has a bearer token. With a name it switches to it.

            The local model is a provider too, called `local`, and it is always present.
            """,
            new[] { "providers" }),

        new(
            "providers",
            "List every configured provider",
            "/providers",
            """
            **/providers** - the full provider list.

            Shows each provider's base URL, its API style (OpenAI-compatible or Anthropic),
            the model it would use and whether a bearer token is set. The provider in use
            is marked.
            """),

        new(
            "addprovider",
            "Add or edit a provider by hand",
            "/addprovider",
            """
            **/addprovider** - open the form that adds or edits a provider.

            The form takes a name, a base URL, a bearer token, the API style and the
            model list. There is a **Scan GGUF** button: point it at a `.gguf` file and
            it reads the chat template the model was trained with straight out of the
            file header, so an endpoint that needs a particular template gets the right
            one.

            The bearer token is encrypted with Windows DPAPI for your user account
            before it is written to disk. It is never stored in the clear and never
            leaves the machine except to the endpoint you point it at.
            """,
            new[] { "add-provider", "provider-add" }),

        new(
            "settings",
            "Open the settings: providers, agent, model, performance, tools",
            "/settings [page]",
            """
            **/settings** - the settings window, the same one as the Settings menu.

            `/settings` opens it on Providers; `/settings agent`, `/settings model`,
            `/settings performance`, `/settings chat`, `/settings mcp` or
            `/settings editor` open it on that page.

            **/settings hardware** - the hardware page.

            It shows what this machine is - the processor, the core counts, the memory,
            the display adapter and how much video memory it really has - and which
            llama.cpp build the scan picked to run the local model.

            | Build | What it is |
            |---|---|
            | CPU only | the CPU build; always available |
            | Vulkan | one driver, every vendor: NVIDIA, AMD and Intel |
            | CUDA | NVIDIA only, and the fastest of the three |
            | Metal | macOS only |

            The rule the scan follows: **under 3 GiB of video memory means CPU only,
            4 GiB or more means the GPU path.** On an NVIDIA card that is CUDA, and
            on everything else it is Vulkan.

            **Scan hardware** re-reads the machine. The saved profiles are listed
            underneath, so a machine that was set up before can be brought back by
            clicking its profile. **Apply and restart** saves the choice and restarts
            notecode, because a llama-server is compiled for one backend and a
            different backend is a different program.
            """,
            new[] { "hardware", "config" }),

        new(
            "sessions",
            "Open the conversations from the past",
            "/sessions",
            """
            **/sessions** - the session window.

            Every conversation notecode has had is kept on disk, one file per
            session, under `%APPDATA%\notecode\sessions`. This command opens the
            window that lists them.

            The list has the pinned conversations first, then the most recent.
            From there a session can be **opened** - which puts the conversation
            back into the transcript so you can carry on - **pinned**, so it stays
            at the top, or **deleted**.

            The same window is behind the notification area icon, under
            **Open session**.
            """,
            new[] { "session", "history", "conversations" }),

        new(
            "version",
            "Show the version of notecode and where it is installed",
            "/version",
            """
            **/version** - what is running.

            ```
            /version
            ```

            It prints the version of notecode, when this copy was built, where it
            is installed, which llama.cpp build the hardware scan picked, and which
            model that build is serving.
            """,
            new[] { "about", "ver" }),

        new(
            "doctor",
            "Check and repair the MCP servers, skills, keys and local model",
            "/doctor [mcp|skills|keys|local|<name>|new|status|endpoints]",
            """
            **/doctor** - the system doctor. It works offline, on the local model.

            ```
            /doctor                  everything, or carry on with an unfinished run
            /doctor mcp              the MCP servers
            /doctor skills           the skills, and the servers they need
            /doctor keys             the cloud providers' API keys
            /doctor local            the local model server
            /doctor blender          one server, skill or provider by name
            /doctor new              start again instead of carrying on
            /doctor status           what the last run found
            /doctor endpoints [p]    the web check of the provider endpoints
            ```

            It runs in four phases, and writes `doctor.md` (in the notecode data
            folder) after every item, so a stopped run carries on where it was:

            1. **scan** - code checks each item against its recipe in
               `runtime/doctor/recipes`: is the program installed, are the files
               there, is the key there and accepted, is the app it lives in open,
               does the server answer;
            2. **explain** - the model explains each problem, one at a time;
            3. **repair** - each fix is shown, and runs only when you approve it;
            4. **verify** - what was repaired is checked again.

            It says plainly when you have to act: an app to open (Blender, the
            Meshy gateway), or an API key that is missing, refused or out of
            credit, with the provider's key page. Offline, it says so instead of
            blaming the key.

            The model can call it too: ask "why doesn't blender work?" or "repair
            the mcp servers".
            """,
            new[] { "check", "repair", "fix" }),

        new(
            "skills",
            "List the skills that can be loaded",
            "/skills",
            """
            **/skills** - list the skills found on this machine.

            Each skill is a folder with a `SKILL.md` in it. The listing shows the name
            and what the skill is for. Load one with `/skill <name>`.
            """),

        new(
            "skill",
            "Load a skill into the conversation",
            "/skill <name>",
            """
            **/skill** - load a skill's instructions into the conversation.

            ```
            /skill rust-guidelines
            ```

            The skill's text is inserted as context and the model follows it for the
            rest of the conversation. List what is available with `/skills`.
            """,
            new[] { "use", "load" }),

        new(
            "addskills",
            "Add a skill and repair it so it works here",
            "/addskills [path to a .md file]",
            """
            **/addskills** - add a skill, and fix it so it works in notecode.

            ```
            /addskills
            /addskills C:\path\to\SKILL.md
            ```

            With no argument it opens the file picker so you can choose the
            markdown file. With a path it goes straight to that file.

            The **skill fixer** then looks the skill over and repairs it:

            1. the front matter is checked, because that is what `/skills` reads -
               a skill without a `name:` and a `description:` cannot be picked;
            2. the skill is checked against what notecode can really do, so a skill
               that tells the model to use a tool this app does not have gets
               corrected;
            3. the model rewrites the file, keeping what the author wrote and
               fixing what is broken.

            The fixed skill is then installed in a skill folder, so `/skills` lists
            it and `/skill <name>` loads it.

            The fixer runs on the model you are using - the local one or a provider -
            and it is given a fixed number of turns, so it cannot run away. When the
            model cannot be reached, the front matter is still put right by hand.
            """,
            new[] { "add-skill", "skill-add" }),

        new(
            "addtool",
            "Add a tool source the model can call",
            "/addtool [name stdio <command> [args] | name http <url>]",
            """
            **/addtool** - register an MCP server, so the model gets new tools.

            ```
            /addtool
            /addtool playwright stdio npx -y @playwright/mcp@latest --stdio
            /addtool files http http://127.0.0.1:8765/mcp
            ```

            With no argument it lists the tool sources that are registered.

            The server is written into both places that need it: the app's own
            config, and the one llama-server reads. The app connects to it at once,
            so a provider can call the new tools straight away. The local model
            needs the server restarted before it sees them.

            A tool source is an MCP server. The notecode server itself is one, and
            so are the ones this app already talks to: the REA reverse-engineering
            agent, the Blender addon and the global brain.
            """,
            new[] { "add-tool", "tool-add", "mcp" }),

        new(
            "fontsize",
            "Make the text bigger or smaller",
            "/fontsize [0.8 - 2.0]",
            """
            **/fontsize** - how big the text is.

            ```
            /fontsize
            /fontsize 1.2
            ```

            With no argument it says how big the text is now. With a number it
            sets the factor and writes it into the config; the change takes
            effect the next time notecode starts.

            At 1.00 the body text is 10 point, which is the floor Microsoft sets
            for WinForms text whose size cannot be changed by the person using
            it. Here it can be changed, which is the point: a code editor that
            someone reads for hours has to be able to go up as well as down.
            """,
            new[] { "font-size", "text-size", "zoom" }),

        new(
            "tools",
            "List the tools the model can call",
            "/tools",
            """
            **/tools** - the tools in this turn.

            Shows which tools the model has: the built-in file tools, the notecode tools
            and, when a `/rea` session is running, the REA tools. It also shows how many
            prompt tokens the schemas cost, because on a CPU-sized model that is the
            number that decides whether a turn is fast.
            """),

        new(
            "context",
            "Turn the active file on or off",
            "/context [on|off]",
            """
            **/context** - whether the file you are editing is sent to the model.

            ```
            /context
            /context off
            /context on
            ```

            With no argument it reports the current state. This is the same switch that
            used to be the first checkbox in the panel.
            """),

        new(
            "clear",
            "Clear the conversation",
            "/clear",
            """
            **/clear** - empty the transcript.

            The conversation history, not the files on disk.
            """),

        new(
            "status",
            "Show what the IDE is running right now",
            "/status",
            """
            **/status** - the state of the harness.

            The model, the provider, whether the local server is up, the context size,
            the resident memory of the server and the number of tools in the current
            turn.
            """)
    };

    public static SlashCommand? Find(string name)
    {
        var key = name.Trim().ToLowerInvariant();
        return All.FirstOrDefault(c => c.Keys.Contains(key));
    }

    /// <summary>
    /// The commands that match what the user has typed so far. A prefix match on the
    /// name or on any alias, ordered so the exact names come first.
    /// </summary>
    public static IReadOnlyList<SlashCommand> Match(string prefix)
    {
        var key = (prefix ?? "").TrimStart('/').ToLowerInvariant();
        if (key.Length == 0) return All;
        return All
            .Where(c => c.Keys.Any(k => k.StartsWith(key, StringComparison.Ordinal)))
            .OrderBy(c => c.Name.StartsWith(key, StringComparison.Ordinal) ? 0 : 1)
            .ToList();
    }

    /// <summary>
    /// The command reference. This is what `/help` prints.
    /// </summary>
    public static string HelpText(string? only = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(only))
        {
            var one = Find(only);
            if (one is null) return $"No command called `{only}`. Try `/help` for the full list.";
            sb.AppendLine(one.Help.Trim());
            return sb.ToString().Trim();
        }

        sb.AppendLine("**notecode commands**");
        sb.AppendLine();
        sb.AppendLine("Type `/` in the prompt and pick from the list. Arrow keys move, Enter accepts.");
        sb.AppendLine();

        foreach (var c in All)
        {
            sb.AppendLine($"### `/{c.Name}`");
            sb.AppendLine();
            sb.AppendLine(c.Summary);
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(c.Usage);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("`/help <command>` prints the long form for one command.");
        return sb.ToString().Trim();
    }
}

/// <summary>
/// Recognises a command in the prompt and works out where the command token is.
///
/// The palette needs to know which run of characters the caret is sitting in, so that
/// `/rea foo` keeps the palette open on `rea` while the arguments are typed.
/// </summary>
internal static class SlashCommandParser
{
    /// <summary>
    /// The command token the caret is inside, or null when there is none.
    /// The token starts at a slash that begins the prompt or follows whitespace.
    /// </summary>
    public static (int Start, int Length, string Name)? ActiveToken(string text, int caret)
    {
        if (string.IsNullOrEmpty(text)) return null;
        caret = Math.Clamp(caret, 0, text.Length);

        int slash = -1;
        for (int i = caret - 1; i >= 0; i--)
        {
            char c = text[i];
            if (c == '/')
            {
                // A slash only starts a command at the beginning of the prompt or
                // after whitespace - "/rea" in a path like "src/rea" is not a command.
                if (i == 0 || char.IsWhiteSpace(text[i - 1])) slash = i;
                break;
            }
            if (char.IsWhiteSpace(c)) break;
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') break;
        }
        if (slash < 0) return null;

        int end = slash + 1;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_' || text[end] == '-')) end++;
        if (caret > end) return null;   // the caret is past the token

        return (slash, end - slash, text[(slash + 1)..end]);
    }

    /// <summary>
    /// Parses the whole prompt as a command. Returns null when the text is not a
    /// command, or when the command name is not one we know.
    /// </summary>
    public static ParsedCommand? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('/')) return null;

        int space = trimmed.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        string name = space < 0 ? trimmed[1..] : trimmed[1..space];
        string args = space < 0 ? "" : trimmed[(space + 1)..].Trim();

        var def = SlashCommands.Find(name);
        if (def is null) return null;
        return new ParsedCommand(def.Name, args, def);
    }

    /// <summary>
    /// True when the text looks like a command - it starts with a slash - even if the
    /// name is unknown. The dispatcher uses this to answer "no such command" instead
    /// of silently sending the text to the model.
    /// </summary>
    public static bool LooksLikeCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('/') && trimmed.Length > 1;
    }
}
