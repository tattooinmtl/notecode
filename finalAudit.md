# notecode — final audit

**The app.** `C:\NexusOne\prototypes\notecode` — a local-model coding IDE in C#, WinForms and
WebView2, sitting on a llama.cpp server. Workspace explorer on the left, CodeMirror editor
with tabs in the middle, a real ConPTY terminal at the bottom, the agent conversation on the
right, a status bar underneath.

**The bar.** Microsoft's official WinForms and .NET desktop guidance, accessibility rules
included. The guidance was fetched and read, not paraphrased from memory — the sources are
listed at the end.

**The scope.** Fix and audit. The loop may change the app, and then re-audit until the
result holds.

**The measurable half.** The accessibility and UX gaps the current test suite misses. Every
gap below is one that **no script in `scripts/` caught before this pass**, and every fix
lands with the check that now catches it.

**The second pass.** The first pass was about how the app looks and announces itself. The
second was about whether the model can do what the app says it can — the terminal, the
editor and the chat, on the local backend and on a provider alike. It found six more, and
one of them was severe enough to take out every tool in the app.

---

## The verdict

Two passes were made. The first was the accessibility pass: **five gaps found, five fixed,
five guards added**. The second was the pass over the app's *reach*, and it found **eight
more** — of which one was severe, and two were the audit's own guard rails.

The second pass is in its own section below, because the two are different kinds of
finding: the first was about how the app looks and announces itself, the second was about
what happens when the model tries to act.

| # | Gap | The rule it broke | Before | After | The check that now catches it |
|---|---|---|---|---|---|
| 1 | The quiet text colour was used for text that carries meaning | WCAG 2.2 SC 1.4.3 (AA) — 4.5:1 for text | 3.32:1 on the surface, 2.89:1 on a hovered row | 4.57:1 on the worst surface | `scripts/check-contrast.ps1` |
| 2 | The Send button's label was unreadable | WCAG 2.2 SC 1.4.3 (AA) | 2.72:1 at the bright end of the gradient | 4.70:1 to 11.01:1 | `scripts/check-contrast.ps1` |
| 3 | The body text was below the floor | Microsoft's WinForms rule: 10 point unless the size is the user's to change | 9.5 point, not adjustable | 10 point, and adjustable | `scripts/check-contrast.ps1` |
| 4 | Four controls had no accessible name | WCAG 2.2 SC 4.1.2 (A) — name, role and value | "tree", "tab", "edit", with nothing else | named | `scripts/ui-a11y-audit.ps1` |
| 5 | Two checkboxes were a pixel under the smallest target | WCAG 2.2 SC 2.5.8 (AA) — 24×24 | 396×23 | 396×25 | `scripts/ui-a11y-audit.ps1` |

The two guards are new, and they read their numbers **out of the source**: the colours and
the font sizes come from `Theme.cs` and `chat.html`, so a value that is changed cannot slip
past a check that still remembers the old one.

---

## 1. The quiet text colour

`Theme.TextDim` was `#5F6980`.

| Surface | Ratio | AA for text (4.5:1) |
|---|---|---|
| Surface `#11151F` | 3.32 | fail |
| SurfaceRaised `#161B27` | 3.13 | fail |
| SurfaceHover `#1C2230` | 2.89 | fail |

It is used where the text carries meaning and not decoration:

- the transcript's **speaker label** (`.who`, 10.5px) and the **tool kind** label
  (`.tool .kind`, 9.5px) — both `--dim` in `chat.html`;
- the **command palette's** summary column for the rows that are not selected, and its
  **footer hint**;
- the **label of an unchecked box** — `on ? Theme.Text : Theme.TextDim`. An unchecked box
  is not a disabled control, so the rule about exempting inactive components does not
  apply to it;
- the **AI panel's subtitle** and the **status bar's** MCP line when nothing is connected;
- the provider form's **token state** and its **hint**.

`TextDim` is now `#7E8AA0` — **4.57:1** on the worst surface it is painted on. One colour
fixed eight places.

## 2. The Send button's label

`AccentButton` paints a violet gradient and writes the label in white.

