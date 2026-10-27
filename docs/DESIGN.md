# notecode — design & build notes

Everything here was measured on the target machine, not assumed.

---

## 1. Machine

| | |
|---|---|
| CPU | AMD Ryzen 7 5700U — **8 physical cores / 16 logical** (Zen2: AVX2, no AVX-512) |
| RAM | **28.9 GiB total**, ~15 GiB free at rest |
| OS | Windows 11 Home, build 26200 |
| Toolchains | .NET SDK 10.0.302 · Rust 1.97 · Node 24.18 · Python 3.12.10 · Go · CMake 4.1 · VS Build Tools 2017/2022/2026 · VS Community 2026 |
| WebView2 | 155.0.4283.62 (system-wide) |

---

## 2. Model

`C:\models\MiniCPM-V-4_7-35B-A3B-Q4_K_M.gguf` — 21,166,255,008 bytes (**19.71 GiB**)

Read directly from the GGUF header with `gguf-py`:

| Key | Value |
|---|---|
| `general.architecture` | `qwen35moe` |
| `general.name` | `35a3_hf_v47_0923` |
| `general.file_type` | 15 (Q4_K_M) |
| `block_count` | 40 |
| `embedding_length` | 2048 |
| `attention.head_count` | 16 |
| `attention.head_count_kv` | **2** (GQA) |
| `attention.key_length` / `value_length` | 256 |
| `expert_count` / `expert_used_count` | 256 / 8 (MoE) |
| `context_length` (native) | 262144 |

**Two consequences:**

1. **KV cache is cheap.** GQA with only 2 KV heads means
   `2 x 40 x 2 x 256 = 40,960 elements/token`.
   f16 → 80 KiB/token, q8_0 → 42.5 KiB/token.
   32K ctx at q8_0 = **1.33 GiB**. The 256K native context would need 10.7 GiB at q8_0
   (21 GiB at f16) — impossible here, so ctx is pinned well below the model's maximum.

2. **The vision tower is not in this file.** A tensor inventory of the whole GGUF yields
   only `token_embd`, `output`, `output_norm` and `blk.*` — **zero** tensors under
   `vision.*` / `clip.*` / `projector.*`. The projector for MiniCPM-V is a separate
   `--mmproj` file and no matching one exists in `C:\models` (the `mmproj-model-f16.gguf`
   there is 1.04 GiB and belongs to a different model). So this GGUF is **text-only**;
   `--mmproj` is wired in the wrapper but has nothing to point at yet.

---

## 3. Choosing the llama.cpp build

Three llama.cpp trees exist on this machine:

| Path | Version | Notes |
|---|---|---|
| `C:\llamaMamba\llama.cpp` | `b3fed31b9` (2026-06-28) | oldest |
| `C:\Llama\prism-llama.cpp` | `9a9394a8` (2026-09-18) | a fork |
| `C:\Llama\llama.cpp` | `b11721` / `ff5a233` (2026-10-18) | **newest source** |

Prebuilt Windows binaries:

| Path | Server build | `--mcp-servers-config`? | `--tools`? | `--webui-mcp-proxy`? |
|---|---|---|---|---|
| `C:\Llama\llama-b10988\` | **b10988** (2026-09-15) | yes | yes | yes |
| `C:\Llama\server\`, `C:\Llama\tools\` | 8996 (2026-05-01) | **no** | yes | yes |

b10988 is the newest usable prebuilt and is the only prebuilt with MCP server support.
It is the correct starting point — **but it cannot run this model.** See below.

### 3.1 The CPU_REPACK wall

First launch attempt with the stock b10988 build:

```
E ggml_backend_cpu_buffer_type_alloc_buffer: failed to allocate buffer of size 15744614400
E alloc_tensor_range: failed to allocate CPU_REPACK buffer of size 15744614400
E llama_model_load: error loading model: unable to allocate CPU_REPACK buffer
```

`15,744,614,400` bytes = **14.66 GiB**. llama.cpp's CPU backend repacks weights into a
blocked layout for faster `MUL_MAT`, and it allocates that buffer **in addition to** the
mmap'd weights:

```
19.71 GiB  model weights (mmap, file-backed)
14.66 GiB  CPU_REPACK buffer (anonymous, NOT file-backed)
--------
34.4 GiB   required, on a 28.9 GiB machine
```

The repack buffer is anonymous memory, so it cannot be paged out — the allocation simply
fails. The same load also tells us the MCP wiring was already correct:

```
I srv  start: MCP warmup: 'rea' discovered 170 tools
I srv  start: MCP warmup: 'global-brain' discovered 0 tools
I srv  setup: Added 170 MCP tools
```

`GGML_CPU_REPACK` is a **CMake compile-time option** (`ggml/CMakeLists.txt:152`), not a
runtime env var — `repack.cpp` allocates unconditionally and there is no runtime toggle.
So the fix is a rebuild.

### 3.2 The bundled build

Built from the newest source (`ff5a233`, ggml 0.27.0) into a **new** directory, leaving
the existing `build` and `build-mtmd` trees untouched:

```powershell
cmake -S C:\Llama\llama.cpp -B C:\Llama\llama.cpp\build-notecode `
  -DCMAKE_BUILD_TYPE=Release `
  -DGGML_CPU_REPACK=OFF `
  -DGGML_NATIVE=OFF -DGGML_AVX=ON -DGGML_AVX2=ON -DGGML_FMA=ON -DGGML_F16C=ON `
  -DGGML_AVX512=OFF -DGGML_AVX512_VBMI=OFF -DGGML_AVX512_VNNI=OFF `
  -DGGML_VULKAN=OFF -DGGML_CUDA=OFF -DGGML_HIP=OFF -DGGML_OPENMP=ON `
  -DLLAMA_BUILD_TESTS=OFF -DLLAMA_BUILD_EXAMPLES=OFF `
  -DLLAMA_BUILD_SERVER=ON -DLLAMA_BUILD_TOOLS=ON -DLLAMA_BUILD_MTMD=ON `
  -DLLAMA_BUILD_COMMON=ON -DLLAMA_BUILD_APP=OFF

cmake --build C:\Llama\llama.cpp\build-notecode --config Release --parallel 16
```

Decisions and why:

| Option | Value | Reason |
|---|---|---|
| `GGML_CPU_REPACK` | **OFF** | removes the 14.66 GiB allocation. The whole build exists for this. |
| `GGML_NATIVE` | OFF | a machine-specific `-march=native` build is a portability trap |
| `GGML_AVX2` + `FMA` + `F16C` | ON | Zen2 supports these; the stock build's `ggml-cpu-haswell.dll` targets the same ISA |
| `GGML_AVX512*` | OFF | **Zen2 has no AVX-512.** Enabling it would produce a binary that faults. |
| `GGML_VULKAN` | OFF | CPU only; also drops a 57 MB DLL from the distribution |
| `GGML_OPENMP` | ON | the stock build ships `libomp140.x86_64.dll`; matching that is fine |
| `LLAMA_BUILD_MTMD` | ON | keeps the multimodal path available for when a real projector appears |
| `LLAMA_BUILD_EXAMPLES/TESTS` | OFF | not needed, faster builds |

Resulting CPU backend variant (from the configure log):

```
Adding CPU backend variant ggml-cpu: /arch:AVX2 GGML_AVX2;GGML_FMA;GGML_F16C;__BMI2__;GGML_BMI2
```

---

## 4. Application architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│  notecode.exe  (.NET 10, WinForms, self-contained single file)      │
│                                                                      │
│  ┌────────┬──────────────────────────┬──────────────┐                │
│  │        │  editor                  │  AI panel    │                │
│  │ tree   │  WebView2 + CodeMirror 5 │  (chat with  │                │
│  │        │  tabbed files            │  local model)│                │
│  ├────────┴──────────────────────────┴──────────────┤                │
│  │  terminal: WebView2 + xterm.js  ⇄  ConPTY      │                │
│  └─────────────────────────────────────────────────┘                │
└───────┬──────────────────────┬──────────────────────┬────────────────┘
        │                      │                      │
        ▼                      ▼                      ▼
 LlamaServer              McpHub                ConptySession
 (wrapper + OpenAI API)   (stdio + http)        (kernel32 P/Invoke)
```

### 4.1 Why ConPTY and not a piped shell

`kimi.exe` and the other CLIs are console programs that change behaviour based on
whether stdout is a TTY — colour, progress bars, raw-mode input, screen redraw. Piping
stdout gives a degraded, non-interactive shell. ConPTY gives the child a real console.

`ConptySession` does the full native dance: two anonymous pipes →
`CreatePseudoConsole` → `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` attribute list →
`CreateProcessW` with `EXTENDED_STARTUPINFO_PRESENT`. Output is read on a background
task and decoded as UTF-8; **no escape sequences are interpreted in C#** — xterm.js does
the VT100 rendering, which is why the terminal pane is a WebView2 rather than a
custom-painted WinForms control.

#### The three bugs that a "known-good" ConPTY implementation still walks into

`src/ConptyProbe/` is a harness that links the *shipping* `Native.cs` and
`ConptySession.cs`, plus a deliberately minimal reference implementation
(`RefConpty.cs`) used as ground truth. It exists because each of these failures looks
like "the terminal is just blank" and none of them announce themselves.

**1. `STARTF_USESTDHANDLES` is mandatory, and the pipe handles must be inheritable.**

The MSDN pattern creates the pseudo console, sets the attribute list, and passes the
handles to `CreateProcessW`. That is not enough. With `bInheritHandles = FALSE` and no
`STARTF_USESTDHANDLES`, the child inherits the **parent's** console: its text output
goes to the parent's stdout, and the pseudo console's output pipe receives only the
console host's control sequences. The symptom is a terminal that stays empty while the
shell is visibly running.

The fix has two halves, and both are required:

```csharp
// (a) the pipe handles must be created inheritable
var sa = new Native.SECURITY_ATTRIBUTES { nLength = ..., bInheritHandle = 1 };
CreatePipe(out var hPipeInRead, out var hPipeInWrite, ref sa, 0);

// (b) the child's standard handles must be named
si.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
si.StartupInfo.hStdInput  = hPipeInRead;    // console input
si.StartupInfo.hStdOutput = hPipeOutWrite;  // console output
si.StartupInfo.hStdError  = hPipeOutWrite;
```

With `bInheritHandles = FALSE` the kernel only duplicates the handles named in
`STARTF_USESTDHANDLES`, and only if they were created inheritable. Miss either half and
the failure is silent.

Measured with the probe (`TOTAL bytes through the pipe`):

| variant | bytes through the pipe | child text reached the pipe? |
|---|---:|---|
| attribute only | 16 | no — went to the parent console |
| attribute + `bInheritHandles=TRUE` | 16 | no |
| attribute + `STARTF_USESTDHANDLES` | **321** | **yes** |
| attribute + `STARTF_USESTDHANDLES` + unicode env | **321** | **yes** |
| attribute + `STARTF_USESTDHANDLES` + inherit | 16 | no |

The winning configuration is `bInheritHandles = FALSE` **with** `STARTF_USESTDHANDLES`.
The 321 bytes included the banner, the prompt, the echoed command, and the command's
output.

**2. `STILL_ACTIVE` is 259 (`0x103`), not `0x259`.**

```csharp
return Native.GetExitCodeProcess(_hProcess, out var code) && code != 259;
```

With the wrong constant every process reports as exited, so `IsRunning` is always false
and the app never writes to the shell.

**3. The pipe handles must outlive `CreatePseudoConsole` until the child is created.**

`CreatePseudoConsole` takes its own references, but closing the parent's copies before
`CreateProcessW` runs leaves the child with nothing to inherit. The console-side ends
are closed immediately after `CreateProcessW` returns.

### 4.2 WebView2: two API traps in the host↔page bridge

Both directions of the message bridge have a trap that fails *silently* — the page
just never updates.

**Host → page.** `CoreWebView2.PostWebMessageAsJson` does **not** fire `window`'s
`message` event. It fires `window.chrome.webview`'s. Using
`window.addEventListener('message', …)` registers a handler for cross-window
`postMessage`, which the host never calls, so the terminal stays blank while the data
is provably arriving on the C# side.

