#!/usr/bin/env node
/**
 * Probes an HTTP MCP endpoint: the tools, the prompts and the resources it
 * offers.
 *
 *   node scripts/mcp-http-probe.mjs http://127.0.0.1:8765/mcp [what]
 *
 * "what" is tools, prompts, resources or all (the default).
 *
 * The endpoint has to be reachable. The Blender one is only listening while
 * Blender is open, so this is a way to see what the app will find.
 */

import { spawn } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const bridge = join(here, '..', 'runtime', 'mcp-stdio-bridge.mjs');

const url = process.argv[2];
if (!url) {
  console.error('usage: mcp-http-probe.mjs <http-mcp-url> [tools|prompts|resources|all]');
  process.exit(2);
}
const what = (process.argv[3] || 'all').toLowerCase();

const child = spawn(process.execPath, [bridge, url], { stdio: ['pipe', 'pipe', 'inherit'] });

const replies = [];
let buffer = '';
child.stdout.setEncoding('utf8');
child.stdout.on('data', (chunk) => {
  buffer += chunk;
  let i;
  while ((i = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, i).trim();
    buffer = buffer.slice(i + 1);
    if (line) {
      try { replies.push(JSON.parse(line)); } catch { /* not a frame */ }
    }
  }
});

const send = (o) => child.stdin.write(JSON.stringify(o) + '\n');
const waitFor = async (id, ms = 10000) => {
  const deadline = Date.now() + ms;
  while (Date.now() < deadline) {
    const hit = replies.find((r) => r.id === id);
    if (hit) return hit;
    await new Promise((r) => setTimeout(r, 50));
  }
  return null;
};

send({
  jsonrpc: '2.0', id: 1, method: 'initialize',
  params: { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'probe', version: '1' } }
});
const init = await waitFor(1);
if (!init) {
  console.error(`no answer from ${url}`);
  child.kill();
  process.exit(1);
}

console.log(`server: ${init.result?.serverInfo?.name ?? '?'} ${init.result?.serverInfo?.version ?? ''}`);
console.log(`url:    ${url}`);
console.log('');

send({ jsonrpc: '2.0', method: 'notifications/initialized' });

if (what === 'all' || what === 'tools') {
  send({ jsonrpc: '2.0', id: 2, method: 'tools/list', params: {} });
  const tools = await waitFor(2);
  const list = tools?.result?.tools ?? [];
  console.log(`tools: ${list.length}`);
  for (const t of list) {
    const desc = (t.description ?? '').split('\n')[0];
    console.log(`  ${t.name}`);
    if (desc) console.log(`      ${desc.slice(0, 110)}`);
  }
  console.log('');
}

if (what === 'all' || what === 'prompts') {
  send({ jsonrpc: '2.0', id: 3, method: 'prompts/list', params: {} });
  const prompts = await waitFor(3);
  const list = prompts?.result?.prompts ?? [];
  console.log(`prompts: ${list.length}`);
  for (const p of list) {
    console.log(`  ${p.name}${p.description ? ' - ' + p.description.split('\n')[0] : ''}`);
  }
  if (!list.length && prompts?.error) console.log(`  (${prompts.error.message})`);
  console.log('');
}

if (what === 'all' || what === 'resources') {
  send({ jsonrpc: '2.0', id: 4, method: 'resources/list', params: {} });
  const res = await waitFor(4);
  const list = res?.result?.resources ?? [];
  console.log(`resources: ${list.length}`);
  for (const r of list) console.log(`  ${r.uri}${r.name ? ' - ' + r.name : ''}`);
  if (!list.length && res?.error) console.log(`  (${res.error.message})`);
  console.log('');
}

child.stdin.end();
child.kill();
process.exit(0);
