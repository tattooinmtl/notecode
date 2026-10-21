// Measures what the second turn of a conversation costs to prefill, with the
// editor buffer placed after the history (notecode before this fix) and before
// it (after the fix).
//
// The server reuses the longest unchanged opening of the prompt. History grows
// by one exchange each turn, so anything placed after it moves, and is read
// again on every turn.
//
//   node scripts/bench-history.mjs [baseUrl]

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import http from 'node:http';

const base = (process.argv[2] || 'http://127.0.0.1:8080').replace(/\/+$/, '');
const here = path.dirname(fileURLToPath(import.meta.url));
const config = JSON.parse(readFileSync(path.join(here, '..', 'runtime', 'notecode.config.json'), 'utf8'));
const buffer = readFileSync(path.join(here, '..', 'src', 'Notecode.App', 'MainForm.cs'), 'utf8').slice(0, 6_000);

const catalogue = await getJson(`${base}/tools`);
const byName = new Map(catalogue.map((t) => [t.tool, t.definition]));
const tools = config.llama.agent.chatTools.map((n) => byName.get(n)).filter(Boolean);

const editor = { role: 'user', content: `[current editor buffer]\npath: MainForm.cs\n---\n${buffer}` };
const q1 = { role: 'user', content: 'What does RunToolLoopAsync do?' };
const a1 = { role: 'assistant', content: 'It runs the assistant turn, dispatches any tool calls, feeds the results back and stops when a turn has no tool call.' };
const q2 = { role: 'user', content: 'How many passes can it make?' };

function system() {
  // Unique per scenario, so the two orders do not share a cache.
  return { role: 'system', content: `run ${Date.now()}\nYou are notecode, a local coding assistant.` };
}

const orders = {
  'editor after history (old)': (sys) => [
    [sys, editor, q1],
    [sys, q1, a1, editor, q2],
  ],
  'editor before history (new)': (sys) => [
    [sys, editor, q1],
    [sys, editor, q1, a1, q2],
  ],
};

for (const [name, build] of Object.entries(orders)) {
  const [turn1, turn2] = build(system());
  await ask(turn1);
  const t = await ask(turn2);
  console.log(name, JSON.stringify({
    turn2PromptTokens: t.prompt_n + t.cache_n,
    reusedFromCache: t.cache_n,
    readFresh: t.prompt_n,
    prefillS: +(t.prompt_ms / 1000).toFixed(1),
  }));
}

async function ask(messages) {
  const body = { model: 'notecode-minicpm', messages, tools, tool_choice: 'none', max_tokens: 1, stream: false };
  const { status, json } = await post(`${base}/v1/chat/completions`, body);
  if (status !== 200) throw new Error(`${status}: ${JSON.stringify(json).slice(0, 300)}`);
  return json.timings;
}

function getJson(url) {
  return new Promise((resolve, reject) => {
    http.get(url, (res) => {
      let data = '';
      res.on('data', (c) => (data += c));
      res.on('end', () => resolve(JSON.parse(data)));
    }).on('error', reject);
  });
}

// node:http and not fetch: fetch gives up after 300 s without response headers.
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