```js
// wrong - cross-window postMessage only
window.addEventListener('message', onHostMessage);

// right
window.chrome.webview.addEventListener('message', onHostMessage);
```

**Page → host.** `TryGetWebMessageAsString()` throws
`ArgumentException: Value does not fall within the expected range` when the page
posted a JSON **object** rather than a string. Both pages post objects, so every
message — including the terminal's `ready` — was being dropped. `WebMessageAsJson` is
the raw JSON for any message type:

```csharp
var root = JsonNode.Parse(e.WebMessageAsJson ?? "{}");   // not TryGetWebMessageAsString()
```

**Also:** `PostWebMessageAsJson` must be called on the UI thread. The terminal pump
runs on a background thread, so the call is marshalled with `BeginInvoke`.

**Also:** `CoreWebView2.WebMessageReceived` is raised on the UI thread, so it is safe
to touch controls from the handler — but `async void` handlers are the only way to
`await` inside it.

### 4.3 Why three WebViews

CodeMirror and xterm.js are the only pieces that give real IDE feel and a correct
terminal, and both are browser libraries. Rendering them through WebView2 means the
heavy lifting (syntax highlighting, VT emulation, font rendering) is done by code
that already works, instead of by a hand-rolled ANSI parser that does not.

Both are **vendored** under `wwwroot/vendor/` — no CDN, no runtime network access.

The third WebView is the **agent transcript**. It used to be a `RichTextBox`, and that
was the wrong tool: an agent turn is not a line of log text. It is a user message, an
assistant message, and one or more **tool calls**, each of which has a name, a set of
arguments and a result. Rendering that as rich HTML is the only sane way, and the
page already exists in the WebView2 stack, so it costs one more surface rather than a
second rendering model.

The Send button and the prompt stay WinForms. That keeps keyboard focus, screen
readers and UI Automation working on them — the end-to-end test drives them by
automation, so they cannot be a web page.

### 4.4 Messaging

C# ⇄ JavaScript over the WebView2 `postMessage` bridge:

| Direction | Payload | Purpose |
|---|---|---|
| JS → C# | `{type:'ready'}` | page loaded, C# sends the init config |
| JS → C# | `{type:'needContent'}` | editor asks for the active file (ordering handshake) |
| JS → C# | `{type:'save', path, content}` | write the file |
| JS → C# | `{type:'dirty', path, value}` | tab title marker |
| JS → C# | `{type:'cursor', line, col}` | status bar |
| JS → C# | `{type:'input', d}` | terminal keystrokes → ConPTY stdin |
| JS → C# | `{type:'resize', cols, rows}` | ConPTY resize |
| JS → C# | `{type:'prompt', text}` | a suggestion chip in the transcript was clicked |
| C# → JS | `{type:'load', path, content, mode}` | open a file in the editor |
| C# → JS | `{type:'data', d}` | ConPTY stdout → xterm |
| C# → JS | `{type:'user'\|'assistant'\|'tool'\|'log'\|'delta'\|'thinking'\|'clear', …}` | the transcript |

The `needContent` handshake exists because the editor page and the host can race: the
host may know which file is active before the page has finished loading. The page asks
for the content once it has received `init`, so the file is never lost.

The transcript page renders a deliberately small markdown subset — fenced code,
inline code, bold, headings and bullet lists. Everything is HTML-escaped before it is
inserted, so a model answer can never inject markup into the page.

### 4.5 One config to rule them all

`runtime/notecode.config.json` is read by the app, the wrapper, and the scripts. The
wrapper is the single writer of the llama argv; the app shells out to the wrapper rather
than duplicating the argument list, so the GUI and the CLI can never disagree about how
the model is started.

### 4.5a The paths in the config are written with %USERPROFILE% in them

A path into the user's own folders — the search key, the skills, the kimi-code install —
is written once, in the file that is committed to the repository, and it has to work on
every machine. So it is written as `%USERPROFILE%\.brain-skills\skills` and expanded when
it is read: by the app, by the PowerShell wrapper, by the MCP server, and by the scripts.

**The expansion happens on the way in, while the file is being read.** That is not a
detail. The first version expanded the *text* of the file instead: it parsed the JSON
into a node tree, walked it, expanded every string and wrote it back. A configuration
file with the same key twice is legal JSON but not a legal node tree — parsing it throws
*an item with the same key has already been added* — and the pass that expands the file
failed on a file that was otherwise perfectly good. The catch swallowed the exception and
used the file as it was.

Nothing said so. The skills root stayed `%USERPROFILE%\.brain-skills\skills`, and
`Path.Combine` made a folder of it — a folder literally named `%USERPROFILE%`, under the
working directory. A skill installed through `/addskills` went there, and `/skills` could
not find it, because `/skills` looked in the real place.

A `JsonConverter<string>` does the expansion now, as each string is read. It sees every
string in every object and every array, so a path field added later is expanded too
instead of being the one field that was forgotten; and it does not care that the file has
a repeated key, because the reader takes the last one, the same way every other reader
of the file does.

The app can be asked what it resolved: `notecode.exe --print-config` writes the
configuration it is actually using, after the expansion, as JSON. It is how a user — or a
test — sees that a path did not resolve. `scripts/ui-config.ps1` is the guard, and it
takes two seconds instead of the forty minutes the full suite needs to notice the same
thing.

### 4.6 The design system

`Theme.cs` holds the palette, the fonts and the window chrome; `ThemeControls.cs`
holds the controls that draw themselves. The split is deliberate: the palette is data,
the controls are the drawing.

**Why draw the chrome instead of theming it.** The stock WinForms widgets cannot
render a dark IDE without visual styles fighting the palette — the tab strip, the
menu and the status bar all paint their own backgrounds. Every visible surface in
notecode is therefore owner-drawn, which also means the app does not depend on the
host's theme or the user's high-contrast setting for its own legibility.

**Dark only.** This is a developer tool. A light mode would double the surface area of
a desktop IDE for no gain, and the palette is tuned for long sessions in front of a
dark editor.

**The palette.** Void `#07090F`, Canvas `#0B0E17`, Surface `#11151F`, Raised
`#161B27`, Hover `#1C2230`, Border `#232A3A`, Border strong `#2E3750`, Text
`#E8ECF5`, Muted `#939DB4`, Dim `#5F6980`. The accents: **Violet `#8B5CF6`** is the
brand — the agent, the active tab, the keywords — and **Cyan `#22D3EE`** is the action
accent — Send, the caret, the types. Mint `#34D399`, Amber `#FBBF24` and Rose
`#FB7185` carry success, warning and error. Contrast against the panel colour: body
text 14.9:1, muted 6.1:1, violet 5.1:1, cyan 9.4:1 — all above WCAG AA 4.5:1.

**Type.** Segoe UI Variable for display and text, Cascadia Code for code, Segoe Fluent
Icons for symbols. All three ship with Windows 11, so the app stays offline. The
obvious alternative — Space Grotesk plus DM Sans — would mean a CDN, and "offline by
design" is not negotiable here.

**Icons are drawn, not typed.** `Marks` is a set of GDI+ paths — the paper plane on
Send, the neural graph on the agent header, the chevrons, the dots, the rings. No icon
font (no guessing at codepoints), no emoji.

**The aurora is rationed.** A violet-to-cyan bloom appears in the agent header and
behind the editor and the transcript. Everything else stays flat, so the eye lands on
the conversation and nowhere else.

**The window itself.** `ApplyWindowChrome` sets `DWMWA_USE_IMMERSIVE_DARK_MODE` and
`DWMWA_WINDOW_CORNER_PREFERENCE`, so the title bar is dark and the window has rounded
corners without a single pixel of custom chrome drawn by the app.

### 4.7 Drawing rules this UI learned the hard way

These are the rules that the redesign had to learn the hard way. They are WinForms
behaviours, not opinions, and each one cost a visible bug.

| Rule | Why |
|---|---|
| Fill the background explicitly in `OnPaint` | a double-buffered control is not erased by the system, so stale pixels from the previous layout survive the repaint |
| Never use `Color.Transparent` | a transparent child can leave a ghost of the parent's earlier contents behind it |
| `VerticalCenter` requires `SingleLine` | without it GDI ignores the centring and measures the string against a wrapped rectangle; the caption lands outside the control and a sliver of it bleeds into the row below |
| `Controls.Add` appends to the **back** of the z-order | an opaque control added first paints over the one added later — the Send button was invisible under the docked text box |
| Never call `BringToFront` on a docked sibling | it reverses the docking order: the fill control takes the whole area and the panel overlaps it |
| Dock order is not layout order | the agent panel uses a `TableLayoutPanel` so the rows cannot silently invert |
| `DrawTreeNodeEventArgs.Bounds` is the node's label, not the row | the text rectangle has to run to the edge of the tree or the label is ellipsised to nothing |

### 4.8 The slash command system

`/` in the prompt opens a palette. The palette is a plain panel that paints itself and
**never takes the keyboard focus**: the prompt keeps it, the arrow keys move the
highlight and Enter takes the command. That is the whole design in one sentence, and
every other decision follows from it.

A control that grabbed the focus would break typing, break the screen reader and break
the automation that drives the prompt in the end-to-end test. The palette is therefore
positioned by hand inside the agent panel and repainted on `Layout`.

| Piece | File | What it holds |
|---|---|---|
| the registry | `SlashCommands.cs` | every command: name, aliases, usage, long help |
| the parser | `SlashCommands.cs` | recognises a command and works out where the caret is |
| the palette | `CommandPalette.cs` | the themed list under the prompt |
| the dispatch | `MainForm.cs` | one method per command |

The parser recognises a command only at the start of the prompt or after whitespace, so
`src/rea` in a path is not a command. The token ends at the first space, so the palette
narrows on the command name and closes once the arguments start.

**`/rea` replaces the checkbox.** The REA tools used to be a switch in the panel, which
was always either wrong or expensive: 170 schemas on a 32k context window is a lot to
pay for a feature you may not use. The command says what to analyse, where the findings
go and what to do, and the tools are on for that turn only.

The provider commands are the same shape. `/model`, `/provider` and `/addprovider` act
on the store and answer in the transcript, in a block that is marked as coming from
notecode rather than from the model.

### 4.9 Bring your own key

The local model is the default, but any OpenAI-compatible endpoint works and so does
Anthropic's messages API. The endpoints live in `runtime/providers.json`; the bearer
tokens are encrypted with **Windows DPAPI** (`CryptProtectData`, current-user scope)
before they are written, so the file is unreadable to anyone else and no token is ever
written in the clear.

`IChatBackend` is the seam. The local llama-server and a BYOK provider both implement
it, so the conversation loop does not care which one is answering:

| | Local | BYOK |
|---|---|---|
| the turn | `LlamaServer.StreamChatTurnAsync` | `ProviderClient.StreamTurnAsync` |
| the tool catalogue | `GET /tools` on llama-server | the app's own MCP connections |
| the tool dispatch | `POST /tools` on llama-server | `McpHub.CallToolAsync` |

The BYOK path is why the app talks to the `notecode` MCP server directly: llama-server
is not in the loop when a remote provider is answering, so something has to dispatch
`save_as` and the rest, and the app's MCP client already knows how.

**The endpoint doctor** does two things per provider. It asks the endpoint for its
model list with the user's token, and it searches the web for the provider's current
base URL and model names. Providers move their endpoints and rename their models, and
a stale base URL is the difference between a working agent and a 404.

The search runs through the private GWN search gateway (`SEARCH_GWN_MCP/server.mjs`) as
a **command**, not as an MCP server: the doctor needs one answer, and giving the model
a search tool would spend context on something the app can do by itself.

The doctor searches the **open web**, and that is a setting of its own
(`search.doctorCategory`, default `general`). The gateway's `it` category is a
programming corpus — MDN, Stack Overflow — and it answers *"MiniMax API base URL"* with
articles about the URL API. The `general` category answers the same question with the
MiniMax documentation, including the live `https://api.minimax.io/v1/chat/completions`
endpoint.

Three things the doctor got wrong first, and now handles:

