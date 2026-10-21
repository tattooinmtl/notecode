#!/usr/bin/env node
/**
 * Calls the notecode MCP server with a list of tools and prints what came back.
 *
 *   node scripts/mcp-probe.mjs '[{"name":"run_command","arguments":{"command":"echo hi"}}]'
 *
 * The output is a JSON array, one entry per call, in the order the calls were
 * made. Each entry is {"name": ..., "text": ..., "isError": bool}.
 *
 * A call may carry "waitMs", which is a pause before it is made. The pause
 * matters when one call has to land before the next one: opening a file in the
 * editor and then reading the buffer out of it, for example.
 *
 * A call may carry "file", which is a path. The content of that file is sent
 * as the "content" argument, so a big argument does not have to survive a
 * command line.
 *
 * This is the same door the model goes through, so what it proves here is what
 * the model gets.
 */

import { spawn } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readFileSync } from 'node:fs';

const here = dirname(fileURLToPath(import.meta.url));
const server = join(here, '..', 'runtime', 'notecode-mcp.mjs');

let calls;
try {
  calls = JSON.parse(process.argv[2] ?? '[]');
} catch (e) {
  console.error(`the argument must be a JSON array: ${e.message}`);
  process.exit(2);
}
if (!Array.isArray(calls)) {
  console.error('the argument must be a JSON array');
  process.exit(2);
}

const child = spawn('node', [server], { stdio: ['pipe', 'pipe', 'inherit'] });

let buffer = '';
const pending = new Map();
let nextId = 1;

child.stdout.setEncoding('utf8');
child.stdout.on('data', (chunk) => {
  buffer += chunk;
  let i;
  while ((i = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, i).trim();
    buffer = buffer.slice(i + 1);
    if (!line) continue;
    let msg;
    try { msg = JSON.parse(line); } catch { continue; }
    if (msg.id !== undefined && pending.has(msg.id)) {
      pending.get(msg.id)(msg);
      pending.delete(msg.id);
    }
  }
});

function request(method, params) {
  const id = nextId++;
  const p = new Promise((resolve) => pending.set(id, resolve));
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  return p;
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

await request('initialize', {
  protocolVersion: '2024-11-05',
  capabilities: {},
  clientInfo: { name: 'probe', version: '1' }
});
child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} }) + '\n');

const out = [];
for (const call of calls) {
  if (call.waitMs) await sleep(call.waitMs);

  const args = { ...(call.arguments ?? {}) };
  if (call.file) args.content = readFileSync(call.file, 'utf8');

  const res = await request('tools/call', {
    name: call.name,
    arguments: args
  });

  const content = res?.result?.content ?? [];
  const text = content.map((c) => c.text ?? '').join('');
  out.push({
    name: call.name,
    text,
    isError: res?.result?.isError === true
  });
}

console.log(JSON.stringify(out, null, 2));
child.stdin.end();
child.kill();
process.exit(0);
