// Measures a genuinely cold prefill.
//
// The server keeps a prompt cache in its slot. Two things hide the real cost of
// a cold turn behind it: an identical prompt hits the cache outright, and even
// a changed prompt reuses most of it through KV shifting. So this erases the
// slot cache first and then asks for one turn with the full tool set.
//
//   node measure-cold-prefill.mjs [baseUrl]

const base = process.argv[2] || 'http://127.0.0.1:8080';
const openai = base.replace(/\/+$/, '') + '/v1';

const catalogue = await (await fetch(`${base}/tools`)).json();
const isDefault = (t) => t.type === 'server' || (t.tool || '').startsWith('notecode_');
const defaults = catalogue.filter(isDefault);

console.log(`default tool set: ${defaults.length} tools`);

// Erase the slot cache. /slots/{id}?action=erase drops the cached prompt.
try {
  const r = await fetch(`${base}/slots/0?action=erase`, { method: 'POST' });
  console.log(`slot cache erased: ${r.status}`);
} catch (e) {
  console.log(`could not erase the slot cache (${e.message}); the number below may be warm`);
}

const body = {
  model: 'notecode-minicpm',
  messages: [{ role: 'user', content: 'hi' }],
  max_tokens: 1,
  stream: false,
  timings_per_token: true,
  // The tools are reversed so the rendered prompt differs from the cached one
  // from the very first token. Same size, different bytes - no cache hit.
  tools: defaults.slice().reverse().map((t) => t.definition),
  tool_choice: 'auto',
};

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
console.log('');
console.log(`cold prompt : ${usage.prompt_tokens} tokens`);
console.log(`prefill     : ${Math.round(t.prompt_ms)} ms  (${(t.prompt_per_second || 0).toFixed(1)} tok/s reported, cache_n ${t.cache_n ?? '-'})`);
console.log(`wall clock  : ${wall.toFixed(1)} s`);
if (usage.prompt_tokens > 0) {
  console.log(`per token   : ${(t.prompt_ms / usage.prompt_tokens).toFixed(1)} ms`);
}