| Fill | White on it |
|---|---|
| VioletBright `#A78BFA` | 2.72:1 |
| Violet `#8B5CF6` | 4.23:1 |
| Cyan `#22D3EE` (the hover gradient) | 1.81:1 |

The Send button is the primary action of the app and its label sat at about 3.4:1.

One label colour does not work on both of the things this button can be filled with: the
bright gradient, and a flat `SurfaceRaised` panel (which is what Cancel and Auto-update
use). So the label follows the fill:

```csharp
private static Color LabelOn(Color a, Color b)
{
    double lightest = Math.Max(Theme.RelativeLuminance(a), Theme.RelativeLuminance(b));
    return lightest >= 0.177 ? Theme.Void : Color.White;
}
```

The threshold is where the two swap: a near-black label clears 4.5:1 from a background of
luminance 0.177 and above, and a white one clears it from 0.183 and below. The result is
**4.70:1 to 11.01:1** on the gradient states and **17.21:1** on a flat button.

## 3. The size of the text

Microsoft's rule for WinForms is that text must be at least **10 point** unless the size is
adjustable by the person using it. notecode's body text was 9.5 point and the size was not
adjustable at all.

Both halves are now true. The body text is **10 point**, the quiet text and the code text
are **9 point** (12 pixels, the Windows 11 minimum), and every size is scaled by a factor
the user owns:

```
runtime/notecode.config.json -> "ui": { "fontScale": 1.0 }
/fontsize 1.2        shows the current factor, or sets it
```

## 4. Controls with no name

Driven through UI Automation against the real window, four controls a person can use had
no accessible name. A screen reader announces the role and nothing else.

| Control | Was | Now |
|---|---|---|
| the project explorer | "tree" | "Project files" |
| the tab strip | "tab" | "Open files" |
| the prompt box | "edit" | "Message to the model" |
| the status bar | "status bar" | "Status" |

The prompt box is the one that mattered most: it is the box the person types their
question into, and it was the only text field in the app with no name.

## 5. Targets under the smallest size

The two checkboxes in the agent panel were **23 pixels** tall. Microsoft's floor is 24.
They are 25 now.

---

## The second pass: the AI's reach, and Blender

The accessibility pass was about the app. This one is about the app's **reach**: whether the
model can actually do what the app says it can, on the local backend and on a provider
alike. It was run against the Blender MCP server that is running on this machine
(`http://127.0.0.1:8765/mcp`, 26 tools).

Five defects were found. All five are fixed, and four of the five now have a guard behind
them.

| # | Defect | What it meant | Fix | The check |
|---|---|---|---|---|
| 6 | The HTTP MCP client returned a disposed `JsonElement` | the Blender tool list **always** failed: *"Cannot access a disposed object"* | `result.Clone()` | `scripts/mcp-http-probe.mjs` |
| 7 | A BYOK provider was offered the notecode tools and nothing else | a provider could not drive Blender at all | every configured server is offered, REA excepted | `scripts/ui-byok-turn.ps1` |
| 8 | The tools were offered to a provider under their bare names | a skill naming `blender_execute_python` worked on one backend and not the other | the prefixed name, the same the catalogue uses | `scripts/ui-byok-turn.ps1` |
| 9 | The skill fixer's tool check only knew bare names | the fixer would have **taken the real tools out** of a Blender skill | both spellings are known | `scripts/ui-blender-skill.ps1` |
| 10 | The fixer wrote the file back unchecked | a slow model could lose a good skill without anything noticing | the rewrite is checked, and refused if it is worse | `scripts/ui-blender-skill.ps1` |
| 11 | The local backend's MCP tool calls went through llama.cpp's MCP client | **one argument over ~4 KB took out every tool for the rest of the session** | the app dispatches the MCP servers itself | `scripts/find-mcp-arg-limit.ps1` |
| 12 | The editor's editing surface had no name | WCAG 2.2 SC 4.1.2 (A) — the browser's hidden textarea was an unnamed edit box | "Code editor" | `scripts/ui-a11y-audit.ps1` |
| 13 | The model could rename the skill under the user's hand | a skill the user picked as `my-skill.md` was listed as something else | the app owns the name; the model owns the description | `scripts/ui-addskills.ps1` |

