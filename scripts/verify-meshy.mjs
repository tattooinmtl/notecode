#!/usr/bin/env node
/**
 * verify-meshy.mjs
 *
 * Checks the two Meshy servers the way notecode starts them, so a broken
 * wiring shows up here rather than in the middle of a generation call.
 *
 *   1. `meshy`     - the stdio server, launched through scripts/meshy-mcp.cmd
 *                    exactly as runtime/notecode.config.json launches it.
 *   2. `meshy-dcc` - the gateway, reached through runtime/mcp-stdio-bridge.mjs
 *                    exactly as the streamable-http transport reaches it.
 *
 * Both are asked for their tool list over a real MCP handshake. The counts are
 * asserted, because a server that starts but publishes nothing is the failure
 * that matters here - it looks connected and does nothing.
 *
 * Usage: node scripts/verify-meshy.mjs
 */

import { spawn } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');

/** Spawn an MCP server over stdio and return a request/notify pair. */
function mcpClient(command, args, label) {
  const child = spawn(command, args, { stdio: ['pipe', 'pipe', 'pipe'], shell: true });
  const pending = new Map();
  let nextId = 1;
  let buffer = '';
  const stderr = [];

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
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', (c) => stderr.push(String(c)));

  const request = (method, params, timeoutMs = 60000) =>
    new Promise((resolve) => {
      const id = nextId++;
      const timer = setTimeout(() => {
        pending.delete(id);
        resolve({ id, error: { message: `timeout after ${timeoutMs}ms` } });
      }, timeoutMs);
      pending.set(id, (m) => { clearTimeout(timer); resolve(m); });
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    });

  const notify = (method, params) =>
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');

  return {
    child,
    request,
    notify,
    stderr: () => stderr.join(''),
    stop: () => { try { child.stdin.end(); } catch { /* gone */ } },
  };
}

async function handshake(client, label) {
  const init = await client.request('initialize', {
    protocolVersion: '2024-11-05',
    capabilities: {},
    clientInfo: { name: 'notecode-verify', version: '0.1.0' },
  });
  if (init?.error) throw new Error(`${label}: initialize failed - ${init.error.message}`);
  client.notify('notifications/initialized', {});
  const list = await client.request('tools/list', {});
  if (list?.error) throw new Error(`${label}: tools/list failed - ${list.error.message}`);
  return {
    server: init?.result?.serverInfo ?? null,
    instructions: init?.result?.instructions ?? null,
    tools: list?.result?.tools ?? [],
  };
}

let failures = 0;
function check(label, ok, detail) {
  const mark = ok ? 'PASS' : 'FAIL';
  if (!ok) failures += 1;
  console.log(`  [${mark}] ${label}${detail ? ` - ${detail}` : ''}`);
}

// ------------------------------------------------------ 1. the generation API

console.log('\nmeshy (stdio, via scripts/meshy-mcp.cmd)');
{
  // The same argv the config carries. If the launcher stops finding the
  // server, or the .env, this fails here.
  const launcher = join(root, 'scripts', 'meshy-mcp.cmd');
  const client = mcpClient('cmd.exe', ['/d', '/c', launcher], 'meshy');
  try {
    const res = await handshake(client, 'meshy');
    check('server starts and answers the handshake', true,
      `${res.server?.name} ${res.server?.version}`);
    check('publishes 28 tools', res.tools.length === 28, `got ${res.tools.length}`);

    const names = res.tools.map((t) => t.name);
    for (const t of ['meshy_text_to_3d', 'meshy_get_task_status', 'meshy_download_model']) {
      check(`has ${t}`, names.includes(t));
    }
  } catch (e) {
    check('server starts and answers the handshake', false, e.message);
  } finally {
    client.stop();
  }
}

// ------------------------------------------------------- 2. the DCC gateway

console.log('\nmeshy-dcc (streamable-http, via runtime/mcp-stdio-bridge.mjs)');
{
  const bridge = join(root, 'runtime', 'mcp-stdio-bridge.mjs');
  const client = mcpClient('node', [bridge, 'http://127.0.0.1:19765/mcp'], 'meshy-dcc');
  try {
    const res = await handshake(client, 'meshy-dcc');
    check('gateway answers the handshake', true,
      `${res.server?.name} ${res.server?.version}`);
    check('publishes 4 tools', res.tools.length === 4, `got ${res.tools.length}`);
    const names = res.tools.map((t) => t.name);
    for (const t of ['search', 'describe', 'load_skill', 'call']) {
      check(`has ${t}`, names.includes(t));
    }
  } catch (e) {
    check('gateway answers the handshake', false,
      `${e.message} - is meshy-dcc-server.exe running?`);
  } finally {
    client.stop();
  }
}

console.log(failures === 0 ? '\nAll checks passed.' : `\n${failures} check(s) failed.`);
process.exit(failures === 0 ? 0 : 1);
