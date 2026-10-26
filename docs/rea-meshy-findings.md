# REA findings: the Meshy MCP server

What REA was asked to do, what it found, and the three things it could not do.

Produced by `scripts/rea-meshy-probe.mjs`. The raw evidence is under
`rea/findings/` (gitignored - it is evidence for a run, not part of the
project):

| File | What it holds |
|---|---|
| `rea-meshy-evidence-meshy-mcp-server-dist.json` | The complete REA Evidence bundle, 33 MB |
| `rea-meshy-graph-meshy-mcp-server-dist.json` | The same, mined down to the graph and the statistics |
| `rea-meshy-evidence-meshy-3d-agent.json` | The same for the agent skill pack |
| `rea-meshy-graph-meshy-3d-agent.json` | The mined version |
| `meshy-mcp-live-tools.json` | The live `tools/list` handshake, and the server's stderr |
| `rea-meshy-summary.json` | The cross-check: static view against live view |

## The method

Two passes, deliberately different, so they can be compared.

**Static.** `analyze_javascript_application` reconstructs the application graph
from the built bundle without executing it. That is the reverse-engineering
view: what the code says it does.

**Live.** The server is started over stdio and asked for `tools/list`. That is
the contract view: what a client actually receives.

Where the two disagree, the disagreement is the finding.

## What REA recovered

REA 6.4.0, MCP protocol 2024-11-05, 170 tools advertised.

### The Meshy MCP server (`meshy-mcp-server` 0.6.5, built to `dist/`)

| Measure | Value |
|---|---|
| Files parsed | 68 |
| Bytes read | 401,177 |
| AST nodes visited | 39,367 |
| Findings | 730 |
| **Parse failures** | **0** |
| **Invalid UTF-8 files** | **0** |
| Coverage | complete, 0 omitted, not truncated |
| Integrity contradictions | 0 |
| Graph nodes / edges | 496 / 866 |
| Semantic nodes / relations | 19,874 / 15,624 |

The relation kinds are the interesting part, because they say what the server
actually does rather than what it is named:

| Relation | Count | What it says |
|---|---|---|
| aliases | 5,051 | the usual local-name churn |
| reads | 4,954 | |
| reads-property | 2,047 | |
| defines | 1,667 | |
| captures | 691 | |
| writes-property | 309 | |
| returns-to-call | 242 | |
| calls | 225 | |
| argument-to-parameter | 171 | |
| writes | 102 | |
| awaits | 70 | |
| creates-promise | 8 | |
| reads-config | 8 | |
| **registers-listener** | **5** | the MCP server's own lifecycle |
| **reads-environment** | **7** | `MESHY_API_KEY` and friends |
| **returns-task** | **3** | async generation |
| **detaches-task** | **2** | cancellation |

No parse failures and no integrity contradictions across 68 files means the
bundle REA read is the bundle that ships. Nothing was skipped and nothing
contradicted itself.

### The agent skill pack (`meshy-3d-agent`)

76 graph nodes, 8,001 semantic nodes, 8,792 relations over 4 parsed files.
The relation kinds here are a different vocabulary - `connects-stdio` (7),
`spawns` (7), `supplies-env` (7), `forwards-signal` (7), `supplies-argv` (5),
`listens-error` (7) - which is the shape of a process supervisor. That pack
is not an MCP server; it is a launcher that runs one.

## The cross-check, and the one finding worth keeping

25 of the 28 live tool names were attributed to a specific static node with a
source location. All 25 land in the same literal: `instructions.d.ts`, line 6 -
the `MESHY_INSTRUCTIONS` string the server hands to the client as its
instructions.

Three did not:

- `meshy_cancel_task`
- `meshy_check_balance`
- `meshy_list_usage`

That is not a REA gap. Confirmed directly against the source:

| Tool | Registered at | In `instructions.ts`? |
|---|---|---|
| `meshy_cancel_task` | `src/tools/tasks.ts:734` | no |
| `meshy_check_balance` | `src/tools/balance.ts:19` | no |
| `meshy_list_usage` | `src/tools/balance.ts:75` | no |

**The vendor's own agent instructions name 25 of its 28 tools and omit
exactly these three.** They are the credit-balance check, the usage breakdown
and the cancellation. An agent that trusts the instructions alone will spend
credits without checking the balance and will not know a cancellation tool
exists. The notecode skill (`skills/meshy-3d/SKILL.md`) says so explicitly,
and it tells the agent to check the balance *before* an expensive call
rather than after a 402.

## Three things REA could not do here

**No managed artifact to inspect.** `inspect_managed_artifact` needs a
compiled .NET assembly. The Unity plugin ships as C# source - an Editor
asmdef plus `.cs` files - and its `.unitypackage` contains only source, an
icon and a `package.json`. Unity's own managed assemblies are not on this
machine either: `C:\Program Files\Unity\Hub\Editor\2022.3.40f1` contains
`Editor\Data\PlaybackEngines` and nothing else, so there is no
`UnityEngine.dll` to compile against and no editor to compile with.

**No Python analyzer.** REA covers native binaries, JavaScript/Electron,
.NET assemblies and websites. The Blender and Unity DCC adapters are Python,
so there is no analyzer for them; they were read as source instead.

**The Unity editor is a stub.** The Meshy CLI's own installer refuses to run
for Unity without a real `Unity.exe`. The placement was done by calling the
vendor's own `_replace_package` and `_write_launch_files` directly
(`scripts/place-unity-meshy-package.py`), which skips only the editor
preflight. What is on disk is what the sanctioned installer would have
written.

## Three things REA did that are worth remembering

**The response budget is real.** The full analysis of the MCP server is
~10.5 MB of MCP response against a ~10 MB budget. `detail: "complete"` fails
with `resource_constraint` even though the analysis itself succeeded. The
remedy REA gives is the right one: `detail: "summary"` retains the complete
Evidence in-session, and `export_evidence_bundle` writes all of it to a file
(33 MB here).

**With `detail: "summary"`, the evidence id you get back belongs to the
projection, not to the analysis.** The analysis id is
`parameters.parent_evidence_id`. Handing the projection's id to
`inspect_analysis_view` fails with `unsupported_target` and the message
*"Parent Evidence operation `inspect_analysis_view` is not
`inspect_binary_layout`, `analyze_javascript_application` or
`analyze_function`"* - which reads like a REA bug and is really a
parent-pointer mismatch.

**Tool names live in string-literal node values.** A node's `properties.value`
is where `meshy_text_to_3d` and the rest actually sit; the label and the
identity fields do not carry them. A cross-check that only scans labels finds
zero of twenty-eight tools and looks like a total failure.