### 6.1 A disposed document

`HttpMcpClient.RequestAsync` returned a `JsonElement` that belonged to a `JsonDocument` it
had already disposed. The stdio client was right and cloned; the HTTP client did not. The
symptom was not a crash — it was a server that never once published a tool.

### 6.2 The provider could not see Blender

The BYOK path built its tool list from the notecode server alone. The reason was sound
(REA is an npx cold start that publishes 170 tools, and a turn that does not want them
should not wait for them), but the rule was too wide: it excluded every server, not just the
slow one. A provider was given no way to reach Blender.

The rule now is: every configured server, REA excepted. The tools are offered **with the
server name in front** — `blender_execute_python`, `notecode_run_command` — which is how
llama-server publishes them, so one skill works on the local backend and on a provider.

### 6.3 The fixer would have taken the real tools out

The fixer checks that a skill does not name a tool the app does not have, and it did that
against the **bare** names. The catalogue publishes `blender_execute_python`; the server
publishes `execute_python`. A skill that named the real tool was told to take it out.

This one is worth being precise about, because it was invisible: the fixer's own list of
known tools was assembled correctly, and the check was still wrong, because the two
spellings were never reconciled.

### 6.4 The fixer could make a skill worse

The fixer asks the model to write the whole file back. On a model that takes five minutes
to say 1,900 tokens, that is a long way to lose something: the front matter, a table of
tools, half the instructions. Nothing was checking.

The rewrite is now checked before it is kept. It is refused when it has no front matter,
when it names a tool that is not there, or when it keeps less than a third of a body that
was already well formed. A refused rewrite is put back and the model is told why, so it
can try again. A skill that was already broken is still free to change shape — that is
what the fixer is for.

### 6.5 The fixer spent its turns on the wrong tools

The fixer was given `list_skills` and `describe_notecode` as tools, while the app was
already putting both in the prompt. The model asked for the list of installed skills
again: 124 skills, about 8,000 tokens, a whole turn on a model where a turn is minutes —
three turns of the six, on the first real run.

The tool list is now the two the job needs: `read_skill` and `write_skill`. The ground
truth and the house style come in the prompt, where they cost nothing but a prefill. The
fixer also writes each tool call to the log now, so a run that goes wrong can be read back
instead of guessed at.

### 6.6 One argument over 4 KB took out every tool

The one that cost the most time, and the one whose symptom pointed somewhere else
entirely.

The skill fixer asked the model to write a 7.6 KB skill. The call never arrived — the MCP
server's own log showed no request at all — and from then on **nothing** answered: not
the write, not the reads, nothing. The symptom was "the server is dead", and every
diagnosis that followed was wrong because the question was never "is this one call too
big".

It is in llama.cpp's MCP client. One tool argument over roughly 4 KB and the call is
lost, and the client stops answering: every later call queues behind the lost one, so a
single big argument takes out every tool for the rest of the session. The same size
through llama.cpp's own built-in tool is fine — 7,600 characters to `write_file` is
answered in 105 ms.

Measured with a server restarted for each size, so that one hung call did not poison the
next: 50 to 3,900 characters of arguments are answered in about 200 ms; 4,000 and up
hang. The request envelope is about 180 characters, so the boundary is the **4,096-byte
JSON-RPC request line**. The same size through llama.cpp's own built-in tool is fine —
7,600 characters to `write_file` is answered in 105 ms.

**The fix is the app's own client.** notecode already talks to the MCP servers itself,
in a way that has no such limit. The local backend now dispatches the MCP servers' tools
through that client, and falls back to llama-server only when the app does not have the
server connected. The model cannot tell the difference, and a Blender script or a file of
a few thousand characters now works.

The skill fixer writes through the same door, and for one more reason: the model
produces the content and the app puts it on the disk, so the content can be **checked
before** it is written rather than after.

### 6.7 The editor's editing surface had no name

