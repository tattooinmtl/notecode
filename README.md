# notecode

A notepad with the power of a full coding IDE, in **one window**, running entirely on a
**local model**.

- **Left** — workspace explorer
- **Centre** — editor (CodeMirror) with file tabs
- **Bottom** — a **real terminal** (Windows ConPTY + xterm.js), not a piped shell
- **Right** — chat with the local model (toggleable)
- **Status bar** — server state, model, MCP connections, cursor, memory

The local model is served by **llama.cpp**, with the MCP servers already wired so the model
can call them directly. It runs on the CPU, or on a GPU when the machine has one - the app
scans the machine on startup and picks the build that fits it.

---

## Installing it

```powershell
.\scripts\build-installer.ps1
```

That produces **`installer\output\Notecode-Setup.exe`**, one file that installs:

- the app, as a single self-contained executable;
- the **llama.cpp runtime** it needs - `llama-server.exe` and its libraries - so the
  installed copy can serve a model without a separate llama.cpp build;
- a **shortcut on the desktop** and one in the start menu;
- an entry in the list of installed programs, with an uninstaller that takes all of it
  away again and leaves your conversations alone.

It installs per user, into `%LOCALAPPDATA%\Programs\notecode`, so it needs no
administrator and does not fight another version of itself.

**The models are not in the installer.** The weights are 17 GB and more, they live where
the machine put them, and the app is pointed at one in the settings.

---

## Quick start

```powershell
# 1. build the app (produces dist\notecode.exe, ~50 MB self-contained)
.\scripts\build.ps1

# 2. check the local model runtime
.\runtime\notecode-llama.ps1 doctor

# 3. start llama-server (loads ~19.7 GiB; first start takes a while)
.\runtime\notecode-llama.ps1 start

# 4. launch the IDE (optionally with a file)
.\dist\notecode.exe
.\dist\notecode.exe .\workspace\Sample.cs
```

Verify the whole stack at once:

```powershell
.\scripts\smoke-test.ps1
```

Drive the AI -> tool -> editor loop through the real GUI (it types into the AI box via UI
Automation and waits for the file to appear):

```powershell
.\scripts\ui-tool-smoke.ps1 -FileName gui-check.md
```

The same run with the keyboard instead of the button, which is the path most people
will actually use:

```powershell
.\scripts\ui-tool-smoke.ps1 -FileName gui-check.md -SendWith Enter
```

Check that the AI panel keeps its place when it is toggled from the View menu:

```powershell
.\scripts\ui-layout-check.ps1
```

Check that the agent really writes, opens and moves files, and that the cross on a tab
closes it:

```powershell
.\scripts\ui-agent-files.ps1     # the page opens as a tab, the cross closes it, the file moves
.\scripts\ui-tab-close.ps1       # just the cross, and it needs no model, so it is quick
```

The app writes a diagnostics log to `runtime\notecode-app.log` — terminal events, shell
startup, WebView2 message traffic. It is the first place to look when something is
blank.

### The AI panel

The agent lives in a 420 px panel on the right: an aurora header, the conversation, the
switches, the **paperclip**, and the composer.

| Switch | Default | Effect |
|---|---|---|
| Send active file to the model | on | the active buffer is injected into the prompt |
| Enable file tools | on | the model gets `save_as`, `open_in_editor`, `list_files`, `read_file` |

