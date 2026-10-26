#!/usr/bin/env node
/**
 * rea-meshy-probe.mjs
 *
 * Drives the published REA MCP server (rea-agents@6.4.0) over stdio against the
 * Meshy stack that now lives in meshy/, and separately asks the Meshy MCP server
 * itself for its live tool list.
 *
 * The point of the two passes is the comparison:
 *
 *   - REA's `analyze_javascript_application` reconstructs the Meshy MCP server's
 *     application graph *statically*, from the built bundle, without executing
 *     it. That is the reverse-engineering view: what the code says it does.
 *   - The live `tools/list` handshake says what the server actually publishes to
 *     a client. That is the contract view.
 *
 * Where the two disagree, the disagreement is the finding. A tool REA can see in
 * the source but the handshake does not list is dead code; a tool the handshake
 * lists that REA cannot attribute is built or injected somewhere REA does not
 * follow.
 *
 * The full analysis is larger than the MCP stdio response budget, so REA is
 * driven the way its own remediation says: `detail: "summary"` retains the
 * complete Evidence in-session, `inspect_analysis_view` pages it, and
 * `export_evidence_bundle` writes the whole thing to disk.
 *
 * Usage: node rea-meshy-probe.mjs
 */

import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, '..');
const outDir = join(repoRoot, 'rea', 'findings');
const meshyRoot = join(repoRoot, 'meshy');
mkdirSync(outDir, { recursive: true });

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
        const resolve = pending.get(msg.id);
        pending.delete(msg.id);
        resolve(msg);
      }
    }
  });

  child.stderr.setEncoding('utf8');
  child.stderr.on('data', (c) => {
    const s = String(c);
    stderr.push(s);
    if (process.env.REA_VERBOSE) process.stderr.write(`[${label}] ${s}`);
  });

  const request = (method, params, timeoutMs = 300000) => {
    const id = nextId++;
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        pending.delete(id);
        resolve({ id, error: { message: `timeout after ${timeoutMs}ms` } });
      }, timeoutMs);
      pending.set(id, (msg) => { clearTimeout(timer); resolve(msg); });
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    });
  };

  const notify = (method, params) =>
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');

  return {
    child,
    request,
    notify,
    stderrText: () => stderr.join(''),
    stop: () => { try { child.stdin.end(); } catch { /* already gone */ } },
  };
}

/** Pull the human-readable text out of an MCP tool result. */
function textOf(res) {
  if (res?.result?.content) {
    return res.result.content.map((c) => c.text ?? JSON.stringify(c)).join('\n');
  }
  if (res?.error) return `ERROR: ${res.error.message ?? JSON.stringify(res.error)}`;
  return '(empty)';
}

/** Best-effort parse of a tool result whose payload is JSON. */
function jsonOf(res) {
  const t = textOf(res);
  try { return JSON.parse(t); } catch { return null; }
}

/**
 * Find the Evidence id of the *analysis* itself.
 *
 * With `detail: "summary"` REA does not return the analysis inline: it retains
 * it and answers with a projection whose own evidence_id belongs to the
 * projection, not to the analysis. The analysis id is the projection's
 * `parameters.parent_evidence_id`. Handing the projection's id back to
 * inspect_analysis_view fails with `unsupported_target`, because the parent
 * operation it reports is `inspect_analysis_view` rather than the analysis.
 */
function findAnalysisEvidenceId(payload) {
  const parent = payload?.parameters?.parent_evidence_id;
  if (typeof parent === 'string' && /^ev_[a-f0-9]{64}$/.test(parent)) return parent;
  const m = JSON.stringify(payload ?? '').match(/ev_[a-f0-9]{64}/);
  return m ? m[0] : null;
}

function countFiles(dir) {
  let files = 0;
  let bytes = 0;
  const walk = (d) => {
    let entries = [];
    try { entries = readdirSync(d, { withFileTypes: true }); } catch { return; }
    for (const e of entries) {
      if (e.name === 'node_modules' || e.name === '.git') continue;
      const p = join(d, e.name);
      if (e.isDirectory()) walk(p);
      else {
        files += 1;
        try { bytes += statSync(p).size; } catch { /* ignore */ }
      }
    }
  };
  walk(dir);
  return { files, bytes };
}

// ---------------------------------------------------------------- REA session

console.log('Starting REA MCP server (rea-agents@6.4.0)...');
const rea = mcpClient('npx.cmd', ['-y', 'rea-agents@6.4.0', 'mcp'], 'rea');

