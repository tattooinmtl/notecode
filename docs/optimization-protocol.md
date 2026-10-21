# notecode optimization protocol — back to 10 tok/s

Target: **MiniCPM-V-4.7-35B-A3B Q4_K_M, CPU only, Ryzen 7 5700U (8C/16T, 28.9 GiB), ≥10 tok/s decode**
in real notecode turns, and no cold prefill over ~20 s.

## 1. Where we are (measured 2026-10-21 on the live server)

| Request | Prompt tokens | Prefill | Decode |
|---|---|---|---|
| Bare prompt, no tools | 41 | 1.2 s | **9.5 tok/s** (10.4 on a warm repeat) |
| App's default tool set (26 tools) | 3,908 | 9.1 s partly cached / ~100 s cold | 8.2–8.4 tok/s |
| Last real turn seen in `/slots` | 9,860 | ~4 min if cold | ~6–7 tok/s (the range you report) |

The engine still does 10 tok/s. The app takes it away in two ways:

1. **Context length.** On CPU every decoded token attends over the whole KV cache. The app
   sends 4–10k tokens per turn, while llama.cpp alone sends about 40.
2. **Memory pressure.** Model 19.7 GiB + mmproj 1.04 GiB + KV 1.33 GiB (ctx 32k) + runtime,
   with 2.8 GiB of system RAM free. The llama-server working set is 19.1 GiB, *smaller than the
   model*, and the process has taken **20.6 million page faults**. An MoE model reads different
   experts for every token, so the experts that were paged out get read back from disk during decode.
   This also makes decode speed swing from run to run (8.2 to 10.4 on identical requests).

## 2. Bugs found in the audit

| # | Bug | Effect | Status |
|---|---|---|---|
| B1 | `_reaRequest` was set by `/rea` and never cleared | After one `/rea`, **every later turn carried 170 REA tool schemas**, roughly 20k+ tokens | **Fixed**: REA now loads on `/rea` only |
| B2 | REA was started at app launch *and* by llama-server (`mcp-servers.json`), plus a 180 s `npx` prime on every server start | Two idle node trees eating RAM the model needs; slow server start | **Fixed**: `onDemand: true`, removed from `mcp-servers.json` |
| B3 | MCP stdio client redirected stderr but never read it | A chatty server (npx, REA) fills the pipe and every later call hangs forever | **Fixed** |
| B4 | MCP `_pending` dictionary used from two threads without a lock | Lost or crossed responses under load | **Fixed** |
| B5 | `toolTimeoutMs` was in config but never applied | A hung server hangs the turn forever | **Fixed** |
| B6 | Every tool call ran `tools/list` on **every** MCP server (`DispatchToolAsync` → `ListAllToolsAsync`) | Extra seconds per tool call, and tens of seconds when REA was connected | **Fixed**: routed by name prefix, catalogue cached per connection |
| B7 | `ConnectOneAsync` registered a connection before checking it answered | A dead server stayed in the list as "connected" | **Fixed** |
| B8 | llama-server spawns the blender bridge, global-brain and a second notecode-mcp, but the local tool filter (`GetToolsAsync`) throws their tools away, and the app dispatches MCP calls through its own client anyway | 3 extra processes and RAM for nothing | **Fixed** (Stage 1) |
| B9 | The editor buffer (up to **40,000 chars ≈ 10k tokens**) is added to every turn, on by default | This is most of the 9,860-token turn | **Fixed**: capped at 6,000 chars (Stage 2) |
| B10 | Tool results go back to the model untruncated | One `read_file` of a big file inflates every later pass of the tool loop | **Fixed**: 4,000 chars to the model (Stage 2) |
| B11 | Duplicate tools: `read_file` / `notecode_read_file`, `write_file`+`edit_file` / `save_as`+`write_text`, `exec_shell_command` / `run_command`. The system prompt names `save_as`, but the tool is called `notecode_save_as` | About 1k tokens of duplicate schemas, and the model can pick the wrong tool | **Fixed**: `llama.agent.chatTools`, 10 tools (Stage 2) |
| B12 | No conversation history is sent: each turn has only the system prompt, context and the new message | The model forgets the previous turn. This is a functional bug, and it must be fixed with a hard cap or it will undo the speed work | **Fixed**: last 4 exchanges, ≤ 6,000 chars (Stage 2) |
| B13 | `LlamaServer.IsHealthy` blocks on the UI thread (`GetAwaiter().GetResult()`, up to 3 s) at the start of every turn | UI freezes before each turn | **Fixed**: `IsHealthyAsync` |
| B14 | `RunWrapperAsync` reads all of stdout before it reads stderr | Classic pipe deadlock if the wrapper writes a lot to stderr | Stage 3 |
| B16 | Introduced by the first Stage 2 commit: the editor buffer came after the history, and the history grows every turn | The buffer moved each turn and was read again: **43.3 s** of prefill on turn 2 (`scripts/bench-history.mjs`); `--cache-reuse` did not recover it | **Fixed**: buffer goes before the history; turn 2 now reads 47 tokens in **1.8 s** |
| B15 | The mmproj (1.04 GiB f16) is always loaded | RAM taken even when no image is ever attached | **Fixed**: `llama.loadMmproj`, off by default (Stage 1) |