The audit found an unnamed `Edit` of 1002×16 in the editor pane. It is CodeMirror's own
hidden textarea — the element the browser takes the keystrokes in. CodeMirror 5 supports
`screenReaderLabel`, which puts an `aria-label` on it, and the app now sets it:

```js
screenReaderLabel: 'Code editor',
```

It is in the focus order, so a screen reader walks straight into it. A name costs nothing
and the surface is the app's most-used one.

The audit also learned something about itself. The tree's scroll bar arrow buttons showed
up as three targets of 17×17 — under the 24-pixel floor — and the tree had one more file
in it than the last run. They are the **system's** scroll bar, at the system's size, and
the app does not draw them. The audit now asks a control for its parent and does not count
the scroll bar's own buttons; it says so in the report rather than dropping them
silently.

### 6.8 The model could rename the skill

The fixer is asked to write the file back, front matter and all, and the app then installs
the skill under the name it finds there. The model renamed a test skill from
`notecode-reach-test-skill` to `notecode-reach-test` — a small thing, and the skill
worked. But the user picked a file called `notecode-reach-test-skill.md`, and a skill that
answers to another name is a skill the user cannot find again.

**The name is the app's to decide.** It is the name of the file the user picked — or the
name of the folder, for a skill that is already in one, because the file inside a skill
folder is always `SKILL.md`. The model writes the description and the shape; the name is
put back if it was changed, and the fixer is told the name in the prompt so it does not
have to be corrected.

### 6.9 Two failures that were the suite's own

The suite failed three times on this pass. One was finding 12. The other two were the
suite's fault, and they are worth writing down because both looked like product bugs.

**A test left the app pointed at a provider that was not running.** The BYOK test points
the app at a mock provider and takes it away again at the end. The removal did not always
take, and the next test — one that expects the local model — spent five minutes talking
to a dead endpoint and reported that the agent had not written its file. The fix is
structural: every test that talks to the model now says which provider it wants before
the app starts (`Use-LocalProvider` in `winutil.ps1`), and the tests that use a mock say
so in their checks instead of asserting `true`.

**A test asserted that a file existed without saying where.** The skill test looked for a
folder with the skill's name in it, and the app had installed the skill under the name the
model had chosen. The test was right and the app was wrong; see 6.8.

---

## 6.10 The hardware pass - fixed

The app now scans the machine before it opens and picks the llama.cpp build that
runs on it. Three things went wrong while it was being built, and all three are
worth writing down because none of them announced itself.

### 6.10.1 Windows reports the wrong video memory twice

`Win32_VideoController.AdapterRAM` is a 32-bit field, so an 8 GiB card reports
4 GiB and a 16 GiB card reports the same 4 GiB. `dxdiag` has the same problem in
another shape: it reports the **shared** system memory on an integrated GPU as
"Display Memory", so the laptop in this room looked like it had 17.4 GiB of
video memory. It has none - it has 3.0 GiB of a UMA carve-out and 14.5 GiB of
the same system RAM.

The scan reads **DXGI** now, and it takes 83 ms instead of 18.6 seconds. DXGI
answers the question directly and can tell a card's own memory from a borrow:
when the shared pool is at least as big as the dedicated one, the adapter is
sharing, and the memory is system RAM either way.

### 6.10.2 The COM call was silently calling the wrong function

`IDXGIFactory1` inherits `IDXGIFactory`, which inherits `IDXGIObject`, and the
vtable order is the whole game. `CheckDeviceSupport` is a method of
**IDXGIAdapter**, not of the factory; declaring it on the factory interface
pushed `EnumAdapters1` one slot along, so the call returned `IsCurrent`'s
answer - `S_FALSE`, which reads like "no adapters" - and the scan fell back to
dxdiag.

The failure was invisible: the scan still returned numbers, they were just the
wrong ones. It took a direct probe of the HRESULTs to see it.

### 6.10.3 A profile row that was drawn outside its list

The saved profiles were listed in a `FlowLayoutPanel`, and the row was laid out
**below the panel** - at y=861 when the panel ended at y=858 - so it landed on
top of the Apply button. A `TableLayoutPanel` with one row per profile puts it
where it belongs.