The **paperclip** in the composer opens the file picker. A picture is sent as an image
for the model to look at; any other file is read and put in front of the model as its
own turn, so it can answer a question about it. See
[It can see, and it can read](#it-can-see-and-it-can-read).

**Enter sends, Shift+Enter starts a new line.** The Send button is the visible
fallback, but the keyboard is the primary path.

The conversation is a WebView2, not a text box, so a turn can carry real structure:
your message on the right, the agent's answer on the left, and **tool calls rendered as
cards** — the tool name as a chip, the arguments as a block, the result underneath.
Markdown is rendered through a deliberately small safe subset (fenced code, inline code,
bold, headings, bullet lists); everything is HTML-escaped before it is inserted.

While the agent works, an **activity row** sits above the answer: a drawn icon and a
phrase for what it is doing — thinking, reading, writing, running, making a folder,
moving, copying, deleting — and, on the line under the blinker, the model's own
reasoning when the provider sends it. See [What the agent is doing right now](#what-the-agent-is-doing-right-now).

---

## Slash commands

Type `/` in the prompt and the palette opens under it. Arrow keys move the highlight,
Enter takes the command, Tab completes it, Esc closes the palette. The palette never
takes the keyboard focus — the prompt keeps it, so typing keeps working.

`/help` prints the whole reference; `/help <command>` prints one command in full.

| Command | What it does |
|---|---|
| `/help [command]` | the command reference |
| `/rea <project> <destination> <actions>` | run a REA reverse-engineering session |
| `/model [name]` | show or switch the model |
| `/provider [name]` | show or switch the provider |
| `/providers` | list every configured provider |
| `/addprovider [name]` | add or edit a provider by hand |
| `/doctor [provider]` | check and refresh the endpoints over the web |
| `/skills` | list the skills on this machine |
| `/skill <name>` | load a skill into the conversation |
| `/addskills [file.md]` | add a skill and repair it so it works here |
| `/addtool [name stdio <cmd> \| name http <url>]` | register a tool source for the model |
| `/fontsize [0.8 - 2.0]` | make the text bigger or smaller |
| `/tools` | the tools in this turn |
| `/context [on\|off]` | turn the active file on or off |
| `/clear` | clear the transcript |
| `/status` | what the IDE is running right now |

### `/rea`

The REA checkbox is gone — the command replaces it, and the 170 REA tools are turned on
for that turn only:

```
/rea <project> <output destination> <action requests>

/rea kimi.exe rea/findings/kimi list the exports and explain the startup path
```

The project is the binary, folder or file to analyse. The destination is where the
findings are written — relative paths are resolved inside the workspace, and the folder
is created if it is missing. The actions are what you want done, in plain words; leave
them empty and the model asks instead of guessing. A path with spaces goes in double
quotes.

---

## It can see, and it can read

MiniCPM-V is a **vision** model, and the projector that goes with it lives next to the
model file:

```
C:\models\MiniCPM-V-4_7-35B-A3B-Q4_K_M.gguf     the model
C:\models\mmproj-model-f16.gguf                 the projector (1.04 GB)
```

The projector is what turns a picture into tokens. It is named in the config
(`llama.mmproj`) and the launcher passes it to llama-server as `--mmproj`; without it
the server answers `400` to any request that carries a picture. The running server says
so itself:

```json
"modalities": { "vision": true, "video": true, "audio": false }
```

### The paperclip

The **paperclip** in the composer opens the file picker. Whatever you choose goes into
the next turn, and how it gets there depends on what it is:

| The file | How the model gets it |
|---|---|
| a picture (`.png`, `.jpg`, `.webp`, `.gif`, `.bmp`) | scaled down to 1024 px on the long side, then sent as an image part — the model **looks** at it |
| anything else it can read | read as text and put in front of the model as its own turn — the model **reads** it |

A picture off a camera is several megabytes and the model resizes it anyway, so it is
scaled first. A text file is cut at 40,000 characters, the same limit as the editor's
buffer. A binary file that is not a picture is refused: the model cannot read it, and
saying so is better than sending it bytes.

The attachment shows up in the transcript as a chip with the paperclip and the file name
— so you can see what the model is looking at or reading — and it goes with **that** turn
and not with the next one. The **Remove** button on the chip takes it back.

There is a second projector in `C:\models` (`mmproj-Qwythos-27B-F16.gguf`). It belongs
to the Qwythos-27B model, not to this one, so it is not the one that is configured.

The same picture is sent to a BYOK provider, in the same content parts array the chat
API uses:

```json
{"role": "user", "content": [
  {"type": "text", "text": "What colour is the square?"},
  {"type": "image_url", "image_url": {"url": "data:image/png;base64,..."}}]}
```

`scripts/ui-vision.ps1` drives the picture side: it attaches a red square, asks what
colour it is, and the model answers **Red**. `scripts/ui-attach-file.ps1` drives the
reading side: it attaches a note with a word in it, asks what the word is, and checks
the model says it.

---

## The AI can reach the whole app

The model is not a chatbot that talks about code. It drives the app. There are three
surfaces, and the model has tools for all three — the same tools whether the answer comes
from the local model or from a provider you bring yourself.

| Surface | The tools | What the model can do |
|---|---|---|
| **The terminal** | `run_command` | run a command in the workspace and read its output and its exit code |
| **The editor** | `get_editor_buffer`, `set_editor_text` | read the live buffer of the tab you are looking at, including unsaved edits, and write into it |
| **The transcript** | `post_to_chat`, `read_conversation` | put a note where you read it, and look back at what has been said |

The file tools sit on top of the same set: `save_as`, `open_in_editor`, `read_file`,
`list_files`, `write_text`, `make_folder`, `move_path`, `copy_path`, `delete_path`,
`list_supported_types`.

```powershell
.\scripts\ui-ai-reach.ps1    # the model opens a file, reads the buffer, writes into it,
                             # runs a command, posts a note and reads the transcript back
```

That script drives the same MCP server the model goes through, so what it proves is what
the model gets.

### How the app is reached

The MCP server is a separate process, so it cannot touch the window. The three surfaces
live in the window, so the app and the MCP server talk over a small file protocol:

```
runtime/app-requests.jsonl      the MCP server writes a request
runtime/app-results/<id>.json   the app writes the answer
runtime/app-context.json        what the app is working on right now
```

The answer is written when the work is **done**, not when it is picked up, so the model
waits for the real result. And when the IDE is closed, nobody answers — so a command that
does not need the window is run by the MCP server itself, and a tool that does need the
window says so plainly instead of hanging.

### The command runs in a shell of its own

`run_command` does **not** type into the shell you are sitting at. That shell is driven by
PSReadLine, a line editor that cannot be handed text reliably — it answers with a
continuation prompt and swallows the line — and a command that leaves a bracket open would
strand your shell.

So the command runs in a clean shell of its own, in the workspace, and the command and its
output are written into the terminal pane. You watch it happen, your shell is untouched,
and the model gets plain text with the exit code.

---

## Adding a skill, and adding a tool

### `/addskills`

A skill is a markdown file of instructions for the model. One that is missing the front
matter the app reads, or that tells the model to use a tool this app does not have, is a
skill that does nothing when it is loaded.

```
/addskills                              opens the file picker
/addskills C:\path\to\SKILL.md         goes straight to that file
```

The **skill fixer** looks the skill over, finds what is wrong with it, and hands the file
to the model with the rules of this app:

1. the front matter is checked, because that is what `/skills` reads;
2. the skill is checked against what notecode can really do;
3. the model rewrites the file, keeping what the author wrote and fixing what is broken.

The fixed skill is then installed in a skill folder, so `/skills` lists it and
`/skill <name>` loads it.

The fixer runs on the model you are using — the local one or a provider — and it is given a
fixed number of turns, so it cannot run away. A tool that fails is reported back to the
model as text rather than ending the run. And when the model cannot be reached at all, the
front matter is still put right by hand, because that part does not need a model.

**The fixer cannot make a skill worse.** The model writes the whole file back, and on a slow
model that is a long way to lose something — the front matter, a table of tools, half the
instructions. So the rewrite is checked before it is kept: it is refused if it has no front
matter, if it names a tool that is not there, or if it keeps less than a third of a body
that was already well formed. A refused rewrite is put back and the model is told why.

**The name is yours.** It is the name of the file you picked, or the name of the folder for a
skill that is already in one (the file inside a skill folder is always `SKILL.md`). The
model writes the description and the shape; if it changes the name, the name is put back —
a skill the user cannot find again is not a repair.

### `/addtool`

A tool source is an MCP server. Registering one used to mean editing two JSON files by
hand. Now:

```
/addtool                                  lists the tool sources that are registered
/addtool playwright stdio npx -y @playwright/mcp@latest --stdio
/addtool files http http://127.0.0.1:8765/mcp
```

The server is written into both places that need it — the app's own config and the one
llama-server reads — and the app connects to it at once, so a provider can call the new
tools straight away. The local model needs the server restarted before it sees them.

---

## Blender

Blender runs on this machine and publishes an MCP server on
`http://127.0.0.1:8765/mcp`. notecode is registered to reach it, so the model can drive the
Blender that is open right now:

```json
"blender": {
  "transport": "streamable-http",
  "url": "http://127.0.0.1:8765/mcp"
}
```

The server publishes **26 tools**: `blender_scene_info`, `blender_list_objects`,
`blender_get_object`, `blender_create_object`, `blender_transform_object`,
`blender_set_material`, `blender_add_modifier`, `blender_import_asset`,
`blender_render_image`, `blender_execute_python`, and the `blender_dominator_*` builders.

Note the names: an MCP server's tools are published **with the server name in front**, so
the model calls `blender_execute_python` and not `execute_python`. A BYOK provider is
offered the same spelling, so one skill works on the local backend and on a provider.

**The manuals are in the workspace.** The Blender API reference and the user manual are
vendored under `workspace/vendor/blender-reference/` — 4,084 files, 14.9 MiB — so the model
can look an operator up instead of guessing it. The API folder is one file per class
(`bpy.types.Object.rst`, `bmesh.ops.rst`); the manual folder mirrors the official manual's
layout; and the add-on's own Python is there too, which is the ground truth for what the
tools actually do.

**The skill.** `skills/blender-scene/SKILL.md` puts it all in front of the model: the tools
by name, the order to work in, the Blender traps that cost real time (objects and their
data are separate, the active object and the selection are different things, the wrong mode
fails silently, a bmesh edit is lost if it is not flushed back), and where the manuals are.
Install it with `/addskills skills\blender-scene\SKILL.md`.

Blender has to be running for any of this to work. When it is not, the app retries the
server every ten seconds and says so in the transcript.

---

## Accessibility

The app is dark, owner-drawn and paints almost every pixel itself, so its accessibility is
not something it inherits — it is something it has to do. Two guards keep it honest, and
both read their numbers out of the source rather than remembering them:

```powershell
.\scripts\check-contrast.ps1     # every text colour against every surface, and the font sizes
.\scripts\ui-a11y-audit.ps1      # the real window, through UI Automation
```

- **Contrast.** Text is at least 4.5:1 on every surface it is painted on, and the marks
  and borders are at least 3:1. The accent button's label follows the fill it sits on,
  because one label colour cannot work on both a bright gradient and a flat dark panel.
- **Names.** Every control a person can use has an accessible name — the tree, the tab
  strip, the prompt box, the status bar, the paperclip, the Send button.
- **Targets.** Nothing a person has to hit is under 24 pixels.
- **Keyboard.** Every action is reachable with the keyboard: the custom buttons turn
  Space and Enter into a click, the dropdown takes the arrow keys, and the tab crosses
  are real focusable controls.

The size of the text is yours: `/fontsize 1.2` makes it bigger, `/fontsize 0.9` smaller,
and the factor is kept in `notecode.config.json` under `ui.fontScale`. At 1.00 the body
text is 10 point, which is the floor Microsoft sets for WinForms text.

What is **not** there yet is in `finalAudit.md` — the high-contrast theme, the custom
painting at a scaled display, and the contents of the tab strip, which a screen reader
cannot enumerate yet.

---

## Bring your own key

notecode talks to the local model by default, but any OpenAI-compatible endpoint works,
and Anthropic's messages API works too. The endpoints live in `runtime/providers.json`;
the bearer tokens are encrypted with **Windows DPAPI** for your Windows account before
they are written, so the file on disk is unreadable to anyone else.

Presets are pre-filled for MiniMax, Moonshot, Anthropic, OpenAI, xAI (Grok),
Antigravity (Google), DeepSeek, NVIDIA NIM and Qwen (DashScope), plus the local model.
The rest of the catalogue is there too: OpenRouter, Groq, Mistral, Ollama, Agnes AI,
xKiro, Atria ASI and Reflection AI.

| Command | What it does |
|---|---|
| `/addprovider` | the form: name, base URL, bearer token, API style, models |
| **Scan GGUF** | reads the chat template straight out of a `.gguf` header |
| **Auto update endpoints** | runs the doctor for this provider |
| `/doctor` | the same check for every provider |

**The endpoint doctor** does two things per provider: it asks the endpoint for its model
list with your token, and it searches the web for the provider's current base URL and
model names, because providers move their endpoints and rename their models. The search
goes through the private GWN search gateway (`SEARCH_GWN_MCP/server.mjs`), so it works
without a public search key.

The doctor searches the **open web**, not the programming corpus. `"MiniMax API base URL"`
in the `it` category returns articles about the URL API; in `general` it returns the
MiniMax documentation. That is why `search.doctorCategory` is a setting of its own.

Two things the doctor learned the hard way, both of which are now handled:

- **A missing model list does not mean a wrong URL.** Not every OpenAI-compatible endpoint
  implements `/models`; Google's `v1beta/openai` layer answers 404 there and works fine on
  `/chat/completions`. A 404 on the model list is followed by a probe of the chat path, and
  the endpoint is reported as reachable if the chat path answers.
- **`api.deepseek.com` and `api.deepseek.com/v1` are the same endpoint.** A search result
  that drops the version segment is not a different provider, and the doctor no longer
  offers to "fix" a base URL that was already right.

### Providers that need something extra

**Reflection AI (Beam)** reasons on every request and answers 400 if an earlier assistant
message comes back without the reasoning that produced it. Turn on **Send the reasoning
back with every turn** in the provider form — the setting is on by default for that
preset, and the reasoning is carried in `reasoning_content`, which is not part of the
OpenAI chat spec.

**Ollama** and the **local** model need no key at all, so leave the bearer token empty.
The other presets need one: `/addprovider <name>`, or fill the token in the form.

When a BYOK provider is active, the tools come from the app's own MCP connections rather
than from llama-server, so `save_as` and the rest still work.

---

## Layout

```
prototypes/notecode/
├─ dist/                       notecode.exe (built output)
├─ runtime/
│  ├─ notecode.config.json     single source of truth for every component
│  ├─ notecode-llama.ps1       the llama.cpp wrapper: doctor|args|start|stop|status|logs
│  ├─ mcp-servers.json         MCP definitions in Cursor format for llama-server
│  ├─ notecode-mcp.mjs         the AI's hook (MCP stdio server, 19 tools)
│  ├─ file-types.json          the shared extension -> syntax-mode registry
│  ├─ open-queue.jsonl         hand-off from the AI to a running editor
│  ├─ app-requests.jsonl       the MCP server asking the app to act on a surface
│  ├─ app-results/             the app's answers, one file per request
│  ├─ app-context.json         what the app is working on right now
│  ├─ mcp-stdio-bridge.mjs     stdio -> streamable-http bridge (Blender)
│  ├─ mcp-trace.log            every call the MCP server got and what it answered
│  └─ workspace/               the llama-server working directory (model file tools are scoped here)
│     └─ vendor/blender-reference/   the Blender API reference, manual and add-on source
├─ skills/
│  └─ blender-scene/SKILL.md   the skill that puts the Blender tools in front of the model
├─ scripts/
│  ├─ build.ps1
│  ├─ smoke-test.ps1              20 layers, config -> PE image -> tool catalogue
│  ├─ ui-tool-smoke.ps1           drives the GUI: prompt -> tool call -> file in the editor
│  ├─ ui-agent-files.ps1          the agent writes a page, it opens as a tab, the cross closes it
│  ├─ ui-tab-close.ps1            the cross on a tab, without needing the model
│  ├─ ui-attach-file.ps1          a file the user attaches is read by the model
│  ├─ ui-reasoning.ps1            a provider that thinks out loud fills the line under the blinker
│  ├─ ui-command-smoke.ps1        the slash palette: opens, narrows, runs a command
│  ├─ ui-provider-form.ps1        opens /addprovider and captures the form
│  ├─ ui-byok-turn.ps1            a real turn to a BYOK provider, against a local mock
│  ├─ ui-vision.ps1               attaches an image and asks the local model about it
│  ├─ ui-layout-check.ps1         the AI panel keeps its place when it is toggled
│  ├─ ui-ai-reach.ps1             the model reaches the terminal, the editor and the transcript
│  ├─ ui-addskills.ps1            a broken skill is repaired, installed and listed
│  ├─ ui-addtool.ps1              a tool source is registered and connected
│  ├─ ui-blender-skill.ps1        the Blender server, the skill, and the fixer's tool names
│  ├─ mcp-probe.mjs               calls the MCP server the way the model does
│  ├─ mcp-http-probe.mjs          the same, for a streamable-http server
│  ├─ measure-prompt-cost.mjs     what the tool schemas cost in the prompt
│  ├─ find-mcp-arg-limit.ps1      where llama.cpp's MCP client gives up
│  ├─ run-suite.ps1               the whole suite, one script at a time
│  ├─ check-modes.ps1             every registry MIME must have a vendored CodeMirror mode
│  ├─ screenshot.ps1              a live capture of a window
│  ├─ winutil.ps1                 the shared Win32 and UI Automation helpers
│  ├─ mock-openai.mjs             the mock endpoint the BYOK test talks to
│  ├─ test-mcp-bridge.mjs
│  └─ rea-probe.mjs
├─ src/
│  ├─ Notecode.App/           C# / WinForms / WebView2 source
│  │  ├─ Theme.cs             the palette, the fonts and the window chrome
│  │  ├─ ThemeControls.cs     the themed controls (tabs, tree, buttons, dropdown, marks)
│  │  ├─ ChatView.cs          the WebView2 transcript host
│  │  ├─ SlashCommands.cs     the command registry, the parser and the /help text
│  │  ├─ CommandPalette.cs    the palette popup under the prompt
│  │  ├─ Providers.cs         the provider model, the store and the presets
│  │  ├─ ProviderClient.cs    a turn to a BYOK provider (OpenAI and Anthropic shapes)
│  │  ├─ EndpointDoctor.cs    the endpoint check and the web search
│  │  ├─ AddProviderForm.cs   the form that adds or edits a provider
│  │  ├─ Gguf.cs              the chat template inside a .gguf header
│  │  └─ wwwroot/chat.html    the transcript page (tool cards + markdown)
│  ├─ ConptyProbe/            ConPTY regression harness (links the shipping code)
│  └─ ToolProbe/              assistant -> tool -> result harness (links the shipping code)
├─ rea/findings/               REA reverse-engineering evidence + app screenshots
└─ workspace/                  files you edit
```

### The GUI tests and the foreground

The GUI tests drive the app through **UI Automation** and post the keystrokes straight to
the control's own window. They do not use `SendKeys`, because `SendKeys` only reaches the
foreground window and Windows will not hand the foreground to a background process — on
this machine the foreground is a system service window (`GameInputServiceWindow`), so
`SetForegroundWindow` is refused and `SwitchToThisWindow` is ignored. Posting the keys to
the control works without any of that, and it is also more honest about what is being
tested.

A capture reads the screen, so the window is made **topmost** first. Being topmost does
not need the foreground either.

---

## Design system

Dark only, because this is a developer tool and a light mode would double the surface
area of a desktop IDE for no gain.

| Role | Colour | |
|---|---|---|
| Void | `#07090F` | behind everything |
| Canvas | `#0B0E17` | panels |
| Surface | `#11151F` | raised chrome |
| Raised | `#161B27` | inputs, tree |
| Hover | `#1C2230` | selection |
| Border / Border strong | `#232A3A` / `#2E3750` | hairlines |
| Text / Muted / Dim | `#E8ECF5` / `#939DB4` / `#5F6980` | copy |
| **Violet** | `#8B5CF6` | the brand accent — the agent, the active tab, keywords |
| **Cyan** | `#22D3EE` | the action accent — Send, the caret, types |
| Mint / Amber / Rose | `#34D399` / `#FBBF24` / `#FB7185` | success / warning / error |

Contrast against the panel colour: body text 14.9:1, muted 6.1:1, violet 5.1:1, cyan
9.4:1 — all above WCAG AA.

**Type** is Segoe UI Variable (display and text) with Cascadia Code for code and
Segoe Fluent Icons for symbols. All three ship with Windows 11, so the app stays
offline — the usual choice of Space Grotesk and DM Sans would need a CDN.

**The chrome is drawn, not themed.** `ThemeControls.cs` paints its own tabs, tree,
buttons and section captions; the stock WinForms widgets cannot render a dark IDE
without visual styles fighting the palette. The icons are vector marks in GDI+
(`Marks`), not an icon font and never emoji — the paperclip in the composer, the cross
on a tab and the agent mark are all drawn the same way.

A drawn mark on a stock WinForms `Button` does not work: a themed button paints itself
in the system's accent colour and never asks the app for its `OnPaint`. The marks live
on plain `Control`s that paint themselves, with the keyboard, the focus ring and the
accessible name made up by hand.

The agent panel is the one place with an aurora: a violet-to-cyan gradient header with
a neural-graph mark, and the same bloom behind the editor and the transcript. Every
other surface stays calm so the eye lands on the conversation.

The editor and the terminal use the same palette: CodeMirror runs the `notecode`
theme (violet keywords, cyan atoms, mint strings, amber numbers, dim comments) and
xterm.js a matching 16-colour set.

---

## The llama.cpp wrapper

`runtime/notecode-llama.ps1` is the only thing that starts the model. The GUI calls it,
so the argv the app uses is byte-for-byte the argv the CLI produces.

| Command | What it does |
|---|---|
| `doctor` | Validates exe, model, MCP config, workspace; prints the KV/RAM budget |
| `args` | Prints the exact argv `start` will use |
| `start` | Launches detached, waits for `/health`, writes a pid file |
| `stop` | Stops the server it started |
| `status` | Health, resident memory, KV size, base URL |
| `logs` | Tails `llama-server.log` |

```powershell
.\runtime\notecode-llama.ps1 start -Ctx 16384 -Threads 8
.\runtime\notecode-llama.ps1 status
.\runtime\notecode-llama.ps1 stop
```

### The argv it produces

```
-m  C:\models\MiniCPM-V-4_7-35B-A3B-Q4_K_M.gguf
--host 127.0.0.1        --port 8080        --alias notecode-minicpm
--device none           --n-gpu-layers 0           <- CPU only, no iGPU
--threads 16            --threads-batch 16         <- 8 physical cores / 16 SMT
--ctx-size 32768
--cache-type-k q8_0     --cache-type-v q8_0        <- halves the KV cache
--jinja                 --reasoning off
--tools read_file,file_glob_search,grep_search,write_file,edit_file,get_info,exec_shell_command
--mcp-servers-config ...\runtime\mcp-servers.json  <- the model can call MCP tools
--webui-mcp-proxy                                 <- browser can reach remote MCP
--cors-origins localhost
```

---

## Hardware profiles

notecode runs on more than one machine, and the machines are not the same kind of
machine. A llama.cpp binary is compiled for exactly one backend, so the model on
this PC and the model on the RTX desktop are not the same program.

### The splash

Every start begins with a scan, shown on a terminal-style splash screen that
writes down what it found before the editor opens:

```
notecode  hardware scan
--------------------------------------------------------------------------
[os        ] Windows 11 Home (build 26200) x64
[cpu       ] AMD Ryzen 7 5700U with Radeon Graphics  8 cores / 16 threads
[memory    ] 28.9 GiB
[gpu       ] AMD Radeon(TM) Graphics  0.0 GiB shared  (dxgi)
[npu       ] none
[backend   ] cpu  -  0.0 GiB of video memory is under the 4 GiB floor
[server    ] C:\Llama\llama.cpp\build-notecode\bin\Release\llama-server.exe
[model     ] MiniCPM-V-4_7-35B-A3B-Q4_K_M.gguf  (19.7 GiB)
[memory check] fits  -  model 19.7 GiB (19.7 in RAM), kv 1.3 GiB, runtime 1.3 GiB, needs 22.3 GiB of the 27.9 GiB free. 5.6 GiB to spare.
[profile   ] amd-ryzen-7-5700u-with-radeon-graphics  (saved)
starting notecode ...
```

The scan takes **83 ms**, because it reads DXGI rather than asking WMI: WMI's
`AdapterRAM` is a 32-bit field, so an 8 GiB card reports 4 GiB, and an
integrated GPU reports the system memory it borrows as if it were its own.

### The rule

| The machine | The build |
|---|---|
| No display adapter, or under 3 GiB of video memory | **CPU only** |
| 4 GiB or more, NVIDIA | **CUDA**, or Vulkan if the CUDA build is not there |
| 4 GiB or more, anything else | **Vulkan** - one driver, every vendor |
| Apple silicon | **Metal** |

### The settings page

`/settings`, or **Tools > Hardware settings**. It shows the machine, re-runs the
scan on demand, lists the saved profiles, and offers the four builds as
checkboxes. **Apply and restart** saves the profile and restarts notecode,
because a different backend is a different program.

|  |  |
|---|---|
| ![the hardware settings page](rea/findings/notecode-hardware-settings.png) | ![the scan on the splash screen](rea/findings/notecode-hardware-splash.png) |

After the restart the status bar says which build is running:
`server: up (CPU)`, or `server: up (CUDA, 99 layers on the GPU)` on the RTX
desktop. That label is the receipt for the profile you applied.

### The profiles

They live in `%APPDATA%\notecode\hardware-profiles.json` - a property of the
machine, not of the checkout, so a repository that is synced between two PCs
does not carry one machine's GPU path to the other.

`runtime/hardware-profiles.seed.json` describes machines that have not run
notecode yet, so the RTX 5060 Ti desktop is set up from the laptop:

```json
{
  "name": "rtx-5060-ti",
  "match": { "gpuVendor": "NVIDIA", "minVramGiB": 6 },
  "backend": "cuda",
  "model": "C:\\models\\Qwen3-Coder-30B-A3B-Instruct-Q4_K_M.gguf",
  "ctxSize": 16384,
  "nGpuLayers": 99
}
```

The context is 16384 rather than 32768 because that machine has 16 GB of RAM and
the model is 17.3 GiB: about 7 GiB of it lives on the card and the rest is system
memory. The page says whether it fits.

### Building the GPU builds

`runtime/build-llama-gpu.ps1` compiles the extra builds. It has to be run **on
the machine with the card** - a CUDA build is compiled for the GPU it finds.

```powershell
.\runtime\build-llama-gpu.ps1 -Backend cuda      # needs the CUDA toolkit 12.8+
.\runtime\build-llama-gpu.ps1 -Backend vulkan     # needs the Vulkan SDK
```

Neither toolkit is installed by the script. A Blackwell card needs CUDA 12.8 or
newer, because the older toolkits do not know the sm_120 architecture.

---

## The notification area, and the sessions

Closing the window does not end notecode. The model is loaded, the MCP servers are
connected, and a person who closes the window to go and do something else does not want
to pay for that again - so the app goes to the notification area and stays there until
**Exit** is chosen.

The icon's menu is the whole control surface when the window is closed:

| | |
|---|---|
| **Open notecode** | brings the window back |
| **New session** | starts a fresh conversation and keeps the one that was happening |
| **Open session...** | opens the window that lists the past conversations |
| **Exit** | really leaves |

Clicking the icon opens the menu; double-clicking it brings the window back.

### The sessions

Every conversation is kept on disk, one file per session, under
`%APPDATA%\notecode\sessions`. `/sessions` opens the window that lists them.

![the session window](rea/findings/notecode-sessions.png)

Pinned sessions stay at the top. A session can be **opened** - which puts the
conversation back into the transcript so you can carry on where it left off - **pinned**,
or **deleted**. Starting the app again carries on with the last conversation for that
workspace, so closing the window and opening it again does not lose the thread.

Only one copy of notecode runs at a time. Clicking the shortcut while the app is in the
notification area brings that copy forward instead of starting a second one.

---

## The version

notecode is at **0.2.0**. The number lives in `src/Notecode.App/Notecode.App.csproj` and
the build stamps it into the assembly, so the app, the About box and the installer cannot
disagree about it.

```
> /version
```

says what is running, when this copy was built, where it is installed, which llama.cpp
build the hardware scan picked, and which model that build is serving.

---

## MCP

Four servers, defined once in `runtime/notecode.config.json` and mirrored into
`runtime/mcp-servers.json` for llama-server.

| Server | Transport | Notes |
|---|---|---|
| `notecode` | stdio | `runtime/notecode-mcp.mjs` — the AI's file hook: `save_as`, `write_text`, `read_file`, `list_files`, `open_in_editor`, `make_folder`, `move_path`, `copy_path`, `delete_path`, `list_supported_types`. |
| `rea` | stdio | `npx -y rea-agents@6.4.0 mcp` — 170 reverse-engineering tools. Same wiring kimi-code already uses. |
| `blender` | streamable-http | Blender addon on `127.0.0.1:8765`. Reaches llama-server through `mcp-stdio-bridge.mjs`. |
| `global-brain` | stdio | `brain-system.exe` — memory vault / session digests. |

**Why the bridge exists:** llama.cpp's `--mcp-servers-config` parser is stdio-only. It
reads `command`, `cwd`, `args`, `env` and `timeout_ms`, and **silently skips** any entry
without a `command` — so an HTTP server in that file is dropped without a word.
`mcp-stdio-bridge.mjs` accepts MCP on stdin and forwards it to an HTTP MCP endpoint,
keeping the session id alive across requests. Verified by `scripts/test-mcp-bridge.mjs`.

The notecode app has its **own** MCP client that speaks both transports natively, so the
Blender server is reachable from the UI even when Blender is closed (it will report the
connection failure rather than silently vanishing).

**The app is also the one that dispatches the MCP tools.** llama.cpp's own MCP client
cannot carry a tool argument of about 4 KB: the call never reaches the server, the client
stops answering, and every later call queues behind it — so one big argument takes out
every tool for the rest of the session. notecode's client has no such limit, so the MCP
servers' tools are called through it, and llama-server is only the fallback for a server
the app has not connected. A Blender script or a file of a few thousand characters works
because of this.

**The npx cache has to be warm before the server starts.** llama.cpp gives an MCP server
ten seconds to register its tools and then moves on (`MCP_WARMUP_TIMEOUT_SECONDS = 10`,
hard-coded in `tools/server/server-mcp.cpp`). A cold `npx` cache spends those ten
seconds downloading and the REA server registers **zero** tools — with no error, just a
silently smaller catalogue. `runtime/notecode-llama.ps1` primes it first by running
`npx -y <package> --help` for every npx-backed server before llama-server is started.

---

## The AI's file hook

The model can read and write the workspace, and it can put a file in front of you in the
editor. Say *"save as markdown"*, *"save as plan.md"*, *"save as agents.md"*, *"save as
main.cpp"* or *"save as index.html"* and it writes the file with the right extension and
opens it.

### How a tool call actually happens

llama.cpp does **not** run tools on the client's behalf. It only publishes them. The client
has to do the whole cycle:

```
GET  /tools                          -> the catalogue (built-ins + MCP tools)
  ↓  (the client sends the schemas in the request body)
POST /v1/chat/completions            -> the model answers with tool_calls
  ↓
POST /tools  {tool, params}          -> the client dispatches the call
  ↓  (the result is fed back as a role=tool message)
POST /v1/chat/completions            -> the model writes the final answer
```

`LlamaServer.StreamChatTurnAsync` does one turn of that; `MainForm.RunToolLoopAsync` drives
the loop, up to 8 turns. Tool calls arrive as *partial* stream deltas — the function name
once, the arguments in fragments — so `ToolCallAccumulator` stitches them back together by
index before dispatch.

**The schemas have to be supplied by the client.** A request without a `tools` array gets
plain prose back and `usage.prompt_tokens` stays at ~26: the model never even sees the
tools. This was the single biggest gap in the first working build.

### Which tools the model gets

| Set | Tools | Why |
|---|---|---|
| Always | 7 llama.cpp built-ins + 10 `notecode_*` | 17 schemas ≈ 3k prompt tokens. |
| Opt-in | 170 `rea_*` | Their schemas alone would eat most of a 32k context window. Toggle with **Enable REA tools** in the AI panel. |

### The filesystem tools

`notecode-mcp.mjs` publishes ten tools. Five are about reading and writing a file, and
five are about the filesystem itself — so you can talk to the agent instead of naming
tools:

| Tool | What it does |
|---|---|
| `save_as(path, content, type?, open?)` | write a file and open it in the editor as a tab |
| `write_text(path, content)` | write a file and leave the editor alone |
| `read_file(path)` | read a file |
| `list_files(dir?, recursive?)` | list the workspace |
| `open_in_editor(path)` | open an existing file |
| `make_folder(path)` | create a folder, and any missing parents |
| `move_path(from, to)` | move **or rename** a file or a folder; the destination folder is created |
| `copy_path(from, to)` | copy a file or a whole folder |
| `delete_path(path)` | remove a file, or a folder and everything in it |
| `list_supported_types()` | the extension/alias table |

So this works:

> *"Make an HTML page called agent-page.html that says hello."* → the page is written
> and **opens as a tab** in the editor.
>
> *"Move agent-page.html into a new folder called website."* → the folder is made and
> the file goes into it.

Every path is resolved inside the workspace, and the workspace root itself can be
neither moved, copied nor deleted.

### What the agent is doing right now

The panel shows an **activity row**: an icon and a phrase for whatever the agent is
doing — thinking, reading a file, writing one, running a command, making a folder,
moving, copying, deleting. The icon is drawn, not typed, and the phrase names the file
it is working on. The row follows the work: it goes back to *Thinking* when the model
starts its next pass, and it changes to the tool's own action while the tool runs.

When a provider sends its thinking (`reasoning_content`, or Anthropic's
`thinking_delta`), the reasoning streams in **on the line under the blinker**, so you
can watch the model work out the answer before it gives one. The row stays in the
transcript when the answer arrives — the blinker stops, but the icon and the thinking
are still there to read.

MiniCPM-V does not produce any thinking of its own — it answers and stops — so with the
local model the row shows the activity and the reasoning line stays empty. Switch to a
reasoning model through `/provider` and the line fills in. `scripts/ui-reasoning.ps1`
proves the path with a mock provider that thinks out loud.

### Closing a tab

Every tab in the editor strip carries a cross, and it is a **real button** rather than a
painted mark: it can be reached with the keyboard, it is named for a screen reader
(*Close plan.md*), and it shows a focus ring. Clicking it closes the tab, and a tab with
unsaved work asks first.

It has to be a real control. The strip is owner-drawn, and the native tab control cannot
describe an owner-drawn tab to assistive technology at all — a painted cross is
invisible to everyone but the mouse. The crosses live in the panel that holds the strip,
because WinForms refuses any child of a `TabControl` that is not a `TabPage`.

### The extension hook

`save_as(path, content, type?, open?)` resolves the file type through
`runtime/file-types.json` — the **same** registry the editor uses to pick its syntax mode,
so the model and the editor can never disagree about a type.

- `path` may be a bare name (`plan.md`) or a path (`docs/plan.md`).
- `type` names the language when the path has none: `markdown`, `md`, `cpp`, `html`, `php`,
  `js`, `py`, `rust`. It resolves through ~70 aliases.
- 88 extensions map to CodeMirror modes; 20 more are vendored for the ones the model is
  most likely to write.
- Writes are contained inside the workspace; binary extensions are refused outright.

### Opening the result in the editor

The MCP server is a separate process and cannot touch the UI, so `save_as` appends to
`runtime/open-queue.jsonl` and the app polls it every 700 ms. The app **primes the queue
offset at startup**, so entries written while no editor was running are not replayed as
stale tabs.

### The AI also sees your editor

With **Send active file to the model** ticked (the default), the active buffer is injected
into the conversation with its path and detected type, truncated at 40,000 characters.

---

## File types

`runtime/file-types.json` is the single registry, read by both `notecode.exe` and
`notecode-mcp.mjs`:

| Key | Meaning |
|---|---|
| `modes` | 88 extensions → CodeMirror MIME mode |
| `aliases` | ~70 language names → extension (`markdown`→`md`, `c++`→`cpp`, `pwsh`→`ps1`) |
| `sniff.rules` | content patterns used only when the extension is unknown |
| `binary` | extensions the editor refuses to open as text |
| `defaultMode` | `text/plain` |

The extension always wins. Content sniffing is a fallback for files with no extension, so a
`Makefile` or a `.env` still opens sensibly.

The status bar shows the detected type of the active file (`md · text/x-markdown`).

> **A MIME type that no vendored mode registers renders as plain text, silently.**
> `scripts/check-modes.ps1` compares the registry against the `defineMIME` calls in the
> vendored mode files and fails if anything is missing — it caught `.rs` (the registry said
> `text/x-rust`, CodeMirror registers `text/x-rustsrc`) and `.tex` / `.bib` (`stex.js` was
> never vendored). It runs as check 13 of the smoke test.

---

## Pointing kimi-code at the local model

`Run ▸ Copy model config for kimi` writes `runtime/kimi-local-provider.toml`. Append it to
`C:\Users\ThePa\.kimi-code\config.toml`:

```toml
[providers.notecode-local]
type = "openai"
base_url = "http://127.0.0.1:8080/v1"
api_key = "notecode-local"

[models."notecode-local/notecode-minicpm"]
provider = "notecode-local"
model = "notecode-minicpm"
max_context_size = 32768
capabilities = [ "tool_use" ]
```

Then `Run ▸ Run kimi-code` launches `kimi.exe` inside the notecode terminal pane.

---

## Memory budget

The machine has **28.9 GiB** of RAM and the model file is **19.71 GiB**. The KV cache is
what you control:

```
KV bytes/token = 2 (K+V) x 40 layers x 2 kv heads x 256 head_dim x bytes/elem
  f16  = 80.0 KiB/token
  q8_0 = 42.5 KiB/token
```

| ctx | KV @ q8_0 | KV @ f16 |
|---:|---:|---:|
| 16,384 | 0.66 GiB | 1.25 GiB |
| 32,768 | 1.33 GiB | 2.50 GiB |
| 65,536 | 2.66 GiB | 5.00 GiB |

Default is **32,768 ctx with q8_0** → 1.33 GiB of KV, ~22.3 GiB total resident
(model + KV + ~1.3 GiB of runtime buffers).

> **This build must use a llama.cpp compiled with `GGML_CPU_REPACK=OFF`.**
> The stock Windows build tries to allocate a 14.66 GiB contiguous CPU_REPACK buffer
> for this model's weights, *on top of* the 19.71 GiB of weights. That is 34 GiB on a
> 28.9 GiB machine, and the server dies with
> `failed to allocate CPU_REPACK buffer of size 15744614400`.
> See `docs/DESIGN.md` for how the bundled build was produced.

---

## Keyboard

| Key | Action |
|---|---|
| `Ctrl+S` | Save the active file |
| `Ctrl+F` / `Ctrl+G` | Find / find next |
| `Ctrl+/` | Toggle comment |
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo |
| `Tab` | Indent or insert spaces |
| `Enter` | Send the AI prompt |
| `Shift+Enter` | New line in the AI prompt |

---

## Offline by design

CodeMirror 5 and xterm.js are **vendored** under `src/Notecode.App/wwwroot/vendor/`.
notecode never contacts a CDN, and the model never leaves the machine.