## 3. The protocol

Rule: **one change at a time, measured with the same command before and after.** Keep a change
only if it wins.

### Stage 0 — Benchmark harness (15 min)

```
node scripts/measure-latency.mjs
node scripts/measure-prompt-cost.mjs
```
plus a fixed decode probe: 96 tokens, temperature 0, the same C prompt, run with no tools and
with the app's tool set. Record `prompt_n`, `cache_n`, `prompt_ms` and `predicted_per_second` from
`timings`. Run each case **3 times** and keep the median, because paging makes single runs noisy.

### Stage 1 — Free the RAM, re-tune the server (config only, no rebuild)

Edit `runtime/notecode.config.json` and `Build-ServerArgs` in `runtime/notecode-llama.ps1`:

| Change | Why | Expected |
|---|---|---|
| `ctxSize` 32768 → **12288** | KV 1.33 → 0.5 GiB. After Stage 2, turns fit in 4–6k | +0.8 GiB free, faster attention |
| Drop `--mmproj` by default; load it only in a "vision" profile | 1.04 GiB back | Ends most of the paging |
| `mcp-servers.json`: keep **only** `notecode` (B8) | Removes 2–3 node/exe processes | ~0.3–0.6 GiB back, faster start |
| `--threads 16` → **8**, keep `--threads-batch 16` | SMT siblings fight over the same FPU and L2 in decode; prefill likes 16 | Usually +5–15% decode on Zen 2 (A/B it) |
| `--cache-reuse 256` | Reuses KV chunks when only part of the prompt changed | Shorter cold turns |
| `server.mlock` (this build: `--load-mode mmap+mlock`) **only once free RAM ≥ 23 GiB** | Pins the model so experts are never paged out | Stable 10+ tok/s; do not use before the RAM is freed, or the OS will thrash |
| A/B `cacheType` q8_0 vs f16 at ctx 12288 | On some CPU builds, attention over a quantized KV cache is slower; f16 at 12k is only ~1 GiB | ±5%, measure |
| Close heavy apps before starting the model (the doctor warns below 6 GiB free) | 13 GiB was held by other processes | Most important single factor |

Exit criteria: no-tools decode ≥ 10 tok/s median, and the working set ≥ model size (no paging growth).

### Stage 2 — Prompt diet in the app (the real 6 → 10 fix)

Goal: a normal turn ≤ **2,000 prompt tokens**, with a byte-stable prefix.

1. **Tool set: 26 → about 10.** Offer one tool per job: `notecode_save_as`, `notecode_read_file`,
   `edit_file`, `grep_search`, `file_glob_search`, `notecode_list_files`, `notecode_run_command`,
   `notecode_move_path`, `notecode_delete_path`, `notecode_get_editor_buffer`. Leave the rest out
   unless a slash command asks for them, the same way `/rea` works now. Fix the system prompt to
   use the real (prefixed) names. ~3.9k → ~1.3k tokens.
2. **Editor buffer: off by default, or capped at ~6,000 chars around the cursor.** The model can
   call `get_editor_buffer` when it needs more. This is the biggest win on long files.
3. **Truncate tool results to ~4,000 chars** before they go into `messages` (the UI can still show
   them in full).
4. **Stable prefix order:** system prompt → tools → (skill) → editor/attachment → user. Nothing
   that changes per turn may come before the tools.
5. **History with a hard budget** (fixes B12): last 4 user/assistant pairs, at most ~1,500 tokens;
   drop the oldest first. No tool results in the history.