### 6.10.4 A comment inside a dictionary

The build map in `notecode.config.json` had a `$comment` key inside it, and the
map deserialises into a `Dictionary<string, LlamaBuildDef>`. The comment is a
string, so the whole configuration failed to load and the app said so with a
dialog on startup. The comment moved out of the map.

### 6.10.5 The status bar forgot which build was running

The status label was written twice, and the second writer won. `StartServerAsync`
puts the backend in it — `server: up (CUDA, 99 layers on the GPU)` — and the
startup health check rewrote the same label a moment later as a bare
`server: up`. The app was running the right build the whole time; the status
bar just stopped saying which one, which is the only thing it is there to say
after a profile has been applied.

It was found by accident. The accessibility audit prints every control in the
window, and the status strip was in the list as `server: up` — 58 pixels wide,
which is about the width of the text with no parentheses in it.

The backend now lives in a field that both writers read, so the poll cannot
strip it. `scripts/ui-hardware.ps1` checks the label says `server: up (CPU)`,
and the check was run against a build with the old behaviour first: it failed
with *the label says 'server: up'*, and passed once the fix was back.

All five are fixed, and `scripts/ui-hardware.ps1` checks the result: the splash
appears, the scan finds the processor, the page offers the four builds, the
status bar names the backend, and the profile is saved.

### 6.10.6 A repeated key in the config file silently turned off every path in it

The config file carries paths into the user's own folders written as
`%USERPROFILE%\.brain-skills\skills`, and every reader of it expands them: the app, the
PowerShell wrapper, the MCP server, the scripts. The app's expansion was a pass over the
*text* of the file — parse it into a node tree, walk it, expand every string, write it
back.

A configuration file with the same key twice is legal JSON but not a legal node tree.
`runtime/notecode.config.json` had two `"$comment"` keys in its `memory` section, so the
parse threw *an item with the same key has already been added*, the `catch` around the
whole pass swallowed it, and the file was used exactly as it was written.

Nothing failed and nothing was logged. The skills root stayed
`%USERPROFILE%\.brain-skills\skills`, and `Path.Combine` turned that into a folder named
after the variable — a folder literally called `%USERPROFILE%`, created under the
working directory. `/addskills` installed the skill into it. `/skills` looked in the
real place and did not find it.

The failing check said *"the skill was installed — it is in a skill folder"*, and the
skill *was* installed, into a folder with a variable in its name. The other checks in the
same run passed, because they did not care where the skill went.

Two things are fixed.

The expansion happens on the way in now, as each string is read, with a
`JsonConverter<string>`. A reader that takes the last of a repeated key does not mind
one, and a converter sees every string without a list of the fields that hold a path.

The file itself no longer has the repeated key.

The guard is `scripts/ui-config.ps1`, and it is fast: the app is asked what it resolved
(`notecode.exe --print-config`), and the answer is checked for a path that is still a
variable. It was run against a build without the expansion first, and it failed with
*still unexpanded: %USERPROFILE%*; it passed once the fix was back. A guard that has only
ever passed proves nothing.

### 6.10.7 A guard that had stopped guarding

The accessibility audit had a rule about the size of the things a person has to hit. It
excluded the scroll bar's own arrow buttons, because they are the system's, at the
system's size, and the app does not draw them.

The exclusion read the parent of the control to see whether it was a scroll bar. It used
`AutomationElement.Parent`, and on this window that answers with an element whose control
type is **empty**. So the parent was never `ScrollBar`, the exclusion never fired, and
the system's own arrow buttons were counted against the app — three failures that had
nothing to do with the app and that only appeared once the workspace held enough files
for the tree to have a scroll bar at all.

It was the `TreeWalker` that answered correctly all along. The audit uses it now, and
walks the whole chain rather than the direct parent, because where the buttons hang off
the scroll bar is up to the control that owns it.

Two other rules in the same audit were reading the same empty chain: a list inside the
transcript was reported as a control with no name, when it is the text the app is showing
— its items carry the content, and a screen reader reads them one by one.

