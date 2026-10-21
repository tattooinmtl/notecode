// Replays a realistic notecode turn against the local server and reports
// prefill and decode, so a change to the server or to the prompt can be
// measured the same way before and after.
//
// Two prompt shapes are sent:
//   old  - the 26-tool catalogue and a 40,000-char editor buffer (notecode 0.2.0)
//   lean - the chat tool set and a 6,000-char editor buffer (optimization protocol stage 2)
// and a bare prompt with no tools, which shows what the engine alone can do.
//
// Each shape is sent once cold (a unique first line defeats the prompt cache)
// and then RUNS times warm. Decode is a fixed 128 tokens (ignore_eos), so the
// rates compare like with like.
//
//   node scripts/bench-turn.mjs [label] [baseUrl] [shapes, e.g. bare,lean]
//
// The old shape is about 13k tokens and does not fit a 12288 context, so run
// it only against a server with a larger --ctx-size.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import http from 'node:http';

const label = process.argv[2] || 'run';
const base = (process.argv[3] || 'http://127.0.0.1:8080').replace(/\/+$/, '');
const RUNS = 3;
const DECODE = 128;

const here = path.dirname(fileURLToPath(import.meta.url));
const config = JSON.parse(readFileSync(path.join(here, '..', 'runtime', 'notecode.config.json'), 'utf8'));
const chatTools = config.llama.agent.chatTools;

const buffer = readFileSync(path.join(here, '..', 'src', 'Notecode.App', 'MainForm.cs'), 'utf8');
const catalogue = await (await fetch(`${base}/tools`)).json();
const byName = new Map(catalogue.map((t) => [t.tool, t.definition]));

const oldTools = catalogue
  .filter((t) => t.type === 'server' || (t.tool || '').startsWith('notecode_'))
  .map((t) => t.definition);
const leanTools = chatTools.map((n) => byName.get(n)).filter(Boolean);

const question = 'In two sentences, what does RunToolLoopAsync do?';

const shapes = {
  bare: { tools: null, context: null },
  old: { tools: oldTools, context: buffer.slice(0, 40_000) },
  lean: { tools: leanTools, context: buffer.slice(0, 6_000) },
};

async function ask(shape, cold) {
  const messages = [{ role: 'system', content: (cold ? `run ${Date.now()}\n` : '') + 'You are notecode, a local coding assistant.' }];
  if (shape.context) messages.push({ role: 'user', content: `[current editor buffer]\npath: MainForm.cs\n---\n${shape.context}` });
  messages.push({ role: 'user', content: question });

  const body = { model: 'notecode-minicpm', messages, max_tokens: DECODE, ignore_eos: true, temperature: 0, stream: false };
  if (shape.tools) { body.tools = shape.tools; body.tool_choice = 'none'; }

  // node:http and not fetch: fetch gives up after 300 s without response
  // headers, and a cold prefill of the old prompt shape takes longer than that.
  const { status, json } = await post(`${base}/v1/chat/completions`, body);
  if (status !== 200) throw new Error(`${status}: ${JSON.stringify(json).slice(0, 300)}`);
  return json.timings;
}

function post(url, body) {
  return new Promise((resolve, reject) => {
    const req = http.request(url, { method: 'POST', headers: { 'content-type': 'application/json' } }, (res) => {
      let data = '';
      res.setEncoding('utf8');
      res.on('data', (c) => (data += c));
      res.on('end', () => {
        try { resolve({ status: res.statusCode, json: JSON.parse(data) }); } catch (e) { reject(e); }
      });
    });
    req.on('error', reject);
    req.end(JSON.stringify(body));
  });
}

const median = (xs) => [...xs].sort((a, b) => a - b)[Math.floor(xs.length / 2)];
const results = {};

const only = process.argv[4] ? process.argv[4].split(',') : Object.keys(shapes);

for (const [name, shape] of Object.entries(shapes).filter(([n]) => only.includes(n))) {
  const cold = await ask(shape, true);
  const warm = [];
  for (let i = 0; i < RUNS; i++) warm.push(await ask(shape, false));
  results[name] = {
    tools: shape.tools?.length ?? 0,
    promptTokens: cold.prompt_n + cold.cache_n,
    coldPrefillS: +(cold.prompt_ms / 1000).toFixed(1),
    prefillTps: +cold.prompt_per_second.toFixed(1),
    decodeTpsMedian: +median([cold, ...warm].map((t) => t.predicted_per_second)).toFixed(2),
    decodeTpsRuns: [cold, ...warm].map((t) => +t.predicted_per_second.toFixed(2)),
  };
  console.log(label, name, JSON.stringify(results[name]));
}