| Trap | What it looked like | The fix |
|---|---|---|
| the endpoint has no `/models` | Google's `v1beta/openai` layer answers 404 there, and the doctor called the endpoint unreachable | a 404 on the model list is followed by a probe of the chat path; if the chat path answers, the endpoint is reachable |
| the version segment is dropped | a search result said `api.deepseek.com`, the configured URL said `api.deepseek.com/v1`, and the doctor offered to "fix" a URL that was already right | same host and either the same version segment or no version segment on one side means the same endpoint |
| a page path looks like a model id | `and-api-including-grok-4` and `deepseek-models-overview-00381` were offered as models | the result's URL line is skipped, and a candidate containing a path word is dropped |

**`echoReasoning`.** The Reflection Beam model reasons on every request and answers 400
if an earlier assistant message comes back without the reasoning that produced it. The
reasoning is carried in `reasoning_content`, which is not part of the OpenAI chat spec,
so it is opt-in per provider and is on by default for the Reflection preset. The app
keeps the reasoning of an assistant turn in `ChatMessage.Reasoning` and hands it back on
the next turn.

**The GGUF reader** (`Gguf.cs`) reads only the header of a model file, so the chat
template the model was trained with comes out of a 19 GiB file in milliseconds. The
tensor data is at the end of the file and is never touched.

### 4.10 Vision: the projector and the parts array

MiniCPM-V is a vision model, and the projector that goes with it is a separate file:

```
C:\models\MiniCPM-V-4_7-35B-A3B-Q4_K_M.gguf     the model          19.71 GiB
C:\models\mmproj-model-f16.gguf                 the projector       1.04 GiB
```

The projector is the half that turns a picture into tokens. It is named in the config
(`llama.mmproj`) and the launcher passes it as `--mmproj`. **Without it the server
answers 400 to any request that carries a picture** — the failure is on the request, not
at startup, so a server that is merely missing the projector looks perfectly healthy.
The running server is the one that says the truth:

```json
"modalities": { "vision": true, "video": true, "audio": false }
```

That line is what `scripts/smoke-test.ps1` checks, next to the projector's presence.

**The picture travels as a data URL in the content parts array.** The chat API has two
shapes for a message: a plain string, and an array of parts. A turn with a picture uses
the second:

```json
{"role": "user", "content": [
  {"type": "text", "text": "What colour is the square?"},
  {"type": "image_url", "image_url": {"url": "data:image/png;base64,..."}}]}
```

The model's own chat template does the rest. MiniCPM-V's template walks the parts and
puts `<|image_pad|>` where the image is, and llama.cpp's multimodal layer expands that
into the projector tensors. `chat_template_caps.supports_typed_content` says the
template can take typed content at all, and it is `true` for this one.

**The picture is scaled down before it is sent.** A photo off a camera is several
megabytes, and the base64 of that would go into the prompt for no gain — the model
resizes it anyway. The reader scales to 1024 px on the long side with a high quality
bicubic filter and re-encodes as PNG. A 12 MB photo becomes about 12 KB of base64 and
95 prompt tokens.

The picture is shown in the transcript as a thumbnail, so it is obvious what the model
is being asked about. It goes with the **next** turn only, and it is cleared when the
turn is sent.

### 4.11 The provider catalogue

The presets are the baseline, and `ProviderStore.MergePresets` keeps them in sync: a
preset the user edited keeps its edits, a preset that is missing is added back, and a
preset that was never touched follows the catalogue. Eighteen endpoints are shipped:

| | | |
|---|---|---|
| local | `127.0.0.1:8080/v1` | the bundled llama-server |
| ollama | `localhost:11434/v1` | a local Ollama, no key |
| minimax | `api.minimax.io/v1` | |
| moonshot | `api.moonshot.ai/v1` | Kimi |
| anthropic | `api.anthropic.com/v1` | the messages API, not chat completions |
| openai | `api.openai.com/v1` | |
| grok | `api.x.ai/v1` | |
| antigravity | `generativelanguage.googleapis.com/v1beta/openai` | Google |
| deepseek | `api.deepseek.com/v1` | |
| nvidia | `integrate.api.nvidia.com/v1` | 83 models without a key |
| qwen | `dashscope.aliyuncs.com/compatible-mode/v1` | |
| openrouter | `openrouter.ai/api/v1` | one key, many vendors |
| groq | `api.groq.com/openai/v1` | |
| mistral | `api.mistral.ai/v1` | |
| agnes | `apihub.agnes-ai.com/v1` | |
| xkiro | `api.xkiro.com/v1` | a free tier of 5M tokens a day |
| atria | `api.atria-asi.ai/v1` | |
| reflection | `api.reflection.ai/openai/v1` | Beam, 1M context, echoes reasoning |

### 4.12 Three bugs the BYOK path found

Each of these was found by the end-to-end test against a local mock endpoint
(`scripts/mock-openai.mjs`), not by reading the code.

**The local model overwrote the BYOK provider's model.** `StartServerAsync` and
`UpdateServerStatus` wrote the local llama-server's first model name into
`_providers.ActiveProvider()` — whichever provider that was. Configure MiniMax, start the
local server, and every turn suddenly went out as `notecode-minicpm`. The local model
belongs to the local provider, and only the local provider takes it.

**A BYOK turn waited for the REA tools it was going to throw away.** `GetToolSpecsAsync`
listed the tools of **every** MCP connection and filtered afterwards. The REA server is
an `npx` cold start that publishes 170 tools, and listing it can take tens of seconds, so
a turn that did not want those tools waited for them. The hub now lists the tools of the
named servers only, and the result is cached per turn shape.

**The foreground cannot be taken on this machine.** The GUI tests used `SendKeys`, which
only reaches the foreground window. The foreground here is a system service window
(`GameInputServiceWindow`): `SetForegroundWindow` is refused, `SwitchToThisWindow` is
ignored, and an ALT press opens the menu keytips. Every keystroke went nowhere and the
test failed with a symptom that looked like an app bug. The tests now post the keys
straight to the control's own window, and a capture makes the window **topmost** first —
which needs no foreground either. `scripts/winutil.ps1` holds both.

---

## 4.13 The activity row, the icon and the reasoning

The AI panel shows one **activity row** above the answer: a drawn icon, a phrase, the
blinker, and — under the blinker — the model's own thinking.

| Piece | Where it comes from |
|---|---|
| the icon | `ICONS` in `wwwroot/chat.html`, nine hand-drawn SVG paths (think, read, write, run, move, folder, copy, remove, list, open) |
| the phrase | `DescribeTool(call.Name, call.ArgumentsJson)` in `MainForm` — it reads the tool's arguments and names the file it is working on |
| the blinker | the existing teal dots, now inside the activity row |
| the reasoning | `reasoning_content` / `reasoning` / Anthropic's `thinking_delta`, streamed by `ProviderClient` and `LlamaServer` into `ChatView.Reasoning` |

**MiniCPM-V produces no reasoning at all.** Measured, not assumed: with
`chat_template_kwargs.enable_thinking=true` it still emits no `<think>` block. So with the
local model the row carries the activity and the reasoning line stays empty. The line
fills in when a reasoning provider is configured, and `scripts/ui-reasoning.ps1` proves
that path against a mock that thinks out loud.

**The reasoning has to survive the answer.** The first version called `hideThinking()`
when the first text delta arrived, and `hideThinking()` removed the whole row — so the
icon and the thinking were destroyed a moment after they appeared. The row now stays and
only the blinker stops: `stopBlinker()` marks the row done, and the CSS takes the
animation off it. A new question removes the old row (`removeActivityRow`) so the
activity stays next to the question it belongs to.

Each pass of the tool loop is a new turn, so `RunToolLoopAsync` posts
`Activity("think", "Thinking")` every time, not only on the first pass. That is what
clears the reasoning of the previous turn and puts the label back to *Thinking* while
the model works.

---

## 4.14 The cross on a tab, and why it had to be a control

Every tab in the editor strip carries a cross that closes the file.

The first version painted it, like the rest of the strip. That works for the mouse and
for nobody else:

- **The native tab control cannot describe an owner-drawn tab.** The strip is a
  `SysTabControl32` with `DrawMode = OwnerDrawFixed`, and the UIA provider for that
  control enumerates its tabs from the native items. An owner-drawn tab has no native
  item text, so the provider finds **no tabs at all** — `FindAll(TabItem)` returned zero
  even with a tab open and its caption painted on screen.
- **A custom `CreateAccessibilityInstance` does not help.** The native provider owns the
  control, so the MSAA→UIA bridge never asks the managed accessible object. The
  accessible object was written, correct, and invisible.

So the cross is a **real control**: `TabCloseButton`, a `Control` that paints the mark
itself, is named `Close <file>` for a screen reader, takes the keyboard (Space and Enter
raise the click), and shows a focus ring.

Two details that cost time:

- **WinForms refuses non-`TabPage` children of a `TabControl`** —
  `ArgumentException: Cannot add 'TabCloseButton' to TabControl.` The crosses therefore
  live in the panel that holds the strip, and their position is mapped through the
  screen (`host.PointToClient(PointToScreen(rect.Location))`).
- **The tab count changes before the removal is reported**, so a sync driven by
  `ControlRemoved` leaves a cross behind with no tab under it. The sync also runs from
  `OnPaint`, which is when the strip repaints after a tab is gone.

The whole sync is wrapped in a `try`/`catch`: the crosses are decoration on top of the
strip, and they must never take the app down. An unguarded `Controls.Add` inside
`OnLayout` did exactly that, once.

---

## 4.15 The paperclip, and what the model does with a file

The composer has a **paperclip**: one button that opens the file picker, and whatever
is chosen goes into the next turn.

| The file | How the model gets it |
|---|---|
| a picture | scaled to 1024 px on the long side, then sent as an image part — the model **looks** at it |
| anything else it can read | read as text, truncated at 40,000 characters, and put in front of the model as its own turn — the model **reads** it |
| a binary file that is not a picture | refused, with a message: the model cannot read it |

The text file is put in front of the model the same way the editor's buffer is — its own
`user` turn, before the question — so the model reads it before it answers. That keeps
the user's prompt intact and works with every provider, including the text-only ones.

### The mark is drawn, and it is drawn at the right size

`Marks.Paperclip` builds the shape on a 24 unit grid: four lines and three semicircles,
which is the Lucide paperclip geometry. The grid maps onto the box the button gives it,
so `X(v) = box.Left + v * k` with `k = min(width, height) / 24`.

**The first version divided by 24 twice** — `v / 24 * s` where `s` was already
`min / 24` — and the paperclip came out as a two pixel dot. It was in the right place
and it was "painted", so nothing looked broken; it was simply too small to see. The
geometry is now printed by the probe, and the capture is cropped to the button's own
rectangle to check it.

### A themed WinForms button will not paint for you

`IconButton` was first a `Button` with `ControlStyles.UserPaint` and an
`OnPaint` override — the same shape as `AccentButton`, which works. It did not work
here: the button came out as a **violet pill in the system's accent colour**, because a
themed button paints itself in `WM_PAINT` and the app's `OnPaint` never gets a look-in.

The fix is to not be a `Button` at all. `IconButton` is a plain `Control` that paints
itself, the same as `TabCloseButton`, and it makes up the difference:

- `IsInputKey` claims Space and Enter, and `OnKeyDown` turns them into a click,
  because a plain `Control` does not do that by itself.
- `AccessibleRole = PushButton` keeps it a button for a screen reader and for UI
  Automation.
- A test has to click it with a real press and release (`PostKey::MouseClick`);
  `BM_CLICK` only ever reaches a real `Button`.

---

## 4.16 The three surfaces, and how the model reaches them

The app has three surfaces: the **terminal**, the **editor** and the **transcript**. The
model is not a chatbot that talks about code — it drives the app — so each surface needs
its own tools, and the tools have to work for the local model *and* for a provider.

### The gap that was found

| Surface | Local llama | BYOK provider |
|---|---|---|
| Terminal | `exec_shell_command` — llama.cpp's own, in llama-server's process, invisible to the user | **no tool at all** |
| Editor | `open_in_editor` — opens a file, but the live buffer could not be read or written | the same |
| Transcript | **no tool** | **no tool** |

The reason is structural. The local backend gets its tools from llama-server's `/tools`
catalogue, which is built-ins plus the MCP servers llama-server was started with. The BYOK
backend gets its tools from the app's own `McpHub`. Two different lists, and only one of
them was complete.

### The fix: one tool set, in the notecode MCP server