And the version check in the tray script was a tautology: it compared the literal
`0.2.0` against a regular expression, so it passed whatever the build did. It reads the
version out of the executable now and checks that the transcript shows that one.

### 6.10.8 The tests were reading each other's conversations

The app puts the last conversation back on screen when it starts — that is the feature,
and it is a good one. The tests launched the app against the user's own profile, so
every GUI test started with the previous test's conversation on screen.

A check that looks for a word in the transcript could therefore find a word that a run
from an hour ago had left there, and pass without proving anything. The checks that
looked for *the last run's* markers — the skill fixer's report, the MCP server's
connection message — were the ones at risk, and they were passing for a reason that had
nothing to do with what they claimed to check.

`NOTECODE_DATA_DIR` now points the app at a folder of the test's own, so every test
starts with an empty transcript. What a test reads is what the test caused. It also
stops the tests from writing their conversations into the user's list to find later.

The same investigation found that the Blender checks were failing for a reason that was
not notecode's: Blender is a desktop application of its own, and its server only listens
while it is open. Not having it open is not a fault of the app, so those checks skip
instead of failing — the same way the BYOK turn treats it. The check for the connection
now looks for the message the app writes when it connects, rather than for the server's
name, which appears in the failure message too.

---

These are real, and they are not fixed. Each one says what it would take.

### The tab strip's contents are invisible to a screen reader

The strip is a `TabControl` that draws itself, and the native tab provider owns the
control — so it enumerates the **native** items, and an owner-drawn tab has none. The
cross on each tab is a real control with its own name (that was fixed earlier), but the
tab itself is one empty node: **"tab, Open files"** and nothing inside it. A screen reader
cannot tell which files are open.

The fix is the one that worked for the cross: a real control per open file, carrying the
name, instead of painted text. It is a change to how the strip is built, and it is the
biggest single piece of work left.

### The app cannot follow a high-contrast theme

Every surface is painted by the app, with colours the app chose. A person who turns on a
high-contrast theme in Windows gets the app's colours, not the system's. Microsoft's
guidance is to paint with `SystemColors` and to respond to
`SystemEvents.UserPreferenceChanged`.

This was a deliberate choice — the app is a dark IDE and the palette is part of its
identity — but it is a genuine gap, and the honest version of it is: **this app is not
usable for someone who needs a high-contrast theme.** Closing it means making the palette
a theme-aware resource rather than a set of constants, which touches every painted
surface.

### The custom painting does not scale with the display

The app declares `PerMonitorV2`, and WinForms scales the bounds of the controls, but the
paint code draws at absolute pixel offsets. On a display that is not at 100%, the marks,
the chevrons and the tab strip are drawn for the wrong size.

The web surfaces — the editor and the terminal — scale properly, because the browser does
it.

### There are no access keys outside the menus

The menus have them (`&Start llama server`). The two checkboxes, the Send button and the
prompt box do not, so Alt plus a letter reaches the menus and nothing else. Microsoft's
guidance asks for access keys on the controls a person uses most.

### The tree items do not say what they are

A tree item is announced as its file name. It is not announced as a file, and its type —
markdown, C#, image — is only a coloured dot on screen. Colour is the only thing carrying
that information, which is SC 1.4.1.

### The Blender manuals are a snapshot

The vendored reference in `workspace/vendor/blender-reference/` is the documentation that
came with `C:\blender_mcp` — the Blender 5.2 manual and API reference. The Blender that is
running can be a different version, and nothing checks. The skill tells the model to look
things up there, so a moved operator in a newer Blender would be looked up in the old
documentation. Making this exact means reading the version out of the running Blender and
refreshing the copy when it does not match.

### The Blender server is only there when Blender is

The tools are live: they act on the Blender that is open right now, and there is no
offline mode. When Blender is closed the server is not listening, the app retries it
every ten seconds, and the model has 26 tools it cannot use. The skill says so, but a
model that has been given the skill in a session where Blender was closed may still try.

---

## What the audit could not settle

