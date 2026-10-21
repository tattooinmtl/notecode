# DeepInfer → notecode: a proposal

**Status:** proposal only. Nothing here has been implemented.
**Date:** 2026-10-25
**Question asked:** *"look on the web for DeepInfer — can this help us get more speed on responses and in quality? Think about whether we can profit from this plan a way to use it. I'll read it and let you know if we implement it or not."*

---

## 1. The short answer

**DeepInfer cannot run on this machine, and it cannot be made to run here.** It needs Linux, CUDA 13, an NVIDIA SM100 (Blackwell) GPU or a Huawei Ascend 950 NPU, and it serves exactly one model — DeepSeek-V4.1-Flash, 552B parameters. This box is an AMD Ryzen 7 5700U with 28.9 GiB of RAM and no NVIDIA GPU at all. There is no version of that gap that closes with effort.

**But the plan behind DeepInfer is largely reusable — because most of it is already inside the engine notecode runs.** The llama.cpp build in `C:\Llama\llama.cpp\build-notecode` already contains DeepSeek's DSpark speculative decoding, confidence-scheduled verification, n-gram self-speculation, structured-output grammars and continuous batching. DeepInfer's value to us is not code to adopt; it is **a checklist of what a production inference stack should have, which we can then check off against llama.cpp and against our own app.**

The real bottleneck is not the engine. It is that **notecode sends 3,884 tool-schema tokens and up to 9,976 prompt tokens per turn to a model that prefills at ~39 tokens/second on this CPU.** A cold turn costs **1 minute 40 seconds of pure prefill** (measured) before the first token. That is where the time goes, and it is a *prompt-size* problem, not a *framework* problem.

So the proposal is: **do not chase DeepInfer; copy its ideas into the config we already have, cut the prompt, and pick a coding model that can carry a draft model.**

---

## 2. What DeepInfer is, and the four walls

DeepSeek open-sourced DeepInfer on **2026-10-20** under Apache 2.0 (`deepseek-ai/DeepInfer`). It is the framework behind DeepSeek's production API, built originally on vLLM with ideas from SGLang and TensorRT-LLM.

| Requirement | DeepInfer | notecode's box | Wall |
|---|---|---|---|
| OS | Linux, GCC ≥ 13, Python 3.12 | Windows, PowerShell, .NET | ✗ |
| Accelerator | NVIDIA SM100 (Blackwell) or Ascend 950 + CUDA 13 / CANN 9.2 | AMD Radeon iGPU only — `nvidia-smi` does not exist | ✗ |
| Model | **Only** DeepSeek-V4.1-Flash (552B total / 16B active) | MiniCPM-V-4_7-35B-A3B (35B / ~3B active), 19.71 GiB | ✗ |
| Interface | Offline batch inference only (`examples/offline_inference.py`). No HTTP server | llama-server over HTTP (`/v1/chat/completions`, SSE, `/slots`, `/props`, `/tools`) | ✗ |
| Scale | 4 GPUs for prefill, 64 for decode; smallest sane unit is 64+64 cards | 1 socket, 8 cores | ✗ |

The release also **omits the parts that make it a service**: no KV cache system, no prefill/decode disaggregation, no load balancing across replicas. DeepSeek's own note says the engine is "tightly coupled with our internal infrastructure, including cluster management tools, making it impractical for public deployment without significant modifications."

**Conclusion:** DeepInfer is a blueprint for a 64-GPU datacenter, not a desktop inference engine. Adopting it would mean rewriting notecode's entire model layer, changing the OS, and buying hardware. It is not a candidate.

---

## 3. What is worth stealing — and where it already lives

This is the profitable part of the question. DeepInfer's feature list maps almost one-to-one onto things llama.cpp already does. The gap is **configuration and our own app**, not code we have to write.