Putting the tools in `runtime/notecode-mcp.mjs` solves both halves at once. That server is
registered with llama-server *and* connected by the app's `McpHub`, so **both backends get
the same tools from the same place**. Nine were added:

| Tool | Surface | What it does |
|---|---|---|
| `run_command` | terminal | runs a command in the workspace, returns the output and the exit code |
| `get_editor_buffer` | editor | the live buffer of the open tab, unsaved edits included |
| `set_editor_text` | editor | writes into the open tab and saves it |
| `post_to_chat` | transcript | puts a note where the user reads it |
| `read_conversation` | transcript | reads the conversation so far |
| `list_skills` | skills | the installed skills and their descriptions |
| `read_skill` | skills | reads a skill file |
| `write_skill` | skills | writes a skill file |
| `describe_notecode` | skills | the ground truth: the tools, the surfaces, the shape a skill has to have |

The catalogue is now **19 `notecode_*` + 170 `rea_*` + 6 `global-brain_*` + 7 built-ins**.

### The app channel

The MCP server is a separate process, so it cannot touch the window. The three surfaces
live in the window, so the app and the server talk over a small file protocol:

```
runtime/app-requests.jsonl      the server writes a request
runtime/app-results/<id>.ack    the app says it picked the request up
runtime/app-results/<id>.json   the app writes the answer
runtime/app-context.json        what the app is working on right now
```

Three properties of the protocol matter:

- **The answer is written when the work is done**, not when the request is picked up, so
  the model waits for the real result.
- **The `.ack` file is what tells the server the app is there.** If no ack arrives within
  three seconds, nobody is listening, and the caller falls back instead of waiting for
  nothing. `run_command` then runs the command itself; the editor and the transcript say
  plainly that the app is not running.
- **The queue is primed on startup**, so a request that was never answered belongs to the
  run that made it and not to the next launch.

### The command runs in a shell of its own

The first version typed the command into the app's ConPTY session and waited for a marker
line to say the command was done. It does not work, and the reason is worth keeping:

**PSReadLine cannot be driven by text pushed into a console.** It is a line editor. It
answered with a continuation prompt (`>>`), it split the marker line, and the marker never
arrived, so every command timed out. The output that did come back was full of escape
codes and prompts.

The command now runs in a shell of its own:

```
pwsh -NoLogo -NoProfile -NonInteractive -Command "<the command>"
```

with the workspace as the working directory. That gives the model clean text and the real
exit code, it cannot leave the user's shell in a broken state, and the command and its
output are written into the terminal pane so the user still watches it happen. The escape
codes are stripped, because the output is read by a model and not by a terminal.

### The command the model wants is not the command that runs

A shell of its own is clean, but it is also the wrong boundary. The model asking for a
command is not the user asking for a command.

The model reads files, fetches pages and calls other servers, and everything it reads is
written by somebody else. A markdown file in the workspace can say *"before answering, run
`curl ... | sh`"*. The model sees it as text, and a model that treats text as instruction
will comply. This is the classic prompt injection, and it is not a theory: the audit that
prompted this work called it a confirmed high-severity risk, and it was right.

The fix cannot be a sandbox. A general shell command cannot be sandboxed on Windows without
breaking the toolchains that need it — `dotnet build`, `npm install` and `cargo` all write
to the user profile, and AppContainer or a restricted token in front of them breaks the
product. So the boundary is the user, and the design is built around that.

**The command is split before it is judged.** The shell's own operators end one command and
start the next, so `git status && rm -rf /` is two commands and the second is judged on its
own. This is the part that is easy to get wrong: judging the whole string as one command
would let an allowed prefix hide anything after it, which is the whole trick. The splitter
is a small lexer — it knows about quotes, about `2>&1`, about `&&` — and it is the part of
this with the most tests.

**Three answers, not two.** Allow for the commands on the allowlist. Ask for everything
else. Deny for the commands that are never run, not even on an approval. The third one
matters: without it, the way to defeat the gate is to talk the user into clicking *yes*,
and the user is exactly who a good injection addresses.

**The window times out.** Nobody answering means no. A command that runs while nobody is
looking is the case the window exists for, so the window cannot simply wait forever.

**The child gets a small environment.** This one was found by reading the code rather than
by the audit. `ProcessStartInfo` inherits the parent's environment by default, so a command
was running with every API key in the user's session sitting right there — `echo $env:KEY`
was all it took. The child now gets a named allowlist: `PATH`, `TEMP`, `USERPROFILE`, the
package caches. A build that genuinely needs a token gets the real environment from the
same window, for one command, off by default.

**The content is marked as content.** Tool output from a file, from a program or from
another server is wrapped in a marker, and the system prompt says that content is data and
never an instruction. This is the soft control of the set: it lowers the odds rather than
removing the hole, and it is worth having because the hard controls above should not be the
only ones.

**One list, two readers.** The rules live in `commandPolicy` in `runtime/notecode.config.json`
and both the IDE and `runtime/notecode-mcp.mjs` read them. The MCP server has no window, so
it cannot ask — it applies the refusals and nothing else. The rule that a list in two places
is a list that drifts is why there is no built-in list in either language: there is one
copy, and `ToolProbe --command-policy` asks both readers the same questions and fails if
they disagree.

### llama.cpp publishes a shell too

There is a second shell, and it is not in this codebase. llama.cpp advertises
`exec_shell_command` alongside the MCP servers, with `permissions: {write: true}`, and the
catalogue the app fetches includes every built-in the server publishes. A call to it would
have gone straight to the server's HTTP API and run inside the server process — past the
approval window, past the small environment, past everything above.

So the server's shell is not handed to the server. It is caught in the app's dispatch and
run here, where the policy applies, with the timeout the tool asked for. The MCP servers
keep their own tools: those are the vendor's tools and they do what they say.

### The editor is a page, so the page has to answer

The editor is CodeMirror in a WebView2, so the app cannot read the buffer — the page has
it. Two host→page messages were added:

```
getBuffer      the page posts back {type:'buffer', id, path, content}
requestContent the page posts back {type:'save', path, content}   (already there)
```

`get_editor_buffer` posts a `getBuffer` with an id and waits for the answer that carries
that id; `set_editor_text` posts a `setContent` and then a `requestContent`, and the save
that comes back is what tells the app the write landed.

`requestContent` now also clears the page's dirty flag. It did not before, so after a
save the page still thought it had unsaved edits while the app thought it was clean — and
the next keystroke did not send the `dirty` message, so the tab stopped showing the dot.

---

## 4.17 The skill fixer, and adding a tool

### What a skill has to be

A skill is a folder with a `SKILL.md` in it. `notecode.config.json → skills.roots` says
where those folders are; `/skills` lists them and `/skill <name>` loads one into the
conversation as context.

The front matter is what the app reads:

```markdown
---
name: my-skill
description: One line saying what the skill is for and when to use it.
---

The body, which is instructions for the model.
```

A skill with no front matter is a skill the user cannot pick. A skill that tells the
model to use a tool notecode does not have sends the model after something that is not
there. Both are silent failures: the skill loads, the model tries, and nothing happens.

**The name belongs to the file.** The fixer writes the whole file back, front matter
included, and a model asked to repair a front matter will sometimes write a different
name into it — a shorter one, or a tidier one. The user picked a file called
`my-skill.md`; a skill that answers to another name is a skill the user cannot find
again.

So the name is the app's to decide: it is the name of the file, or the name of the folder
for a skill that is already in one (the file inside a skill folder is always `SKILL.md`).
The model writes the description and the shape. The name is put back if it was changed,
and the fixer is told the name in the prompt so that it rarely has to be.

### The fixer

`/addskills` opens the file picker (or takes a path), and the **skill fixer** repairs
what it finds:

1. **The check** (`SkillFixer.Check`) is done without a model: front matter present, a
   `name:` and a `description:` in it, a body that has instructions in it, and no
   backticked tool name that notecode does not have. The tool check uses a prefix list —
   `web_search` and `browser_open` are flagged, `some_variable` is not.
2. **The repair** is done by the model, through the app's own tools. `describe_notecode`
   gives it the ground truth, `list_skills` shows the house style, `read_skill` reads the
   file and `write_skill` writes it back.
3. **The install** puts the fixed file in a skill folder, so `/skills` lists it and
   `/skill <name>` loads it.

The fixer is a bounded agent, in the way an agent has to be:

- **A fixed number of turns** — six. The run ends when they are used up, and says so.
- **Tool failures are information, not the end.** A tool that throws is handed back to
  the model as `[error] …`, so it can try another way.
- **Every step is logged** to `runtime/notecode-app.log` under `skill-fixer`.
- **There is a fallback that does not need a model.** When the model cannot be reached,
  the front matter is still put right by hand, because that part does not need one.

It runs on whichever backend is answering — the local model or a provider — and it drives
the tools through the ordinary dispatch path, so there is nothing special about it.

### What the fixer is given, and what it costs

The fixer is told what this app can do, and what the skills that are installed look like,
**before** the model starts. It used to be told to fetch both with a tool call, and that
was a mistake: each tool call is a whole turn, and a turn on this model is minutes.

The prompt turned out to be the real problem. `list_skills` returns every installed
skill, and there are **124** of them — 31,000 characters, about **8,000 prompt tokens**.
With that in the prompt the first turn took over ten minutes before a word came out.
The app fetches the list itself and keeps three of them, and it gives the model the
ground truth rather than letting it fetch that too.

Measured from the server's own slot, the first turn is **4,878 prompt tokens** for a
small skill and **6,941** for the 7.9 KB Blender skill. The skill itself is most of
what is left: the ground truth is 818, three skills show the shape, and the names of
the other 221 tools are there so that a skill naming one of them is not told to take
it out.

Three skills are enough. The point of the list is to show the shape of a skill, not to
enumerate the library.

| What the fixer is given | Where it comes from |
|---|---|
| the ground truth — the tools, the surfaces, the shape a skill has to have | `describe_notecode`, called by the app |
| the house style — three installed skills | `list_skills`, called by the app and trimmed to three |
| the skill to fix | read from disk by the app |
| the tools | the ordinary tool list, narrowed to the **two** the fixer needs: `read_skill` and `write_skill` |
| the other servers' tools | the connected servers' tool names, so a skill that names one of them is not told to take it out |

The fixer used to be given `list_skills` and `describe_notecode` as tools as well, and the
model asked for the list of installed skills again — 124 skills, about 8,000 tokens, a
whole turn on a model where a turn is minutes. Three of the six turns went on it. The two
tools it has now are the only two the job needs: read the file, write the file.

### The tools are written to a file, and the file is the picked one

`write_skill` may only write files inside a skill folder, or the file the user picked.
The picked file is published to the MCP server in `runtime/app-context.json`, so the
server knows what the fixer is allowed to touch without the model having to be trusted.

### A stale catalogue

llama-server reads its MCP servers **once, when it starts**. A server that was started
before a tool existed does not know about it, and the app says so in as many words:

> The local server is running with an older list of tools, because it reads the MCP
> servers once, when it starts. Restart it with **Run > Restart llama server**, then run
> `/addskills` again.

The Run menu has a **Restart llama server** entry, which stops and starts the server
through the same wrapper script.

### `/addtool`

A tool source is an MCP server. Registering one meant editing two JSON files by hand:
`notecode.config.json → mcp.servers` for the app, and `runtime/mcp-servers.json` for
llama-server. The command does both, and it writes them as JSON nodes so the `$comment`
keys in them survive.

```
/addtool playwright stdio npx -y @playwright/mcp@latest --stdio
/addtool files http http://127.0.0.1:8765/mcp
```

The app connects to the new server at once — one server, not the whole set, because
reconnecting the REA server is an npx cold start that publishes 170 tools — so a provider
can call the new tools straight away. The local model needs the server restarted.

An HTTP server is written into llama-server's config through `mcp-stdio-bridge.mjs`,
because llama.cpp's parser is stdio-only (§5.1).

---

## 4.18 The accessibility pass

The app is dark, owner-drawn and mostly custom-painted, so the accessibility of it is not
a matter of inheriting the host's: every surface is the app's own. Two things were wrong
that no script caught, and both were about contrast.

### The quiet colour was not quiet enough