- **The WebView2 surfaces.** There is no Microsoft page on WebView2 accessibility that
  could be reached, so the requirements for the editor and the terminal were taken from
  WCAG and the UI Automation model instead. What the audit could check — that the
  surfaces appear in the automation tree with names, and that their focusable parts have
  names — it did.
- **The classic Win32 UX guide carries a banner saying it was written for Windows 7 and
  has not been updated.** Its numbers (16×16 minimum control, 9 point Segoe UI) were
  cross-checked against the current Windows design docs, and where the two disagree the
  current docs won.
- **The contrast thresholds are measured in CSS pixels (WCAG) and relative pixels
  (Win32).** They are different units and are not directly comparable. The app's own
  controls are Win32, so 24×24 was applied as the stricter of the two.

---

## How to reproduce

```powershell
# the two new guards
.\scripts\check-contrast.ps1      # the colours and the font sizes, read out of the source
.\scripts\ui-a11y-audit.ps1       # the real window, through UI Automation
.\scripts\ui-a11y-audit.ps1 -Out rea\findings\a11y-audit.md   # the full report

# the guards of the second pass
.\scripts\ui-blender-skill.ps1    # the Blender server, the skill, and the fixer's names
.\scripts\mcp-http-probe.mjs http://127.0.0.1:8765/mcp   # the Blender tool list
.\scripts\ui-byok-turn.ps1        # what a provider is offered
.\scripts\find-mcp-arg-limit.ps1  # where llama.cpp's MCP client gives up
.\scripts\measure-prompt-cost.mjs # what the tool schemas cost

# everything
.\scripts\run-suite.ps1
```

The suite is 18 scripts: the smoke test (20 layers), the mode registry, the contrast, the
accessibility audit, the three-surface reach, the tool source, the tab close, the palette,
the provider form, the layout, a BYOK turn, and the seven that need the model.

---

## Sources

The guidance these findings are measured against:
- [Accessible text requirements — Windows apps](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements) — 4.5:1 for text
- [WCAG 2.2](https://www.w3.org/TR/WCAG22/) — SC 1.4.3, 1.4.11, 2.5.8, 4.1.2
- [High contrast themes — Windows apps](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes) — use `SystemColor`, never hard-coded colours
- [Properties on Windows Forms controls that support accessibility guidelines](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/advanced/properties-on-windows-forms-controls-that-support-accessibility-guidelines) — the 10 point rule
- [Provide accessibility information — Windows Forms](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/provide-accessibility-information) — `AccessibleName`, `AccessibleRole`
- [Walkthrough: creating an accessible Windows-based application](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/advanced/walkthrough-creating-an-accessible-windows-based-application) — high contrast, focus visibility
- [Controls with built-in owner-drawing support](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/controls-with-built-in-owner-drawing-support) — what owner drawing does to accessibility
- [How to set the tab order — Windows Forms](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/how-to-set-the-tab-order) — `TabIndex`, containers, radio groups
- [How to create access keys — Windows Forms](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/how-to-create-access-keys) — mnemonics
- [Keyboard accessibility — Windows apps](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility) — tab order, F6
- [UI Automation overview](https://learn.microsoft.com/en-us/windows/desktop/WinAuto/uiauto-uiautomationoverview) — the model the audit drove the app through
- [Layout — Win32 UX guide](https://learn.microsoft.com/en-us/windows/win32/uxguide/vis-layout) — 16×16 minimum control, 800×600
- [Typography — Windows apps](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography) — 12 pixel regular, 14 pixel semibold
- [Tab controls — Win32 UX guide](https://learn.microsoft.com/en-us/windows/win32/uxguide/ctrl-tabs) — no access keys on tabs
- [Tree views — Win32 UX guide](https://learn.microsoft.com/en-us/windows/win32/uxguide/ctrl-tree-views) — always show selection

The second pass is not measured against a document but against the code and against the
running app: llama.cpp's own MCP client (`tools/server/server-mcp.cpp`), the Blender MCP
server's source (`C:\blender_mcp`), the app's own behaviour driven through UI Automation,
and the servers themselves answering on `127.0.0.1:8765` and `127.0.0.1:8080`.