| DeepInfer feature | Status on this box | What we would do |
|---|---|---|
| **DSpark draft-token speculative decoding** | **Already in our llama.cpp.** `common/speculative.cpp` implements `draft-dspark` (DFlash backbone + semi-autoregressive Markov head), including the confidence head | Enable a draft — see §5.4. Needs a compatible draft model, which is the one real blocker. |
| **Confidence-scheduled verification** | Already there: `--spec-draft-p-min` ("minimum speculative decoding probability"), `--spec-draft-conf-min` (truncate a draft block at the first low-confidence position) | Use `--spec-draft-p-min` to stop drafting low-confidence tokens instead of verifying them. |
| **Speculative decoding (generic)** | Already there, 11 types: `draft-simple`, `draft-eagle3`, `draft-mtp`, `draft-dflash`, `draft-dspark`, `ngram-simple`, `ngram-map-k`, `ngram-map-k4v`, `ngram-mod`, `ngram-cache` | Start with **`ngram-simple`** — it needs *no draft model at all* and llama.cpp's own docs name our exact workload: *"An example to use this approach can be the rewriting of source code by an LLM."* |
| **Structured outputs** | Already there: the server accepts `response_format: {"type":"json_schema","schema":{…}}` and converts JSON Schema → GBNF grammar | Constrain the model's tool calls to the catalogue. Kills invented tool names and malformed arguments — each of which currently costs a multi-minute round trip. |
| **Deterministic sampling** | Already there: `--seed`, greedy sampling, `--spec-draft-sampling greedy` | Make the coding tools deterministic so a rewrite is reproducible. |
| **Asynchronous scheduling / zero-overhead batch scheduler** | Already there: `--cont-batching` is on by default; the server is an async slot scheduler | Nothing to do. |
| **Batch-invariant inference** | DeepInfer: NVIDIA only, GPU kernels. llama.cpp: only "a fixed seed produces repeatable random draws, but stochastic CPU and backend sampling can still select different tokens" | Not portable. Not needed for an IDE. |
| **SWA bounded replay** | Model-specific (DeepSeek V4.1 YOCO). Our GGUF has no SWA window to bound | Not applicable. |
| **MoE expert parallelism + EPLB** | Needs multi-GPU tensor parallelism. Our model is a 256-expert/8-active MoE that fits in RAM | Not applicable — but the *scheduler* idea (balance expert work) has no CPU analogue worth copying. |
| **Prefill/decode disaggregation, DP load balancing** | Multi-node serving | Not applicable. |

**The single most useful thing DeepInfer gave us: permission to look at our own llama.cpp build and find that its speculative-decoding stack already implements DeepSeek's flagship feature.** The `docs/speculative.md` in our checkout even documents DSpark with a working command line.

---

## 4. Where notecode's time actually goes (measured, not guessed)

Measured on the running server (`scripts/measure-latency.mjs`, `scripts/measure-cold-prefill.mjs`), target = MiniCPM-V-4_7-35B-A3B on 8 Zen 2 cores:

| Quantity | Measured | Consequence |
|---|---|---|
| Prompt size, default tool set | **3,884 tokens** (26 tools) | 89% of a typical turn is tool schemas |
| Worst observed prompt | **9,976 tokens** (`/slots`) | ~4 minutes of prefill if cold |
| **Cold prefill** (cache_n 0, tools reordered to defeat the cache) | **3,884 tokens in 100,430 ms** = 25.9 ms/token, **38.7 tok/s** | A cold turn = **1 min 40 s before the first token** |
| Warm prefill (cache hit) | **258 ms** (3,880 of 3,884 tokens reused) | The cache saves **99%** of prefill — and hides the problem |
| Decode rate | **7.3 – 9.4 tok/s** (7 runs) | A 200-token answer ≈ 22–28 s |
| Model | `MiniCPM-V-4_7-35B-A3B`, arch `qwen35moe`, 40 layers, 256 experts / 8 used, vocab 248,144 | A **general/vision** model, not a coding specialist |
| Build flags | `GGML_NATIVE=OFF`, `GGML_LTO=OFF`, `GGML_BLAS=OFF`, `/O2`, AVX2+FMA on, AVX-512 off | Zen 2 has no AVX-512, so that one is correct. LTO and BLAS are left on the table. |