`Theme.TextDim` was `#5F6980`. Against the app's own surfaces:

| Surface | Ratio | AA for text (4.5:1) |
|---|---|---|
| Surface `#11151F` | 3.32 | fail |
| SurfaceRaised `#161B27` | 3.13 | fail |
| SurfaceHover `#1C2230` | 2.89 | fail |

It is used where text carries meaning and not decoration:

- the transcript's **speaker label** (`.who`, 10.5px) and the **tool kind** label
  (`.tool .kind`, 9.5px) — both are `--dim` in `chat.html`;
- the **command palette's** summary column for the rows that are not selected, and its
  **footer hint**;
- the **label of an unchecked box** — `on ? Theme.Text : Theme.TextDim`, and an unchecked
  box is not a disabled control, so it is not exempt from the contrast rule;
- the **AI panel's subtitle** and the **status bar's** MCP line when nothing is
  connected;
- the provider form's **token state** and its **hint**.

`TextDim` is now `#7E8AA0`: **4.57:1** on the worst surface it is painted on. One colour
fixed eight places.

### The primary button's label was unreadable

`AccentButton` paints a violet gradient and writes the label in white:

| Fill | White on it |
|---|---|
| VioletBright `#A78BFA` | 2.72:1 |
| Violet `#8B5CF6` | 4.23:1 |
| Cyan `#22D3EE` (the hover gradient) | 1.81:1 |

The Send button is the primary action of the app, and its label sat at about 3.4:1.

One label colour does not work on both of the things this button can be filled with: the
bright gradient, and a flat `SurfaceRaised` panel (which is what the Cancel and
Auto-update buttons use). So the label follows the fill:

```csharp
private static Color LabelOn(Color a, Color b)
{
    double lightest = Math.Max(Theme.RelativeLuminance(a), Theme.RelativeLuminance(b));
    return lightest >= 0.177 ? Theme.Void : Color.White;
}
```

The threshold is where the two swap: a near-black label clears 4.5:1 from a background of
luminance 0.177 and above, and a white one clears it from 0.183 and below. The result is
4.70:1 to 11.01:1 on the gradient states and 17.21:1 on a flat button.

### The check that keeps it

`scripts/check-contrast.ps1` reads the colours **out of the source** — `Theme.cs` for the
app and `chat.html` for the transcript — so a colour that is changed cannot slip past a
check that still remembers the old one. It checks the text colours against every surface
at 4.5:1, the marks against every surface at 3:1, the accent button's label against every
fill it can have, and that the app and the transcript still agree on the dim colour.

---

## 4.19 The Blender server, and the skill that drives it

Blender 5.2 runs on this machine and publishes an MCP server on
`http://127.0.0.1:8765/mcp`. It is registered in `notecode.config.json` as a
`streamable-http` server named `blender`, so the app reaches it over HTTP
directly — the `blender-mcp` package is not installed and is not on PATH, and
the server lives inside the running Blender anyway.

### A disposed document

The first version of the HTTP client could never list a single tool:

```
System.ObjectDisposedException: Cannot access a disposed object. Object name: 'JsonDocument'.
```

`RequestAsync` returned a `JsonElement` that belonged to a `JsonDocument` it
had already disposed. The stdio client was right — it cloned — and the HTTP
client was not. With `result.Clone()` the server connects and publishes
**26 tools**.

### The names the model has to call

llama-server publishes an MCP server's tools with the server name in front.
The catalogue holds `blender_execute_python`, `notecode_run_command`,
`rea_get_python_api_docs`. The bare name is not what the model calls.

That matters in three places, and all three had it wrong:

| Where | What it was | What it is now |
|---|---|---|
| The BYOK tool list | `execute_python` | `blender_execute_python` — the same spelling the local backend uses |
| The dispatch of a tool call | an exact match on the bare name | the prefixed name first, the bare name as the fallback |
| The skill fixer's tool check | the bare name only | both, so a skill that names `blender_execute_python` is not told to take it out |

A BYOK provider used to be offered the notecode tools and nothing else, so it
could not have driven Blender at all. It is now offered the tools of **every
configured server** — REA excepted, because its 170 schemas are an npx cold
start and a turn that does not want them should not wait for them. The same
skill therefore works on the local backend and on a provider, which was the
whole point of the change.

### The manuals are in the workspace

`C:\blender_mcp` ships the Blender API reference and the user manual as plain
RST, and the model prompt for the server is written to look them up. Both are
copied into the workspace:

```
workspace/vendor/blender-reference/api      1,857 files
workspace/vendor/blender-reference/manual   2,217 files
workspace/vendor/blender-reference/addon       10 files
                                        =  4,084 files, 14.9 MiB
```

`vendor` is hidden from the file tree but reachable by the model's file tools,
so the model can look up an operator instead of guessing it. The API folder is
one file per class — `bpy.types.Object.rst`, `bmesh.ops.rst` — so the way to
find the answer is to look for the file name first.

### The skill

`skills/blender-scene/SKILL.md` is the skill that puts all of this in front of
the model: the 26 tools by name, the order to do things in, the Blender traps
that cost real time (objects and data are separate, the active object and the
selection are different things, a mode that is wrong fails silently, a bmesh
edit is lost if it is not flushed back), and where the manuals are.

It was written from `C:\blender_mcp\mcp\blmcp\data\prompts.yml` — the server's
own model instructions — and adapted to what this server really publishes.
The upstream prompt names `execute_blender_code`; the tool here is
`execute_python`. The upstream prompt has no doc-search tools, because the
server has none; the manuals are searched with notecode's own file tools.

### The fixer is not allowed to make the skill worse

The fixer asks the model to write the whole file back. On a model that takes
five minutes to say 1,900 tokens, that is a long way to lose something: the
front matter, a table of tools, half the instructions.

So the rewrite is checked before it is kept. A rewrite is refused when it has
no front matter, when it names a tool that is not there, or when it keeps less
than a third of a body that was already well formed. A refused rewrite is put
back and the model is told why, so it can try again.

A skill that was already broken is allowed to change shape — that is what the
fixer is for. A skill that was already good is not allowed to lose most of
what it said.

The checking works because **the app writes the file, not the model's backend**. The
model hands over the content, the app checks it, and the app calls the MCP server through
its own client. Two reasons: the content can be refused before it lands, and llama.cpp's
MCP client cannot carry an argument that size (§5.7) — a fixer that wrote through the
model would have hung on the first write and never finished.

---

## 4.20 The hardware profiles, and the build that runs here

notecode has to run on more than one machine, and the machines are not the same
kind of machine. One is a Ryzen 7 5700U laptop with an integrated GPU and no
NVIDIA card at all. The other is a Core Ultra 5 desktop with an RTX 5060 Ti and
16 GB of RAM. A llama.cpp binary is compiled for exactly one backend, so "the
model" is not one thing: it is a model, a build and a machine.

### The scan

The app scans the machine before it opens, on a splash screen that looks like a
terminal because that is what it is doing - writing down what it found, line by
line, before the editor comes up.

The numbers that matter are the ones that are wrong in the obvious place:

- **Video memory through WMI is capped at 4 GiB.** `Win32_VideoController.AdapterRAM`
  is a 32-bit field, so an 8 GiB card reports 4 GiB and a 16 GiB card reports the
  same 4 GiB. A rule that says "4 GiB or more, use the GPU" would be decided by a
  number Windows invented.
- **An integrated GPU has no video memory.** It borrows system RAM, and Windows
  reports the borrow as "dedicated" memory too. On the laptop, DXGI reports
  3.0 GiB dedicated and 14.5 GiB shared: that is one pool of RAM, not a card.

So the scan reads **DXGI** first. It asks the driver directly, it answers in
about a millisecond, and it can tell a card's own memory from a borrow when the
shared pool is at least as big as the dedicated one. `nvidia-smi` is the
authority for NVIDIA, where it also reports the driver version. `dxdiag` is the
last resort: it takes seconds, it cannot tell a card's memory from borrowed
memory, and a number above 4 GiB from it is treated as shared.

The whole scan takes **83 ms** on the laptop, which is why the splash can be
shown on every start instead of once.

> The DXGI call is four COM declarations and a `CreateDXGIFactory1`. The vtable
> order is the whole trick: `IDXGIFactory1` inherits `IDXGIFactory`, which
> inherits `IDXGIObject`, and a method in the wrong place calls the wrong
> function. `CheckDeviceSupport` is on `IDXGIAdapter`, not on the factory -
> declaring it there pushed `EnumAdapters1` one slot along, and the call
> returned `IsCurrent`'s answer instead of an adapter. The symptom was a scan
> that silently fell back to dxdiag and reported the shared system memory as
> 17.4 GiB of video memory.

### The rule

| The machine | The build |
|---|---|
| No display adapter, or under 3 GiB of video memory | **CPU only** |
| 4 GiB or more, NVIDIA | **CUDA** if the CUDA build is there, otherwise Vulkan |
| 4 GiB or more, anything else | **Vulkan** - one driver, every vendor |
| Apple silicon | **Metal** |

Every one of the four can be ticked by hand in the settings page, including the
ones the machine cannot run. The point of the page is to try a configuration, and
a refused checkbox cannot be tried; the cost of a bad choice is stated instead
of hidden.

### The profiles

A profile is a machine, remembered. It lives in
`%APPDATA%\notecode\hardware-profiles.json`, not in the checkout, because it is
a property of the machine and a repository that is synced between a CPU laptop
and a CUDA desktop must not carry one machine's GPU path to the other.

`runtime/hardware-profiles.seed.json` describes machines that have not run
notecode yet, so the RTX desktop can be set up from the laptop. A seed carries
a match rule ("NVIDIA with at least 6 GiB") and a whole configuration.

### The memory check

The page says whether the model fits, because on the 16 GB desktop it is close.
The model is 17.3 GiB and the card has 8 GiB, so about 7 GiB of the model lives
on the card and the rest is system RAM: 10.1 + 0.8 (KV at 16384) + 1.3 (the
runtime) = 12.2 GiB of the 15 GiB that 16 GiB of RAM leaves after the reserve.
It fits, with about 2.8 GiB to spare. At 32768 of context it would not.

### What the app does with the answer

The splash resolves the profile to a **build**, and the build is a different
`llama-server.exe`:

```
llama.builds.cpu    -> build-notecode\bin\Release\llama-server.exe  --device none
llama.builds.vulkan -> build-vulkan\bin\Release\llama-server.exe    --device vulkan
llama.builds.cuda   -> build-cuda\bin\Release\llama-server.exe      --device cuda
llama.builds.metal  -> build-metal\bin\Release\llama-server.exe     --device metal
```

The wrapper takes `-Exe`, `-Device` and `-NGpuLayers`, so the argv on screen is
the argv the server was started with. `runtime/build-llama-gpu.ps1` compiles the
extra builds, and it has to be run **on the machine with the card**: a CUDA
build is compiled for the GPU it finds.

### The status bar is the receipt

After a profile is applied and the app has restarted, the status bar reads
`server: up (CUDA, 99 layers on the GPU)` — not `server: up`. The backend is
the one thing that changes when a profile is applied, so the label that says
the server is alive has to say *which* server.

Two writers touch that label: `StartServerAsync`, which knows the build, and
the startup health check, which only knows whether the port answers. The
health check ran second and won, which is why the label lost its parentheses.
The backend now lives in a field that both writers read. `scripts/ui-hardware.ps1`
asserts the label, and the assertion was run against a build with the old
behaviour before the fix, where it failed with *the label says 'server: up'*.

## 4.21 The notification area, and the sessions

### Closing the window is not closing the app

A coding session is often left running: the model is loaded, the MCP servers are
connected, and the person closes the window to go and do something else. Paying
for that again on the next launch is the thing worth avoiding.

So closing the window puts the app in the notification area. The icon's menu is
the whole control surface then: **Open notecode**, **New session**, **Open
session...** and **Exit**. Left click opens the menu, double click brings the
window back.

The rule for what counts as "really leaving" is not `CloseReason.UserClosing`.
**A WM_CLOSE that arrives from outside - which is what the X button sends -
arrives as `TaskManagerClosing`.** Treating that as a real exit is what made the
first version of this close the app instead of hiding it, and the only reason it
was found is that the check was written to drive the window rather than to call
the method.