6. **`max_tokens` 2048 by default** (`llama.agent.maxTokens`) so a runaway answer cannot keep growing the context. Not 1024: `save_as` carries a whole file in its arguments, and a call cut off mid-argument is lost.

Exit criteria: `/slots` shows ≤ 2k prompt tokens on a normal turn, and the in-app decode rate is
within 5% of the no-tools probe.

### Stage 3 — Make it visible, and the small fixes

- Status strip under the chat from `timings`: `prompt 1,4k · cached 1,3k · prefill 0.3 s · 9.8 tok/s`.
  This keeps future feature work from quietly undoing the gains.
- B13: make the health check async. B14: read stdout and stderr together.

### Stage 4 — Only if still short of 10 (optional, slower to do)

- `--spec-type ngram-simple --spec-draft-n-max 16`: draft-free speculative decoding. It helps on
  code that repeats existing text (edits, rewrites). Measure; keep it only if it wins.
- Rebuild llama.cpp with `GGML_NATIVE=ON` (Zen 2) and LTO in a **new** build folder; A/B it.
- Qwen3-Coder-30B-A3B as the default code model (17.3 GiB, saves 2.4 GiB of RAM), with
  MiniCPM-V kept as the vision profile. See `docs/deepinfer-proposal.md` §5.3.

## 4. Expected result

| After | Normal turn prompt | Decode |
|---|---|---|
| Today | 4–10k tokens, paging | 6–8 tok/s |
| Stage 1 | same | 8.5–9.5 tok/s (paging gone) |
| Stage 1 + 2 | ≤ 2k tokens | **≈ 10 tok/s**, cold prefill ≤ 20 s |
| + Stage 4 | ≤ 2k tokens | 10–13 tok/s on code edits |

## 5. Measured result — Stages 1 + 2 applied (2026-10-21)

`node scripts/bench-turn.mjs <label> http://127.0.0.1:8080 [shapes]`: 1 cold + 3 warm runs, 128
decoded tokens each (ignore_eos, temperature 0), median shown (the mean of the two middle runs, as there are four).

| Server | Prompt shape | Prompt tokens | Cold prefill | Decode (median) |
|---|---|---|---|---|
| before (ctx 32k, 16 threads, mmproj, 4 MCP servers incl. REA) | bare | 56 | 1.3 s | 10.78 tok/s |
| before | **old** (26 tools, 40k-char buffer) | **13,248** | **433 s** | **6.96 tok/s** |
| before | lean (10 tools, 6k-char buffer) | 3,709 | 85 s | 9.25 tok/s |
| **after** (ctx 12k, 8 threads, no mmproj, notecode MCP only, cache-reuse) | bare | 56 | 1.6 s | **12.12 tok/s** |
| **after** | **lean** | **3,709** | 92 s | **10.09 tok/s** |

- A real turn went from **6.96 → 10.09 tok/s** (+45%), with 72% fewer prompt tokens and a 4.7×
  shorter cold prefill.
- The server settings alone are worth about 12% on the bare probe (10.78 → 12.12) and 9% on the lean prompt (9.25 → 10.09).
- The old shape (13,248 tokens) does not fit the new 12,288 context. That is intended: the app no
  longer builds a prompt that size.
- The server starts in 8 s (it used to wait up to 180 s for the npx prime), and `/tools` lists 26
  tools instead of 228.
- Still open: a cold prefill of ~90 s on the first turn. The target was ≤ 20 s. Warm turns reuse
  the cache. The remaining lever is the editor buffer (~1.7k of the 3.7k tokens): turn it off by
  default and let the model call `notecode_get_editor_buffer`, which brings a cold turn to ~2k
  tokens and ~50 s.

## 6. REA on the local model

Measured with the server's tokenizer: the 170 REA tool schemas are **111,434 tokens**. The largest
single tool (`compare_web_captures`) is 15,535 tokens. That is more than 3× the old 32k context, so
`/rea` on the local model never fit, before or after this work. Before the on-demand fix, it also
made every later turn overflow.

REA is therefore **cloud only for now**. `/rea` runs on the active provider and refuses the local
model with a warning that suggests the cloud provider and model in `mcp.servers.rea`
(`/provider minimax`, `MiniMax-M3.1-Flash`). A provider that is not set up yet gets a pointer to
`/addprovider` in the same warning.