const init = await rea.request('initialize', {
  protocolVersion: '2024-11-05',
  capabilities: {},
  clientInfo: { name: 'notecode-meshy-probe', version: '0.1.0' },
});
rea.notify('notifications/initialized', {});
const reaInfo = init?.result?.serverInfo ?? null;
const reaProtocol = init?.result?.protocolVersion ?? null;

const catalog = await rea.request('tools/list', {});
const reaTools = catalog?.result?.tools ?? [];
console.log(`REA ${reaInfo?.version ?? '?'} up - protocol ${reaProtocol}, ${reaTools.length} tools advertised.`);

const findTool = (n) => reaTools.find((t) => t.name === n) ?? null;

// ------------------------------------------------- 1. static JS app analysis

const jsTargets = [
  {
    id: 'meshy-mcp-server-dist',
    path: join(meshyRoot, 'meshy-mcp-server', 'dist'),
    what: 'the built Meshy MCP server (@meshy-ai/meshy-mcp-server 0.6.5)',
  },
  {
    id: 'meshy-3d-agent',
    path: join(meshyRoot, 'meshy-3d-agent'),
    what: 'the Meshy agent skill pack',
  },
];

const analyses = [];

for (const t of jsTargets) {
  console.log(`\n=== analyze_javascript_application: ${t.id} ===`);
  const size = countFiles(t.path);
  console.log(`  input: ${t.path} (${size.files} files, ${size.bytes} bytes)`);

  const res = await rea.request(
    'tools/call',
    {
      name: 'analyze_javascript_application',
      arguments: {
        input_path: t.path,
        format: 'directory',
        integrity_policy: 'fail',
        detail: 'summary',
        analysis_timeout_ms: 300000,
      },
    },
    420000,
  );

  const summary = jsonOf(res);
  const evidenceId = findAnalysisEvidenceId(summary ?? res);
  console.log(`  analysis evidence: ${evidenceId ?? '(none)'}`);
  if (!evidenceId) {
    console.log('  no retained evidence - payload follows');
    console.log(textOf(res).slice(0, 1200));
  }

  const entry = {
    id: t.id,
    what: t.what,
    input_path: t.path,
    input_size: size,
    evidence_id: evidenceId,
    summary: summary,
    views: {},
  };

  if (evidenceId) {
    // Page the retained Evidence: the summary first, then every module.
    const views = [
      { label: 'summary', view: { kind: 'summary' } },
      { label: 'modules-0', view: { kind: 'page', collection: 'modules', offset: 0, limit: 200 } },
      { label: 'modules-200', view: { kind: 'page', collection: 'modules', offset: 200, limit: 200 } },
      { label: 'sections', view: { kind: 'page', collection: 'sections', offset: 0, limit: 200 } },
    ];
    for (const v of views) {
      const r = await rea.request(
        'tools/call',
        {
          name: 'inspect_analysis_view',
          arguments: { source: { kind: 'retained-evidence', evidence_id: evidenceId }, view: v.view },
        },
        180000,
      );
      const isErr = r?.result?.isError === true;
      const payload = jsonOf(r) ?? textOf(r);
      console.log(`  view ${v.label}: ${isErr || r?.error ? 'FAIL' : 'ok'} (${textOf(r).length} chars)`);
      entry.views[v.label] = { ok: !isErr && !r?.error, payload };
    }

    // Export the complete Evidence bundle to disk - the full analysis is bigger
    // than the stdio budget, so this is the only way to keep all of it.
    const bundlePath = join(outDir, `rea-meshy-evidence-${t.id}.json`);
    const ex = await rea.request(
      'tools/call',
      { name: 'export_evidence_bundle', arguments: { path: bundlePath, overwrite: true } },
      180000,
    );
    const exOk = !ex?.error && ex?.result?.isError !== true;
    console.log(`  export_evidence_bundle: ${exOk ? 'ok' : 'FAIL'} -> ${bundlePath}`);
    entry.export = { ok: exOk, path: bundlePath, reply: jsonOf(ex) ?? textOf(ex) };
  }

  analyses.push(entry);
  writeFileSync(join(outDir, `rea-meshy-${t.id}.json`), JSON.stringify(entry, null, 2));
  console.log(`  -> rea/findings/rea-meshy-${t.id}.json`);
}

// ----------------------------------------------- 2. live Meshy MCP handshake

console.log('\n=== live Meshy MCP server handshake ===');
const meshy = mcpClient('node', [join(meshyRoot, 'meshy-mcp-server', 'dist', 'index.js')], 'meshy');