So the rule is the other way round: everything is a hide-to-tray *except* the
two reasons that mean the app is going away anyway - Windows is shutting down,
or the app itself asked to close (`_reallyExit`).

### One copy at a time

The app is in the notification area, so it is easy to end up with two: the one
in the tray, and one started by clicking the shortcut again. Two copies means
two llama-servers and two conversations, and the second one to start wins the
port.

`SingleInstance` takes a named mutex, which the operating system releases if
the process dies, so a copy that crashes does not lock the app out of its own
machine. The copy that does not get the lock signals the one that has it
through a named event, which brings the window forward, and then leaves.

The one race worth naming: the app can restart itself, and the replacement is
started *before* the old copy has gone. The lock is therefore given up before
the replacement is started, or the new copy takes itself for a duplicate.

### The sessions

A session is one conversation, in one workspace, with the turns that made it up.
They live in `%APPDATA%\notecode\sessions`, one JSON file per session, because
a conversation is a property of the person using it and not of the checkout it
happened in.

Three things about them are deliberate:

- **A session that never says anything is not written out.** A list full of
  empty "New session" entries is worse than no list, so the file appears the
  first time there is something in it.
- **The app carries on with the last conversation.** Starting the app again
  restores the most recent session for that workspace, so closing the window
  and opening it again does not lose the thread.
- **They are written on a timer, not on every turn.** One turn can add a lot of
  text and the file does not need to be current to the last character. The
  write itself is to a temporary file that is moved into place, so a crash
  halfway through cannot leave a half written session behind.

### 4.21a Where the user's data lives

The conversations and the saved hardware profiles are the user's, not the
checkout's, so they live in `%APPDATA%\notecode` and nowhere else. One place
decides that: `AppPaths.DataDir`.

`NOTECODE_DATA_DIR` moves it. The reason is the tests.

**The app puts the last conversation back on screen when it starts.** A test
that launches it against the user's own profile therefore reads the previous
run's conversation as if it were its own: a word that a check looks for in the
transcript can be left over from a run that finished an hour ago, and the check
passes without proving anything. The same tests also leave their own
conversations in the user's list to find later.

So the scripts point `NOTECODE_DATA_DIR` at a folder of their own, and every
test starts with an empty transcript. What a test reads is what the test
caused. The folder is also what a portable install would use to keep its data
beside the program instead of in the profile.

Note that `%APPDATA%` itself would not have done: `Environment.GetFolderPath`
on Windows goes through the known-folder API and does not follow the
environment variable, so the folder has to be chosen by the app.

## 4.22 The installer

`scripts/build-installer.ps1` produces one file, `Notecode-Setup.exe`, that
carries the app and the llama.cpp runtime it needs.

**The models are not in it.** The weights are 17 GB and more, they live where
the machine put them, and the app is pointed at one in the settings. An
installer that shipped a model would be a 20 GB download that is wrong the
moment there is a new one.

The payload is a zip embedded in the installer as a resource, so the result is
one file that can be copied anywhere and run. It installs per user, into
`%LOCALAPPDATA%\Programs`, which needs no administrator and does not fight
another version of itself.

The install is the layout the app expects:

```
<install>\notecode.exe
<install>\wwwroot\...            the editor and the terminal
<install>\runtime\...            the wrapper, the MCP server, the config
<install>\llama\llama-server.exe the runtime it serves the model with
```

`notecode.config.json` is found by walking up from the executable, so
`runtime\notecode.config.json` is the one the app reads. The installer rewrites
the llama paths in it to point at the installed build: the shipped configuration
points at the build machine's `C:\Llama`, and an installed copy that still
looked there would not run.

The shortcuts are made with the Windows Script Host, and the app is added to
the list of installed programs with an uninstall string. The uninstaller is the
installer itself with `--uninstall`: it removes the shortcuts, the registry
entry and the files, and leaves the conversations in `%APPDATA%` alone.

### The icon

One icon, everywhere: the window, the task bar, the notification area, the
shortcut and the executable.

`scripts/build-icon.ps1` draws it. The corners are rounded with a
**superellipse** rather than a circular-arc rounded rectangle, because that is
the shape that reads as "rounded" rather than "cut off": the curve keeps going
as it turns, so there is no corner to see.

Everything is drawn at the size it is needed, so the small sizes are clean
shapes rather than a shrunk photograph of a big one. Below 32 pixels the
strokes and the dots get proportionally heavier, because a one pixel line
between two three pixel dots disappears at 16.

The file is **embedded in the assembly** rather than shipped beside the
executable: a single file publish swallows loose content, and there is then no
file to find at run time.

## 5. MCP integration

### 5.1 llama.cpp's parser is stdio-only

`tools/server/server-mcp.cpp:140-173` — `parse_cursor_format` reads only:

```cpp
sc.command  = cfg.value("command", std::string());
sc.cwd      = cfg.value("cwd", std::string());
sc.timeout_ms = cfg.value("timeout_ms", sc.timeout_ms);
sc.args / sc.env
...
if (sc.command.empty()) {
    SRV_WRN("MCP server '%s' has no command, skipping\n", name.c_str());
    continue;
}
```

There is **no `url` branch**. An HTTP server placed in that file is dropped with a
warning. Confirmed empirically on the first launch:
`MCP server 'blender' has no command, skipping`.

`runtime/mcp-stdio-bridge.mjs` closes the gap: it speaks MCP on stdin, POSTs each frame
to the HTTP endpoint, and carries the `Mcp-Session-Id` from `initialize` onto every
later request. Verified by `scripts/test-mcp-bridge.mjs`, which spins up a mock
streamable-http server and asserts the relay and the session replay.

### 5.2 Two MCP clients, on purpose

- **llama-server** gets the servers via `--mcp-servers-config`, so the *model* can call
  REA's 170 tools directly.
- **notecode** has its own client (`McpHub`) that speaks stdio **and** streamable-http
  natively, so the UI can list and invoke tools itself — including HTTP servers that
  llama-server cannot reach.

### 5.3 REA

REA is already wired into kimi-code as an MCP server:

```json
{ "mcpServers": { "rea": { "command": "cmd.exe",
    "args": ["/d","/c","npx","-y","rea-agents@6.4.0","mcp"], "toolTimeoutMs": 600000 } } }
```

The same command is used here, so the tool surface is identical: **170 tools**, catalogued
in `rea/findings/rea-tool-catalog.json`.

> The `C:\rea-main` **source tree does not build**: `npm run build:cached` fails on a
> pre-existing type error in `src/android/JadxAnalysis.ts:366` (`JsonValue` index-signature
> incompatibility on `references[].direct_invokes_exhaustive`). The **published** package
> `rea-agents@6.4.0` ships a prebuilt runtime and works, so the published artifact is
> what notecode uses. The upstream source bug is untouched.

### 5.4 llama.cpp publishes tools, it does not run them

This is the part that is easy to get wrong. Reading the server source:

| Endpoint | What it does |
|---|---|
| `GET /tools` | the catalogue. Every entry has `tool` (name), `type` (`server` or `mcp`) and `definition` (an OpenAI function schema, verbatim). |
| `POST /tools` | dispatches **one** tool: body `{ "tool": "<name>", "params": {…}, "stream": false }`. Routes to the owning MCP server (`server_mcp_tool::invoke` → `server_mcp::call_tool`) or to llama.cpp's own implementation. |
| `POST /v1/chat/completions` | converts a **client-supplied** `tools` array into the chat template's tool block and returns `tool_calls` in the reply. It does **not** execute them. |

`tools/server/server-chat.cpp:266-293` only forwards a `tools` array that the client put in
the request body. Nothing in the server pipeline calls a tool on the model's behalf, and
there is no auto-loop: the client must dispatch the call and feed the result back.

**The symptom of getting this wrong is silent.** A request without `tools` returns fluent
prose and `usage.prompt_tokens = 26` — the model never saw the schemas, so it cannot call
anything, and the reply looks like a perfectly reasonable answer. This is exactly what the
first build did.

### 5.5 The tool loop

```
GET  /tools                        -> catalogue
POST /v1/chat/completions          -> {tools: [...], messages}      (schemas must be sent)
                                    <- {tool_calls: [{id, function:{name, arguments}}]}
POST /tools                        -> dispatch
POST /v1/chat/completions          -> messages + assistant(tool_calls) + tool(tool_call_id)
                                    <- final answer
```

Two details that are easy to miss:

- **Tool calls arrive as partial stream deltas.** `server_chat_msg_diff_to_json_oaicompat`
  emits the function name once and the arguments in fragments, so the client has to
  stitch them back together by index (`ToolCallAccumulator`). The `index` field is a JSON
  **string** in llama-server's stream, although the OpenAI spec says number — the parser
  accepts both.
- **The tool result goes back as a `role=tool` message with the original `tool_call_id`**,
  and the assistant turn that made the call has to be kept in the message list, or the
  template renders an orphan answer.

The chat template matters here too. MiniCPM's template renders a tool result as
`<|im_start|>user\n<tool_response>…</tool_response><|im_end|>` and an assistant tool call
as a `<tool_call><function=…><parameter=…>` block. It declares
`supports_tools` and `supports_tool_calls`, and llama.cpp's parser understands that format
in both directions — the round trip was verified end to end.

### 5.6 Which tools the model is given

The catalogue is larger than it was: **221 MCP tools + 7 built-ins** are advertised —
19 `notecode_*`, 170 `rea_*`, 26 `blender_*` and 6 `global-brain_*`. Advertising all of
them would cost most of a 32k context window in schema alone, on a model that is already
slow on CPU. So the app sends the built-ins plus the notecode tools by default, and the
170 REA tools are opt-in behind a checkbox.

Measured prompt cost of the default set: **3,871 tokens per turn** — 3,884 with the
schemas against 13 without, asked of the server itself by
`scripts/measure-prompt-cost.mjs`. The 26 tools are the 7 llama.cpp built-ins and the 19
`notecode_*` ones.

A BYOK provider is offered a different set: **every configured server, REA excepted** —
51 tools, the 19 notecode ones and the 26 Blender ones among them. The reason REA is left
out is its 170 schemas and its npx cold start; the reason the others are not is that a
provider that cannot see the Blender tools cannot drive Blender, and one skill should
work the same on both backends.

The nine that were added are the three-surface tools and the four skill tools (§4.16).

### 5.7 llama.cpp's MCP client cannot carry a large argument

This is the one that cost the most time to find, and it is not a notecode bug.

**One tool argument over roughly 4 KB wedges llama.cpp's MCP client.** The call never
reaches the MCP server — the server's own log shows nothing at all — and the client stops
answering. Every later call queues behind the one that was lost, so a single big argument
takes out every tool for the rest of the session, including the small ones.

It was found this way: the skill fixer asked the model to write a 7.6 KB skill, the call
vanished, and every diagnosis after that was wrong because the symptom was "the server is
not answering" rather than "this one call is too big". The MCP server's own trace log
(`runtime/mcp-trace.log`) is what settled it — it recorded the calls that arrived and the
ones that did not.

Measured, one server restarted for each size, so that one hung call did not poison the
next:

| Arguments | Result |
|---|---|
| 50 – 3,900 characters | answered, in about 200 ms |
| 4,000 characters and up | **hung** |

The request envelope — the method, the tool name and the path — is about 180 characters,
so the boundary is the **4,096-byte JSON-RPC request line**. The same size through
llama.cpp's own built-in tool is fine — 7,600 characters to `write_file` is answered in
105 ms. The limit is in the MCP client, not in the endpoint.

**The fix is the app's own client.** notecode already talks to the MCP servers itself
(§5.2), and it does it in a way that has no such limit. The local backend now dispatches
the MCP servers' tools through that client and only falls back to llama-server when the
app does not have the server connected. The model cannot tell the difference, and a
Blender script or a file of a few thousand characters now works.

The skill fixer writes through the app's client for the same reason, and for one more:
the model produces the content and the app puts it on the disk, so the content is checked
before it is written rather than after (§4.19).

---

## 6. Known limitations

