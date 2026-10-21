#!/usr/bin/env node
/**
 * test-mcp-bridge.mjs
 *
 * Verifies runtime/mcp-stdio-bridge.mjs end to end:
 *   1. starts a mock streamable-http MCP server on an ephemeral port
 *   2. pipes MCP JSON-RPC frames through the bridge's stdin
 *   3. asserts the bridge relays the upstream replies verbatim
 *   4. asserts the session id from `initialize` is replayed on later requests
 *
 * The mock exists because the only real HTTP MCP server on this machine
 * (Blender) is only listening while Blender is open.
 *
 * Usage: node test-mcp-bridge.mjs
 * Exit code 0 = pass.
 */

import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const bridge = join(here, '..', 'runtime', 'mcp-stdio-bridge.mjs');

const sessionId = 'test-session-1234';
const seenSessionHeaders = [];
let toolsListCalls = 0;

const server = createServer((req, res) => {
  let body = '';
  req.on('data', (c) => (body += c));
  req.on('end', () => {
    seenSessionHeaders.push(req.headers['mcp-session-id'] ?? null);
    let msg = {};
    try { msg = JSON.parse(body); } catch { /* ignore */ }

    const payload = (obj) => {
      res.writeHead(200, {
        'content-type': 'text/event-stream',
        'mcp-session-id': sessionId,
      });
      res.write(`event: message\ndata: ${JSON.stringify(obj)}\n\n`);
      res.end();
    };

    if (msg.method === 'initialize') {
      payload({
        jsonrpc: '2.0', id: msg.id,
        result: { protocolVersion: '2024-11-05', capabilities: { tools: {} }, serverInfo: { name: 'mock', version: '1' } },
      });
    } else if (msg.method === 'tools/list') {
      toolsListCalls++;
      payload({
        jsonrpc: '2.0', id: msg.id,
        result: { tools: [{ name: 'mock_tool', description: 'a mock tool' }] },
      });
    } else {
      payload({ jsonrpc: '2.0', id: msg.id, result: {} });
    }
  });
});

await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const url = `http://127.0.0.1:${port}/mcp`;

const child = spawn(process.execPath, [bridge, url], { stdio: ['pipe', 'pipe', 'pipe'] });

const replies = [];
let buffer = '';
child.stdout.setEncoding('utf8');
child.stdout.on('data', (chunk) => {
  buffer += chunk;
  let i;
  while ((i = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, i).trim();
    buffer = buffer.slice(i + 1);
    if (line) replies.push(JSON.parse(line));
  }
});
child.stderr.setEncoding('utf8');
child.stderr.on('data', (c) => process.stderr.write(`[bridge] ${c}`));

const send = (o) => child.stdin.write(JSON.stringify(o) + '\n');
const waitFor = async (id, ms = 8000) => {
  const deadline = Date.now() + ms;
  while (Date.now() < deadline) {
    const hit = replies.find((r) => r.id === id);
    if (hit) return hit;
    await new Promise((r) => setTimeout(r, 50));
  }
  return null;
};

let failures = 0;
const check = (name, ok, detail = '') => {
  console.log(`  ${ok ? '[PASS]' : '[FAIL]'} ${name}${detail ? ' - ' + detail : ''}`);
  if (!ok) failures++;
};

console.log('\nmcp-stdio-bridge test');
console.log('=====================');
console.log(`  mock upstream: ${url}\n`);

send({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'test', version: '1' } } });
const init = await waitFor(1);
check('initialize relayed', init?.result?.serverInfo?.name === 'mock', JSON.stringify(init?.result?.serverInfo ?? null));

send({ jsonrpc: '2.0', method: 'notifications/initialized' });
send({ jsonrpc: '2.0', id: 2, method: 'tools/list', params: {} });
const tools = await waitFor(2);
check('tools/list relayed', tools?.result?.tools?.[0]?.name === 'mock_tool', JSON.stringify(tools?.result?.tools ?? null));

check('session id captured and replayed', seenSessionHeaders.includes(sessionId), `headers seen: ${JSON.stringify(seenSessionHeaders)}`);

child.stdin.end();
server.close();

console.log('');
console.log(failures === 0 ? 'BRIDGE OK' : `BRIDGE FAILED (${failures})`);
console.log('');
process.exit(failures === 0 ? 0 : 1);