**The decisive insight:** prefill is ~5× more expensive per token than decode (38.7 tok/s vs ~8 tok/s) *and* notecode's prompt is 3,884 tokens of which ~3,900 are repeated every turn. The cache hides that on a warm server, and the cold case is what the user feels after a restart, a new conversation, or any tool-set change.

So the lever order is: **cut the prompt → keep the cache warm → speed up decode**. Not "install a faster inference framework."

---

## 5. The proposal

Six changes, ordered by payoff per unit of effort. Stages 0–2 are the ones I would do first; they need no downloads and no rebuild.

### 5.0 Stage 0 — Make the invisible visible (half a day)

The server already returns `timings` (prompt tokens, cache hits, prefill ms, decode tok/s). notecode shows none of it. Add a one-line status strip under the chat: `3,884 tok prompt · 3,880 cached · prefill 0.26 s · decode 8.1 tok/s`.

Every other stage below is a bet on which part of the turn is expensive. This removes the guesswork — and it is the cheapest thing on the list. It also makes the before/after of every later change visible to you, which is the difference between "we think it got faster" and "it got faster by 34%".

### 5.1 Stage 1 — Free wins in the server config (1 hour, no rebuild, no download)

Four flags, all already supported by the build we run:

1. **`--spec-type ngram-simple --spec-draft-n-max 64`** — speculative decoding with **no draft model**. llama.cpp searches the token history for a matching n-gram and proposes the continuation. For code (rewrites, repeated patterns, imports, boilerplate) this can accept multiple tokens per target step. Expected: 1.2–1.8× on decode for repetitive/code-heavy output, ~0 on prose. *Must be measured — this is the one number I would most like to see before committing.*
2. **`--spec-type ngram-mod`** instead of `ngram-simple` — keeps a hash pool of n-grams shared across slots, so a pattern the model used earlier in the session is still available later. Slightly higher hit rate; slightly more memory.
3. **`--cache-reuse 256`** — currently **0, i.e. disabled**. The server then reuses cached KV *chunks* through KV shifting even when the prompt changed, instead of only on an exact prefix match. This is the direct answer to "a cold turn costs 100 s": with chunk reuse, an edited tool set or an appended system line no longer invalidates the whole prompt.
4. **Thread re-tune** — `--threads 16` uses all 16 SMT threads; on 8 physical Zen 2 cores the decode loop may be better with 8. Cheap to A/B: `--threads 8` vs `16`, `--threads-batch 16` kept.

All four are config keys in `runtime/notecode.config.json` plus a few lines in `runtime/notecode-llama.ps1` (`Build-ServerArgs`). No C# changes. **We should A/B each against the same prompt set before keeping it.**

### 5.2 Stage 2 — Prompt discipline in the app (1–2 days)

The app already learned this lesson once: the skill fixer is given **2 tools instead of 26**, which keeps its schema cost near zero even though its turn still runs 4,878–6,941 tokens (it carries the skill body and the task text). Generalise the pattern:

1. **Task-scoped tool sets.** A file-editing turn needs `read_file`, `edit_file`, `write_file`, `grep_search`. A Blender turn needs three Blender tools. The app already knows the catalogue; it should send the relevant subset. Effect: the common turn drops from 3,884 to ~800–1,200 prompt tokens → **cold prefill from 100 s to ~21–31 s**, and warm turns stay at ~0.3 s.
2. **Keep the prefix byte-stable.** The cache only pays when the system prompt and the tool block are *identical* every turn. Any per-turn injection (a timestamp, a "current file" line, a changing tool order) must go **after** the cached prefix, or it invalidates the cache for the whole turn. Worth auditing the prompt builder for exactly this.
3. **History compaction.** The 9,976-token turn is a long conversation with no compaction. Cap the history sent to the model (keep the last N turns plus a summary of the rest). At 25.9 ms per prefill token, 10k tokens of history is about **4½ minutes** of dead air.
4. **Stable tool naming.** The app already prefixes tool names with their server (`blender_execute_python`). Keep it: renaming a tool changes the cached prefix.

### 5.3 Stage 3 — The model decision (1 day of benchmarking)

This is the quality lever, and it is coupled to the speed lever.

