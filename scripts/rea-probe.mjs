#!/usr/bin/env node
/**
 * rea-probe.mjs
 *
 * Drives the published REA MCP server (rea-agents@6.4.0) over stdio against one or
 * more target binaries and records every reply under rea/findings/.
 *
 * REA's deep-analysis providers (Ghidra / IDA / Hopper) are bring-your-own and none
 * are installed on this machine, so this probe deliberately records which tools work
 * provider-free. That makes the findings file evidence rather than aspiration.
 *
 * Argument shapes differ per tool: open_binary / inspect_pe_resources take `path`,
 * while the session-scoped tools take a `document` handle returned by the server.
 * The probe threads that handle through automatically.
 *
 * Usage: node rea-probe.mjs <binary> [<binary> ...]
 */

import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const outDir = join(here, '..', 'rea', 'findings');
mkdirSync(outDir, { recursive: true });

const targets = process.argv.slice(2);
if (targets.length === 0) {
  console.error('usage: rea-probe.mjs <binary> [<binary> ...]');
  process.exit(2);
}

const child = spawn('npx.cmd', ['-y', 'rea-agents@6.4.0', 'mcp'], {
  stdio: ['pipe', 'pipe', 'pipe'],
  shell: true,
});

const pending = new Map();
let nextId = 1;
let buffer = '';

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
      const resolve = pending.get(msg.id);
      pending.delete(msg.id);
      resolve(msg);
    }
  }
});
child.stderr.setEncoding('utf8');
child.stderr.on('data', (c) => {
  const s = String(c);
  if (!s.includes('DeprecationWarning') && !s.includes('trace-deprecation')) process.stderr.write(`[rea] ${s}`);
});

function request(method, params, timeoutMs = 240000) {
  const id = nextId++;
  return new Promise((resolve) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      resolve({ id, error: { message: `timeout after ${timeoutMs}ms` } });
    }, timeoutMs);
    pending.set(id, (msg) => { clearTimeout(timer); resolve(msg); });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
}

const notify = (method, params) =>
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');

const init = await request('initialize', {
  protocolVersion: '2024-11-05',
  capabilities: {},
  clientInfo: { name: 'notecode-probe', version: '0.1.0' },
});
notify('notifications/initialized', {});

const catalog = await request('tools/list', {});
const toolCount = catalog?.result?.tools?.length ?? 0;
console.log(`REA ${init?.result?.serverInfo?.version ?? ''} up - ${toolCount} tools advertised.`);

const textOf = (res) => {
  if (res?.result?.content) {
    return res.result.content.map((c) => c.text ?? JSON.stringify(c)).join('\n');
  }
  if (res?.error) return `ERROR: ${res.error.message ?? JSON.stringify(res.error)}`;
  return '(empty)';
};

const report = { tools: toolCount, serverInfo: init?.result?.serverInfo, targets: [] };

for (const target of targets) {
  console.log(`\n=== ${target} ===`);
  const entry = { target, steps: [] };
  const record = (tool, ok, isError, text) => {
    entry.steps.push({ tool, ok, isError, text });
    console.log(`  ${ok && !isError ? 'ok    ' : 'skip  '} ${tool}`);
  };

  // 1. open the binary - provider-free PE open.
  const opened = await request('tools/call', { name: 'open_binary', arguments: { path: target } });
  const openText = textOf(opened);
  const openOk = !opened.error && !opened.result?.isError;
  record('open_binary', openOk, opened.result?.isError === true, openText);
  entry.open = openOk ? openText : null;

  // 2. find the document handle the session-scoped tools expect.
  let document = null;
  const cur = await request('tools/call', { name: 'current_document', arguments: {} });
  const curText = textOf(cur);
  try {
    const parsed = JSON.parse(curText);
    document = parsed?.result?.document?.id ?? parsed?.document?.id ?? parsed?.result?.id ?? null;
  } catch { /* not json */ }
  record('current_document', !cur.error, cur.result?.isError === true, curText);
  entry.document = document;

  // 3. provider-free PE resource inspection.
  const pe = await request('tools/call', { name: 'inspect_pe_resources', arguments: { path: target } });
  const peText = textOf(pe);
  record('inspect_pe_resources', !pe.error, pe.result?.isError === true, peText);
  entry.peResources = pe.result?.isError ? peText : peText;

  // 4. session-scoped analysis - these need a Ghidra/IDA provider.
  const sessionTools = [
    ['list_segments', {}],
    ['list_procedures', {}],
    ['list_strings', {}],
    ['list_names', {}],
    ['binary_overview', {}],
  ];
  entry.session = [];
  for (const [tool, extra] of sessionTools) {
    const args = document ? { document, ...extra } : { ...extra };
    const res = await request('tools/call', { name: tool, arguments: args }, 120000);
    const t = textOf(res);
    const ok = !res.error && !res.result?.isError;
    record(tool, ok, res.result?.isError === true, t);
    entry.session.push({ tool, ok, isError: res.result?.isError === true, text: t.slice(0, 4000) });
  }

  report.targets.push(entry);
}

const safe = targets.map((t) => t.replace(/[:\\/]/g, '_')).join('__');
writeFileSync(join(outDir, `rea-session-${safe}.json`), JSON.stringify(report, null, 2));

const total = report.targets.reduce((n, t) => n + t.steps.length, 0);
const passed = report.targets.reduce((n, t) => n + t.steps.filter((s) => s.ok && !s.isError).length, 0);
console.log(`\n${passed}/${total} REA steps succeeded provider-free.`);
console.log(`-> rea/findings/rea-session-${safe}.json`);

child.stdin.end();
process.exit(0);
