// Splits a turn into its two costs: prefill (reading the prompt) and decode
// (writing the answer).
//
// On this box the model is a 35B MoE with 3B active running on 8 Zen 2 cores.
// A turn pays for both, and they have different fixes: decode is what
// speculative decoding attacks, prefill is what a smaller prompt attacks. If
// prefill dominates, spec decoding buys almost nothing - so measure first.
//
//   node measure-latency.mjs [baseUrl]
//
// The base URL defaults to the local server on 127.0.0.1:8080.

const base = process.argv[2] || 'http://127.0.0.1:8080';
const openai = base.replace(/\/+$/, '') + '/v1';

const catalogue = await (await fetch(`${base}/tools`)).json();
const isDefault = (t) => t.type === 'server' || (t.tool || '').startsWith('notecode_');
const defaults = catalogue.filter(isDefault);

async function ask(label, { tools, content, unique }) {
  const messages = [];
  // A unique system message goes first so the cached prefix cannot match.
  if (unique) messages.push({ role: 'system', content: unique });
  messages.push({ role: 'user', content });

  const body = {
    model: 'notecode-minicpm',
    messages,
    max_tokens: 48,
    stream: false,
    timings_per_token: true,
  };
  if (tools) {
    body.tools = tools.map((t) => t.definition);
    body.tool_choice = 'auto';
  }

  const started = Date.now();
  const res = await fetch(`${openai}/chat/completions`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
  const json = await res.json();
  if (!res.ok) throw new Error(`${res.status}: ${JSON.stringify(json).slice(0, 400)}`);
  const wall = (Date.now() - started) / 1000;

  const usage = json.usage || {};
  const t = json.timings || {};
  const row = {
    label,
    wall: `${wall.toFixed(1)}s`,
    prompt_tokens: usage.prompt_tokens ?? 0,
    prompt_ms: t.prompt_ms ?? 0,
    prompt_tok_s: t.prompt_per_second ? `${t.prompt_per_second.toFixed(1)}/s` : '-',
    cache_n: t.cache_n ?? '-',
    predicted: t.predicted_n ?? usage.completion_tokens ?? 0,
    decode_ms: t.predicted_ms ?? 0,
    decode_tok_s: t.predicted_per_second ? `${t.predicted_per_second.toFixed(2)}/s` : '-',
  };

  console.log(
    `${row.label.padEnd(26)} ${row.wall.padStart(7)}  ` +
    `prompt ${String(row.prompt_tokens).padStart(5)} tok in ${String(Math.round(row.prompt_ms)).padStart(6)} ms` +
    ` (${row.prompt_tok_s}, cache ${row.cache_n})  ` +
    `decode ${String(row.predicted).padStart(3)} tok in ${String(Math.round(row.decode_ms)).padStart(6)} ms (${row.decode_tok_s})`
  );
  return row;
}

console.log(`catalogue: ${catalogue.length} tools, default set: ${defaults.length}`);
console.log('');

const tiny = await ask('tiny prompt', { content: 'hi' });
await ask('tool schemas', { tools: defaults, content: 'hi' });
const cached = await ask('tool schemas (again)', { tools: defaults, content: 'hi' });

// A cold turn: a unique first token means the server cannot reuse any of the
// cached prefix, so the whole prompt is computed. This is what a user waits
// through right after a restart, a new conversation or an edited tool set.
const cold = await ask('tool schemas (cold)', {
  tools: defaults,
  content: 'hi',
  unique: `probe ${Date.now()}`,
});

console.log('');
console.log(`a turn with no schemas costs ${tiny.wall}; the schemas push it to ${cached.wall} warm`);
console.log(`cold, the same prompt costs ${cold.wall}`);
console.log(`decode alone runs at ${tiny.decode_tok_s}, so 200 answer tokens is ~${Math.round(200 / parseFloat(tiny.decode_tok_s))}s`);