| Candidate | Size | Vocab | Notes |
|---|---|---|---|
| **MiniCPM-V-4_7-35B-A3B** (current) | 19.71 GiB | 248,144 | `qwen35moe` backbone, 256 experts / 8 active. **Vision** (has `mmproj-model-f16.gguf`). General model. |
| **Qwen3-Coder-30B-A3B-Instruct** | 17.28 GiB | 151,936 | `qwen3moe`, 48 layers, 128 experts / 8 active. **Coding specialist**, same "3B active" decode profile → similar speed, materially better code. **No vision.** |
| GLM-4.7-Flash | 17.05 GiB | 154,880 | Another strong 4.7-era candidate. |
| Qwythos-27B / qwen3.8-27B-uncensored | 16.1 / 15.7 GiB | 248,320 | Same tokenizer family as the current model (see §5.4). |

**Recommendation:** make the local model a *switchable profile*, default **Qwen3-Coder-30B-A3B** for code, keeping **MiniCPM-V** as the "vision model" for image tasks. The app already has a single `llama.model` key; turning it into a profile list with a UI switch is a contained change. The server restart costs ~1 minute (mmap load).

**Two costs to plan for:**
- **KV cache grows 1.2×, not 2.4×** *(corrected after reading the GGUF metadata: Qwen3-Coder is 48 layers × 4 KV heads × 128 head_dim = 49,152 elements/token vs MiniCPM-V's 40 × 2 × 256 = 40,960).* At `q8_0`: **1.59 GiB vs 1.33 GiB** at ctx 32768, or 0.80 vs 0.66 GiB at ctx 16384. Cheap.
- **But the real budget question is the draft model, and it still fits.** Full plan with a Qwen2.5-Coder-3B draft resident at the same time: 17.28 (target) + 3.06 (draft) + 1.59 (KV) + ~1.3 (runtime) ≈ **23.3 GiB against 28.9 GiB physical**. Without the draft: ≈ 20.2 GiB. The current setup is 22.3 GiB. So the full plan costs **about 1 GiB more than today**, not 7 GiB — the 2.43 GiB the smaller target saves is what pays for the draft.
- **Vision.** If Qwen3-Coder becomes the only model, the "attach an image" path dies with it (its projector does not exist; `mmproj-model-f16.gguf` belongs to MiniCPM-V). Hence the profile switch rather than a replacement.

**Benchmark plan before switching:** run the same five notecode tasks (read+edit a file, fix a compile error, explain a stack trace, drive Blender, write a skill) against both models, scoring tool-call correctness, whether the edit compiles, wall time, and tokens. Measure, do not guess.

### 5.4 Stage 4 — Real speculative decoding (2–3 days; the one real blocker)

Speculative decoding needs a draft model whose vocabulary is **identical** to the target's. llama.cpp's check (`common/speculative.cpp:106-165`) compares token text from id 5 upward and rejects a vocab size difference above 128. I checked every model on this disk:

| Target | Candidate draft | Result |
|---|---|---|
| MiniCPM-V-4_7 (248,144) | Qwythos-27B, qwen3.8-27B, Ornith-1.5-9B (248,320) | Tokens are **identical from id 5 to 248,076** — the 176 extra tokens are padding (`[PAD…]` vs `<unk>`). Rejected only by the **size** rule (176 > 128). |
| MiniCPM-V-4_7 | Spark-X2.5-1.7B/4B (131,072) | Different tokenizer entirely. Rejected. |
| MiniCPM-V-4_7 | gemma4-coding (262,144), granite (100,352) | Rejected. |
| **Qwen3-Coder-30B-A3B** (151,936) | **Qwen2.5-Coder-3B** (`unsloth.Q8_0.gguf`, 3.06 GiB) | Vocab size **exactly equal**; tokens identical from id 5 to **151,664**; **4 tokens differ** in the special tail: `151,665 '<tool_response>'` vs `'<|PAD_TOKEN|>'`, and `151,666-151,668`. |

**So the pair we want is 4 token slots away from working.** Three ways to close it:

1. **(Recommended) Download a Qwen3-family draft whose tail matches** — e.g. `Qwen3-4B` or `Qwen3-1.7B` (~2 GB). Qwen3's tokenizer carries the same `<tool_call>`/`<tool_response>`/`<think>` special tokens as Qwen3-Coder, so the tail should match exactly. **Must be verified** by running the same token-comparison script before committing.
2. **Patch llama.cpp's compatibility check to skip control tokens** in a *separate* build directory (`build-notecode-spec` — `build/` and `build-mtmd/` stay untouched). The 4 mismatching ids are control/padding tokens that never appear in normal text, so skipping tokens flagged as control on either side is safe in principle. This reuses the draft already on disk and needs no download. It is a real upstream-worthy patch (a 3-line change), but it means owning a fork of the engine — worth it only if option 1 fails.
3. **DeepSeek's own DSpark checkpoint** (`deepseek-ai/dspark_qwen3_4b_block7`) — this is the genuine DeepInfer technology. But it is trained for **Qwen3-4B**, not for Qwen3-Coder-30B-A3B, and llama.cpp documents that DSpark drafts currently require a **Qwen3 backbone**. So DSpark proper is **not available for our target**; it would need a new checkpoint from DeepSeek.

**The arithmetic that says it is worth doing** (using §4's measured rates):

- One target token costs ~125 ms (8 tok/s).
- A 3B draft in Q8 on 2 pinned cores costs roughly ~20–25 ms per draft token.
- Drafting 4 tokens costs ~80–100 ms; if they are accepted, they save 4 target steps ≈ 500 ms.
- Break-even acceptance ≈ **20%**. For code, where the target is copying established patterns, acceptance is usually well above that.

**CPU affinity matters more than usual here.** The draft and the target want the same 8 cores. llama.cpp gives us `--spec-draft-cpu-range 0-1` and `--spec-draft-threads` so the draft is confined to 1–2 logical cores and the target keeps the rest. Without that split, the draft steals the target's cores and the "speedup" can be negative.

### 5.5 Stage 5 — Quality rails (1–2 days)

1. **Grammar-constrained tool calls.** The server accepts a JSON Schema via `response_format`. The app can build a schema from the tool catalogue and send it, so the model can only emit a tool that exists with a valid argument shape. This is DeepInfer's "structured outputs" feature, and it removes a whole class of failures — on this hardware, one failed tool call is a wasted multi-minute turn. It also doubles as a **safety rail**: the model cannot hallucinate a destructive command name.
2. **BYOK escalation.** The app already offers every connected MCP server (51 tools) to BYOK providers. Add an explicit "run this turn on a strong model" action: local for iteration, cloud for the hard turns (refactors, reviews, Blender scripting). Quality then costs money only where it earns it.
3. **Finish the open second-pass items** in `finalAudit.md` (§6.1–6.9) — the unchecked rewrite path, the fixer's tool check, the unnamed editor surface. These are correctness, not speed, but they are what "quality" means for an agent IDE.

### 5.6 Stage 6 — The build (1 day, ~20 minutes to compile)

The current `build-notecode` is `GGML_NATIVE=OFF`, `GGML_LTO=OFF`, `GGML_BLAS=OFF`, `/O2`. A second build directory (not `build/`, not `build-mtmd/`) with:

- `GGML_NATIVE=ON` (or explicit `-march=znver2`) — the whole compiler targets Zen 2, not just the GGML kernels;
- `GGML_LTO=ON` — MSVC `/GL` + `/LTCG`; on llama.cpp this is routinely worth single-digit to low-teens percent;
- `GGML_BLAS=ON` with OpenBLAS — llama.cpp uses BLAS for the **batched** matmuls in prompt processing, which is exactly our 25.9 ms/token prefill bottleneck;
- `/O3` — benchmark it against `/O2` rather than assuming.

Then A/B the two binaries on the same prompt set. **Keep `build-notecode` as the fallback** and swap the config path only if the new build wins.

---

## 6. What I am *not* proposing, and why

- **Not DeepInfer.** Four independent walls (OS, accelerator, model, interface). No amount of work makes it run here.
- **Not a rewrite of the model layer.** The llama-server HTTP API we already use is the right shape. The 4 KB MCP-client bug we fixed last session was in llama.cpp's MCP client, not in the server's API.
- **Not DSpark specifically** — unless DeepSeek publishes a checkpoint for whichever target we pick. The generic speculative decoding path gets us the same benefit.
- **Not GPU work.** Nothing in this proposal needs a GPU; there is no GPU here.
- **Not touching `C:\Llama\llama.cpp\build` or `build-mtmd`.** Any patched build goes in a new directory.

---

## 7. Effort vs payoff

| # | Change | Effort | Payoff | Risk |
|---|---|---|---|---|
| 0 | Show prefill/decode/cache in the UI | 0.5 d | Everything else becomes measurable | none |
| 1a | `ngram-simple` speculative decoding | 1 h | 1.2–1.8× decode on code (to be measured) | low |
| 1b | `--cache-reuse 256` | 15 min | Shorter cold turns | low |
| 1c | `--threads 8` vs `16` A/B | 30 min | Unknown until measured | none |
| 2 | Task-scoped tool sets + stable prefix + history cap | 1–2 d | Cold prefill 100 s → ~26 s | medium (touches prompt building) |
| 3 | Model profile switch (Qwen3-Coder default) | 1 d + benchmarks | Large quality gain on code | medium (tool-call format, KV memory, vision) |
| 5a | Grammar-constrained tool calls | 1 d | Fewer failed turns; a real safety rail | medium (schema limits) |
| 5b | BYOK escalation button | 0.5 d | Quality ceiling on demand | low |
| 4 | Draft model + `draft-simple` | 2–3 d | 1.3–1.8× decode (needs the draft) | medium (RAM, affinity) |
| 6 | Zen2/LTO/BLAS rebuild | 1 d | Unknown, 5–20% likely | low (keep the old binary) |

---

## 8. The decisions I need from you

1. **Model:** switch the default to **Qwen3-Coder-30B-A3B** (coding quality, unlocks the draft path) and keep MiniCPM-V for vision — or stay on MiniCPM-V and accept generic code quality? *My recommendation: switch, with a vision profile kept.*
2. **Memory:** a Qwen3-Coder + draft setup needs ~23.3 GiB of 28.9 (≈1 GiB more than today, because the smaller target pays for the draft). Accept, or drop to ctx 16384 (saves 0.8 GiB)?
3. **Downloads:** may I pull a ~2 GB Qwen3 draft model (option 1 in §5.4), or must everything come from what is already on disk (option 2, a patched build)?
4. **Patched build:** is a separate, lightly patched llama.cpp build acceptable, or do you want the engine pristine?
5. **Order:** speed first (Stages 0, 1, 2, 6) or quality first (Stages 0, 3, 5)?

I would start with **Stage 0 + Stage 1** regardless of the answers above: they are cheap, reversible, and they turn the rest of this proposal from a set of estimates into a set of measurements.

---

## Appendix — how the numbers were obtained

```powershell
# prefill vs decode, cache behaviour
node scripts\measure-latency.mjs

# cold prefill - the tool schemas are sent in reverse order so the rendered
# prompt differs from the cached one from the first token (cache_n 0)
node scripts\measure-cold-prefill.mjs

# tool-schema cost per turn
node scripts\measure-prompt-cost.mjs

# the live slot's prompt size and cache state
Invoke-RestMethod 'http://127.0.0.1:8080/slots'
```

Model metadata and tokenizer comparison were read directly from the GGUF headers with
`gguf-py` (`C:\Llama\llama.cpp\gguf-py`), comparing `tokenizer.ggml.tokens` entry by entry
against llama.cpp's own compatibility rule in `common/speculative.cpp:106-165`
(`SPEC_VOCAB_MAX_SIZE_DIFFERENCE = 128`, `SPEC_VOCAB_CHECK_START_TOKEN_ID = 5`).

Sources for the DeepInfer facts: the project README (`github.com/deepseek-ai/DeepInfer`),
plus independent coverage from TheNextWeb, MarkTechPost, DeepWiki and agentspulse.