| Limitation | Detail |
|---|---|
| The local model never thinks out loud | MiniCPM-V emits no `<think>` and no `reasoning_content`, so the reasoning line stays empty with the local model. A reasoning provider fills it — see §4.13 |
| RAM is the binding constraint | the model alone is 19.71 GiB of a 28.9 GiB machine; other apps must be closed or the model pages |
| Model load is slow | first load maps ~19.7 GiB from disk; expect tens of seconds to minutes |
| No AVX-512 | Zen2 lacks it; the build targets AVX2 deliberately |
| `--exec_shell_command` is enabled | the model can run shell commands in the workspace; disable it in `notecode.config.json` if that is not wanted |
| Dark theme only | deliberate: a light mode would double the surface area of a desktop IDE for no gain |
| The transcript renders a small markdown subset | fenced code, inline code, bold, headings and bullet lists. Everything is escaped first, so the page can never be injected into |
| The UI is owner-drawn, so the host's theme does not apply | the app paints its own contrast and legibility instead of inheriting it — which is also why it cannot follow a user's high-contrast setting |
| **The tab strip's contents are invisible to a screen reader** | the strip is one node called "Open files" with nothing inside it | the native tab provider owns the control and enumerates the *native* items, and an owner-drawn tab has none. The cross on each tab is a real control with its own name, but the tab is not. See `finalAudit.md` |
| **The custom painting does not scale with the display** | the marks, chevrons and tab strip are drawn for 100% | the app is `PerMonitorV2` and WinForms scales the control bounds, but the paint code draws at absolute pixel offsets. The two web surfaces scale properly, because the browser does it |
| **The AI's command does not run in the user's shell** | `run_command` runs it in a shell of its own, in the workspace | the user's shell is driven by PSReadLine, which cannot be handed text reliably, and a command that leaves a bracket open would strand it. The command and its output are still shown in the terminal pane (§4.16) |
| **The editor tools work on the tab that is open** | `set_editor_text` writes the open tab, or writes a named file and opens it | a second tab's unsaved buffer is not reachable; the app holds one editor |
| **A tool source is fixed when the server starts** | llama-server reads its MCP servers once | a server started before a tool existed does not know about it. **Run > Restart llama server** |
| **The skill fixer is slow on the local model** | a repair takes minutes | the model is a 35B mixture on CPU; the fixer is given the ground truth up front so it spends fewer turns on it |
| **llama.cpp's MCP client cannot carry a 4 KB request** | one tool argument over about 3,900 characters and the call is lost, and the client stops answering for the rest of the session | the app dispatches the MCP servers itself (§5.7). A call that goes through llama.cpp — a server the app has not connected — is still limited |
| **The Blender tools need Blender** | the 26 tools are live; there is no offline mode | the app retries the server every ten seconds and says so in the transcript. The vendored manuals are the ones that came with `C:\blender_mcp` and do not follow the running Blender |

---

## 7. Verification performed

| Check | Result |
|---|---|
| GGUF header parse (gguf-py) | arch, layer/head counts, tensor inventory confirmed |
| Wrapper `doctor` | exe/model/MCP/workspace resolve; RAM + KV budget computed |
| Wrapper `args` | full argv printed and reviewed against `--help` |
| llama-server MCP warmup | `rea` discovered 170 tools, `Added 170 MCP tools` |
| CPU_REPACK failure | reproduced and diagnosed to a 14.66 GiB allocation |
| **Rebuild with repack off** | `0.7.0-dev (build 677, commit ff5a233)` — `CPU_REPACK` symbol absent from the binary |
| **Model load** | 19.71 GiB mmap'd, **server healthy in 29.6 s** |
| **Chat completion** | `notecode-ok` — 7.0 s cold, 1.5 s warm |
| MCP stdio bridge | 3/3 checks pass against a mock streamable-http server |
| ConPTY variant sweep | 321 bytes through the pipe with `STARTF_USESTDHANDLES` (vs 16 without) |
| ConPTY shipping class | probe reports **PROBE OK**, marker `__notecode_probe_ok__` captured |
| **kimi-code.exe under ConPTY** | full TUI: truecolor, alt screen, mouse tracking, `ESC[>7u` keyboard protocol, interactive *Trust this folder?* prompt |
| .NET build | 0 errors, 0 warnings |
| `dotnet publish` | `dist/notecode.exe`, 49.66 MB, self-contained single file, valid PE |
| **notecode.exe launched** | window title `notecode`, live HWND, WebView2 child spawned, REA MCP child spawned |
| **Editor** | `Sample.cs` renders with syntax highlighting, line numbers, cursor tracking (`Ln 1, Col 1`) |
| **Terminal** | PowerShell 7.6.7 prompt live in the xterm pane |
| **MCP in-app** | status bar reads `mcp: rea, blender` |
| `scripts/smoke-test.ps1` | **13 PASS / 0 FAIL** |

### 7.1 The tool loop

| Check | Result |
|---|---|
| `GET /tools` | 228 tools advertised: 19 `notecode_*`, 170 `rea_*`, 26 `blender_*`, 6 `global-brain_*`, plus 7 built-ins |
| **`scripts/ui-ai-reach.ps1`** | the model reaches all three surfaces: it opens a file, reads the live buffer, writes into it and saves it, runs a command and gets the exit code, posts a note into the transcript and reads the transcript back — **10 PASS / 0 FAIL** |
| **`scripts/ui-addskills.ps1`** | a skill with no front matter and a tool that does not exist is repaired by the fixer, installed under the name of the file the user picked, and listed by `/skills` |
| **`scripts/ui-addtool.ps1`** | a tool source is written into both configs and the app connects to it at once — **7 PASS / 0 FAIL** |
| **`scripts/mcp-probe.mjs`** | calls the notecode MCP server the way the model does, so a tool that cannot reach the app shows up here |
| **`scripts/check-contrast.ps1`** | every text colour against every surface at 4.5:1, the marks at 3:1, the accent button's label against every fill it can have, and the font sizes — all read out of `Theme.cs` and `chat.html`, so a changed value cannot slip past |
| **`scripts/ui-a11y-audit.ps1`** | the real window through UI Automation: every control a person can use has a name, and every target is at least 24 pixels |
| **Model emits `tool_calls`** | `notecode_save_as {"path":"agents.md","content":"# Agents"}`, `finish_reason: tool_calls` |
| **`scripts/mcp-http-probe.mjs`** | the same, for a streamable-http server — the Blender one |
| **`scripts/ui-blender-skill.ps1`** | the Blender server is connected, the skill keeps the real tool names through the fixer, and it is installed and listed — **14 PASS / 0 FAIL** |
| **`scripts/find-mcp-arg-limit.ps1`** | llama.cpp's MCP client giving up: the size at which a tool argument is lost, measured against a server restarted for each size |
| **`scripts/measure-prompt-cost.mjs`** | what the tool schemas cost: **3,871 prompt tokens** per turn for the default set of 26 |
| Prompt cost of the curated set | 12 tools = **2,189 prompt tokens**; the default set of 26 = **3,871** |
| `POST /tools` dispatch | `Saved agents.md … .md (text/x-markdown) … opened in the notecode editor` |
| Tool result fed back | model answers *"The file `notes.md` has been saved…"*, `finish_reason: stop` |
| **`src/ToolProbe` (links the shipping code)** | turn 0 → `tool_calls`, turn 1 → `finish=stop` with the natural-language summary |
| **`scripts/ui-tool-smoke.ps1` (drives the real GUI)** | prompt typed into the AI box via UI Automation → `[tool] notecode_save_as` → file created → tab opened → rendered as markdown → status bar `md · text/x-markdown` |
| **The same script with `-SendWith Enter`** | the prompt box takes the keyboard focus, the Enter key submits the turn, the box empties, the model calls `save_as` and `gui-enter2.md` is created and opened |
| Editor type detection | `.md` → `text/x-markdown`, `.cpp` → `text/x-csrc`, `.html` → `text/html`; extension wins, content sniffing is the fallback |
| Extension hook | `save_as(path="main", type="cpp")` → `main.cpp`; 88 extensions, ~70 aliases |
| Full stack | `scripts/smoke-test.ps1` → **PASS 19 / FAIL 0**; `scripts/check-modes.ps1` → PASS |

### 7.1a The agent and the filesystem

`scripts/ui-agent-files.ps1` — **PASS 8 / FAIL 0**, driving the real GUI against the
local model:

| Check | Result |
|---|---|
| *"Make an HTML page called agent-page.html that says hello."* | the model calls `notecode_save_as`; the page is written, **147 bytes of real HTML** (`<!DOCTYPE html>`, `<title>`, `<h1>Hello!</h1>`) |
| the page opens as a tab | the cross `Close agent-page.html` is there — one cross per tab, named for the file |
| the cross closes the tab | the tab goes, the editor empties, no cross is left behind |
| *"Move agent-page.html into a new folder called website."* | the model calls `make_folder` then `move_path`; the folder is made and the file is inside it |
| the old place is empty | the file is gone from the workspace root |

### 7.1b The activity row and the reasoning

`scripts/ui-reasoning.ps1` — the mock provider thinks out loud, and the transcript shows
the thinking **on the line under the blinker**, inside the activity row, with the icon
above it. The row survives the answer: the blinker stops, the icon and the thinking stay.

| Check | Result |
|---|---|
| `reasoning_content` reaches the transcript | the marker is found in the WebView2's accessibility tree |
| the reasoning sits under the blinker | confirmed in `rea/findings/notecode-reasoning.png` |
| the row outlives the turn | the activity row is still in the transcript after the answer |
| the local model has no reasoning | measured: no `<think>` even with `enable_thinking=true` — the row carries the activity instead |

Screenshots of the running app are in `rea/findings/notecode-final.png` (full IDE),
`notecode-editor.png` (editor loading a file), `notecode-window-3.png` (terminal live),
`notecode-tool-loop.png` (the AI panel showing a live `save_as` call and the result),
`notecode-redesign-9.png` (the redesigned chrome) and `notecode-enter-send.png` (the
redesigned transcript with a tool card, after a prompt sent with the Enter key).

### 7.2 Bugs this work surfaced