const meshyInit = await meshy.request('initialize', {
  protocolVersion: '2024-11-05',
  capabilities: {},
  clientInfo: { name: 'notecode-meshy-probe', version: '0.1.0' },
});
meshy.notify('notifications/initialized', {});

const meshyCatalog = await meshy.request('tools/list', {});
const meshyTools = meshyCatalog?.result?.tools ?? [];
const meshyInstructions = meshyInit?.result?.instructions ?? null;

console.log(`  server: ${meshyInit?.result?.serverInfo?.name} ${meshyInit?.result?.serverInfo?.version}`);
console.log(`  tools published: ${meshyTools.length}`);

const liveTools = meshyTools.map((t) => ({
  name: t.name,
  description: (t.description ?? '').split('\n')[0],
  input_keys: Object.keys(t.inputSchema?.properties ?? {}),
  required: t.inputSchema?.required ?? [],
  output_schema: Boolean(t.outputSchema),
}));

writeFileSync(
  join(outDir, 'meshy-mcp-live-tools.json'),
  JSON.stringify(
    {
      server: meshyInit?.result?.serverInfo ?? null,
      protocolVersion: meshyInit?.result?.protocolVersion ?? null,
      instructions: meshyInstructions,
      tool_count: meshyTools.length,
      tools: liveTools,
      stderr: meshy.stderrText().split('\n').filter(Boolean).slice(0, 60),
    },
    null,
    2,
  ),
);
console.log('  -> rea/findings/meshy-mcp-live-tools.json');

meshy.stop();

// ------------------------------------- 3. cross-check static graph vs live

/**
 * Mine an exported Evidence bundle down to the part worth keeping: the analysis
 * record's statistics, its graph, and the semantic graph. The raw bundle for the
 * MCP server is ~33 MB; this writes a few tens of KB that a person can read.
 */
function mineBundle(path) {
  const bundle = JSON.parse(readFileSync(path, 'utf8'));
  const records = bundle?.records ?? [];
  const analysis = records.find((r) => r?.operation === 'analyze_javascript_application');
  if (!analysis) return null;

  const nr = analysis.normalized_result ?? {};
  const graph = nr.graph ?? {};
  const semantic = nr.semantic_graph ?? {};

  const nodeBrief = (n) => {
    // The tool names REA recovers live inside string-literal node values and
    // in the evidence location, not in the label alone, so keep both.
    const identity = {};
    for (const [k, v] of Object.entries(n?.identity ?? {})) {
      if (typeof v === 'string') identity[k] = v;
    }
    const loc = n?.evidence?.location?.value ?? null;
    const value = n?.properties?.value;
    return {
      node_id: n?.node_id ?? n?.id ?? null,
      kind: n?.kind ?? n?.type ?? null,
      label: n?.label ?? n?.name ?? null,
      identity,
      // Only the names, not the whole literal - the instructions literal alone
      // is a wall of text and would swamp the compact graph.
      names: typeof value === 'string' ? [...new Set(value.match(/meshy_[a-z0-9_]+/g) ?? [])] : [],
      source: loc?.source ?? null,
      line: loc?.start?.line ?? null,
    };
  };

  const graphNodes = (graph.nodes ?? []).map(nodeBrief);
  const semanticNodes = (semantic.nodes ?? []).map(nodeBrief);

  const relations = (semantic.relations ?? []).map((r) => ({
    from: r?.from ?? r?.source ?? r?.from_node_id ?? null,
    to: r?.to ?? r?.target ?? r?.to_node_id ?? null,
    kind: r?.kind ?? r?.type ?? r?.relation ?? null,
  }));

  const edgeKinds = {};
  for (const e of graph.edges ?? []) {
    const k = e?.kind ?? e?.type ?? 'unknown';
    edgeKinds[k] = (edgeKinds[k] ?? 0) + 1;
  }
  const relationKinds = {};
  for (const r of relations) {
    const k = r.kind ?? 'unknown';
    relationKinds[k] = (relationKinds[k] ?? 0) + 1;
  }

  return {
    bundle_path: path,
    evidence_id: analysis.evidence_id,
    provider: analysis.provider,
    artifact: analysis.subject,
    statistics: nr.statistics ?? null,
    summary: nr.summary ?? null,
    coverage: graph.coverage ?? semantic.coverage ?? null,
    limitations: analysis.limitations ?? [],
    graph: {
      node_count: graphNodes.length,
      edge_count: (graph.edges ?? []).length,
      edge_kinds: edgeKinds,
      root_node_ids: graph.root_node_ids ?? [],
      nodes: graphNodes,
    },
    semantic_graph: {
      node_count: semanticNodes.length,
      relation_count: relations.length,
      relation_kinds: relationKinds,
      application_graph_id: semantic.application_graph_id ?? null,
      fingerprints: semantic.fingerprints ?? null,
      nodes: semanticNodes,
      relations,
    },
    integrity_contradictions: nr.integrity_contradictions ?? [],
  };
}

// Mine every exported bundle, and write the compact graph beside it.
const mined = [];
for (const a of analyses) {
  if (!a.export?.ok) continue;
  try {
    const m = mineBundle(a.export.path);
    if (!m) continue;
    const compactPath = join(outDir, `rea-meshy-graph-${a.id}.json`);
    writeFileSync(compactPath, JSON.stringify(m, null, 2));
    mined.push({ id: a.id, ...m, compact_path: compactPath });
    console.log(`  mined ${a.id}: ${m.graph.node_count} graph nodes, ` +
      `${m.graph.edge_count} edges, ${m.semantic_graph.relation_count} relations`);
    console.log(`  -> rea/findings/rea-meshy-graph-${a.id}.json`);
  } catch (e) {
    console.log(`  mining ${a.id} failed: ${e.message}`);
  }
}

// The static view: every node REA recovered, and where each live tool name
// was found in it.
const staticNodes = [];
for (const m of mined) {
  for (const n of [...m.graph.nodes, ...m.semantic_graph.nodes]) {
    staticNodes.push({ ...n, target: m.id });
  }
}
const haystack = mined.map((m) => JSON.stringify(m)).join('\n');

const attributed = liveTools.map((t) => {
  const hit = staticNodes.find((n) => JSON.stringify(n).includes(t.name));
  return {
    name: t.name,
    in_static_analysis: haystack.includes(t.name),
    node_id: hit?.node_id ?? null,
    kind: hit?.kind ?? null,
    source: hit?.source ?? null,
    line: hit?.line ?? null,
  };
});

const staticToolNames = new Set();
for (const s of staticNodes) {
  for (const n of JSON.stringify(s).match(/meshy_[a-z0-9_]+/g) ?? []) staticToolNames.add(n);
}

console.log(`\n=== cross-check ===`);
console.log(`  live tools: ${liveTools.length}`);
console.log(`  static nodes recovered: ${staticNodes.length}`);
console.log(`  live tool names appearing in REA's static view: ${attributed.filter((a) => a.in_static_analysis).length}`);
for (const t of attributed) {
  if (!t.in_static_analysis) console.log(`    not attributed: ${t.name}`);
}

const summary = {
  generated_by: 'scripts/rea-meshy-probe.mjs',
  rea: {
    version: reaInfo?.version ?? null,
    protocolVersion: reaProtocol,
    advertised_tools: reaTools.length,
    js_analyzer: Boolean(findTool('analyze_javascript_application')),
    webmcp_discovery: Boolean(findTool('discover_webmcp_tools')),
    managed_inspector: Boolean(findTool('inspect_managed_artifact')),
    evidence_export: Boolean(findTool('export_evidence_bundle')),
  },
  meshy_mcp: {
    server: meshyInit?.result?.serverInfo ?? null,
    live_tool_count: meshyTools.length,
    tools: liveTools,
  },
  static: {
    evidence_ids: analyses.map((a) => ({ id: a.id, evidence_id: a.evidence_id })),
    node_count: staticNodes.length,
    tool_like_names_in_nodes: [...staticToolNames].sort(),
    mined: mined.map((m) => ({
      id: m.id,
      evidence_id: m.evidence_id,
      provider: m.provider,
      statistics: m.statistics,
      coverage: m.coverage,
      limitation_count: (m.limitations ?? []).length,
      integrity_contradictions: (m.integrity_contradictions ?? []).length,
      graph_node_count: m.graph.node_count,
      graph_edge_count: m.graph.edge_count,
      graph_edge_kinds: m.graph.edge_kinds,
      semantic_node_count: m.semantic_graph.node_count,
      semantic_relation_count: m.semantic_graph.relation_count,
      semantic_relation_kinds: m.semantic_graph.relation_kinds,
      compact_path: m.compact_path,
    })),
  },
  attribution: attributed,
};

writeFileSync(join(outDir, 'rea-meshy-summary.json'), JSON.stringify(summary, null, 2));
console.log('  -> rea/findings/rea-meshy-summary.json');

rea.stop();
console.log('\nDone.');
process.exit(0);