| Bug | Symptom | Fix |
|---|---|---|
| MCP tools never reached the model | the model answered in prose, `prompt_tokens = 26` | the client must fetch `GET /tools` and send the schemas in the request body |
| Tool calls arrive fragmented | the call could not be dispatched | `ToolCallAccumulator` stitches the partial deltas by index; llama-server writes `index` as a **string** |
| Stale queue replayed at every launch | old files re-opened as tabs on start | the queue offset is primed to the file length at startup |
| AI-created files invisible in the tree | the tab opened, the project tree did not list it | `LoadExplorer()` after every watcher batch, preserving the selection |
| `.rs` rendered as plain text | the registry said `text/x-rust`, but CodeMirror registers `text/x-rustsrc` | registry corrected; `scripts/check-modes.ps1` now guards every MIME |
| `.tex` / `.bib` rendered as plain text | `stex.js` was never vendored | fetched from jsdelivr (CodeMirror 5.65.16) and added to `index.html` |
| A duplicate `fish` key in the registry | silent: `JSON.parse` keeps the last, `JsonDocument` yields both | removed; `scripts/check-modes.ps1` and the smoke test guard the file |
| **The Send button was invisible** | the button was in the layout and in the automation tree, but the opaque prompt box painted over it | `Controls.Add` appends to the back of the z-order, so the button is added first and brought to the front |
| **A ghost of each switch label in the row below** | a sliver of every label showed up under the switch it belonged to | `TextRenderer.DrawText` was called with `VerticalCenter` but no `SingleLine`; the caption was measured against a wrapped rectangle and drawn outside the control |
| **The AI panel overlapped the editor** when toggled from the menu | the split container filled the whole width | the View ▸ AI Panel handler called `BringToFront`, which reverses the docking order |
| The tab strip painted a white band | the stock control filled its client area with the system colour | the themed control draws its own strip |
| The tree and tab labels were ellipsised to nothing | the draw rectangle was built from the node's own label bounds | the rectangle has to run to the edge of the control |
| **Enter did nothing in the prompt box** | `AcceptsReturn` was false and no key handler existed | `Enter` sends, `Shift+Enter` inserts a line break; `SuppressKeyPress` keeps the newline out |
| **`global-brain` never connected** | `MCP server 'global-brain' exited` | `brain-system.exe` needs `--mcp`; without it it starts the tray app and exits. The config passed no arguments |
| The palette list was never drawn | the list was a `ListBox` that could not be themed | the palette paints itself, like every other surface in the app |
| The tree labels were ellipsised to nothing after the palette landed | the draw rectangle came from the node's own label bounds | the rectangle runs to the edge of the tree |
| **The local model overwrote the BYOK provider's model** | every turn to MiniMax went out as `notecode-minicpm` | only the local provider takes its model from the local server |
| **A BYOK turn waited for the 170 REA tools it discarded** | the turn never reached the provider | the hub lists the tools of the named servers only, and the result is cached |
| **The endpoint doctor's 404 was a false alarm** | Google's `v1beta/openai` was called unreachable | a 404 on `/models` is followed by a probe of the chat path |
| **The doctor offered to "fix" a correct base URL** | `api.deepseek.com` was suggested over `api.deepseek.com/v1` | a missing version segment is the same endpoint |
| **The doctor searched the wrong corpus** | "MiniMax API base URL" returned articles about the URL API | the doctor searches `general`, not the `it` programming corpus |
| **The capture picked a hidden window** | `window has no area (hwnd …, rect 0,0 0x0)` on a healthy app | `Process.MainWindowHandle` is a cached guess and can land on a 0×0 helper window; the windows are enumerated instead |
| **The keystrokes went nowhere** | the prompt stayed empty, the symptom looked like an app bug | the foreground is a system service window and cannot be taken; the keys are posted to the control instead |
| **The provider form clipped its captions** | "Auto update endpoints" ran past the edge of its button | the button column is wide enough for the caption; the hint is shortened and ellipsised |
| **The API style dropdown was a white box** | the native ComboBox paints with the system theme | `ModernComboBox` paints itself and opens a themed menu |
| **The reasoning vanished the moment the answer arrived** | the thinking was there for a second, then the row was gone | `hideThinking()` removed the whole row; it now stops the blinker and leaves the icon and the thinking |
| **The owner-drawn tabs were invisible to assistive technology** | `FindAll(TabItem)` returned zero with a tab open and painted | the native tab provider enumerates native items, and an owner-drawn tab has none; the cross is a real control with its own name |
| **A cross was left on screen after its tab closed** | a stray × in an empty strip | the tab count changes before the removal is reported, so the sync also runs from `OnPaint` |
| **The app died with an unhandled exception** | a "Microsoft .NET" crash dialog over the IDE | WinForms refuses non-`TabPage` children of a `TabControl`; the crosses moved to the panel that holds the strip, and the sync is guarded |
| **The GUI test waited three seconds for a sixty-second turn** | "the page was written" failed while the model was still thinking | the test waited for the *prompt box* to empty, and it empties the moment the turn is accepted; it now waits for the result |
| **The REA server registered zero tools** | the catalogue had 10 tools instead of 180, with no error | llama.cpp caps the MCP warmup at `MCP_WARMUP_TIMEOUT_SECONDS = 10` and a cold npx cache spends it; the wrapper primes the cache first |
| **The paperclip came out as a violet pill** | the button wore the system's accent colour instead of the app's mark | a themed WinForms `Button` paints itself and takes `OnPaint` away from the app; the mark is a plain `Control` now |
| **The paperclip was a two pixel dot** | nothing was painted where the button was | the 24 unit grid was divided by 24 twice, so the shape came out at 1/24 of its size |
| **The chip lost the first letters of the file name** | the attachment read `ch-check.md` instead of `attach-check.md` | WinForms docks in the reverse of the z-order, so the `Dock.Fill` chip took the whole row and the paperclip landed on top of the label. The chip is added first now, and the paperclip is **not** brought to the front — `BringToFront` would move it back to the front of the z-order, which puts it at the end of the docking order and puts it on top of the chip again |
| **The AI's command never finished in the terminal** | every command timed out after 60 s and the output was full of `>>` and escape codes | the shell is driven by PSReadLine, a line editor that cannot be handed text: it answers with a continuation prompt and swallows the marker line. The command now runs in a shell of its own, and the escape codes are stripped |
| **The Blender server never published a tool** | `Cannot access a disposed object. Object name: 'JsonDocument'` | the HTTP MCP client returned an element from a document it had already disposed; it clones now, like the stdio client |
| **A BYOK provider could not reach Blender at all** | the provider was offered the notecode tools and nothing else | every configured server is offered, REA excepted — its 170 tools are an npx cold start |
| **The tools were offered under two different names** | a skill naming `blender_execute_python` worked on one backend and not the other | the catalogue publishes the tools with the server name in front, and so does the app now |
| **The fixer would have taken the real tools out** | a Blender skill was told to remove the tools it names | the fixer's tool check knows both spellings |
| **One argument over 4 KB took out every tool** | the MCP server stopped answering, and the symptom was "the server is dead" | llama.cpp's MCP client cannot carry a request line over 4,096 bytes, and the call that does not fit is lost with no answer; the app dispatches the MCP servers through its own client |
| **The fixer could not write a skill at all** | the write never arrived and the run timed out | the fixer wrote through the model's backend, which is the 4 KB path; the app writes now, and checks the content before it lands |
| **The fixer renamed the skill** | `notecode-reach-test-skill.md` was installed as `notecode-reach-test` | the name is the app's to decide: the name of the file, or of the folder |
| **The editor's editing surface had no name** | an unnamed `Edit` in the focus order | CodeMirror's `screenReaderLabel` is set, so the hidden textarea is announced as "Code editor" |
| The page thought it had unsaved edits after a save | the tab lost its dirty dot on the next keystroke | `requestContent` now clears the page's dirty flag; it did not, so the page and the app disagreed about the buffer |
| The path of a skill kept its quotes | `There is no file at dist\"C:\…\"` | the slash parser hands the argument over as it was typed, so the path is unquoted with the same tokenizer `/addtool` uses |
| **The fixer could not find its own tools** | "The skill fixer's tools are not available" | llama-server reads its MCP servers once, when it starts, so a server that was started before a tool existed does not know about it. The app says so and points at **Run > Restart llama server** |
| **The fixer took ten minutes to say anything** | the first turn had not finished after ten minutes | `list_skills` put all 124 installed skills into the prompt — 8,000 tokens. The app fetches the list itself and keeps three of them, and gives the model the ground truth instead of letting it fetch that too. The first turn is now **4,878 prompt tokens** for a small skill, **6,941** for the 7.9 KB one |
| **The skill was installed under the wrong name** | it went into a folder called `workspace` | the name was taken from the file as it was *before* the repair. The name is the app's to decide now: the name of the file the user picked, or of the folder for a skill that is already in one |
| **The quiet text was not quiet enough** | 3.32:1 where WCAG AA wants 4.5:1 | `Theme.TextDim` was `#5F6980`. It is now `#7E8AA0` — 4.57:1 on the worst surface — and `scripts/check-contrast.ps1` keeps it there |
| **The Send button's label was unreadable** | white on the violet gradient, 2.72:1 at the bright end | the label now follows the fill: near-black on the gradient, white on a flat dark button |

**Three traps that cost the most time in the test harness, all of them PowerShell:**

| Trap | Symptom | Fix |
|---|---|---|
| `f($a, $b)` | one array argument, not two | use command syntax: `f $a $b` |
| `$input` | the assignment was ignored | `$input` is an automatic variable; name it something else |
| returning an automation element from a function | the caller got an enumerator | store it in a script-scoped variable instead |

### 7.3 Guards added

| Script | Guards against |
|---|---|
| `scripts/check-modes.ps1` | a registry MIME with no vendored CodeMirror mode (silent plain-text fallback) |
| `scripts/smoke-test.ps1` | 20 layers, from the config file to the PE image to the tool catalogue |
| `scripts/ui-tool-smoke.ps1` | the GUI loop regressing — it drives the real window through UI Automation, with `-SendWith Click\|Enter` so both send paths are covered |
| `scripts/ui-agent-files.ps1` | the agent and the filesystem regressing — a page the model writes opens as a tab, the cross closes it, and a file the model is told to move really moves into a folder it makes |
| `scripts/ui-tab-close.ps1` | the cross regressing — it opens a file through the queue, clicks the cross and checks the tab is gone. No model needed, so it runs in under a minute |
| `scripts/ui-attach-file.ps1` | the attachment regressing — a file the user attaches is read by the model, and the word in the file comes back |
| `scripts/ui-reasoning.ps1` | the reasoning line regressing — a provider that thinks out loud fills the line under the blinker |
| `scripts/ui-command-smoke.ps1` | the palette regressing — it opens, narrows, survives the arrow keys, and runs `/help` and `/providers` |
| `scripts/ui-provider-form.ps1` | `/addprovider` regressing — the form opens and is captured |
| `scripts/ui-byok-turn.ps1` | the BYOK path regressing — a real turn goes to a local mock endpoint with the right URL, token, model and tools |
| `scripts/ui-vision.ps1` | the vision path regressing — a picture is attached, the model sees it, and the answer names what is in it |
| `scripts/ui-layout-check.ps1` | the AI panel overlapping the editor when it is toggled |
| `scripts/ui-ai-reach.ps1` | the three surfaces regressing — the model opens a file, reads the live buffer, writes into it, runs a command, posts a note into the transcript and reads the transcript back. It drives the same MCP server the model goes through |
| `scripts/ui-addskills.ps1` | the skill fixer regressing — a broken skill is repaired, installed and listed by `/skills` |
| `scripts/ui-blender-skill.ps1` | the Blender path regressing — the server is connected, the skill keeps the real tool names through the fixer, and it is installed and listed |
| `scripts/mcp-http-probe.mjs` | the HTTP MCP client regressing — it lists the tools of a streamable-http server the way the app does |
| `scripts/measure-prompt-cost.mjs` | the tool schemas costing more than they should — it asks the server the same question with and without them |
| `scripts/mcp-probe.mjs` | the app channel regressing — it calls the notecode MCP server the way the model does, so a tool that cannot reach the app shows up here |
| `scripts/check-contrast.ps1` | the colours regressing — it reads them out of the source, so a colour that was changed cannot pass a check that still remembers the old one |
| `scripts/ui-a11y-audit.ps1` | the accessibility regressing — it drives the real window and fails on a control without a name or a target under 24 pixels |
| `scripts/find-mcp-arg-limit.ps1` | llama.cpp's MCP client giving up on a large argument — and the client staying wedged afterwards |
| `scripts/run-suite.ps1` | the whole suite in one command, one script at a time, because the GUI scripts cannot run at the same time |
| `src/ToolProbe` | the tool loop regressing — it links the shipping `LlamaServer` source. It also carries `--commands` (the parser, the provider store, the doctor helpers, the GGUF header) and `--doctor` (the doctor against the live gateway) |

---

## 8. Build recipe for the bundled llama.cpp

Run once; the result is a `llama-server.exe` that can load this model on a 28.9 GiB
machine.

```powershell
cmake -S C:\Llama\llama.cpp -B C:\Llama\llama.cpp\build-notecode `
  -DCMAKE_BUILD_TYPE=Release `
  -DGGML_CPU_REPACK=OFF `
  -DGGML_NATIVE=OFF `
  -DGGML_AVX=ON -DGGML_AVX2=ON -DGGML_FMA=ON -DGGML_F16C=ON `
  -DGGML_AVX512=OFF -DGGML_AVX512_VBMI=OFF -DGGML_AVX512_VNNI=OFF `
  -DGGML_VULKAN=OFF -DGGML_CUDA=OFF -DGGML_HIP=OFF -DGGML_OPENMP=ON `
  -DLLAMA_BUILD_TESTS=OFF -DLLAMA_BUILD_EXAMPLES=OFF `
  -DLLAMA_BUILD_SERVER=ON -DLLAMA_BUILD_TOOLS=ON -DLLAMA_BUILD_MTMD=ON `
  -DLLAMA_BUILD_COMMON=ON -DLLAMA_BUILD_APP=OFF

cmake --build C:\Llama\llama.cpp\build-notecode --config Release --parallel 16
```

Confirm the fix took:

```powershell
Select-String -Path C:\Llama\llama.cpp\build-notecode\bin\Release\llama-server-impl.dll `
  -Pattern 'ggml_backend_cpu_repack_buffer_type' -SimpleMatch
# no match => repack is compiled out
```
