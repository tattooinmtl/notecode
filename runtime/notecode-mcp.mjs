#!/usr/bin/env node
/**
 * notecode-mcp.mjs
 *
 * The AI's file hook. Registers as an MCP server (stdio) so llama-server picks
 * the tools up at startup and the local model can call them directly.
 *
 *   save_as(path, content, type?)  write a file with an extension-aware name and
 *                                  open it in the notecode editor
 *   open_in_editor(path)           open an existing file in the editor
 *   list_files(dir?)               list the workspace
 *   read_file(path)                read a file
 *   list_supported_types()         the extension/alias table the model may use
 *
 * "save as markdown", "save as md", "save as plan.md", "save as agents.md",
 * "save as main.cpp" and "save as index.html" all resolve through the shared
 * registry in runtime/file-types.json - the same table the editor uses to pick
 * its syntax mode, so the model and the editor can never disagree about a type.
 *
 * Opening a file is done through a queue file that notecode.exe watches, because
 * this server is a separate process and cannot touch the UI directly.
 */

import { readFileSync, writeFileSync, mkdirSync, existsSync, readdirSync, statSync, appendFileSync,
         renameSync, copyFileSync, cpSync, rmSync } from 'node:fs';
import { dirname, join, extname, basename, resolve, isAbsolute, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';

const here = dirname(fileURLToPath(import.meta.url));
const configPath = join(here, 'notecode.config.json');
const registryPath = join(here, 'file-types.json');
const queuePath = join(here, 'open-queue.jsonl');
const requestPath = join(here, 'app-requests.jsonl');
const resultDir = join(here, 'app-results');
const contextPath = join(here, 'app-context.json');
const tracePath = join(here, 'mcp-trace.log');

/**
 * What this server was asked and what it answered.
 *
 * The server runs as a child process, and when the caller is llama-server the
 * only thing that comes back is the answer. If a call hangs, the question is
 * "what did it get, and what did it say" - and the answer has to be written
 * down, because after the fact nobody can ask the process.
 */
function trace(line) {
  try {
    appendFileSync(tracePath, `${new Date().toISOString()} ${line}\n`, 'utf8');
  } catch { /* a log that cannot be written is not worth failing over */ }
}

/**
 * Expands %VARIABLE% in every string in a parsed configuration.
 *
 * A path to something in the user's own folders - the skills, the kimi-code
 * install - is written with %USERPROFILE% in it so that the same file works
 * on every machine. A variable that is not set is left exactly as it is.
 */
function expandEnv(node) {
  if (Array.isArray(node)) {
    for (let i = 0; i < node.length; i++) {
      if (typeof node[i] === 'string') node[i] = expandString(node[i]);
      else expandEnv(node[i]);
    }
  } else if (node && typeof node === 'object') {
    for (const key of Object.keys(node)) {
      if (typeof node[key] === 'string') node[key] = expandString(node[key]);
      else expandEnv(node[key]);
    }
  }
  return node;
}

function expandString(text) {
  return text.replace(/%([A-Za-z_][A-Za-z0-9_]*)%/g, (whole, name) => {
    const value = process.env[name];
    return value === undefined ? whole : value;
  });
}

let cfg = {};
try { cfg = expandEnv(JSON.parse(readFileSync(configPath, 'utf8'))); } catch { /* fall back below */ }
let types = {};
try { types = JSON.parse(readFileSync(registryPath, 'utf8')); } catch { /* fall back below */ }

const WORKSPACE = cfg.workspace?.root || join(here, '..', 'workspace');
const ALIASES = types.aliases || {};
const MODES = types.modes || {};
const BINARY = new Set(types.binary || []);
const DEFAULT_MODE = types.defaultMode || 'text/plain';
const SNIFF = types.sniff?.rules || [];

// ---------------------------------------------------------------- helpers

/** Normalise a type the model might name into a canonical extension. */
function resolveExtension(input) {
  if (!input) return null;
  const raw = String(input).trim().toLowerCase();
  if (!raw) return null;
  if (ALIASES[raw]) return ALIASES[raw];
  // A bare extension, e.g. "md" or "cpp".
  if (/^[a-z0-9+#]+$/.test(raw) && MODES[raw]) return raw;
  // A filename, e.g. "plan.md".
  const ext = extname(raw).replace(/^\./, '').toLowerCase();
  if (ext && (ALIASES[ext] || MODES[ext])) return ALIASES[ext] || ext;
  return null;
}

/** Work out the extension for a path, honouring an explicit type override. */
function targetExtension(path, type) {
  if (type) {
    const fromType = resolveExtension(type);
    if (fromType) return fromType;
  }
  const ext = extname(path).replace(/^\./, '').toLowerCase();
  if (ext) return ALIASES[ext] || ext;
  return null;
}

/** The CodeMirror mode for a file, by extension, falling back to content sniffing. */
function modeFor(ext, content) {
  if (ext && MODES[ext]) return MODES[ext];
  if (typeof content === 'string') {
    for (const rule of SNIFF) {
      if (rule.startsWith && content.startsWith(rule.startsWith)) return MODES[rule.ext] || DEFAULT_MODE;
      if (rule.contains && content.includes(rule.contains)) return MODES[rule.ext] || DEFAULT_MODE;
    }
  }
  return DEFAULT_MODE;
}

/** Keep every write inside the workspace. */
function resolveInsideWorkspace(p) {
  const abs = isAbsolute(p) ? resolve(p) : resolve(WORKSPACE, p);
  const rel = relative(WORKSPACE, abs);
  if (rel.startsWith('..') || isAbsolute(rel)) {
    throw new Error(`path escapes the workspace: ${p}`);
  }
  return abs;
}

function isBinary(ext) {
  return BINARY.has(String(ext || '').toLowerCase());
}

/** Ask notecode.exe to open a file in its editor. */
function queueOpen(absPath, reason) {
  try {
    appendFileSync(queuePath, JSON.stringify({ path: absPath, reason, at: new Date().toISOString() }) + '\n');
  } catch { /* the queue is best-effort */ }
}

// ------------------------------------------------------------- the app channel
//
// This server is a separate process, so it cannot touch the notecode window.
// The three surfaces the AI has to reach - the terminal, the editor and the
// transcript - live in that window, so they are asked for over a small file
// protocol: the request goes into runtime/app-requests.jsonl and the answer
// comes back in runtime/app-results/<id>.json.
//
// When notecode.exe is not running nobody answers, so the call comes back as
// null and the caller decides what to do. A tool that still works on its own is
// worth more than one that only works with the IDE open.

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let requestSeq = 0;

/**
 * Sends one request to the app and waits for the answer.
 *
 * Returns the answer object, or null when no app picked the request up.
 */
async function callApp(kind, args, timeoutMs = 60000) {
  const id = `${Date.now().toString(36)}-${(++requestSeq).toString(36)}-${process.pid}`;
  const resultPath = join(resultDir, `${id}.json`);
  const ackPath = join(resultDir, `${id}.ack`);

  try {
    mkdirSync(resultDir, { recursive: true });
    appendFileSync(requestPath,
      JSON.stringify({ id, kind, args: args ?? {}, at: new Date().toISOString() }) + '\n');
  } catch (e) {
    return { ok: false, error: `could not reach the notecode app: ${e.message}` };
  }

  // Wait for the app to pick the request up. Nobody picking it up means the
  // app is closed, and the caller can fall back instead of waiting for nothing.
  const ackDeadline = Date.now() + 3000;
  while (Date.now() < ackDeadline && !existsSync(ackPath)) await sleep(100);
  if (!existsSync(ackPath)) return null;

  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (existsSync(resultPath)) {
      try { return JSON.parse(readFileSync(resultPath, 'utf8')); }
      catch { /* the app is still writing it */ }
    }
    await sleep(150);
  }
  return { ok: false, error: `the notecode app did not answer within ${Math.round(timeoutMs / 1000)}s` };
}

/** What the app told us about the job it is doing right now. */
function appContext() {
  try { return JSON.parse(readFileSync(contextPath, 'utf8')); } catch { return {}; }
}

/** Takes the colour codes out of a command's output. */
function stripAnsi(text) {
  // eslint-disable-next-line no-control-regex
  return String(text ?? '').replace(/\u001b\[[0-9;?]*[ -\/]*[@-~]|\u001b[@-Z\\-_]/g, '');
}

/** Runs a command without the app, for when the IDE is not open. */
function runDirect(command, cwd, timeoutMs) {
  return new Promise((resolve) => {
    const shell = process.env.ComSpec || 'cmd.exe';
    let child;
    try {
      child = spawn(shell, ['/d', '/s', '/c', command], { cwd, windowsHide: true });
    } catch (e) {
      resolve(`[error] ${e.message}`);
      return;
    }

    let out = '';
    let err = '';
    const timer = setTimeout(() => { try { child.kill(); } catch { /* already gone */ } }, timeoutMs);
    child.stdout?.on('data', (d) => { out += d.toString(); });
    child.stderr?.on('data', (d) => { err += d.toString(); });
    child.on('error', (e) => { clearTimeout(timer); resolve(`[error] ${e.message}`); });
    child.on('close', (code) => {
      clearTimeout(timer);
      const body = stripAnsi(out + (err ? `\n${err}` : '')).trim();
      resolve(`exit code ${code}\n${body}`);
    });
  });
}

/**
 * The skill roots, from the app's own config.
 *
 * Skills live outside the workspace, in folders the user keeps their skill
 * library in. The AI may read and write there, and nowhere else.
 */
function skillRoots() {
  const roots = cfg.skills?.roots ?? [];
  return Array.isArray(roots) ? roots.filter((r) => typeof r === 'string' && r.trim()) : [];
}

/** True when the path is inside one of the skill roots. */
function insideSkillRoot(abs) {
  return skillRoots().some((root) => {
    const rel = relative(resolve(root), abs);
    return rel === '' || (!rel.startsWith('..') && !isAbsolute(rel));
  });
}

/** True when the path is the file the user picked for the skill fixer. */
function isSkillTarget(abs) {
  const target = appContext().skillTarget;
  if (!target) return false;
  return resolve(target).toLowerCase() === abs.toLowerCase();
}

/** A skill file the AI is allowed to read or write. */
function resolveSkillPath(input, { mustBeMarkdown = true } = {}) {
  const raw = String(input || '').trim();
  if (!raw) return { error: 'path is required' };

  const candidates = [];
  if (isAbsolute(raw)) candidates.push(resolve(raw));
  for (const root of skillRoots()) candidates.push(resolve(root, raw));

  for (const abs of candidates) {
    if (!insideSkillRoot(abs) && !isSkillTarget(abs)) continue;
    if (mustBeMarkdown && extname(abs).toLowerCase() !== '.md') {
      return { error: `only .md files are skills, and ${abs} is not one` };
    }
    return { abs };
  }
  return { error: `${raw} is not in a skill folder and is not the file the user picked` };
}

/** The skill library, as the app sees it. */
function listSkillFiles() {
  const out = [];
  for (const root of skillRoots()) {
    if (!existsSync(root)) continue;
    let dirs = [];
    try { dirs = readdirSync(root, { withFileTypes: true }).filter((e) => e.isDirectory()).map((e) => e.name); }
    catch { continue; }
    for (const name of dirs) {
      const file = join(root, name, 'SKILL.md');
      if (!existsSync(file)) continue;
      let description = '';
      try {
        const head = readFileSync(file, 'utf8').split('\n').slice(0, 20);
        const line = head.find((l) => l.trim().toLowerCase().startsWith('description:'));
        if (line) description = line.split(':').slice(1).join(':').trim().replace(/^["']|["']$/g, '');
      } catch { /* the description is optional */ }
      out.push({ name, description, path: file });
    }
  }
  return out;
}

// ------------------------------------------------------------------ tools

const TOOLS = [
  {
    name: 'save_as',
    title: 'Save as a file',
    description:
      'Write a file and open it in the notecode editor. The extension decides the file type. ' +
      'path may be a bare filename ("plan.md", "agents.md", "main.cpp", "index.html") or a ' +
      'directory plus name ("docs/plan.md"). type may name the language instead ("markdown", ' +
      '"md", "cpp", "html", "php", "js", "py", "rust") and is used when path has no extension. ' +
      'Call this instead of write_file whenever the user says "save as ...".',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Filename with extension, relative to the workspace.' },
        content: { type: 'string', description: 'Full file content.' },
        type: { type: 'string', description: 'Optional language/extension alias when path has none.' },
        open: { type: 'boolean', description: 'Open the file in the editor (default true).' }
      },
      required: ['path', 'content']
    }
  },
  {
    name: 'open_in_editor',
    title: 'Open a file in the editor',
    description: 'Open an existing file in the notecode editor, detecting its type from the extension.',
    inputSchema: {
      type: 'object',
      properties: { path: { type: 'string', description: 'File path relative to the workspace.' } },
      required: ['path']
    }
  },
  {
    name: 'list_files',
    title: 'List workspace files',
    description: 'List files in the workspace (optionally under a subdirectory).',
    inputSchema: {
      type: 'object',
      properties: {
        dir: { type: 'string', description: 'Subdirectory relative to the workspace.' },
        recursive: { type: 'boolean', description: 'Walk subdirectories (default true).' }
      }
    }
  },
  {
    name: 'read_file',
    title: 'Read a file',
    description: 'Read the text content of a workspace file.',
    inputSchema: {
      type: 'object',
      properties: { path: { type: 'string', description: 'File path relative to the workspace.' } },
      required: ['path']
    }
  },
  {
    name: 'list_supported_types',
    title: 'List supported file types',
    description:
      'The extension/alias table this editor understands, so you can pick the right extension when saving.',
    inputSchema: { type: 'object', properties: {} }
  },
  {
    name: 'make_folder',
    title: 'Create a folder',
    description:
      'Create a folder and any missing parent folders. Use this when the user asks for a new directory, ' +
      'for example "make a folder called website" or "put it in a sources folder".',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Folder path relative to the workspace, for example "website".' }
      },
      required: ['path']
    }
  },
  {
    name: 'move_path',
    title: 'Move or rename a file or folder',
    description:
      'Move a file or a folder to another place, which also renames it. The destination folder is created ' +
      'if it is not there. Use this for "move X into Y", "rename X to Y" and "put X in the website folder".',
    inputSchema: {
      type: 'object',
      properties: {
        from: { type: 'string', description: 'The existing file or folder, relative to the workspace.' },
        to: { type: 'string', description: 'Where it goes, relative to the workspace. A new name is a rename.' }
      },
      required: ['from', 'to']
    }
  },
  {
    name: 'copy_path',
    title: 'Copy a file or folder',
    description:
      'Copy a file or a whole folder to another place. Use this for "copy X to Y", "duplicate X" and ' +
      '"paste a copy of X into the sources folder".',
    inputSchema: {
      type: 'object',
      properties: {
        from: { type: 'string', description: 'The file or folder to copy, relative to the workspace.' },
        to: { type: 'string', description: 'Where the copy goes, relative to the workspace.' }
      },
      required: ['from', 'to']
    }
  },
  {
    name: 'delete_path',
    title: 'Delete a file or folder',
    description:
      'Delete a file, or a folder and everything in it. Only the workspace can be touched. Use this when ' +
      'the user asks for a file or a folder to be removed.',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'The file or folder to delete, relative to the workspace.' }
      },
      required: ['path']
    }
  },
  {
    name: 'write_text',
    title: 'Write a text file without opening it',
    description:
      'Write a text file and leave the editor alone. Use save_as instead when the user should see the file ' +
      'open in the editor.',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'File path relative to the workspace.' },
        content: { type: 'string', description: 'Full file content.' }
      },
      required: ['path', 'content']
    }
  },

  // ------------------------------------------------------- the three surfaces
  //
  // The terminal, the editor and the transcript. The app owns all three, so
  // these tools go through the app channel rather than touching the disk.

  {
    name: 'run_command',
    title: 'Run a shell command in the app terminal',
    description:
      'Run one shell command and get its output back. The command runs in the notecode workspace, ' +
      'in a shell of its own, and the command and its output are shown in the terminal pane so the ' +
      'user watches it happen. One command per call. Use this to build, test, inspect or run ' +
      'anything in the workspace. The answer carries the exit code and the output.',
    inputSchema: {
      type: 'object',
      properties: {
        command: { type: 'string', description: 'The command to run, for example "npm test" or "git status".' },
        cwd: { type: 'string', description: 'Folder to run it in. Defaults to the workspace root.' },
        timeout_ms: { type: 'number', description: 'How long to wait, in milliseconds. Default 60000, max 300000.' }
      },
      required: ['command']
    }
  },

  {
    name: 'get_editor_buffer',
    title: 'Read the buffer in the editor',
    description:
      'Read what is in the editor right now. With no path it returns the live buffer of the tab the ' +
      'user is looking at, including edits they have not saved. With a path it returns that file, from ' +
      'disk when it is not the open tab.',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Optional file to read. Omit for the tab that is open.' }
      }
    }
  },

  {
    name: 'set_editor_text',
    title: 'Write text into the editor',
    description:
      'Put text into the editor. With no path it replaces the content of the tab the user is looking at ' +
      'and saves the file, so they see the change at once. With a path it writes that file and opens it ' +
      'as a tab.',
    inputSchema: {
      type: 'object',
      properties: {
        text: { type: 'string', description: 'The new content of the file.' },
        path: { type: 'string', description: 'Optional file to write. Omit for the tab that is open.' }
      },
      required: ['text']
    }
  },

  {
    name: 'post_to_chat',
    title: 'Write a note into the transcript',
    description:
      'Put a note into the agent transcript, where the user reads it. Use this for something that is not ' +
      'part of your answer: a checklist, a warning, a summary of what you changed, or a question. It ' +
      'appears as a note from notecode, not as your answer.',
    inputSchema: {
      type: 'object',
      properties: {
        text: { type: 'string', description: 'The note. Plain text or simple markdown.' },
        kind: { type: 'string', description: 'A short label for the note, for example "checklist" or "warning".' }
      },
      required: ['text']
    }
  },

  {
    name: 'read_conversation',
    title: 'Read the conversation so far',
    description:
      'Read the conversation in the transcript so far - what the user said, what you answered and which ' +
      'tools you called. Use this when you need to look back at something earlier in the session.',
    inputSchema: {
      type: 'object',
      properties: {
        limit: { type: 'number', description: 'How many of the last entries to return. Default 20.' }
      }
    }
  },

  // ------------------------------------------------------------------ skills

  {
    name: 'list_skills',
    title: 'List the skills on this machine',
    description:
      'List the skills that are installed, with the folder each one lives in and what it is for. This is ' +
      'the house style a skill has to match.',
    inputSchema: { type: 'object', properties: {} }
  },

  {
    name: 'read_skill',
    title: 'Read a skill file',
    description:
      'Read a skill file. The path can be absolute, or the name of a skill folder under a skill root.',
    inputSchema: {
      type: 'object',
      properties: { path: { type: 'string', description: 'The skill file to read.' } },
      required: ['path']
    }
  },

  {
    name: 'write_skill',
    title: 'Write a skill file',
    description:
      'Write a skill file. Only files under a skill folder, and the file the user picked for the skill ' +
      'fixer, can be written.',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'The skill file to write.' },
        content: { type: 'string', description: 'The whole new content of the file.' }
      },
      required: ['path', 'content']
    }
  },

  {
    name: 'describe_notecode',
    title: 'What this app can actually do',
    description:
      'The ground truth about notecode: the tools that exist, the three surfaces they reach and the ' +
      'shape a skill has to have. Use this before writing a skill, so the skill tells the model to do ' +
      'things this app really supports.',
    inputSchema: { type: 'object', properties: {} }
  }
];

async function callTool(name, args) {
  switch (name) {
    case 'save_as': {
      const rawPath = String(args.path || '').trim();
      if (!rawPath) return '[error] path is required';
      const content = args.content == null ? '' : String(args.content);

      let ext = targetExtension(rawPath, args.type);
      let target = rawPath;

      // "save as markdown" with no filename -> pick a sensible default name.
      if (!extname(rawPath).replace(/^\./, '')) {
        ext = ext || resolveExtension(args.type) || 'md';
        target = `${rawPath}.${ext}`;
      }
      ext = ext || 'txt';

      if (isBinary(ext)) {
        return `[error] ".${ext}" is a binary format and cannot be written as text`;
      }

      const abs = resolveInsideWorkspace(target);
      mkdirSync(dirname(abs), { recursive: true });
      writeFileSync(abs, content, 'utf8');

      const mode = modeFor(ext, content);
      const bytes = Buffer.byteLength(content, 'utf8');
      const lines = content.split('\n').length;

      if (args.open !== false) queueOpen(abs, 'save_as');

      return `Saved ${relative(WORKSPACE, abs)}\n` +
             `  path     : ${abs}\n` +
             `  type     : .${ext}  (${mode})\n` +
             `  size     : ${bytes} bytes, ${lines} lines\n` +
             `  editor   : ${args.open !== false ? 'opened in the notecode editor' : 'not opened'}`;
    }

    case 'open_in_editor': {
      const abs = resolveInsideWorkspace(String(args.path || ''));
      if (!existsSync(abs)) return `[error] not found: ${abs}`;
      const ext = extname(abs).replace(/^\./, '').toLowerCase();
      if (isBinary(ext)) return `[error] ".${ext}" is binary and cannot be shown in the editor`;
      const content = readFileSync(abs, 'utf8');
      queueOpen(abs, 'open_in_editor');
      return `Opened ${relative(WORKSPACE, abs)} in the notecode editor (${modeFor(ext, content)}).`;
    }

    case 'list_files': {
      const dir = args.dir ? resolveInsideWorkspace(args.dir) : WORKSPACE;
      if (!existsSync(dir)) return `[error] not found: ${dir}`;
      const out = [];
      const walk = (d, depth) => {
        if (args.recursive === false && depth > 0) return;
        let entries = [];
        try { entries = readdirSync(d, { withFileTypes: true }); } catch { return; }
        for (const e of entries) {
          if (e.name.startsWith('.') || e.name === 'node_modules') continue;
          const full = join(d, e.name);
          if (e.isDirectory()) {
            out.push(relative(WORKSPACE, full) + '/');
            walk(full, depth + 1);
          } else {
            const ext = extname(e.name).replace(/^\./, '').toLowerCase();
            const size = statSync(full).size;
            out.push(`${relative(WORKSPACE, full)}  (${size} bytes${ext ? ', .' + ext : ''})`);
          }
        }
      };
      walk(dir, 0);
      return out.length ? out.join('\n') : '(empty)';
    }

    case 'read_file': {
      const abs = resolveInsideWorkspace(String(args.path || ''));
      if (!existsSync(abs)) return `[error] not found: ${abs}`;
      const ext = extname(abs).replace(/^\./, '').toLowerCase();
      if (isBinary(ext)) return `[error] ".${ext}" is binary`;
      return readFileSync(abs, 'utf8');
    }

    case 'list_supported_types': {
      const exts = [...new Set(Object.keys(MODES))].sort();
      const aliases = Object.entries(ALIASES)
        .filter(([k]) => !k.startsWith('$'))
        .map(([k, v]) => `${k} -> .${v}`);
      return `Extensions (${exts.length}):\n  ${exts.join(', ')}\n\n` +
             `Type aliases you may use with save_as:\n  ${aliases.join(', ')}`;
    }

    case 'make_folder': {
      const abs = resolveInsideWorkspace(String(args.path || '').trim());
      if (abs === WORKSPACE) return 'The workspace root is already there.';
      if (existsSync(abs)) {
        const st = statSync(abs);
        if (!st.isDirectory()) return `[error] ${relative(WORKSPACE, abs)} exists and is not a folder`;
        return `${relative(WORKSPACE, abs)} already exists.`;
      }
      mkdirSync(abs, { recursive: true });
      return `Created the folder ${relative(WORKSPACE, abs)}/`;
    }

    case 'move_path': {
      const from = resolveInsideWorkspace(String(args.from || '').trim());
      const to = resolveInsideWorkspace(String(args.to || '').trim());
      if (from === WORKSPACE) return '[error] the workspace root cannot be moved';
      if (!existsSync(from)) return `[error] not found: ${relative(WORKSPACE, from)}`;
      if (existsSync(to)) return `[error] ${relative(WORKSPACE, to)} already exists`;

      mkdirSync(dirname(to), { recursive: true });
      renameSync(from, to);

      // A moved file that was open in the editor has to follow it.
      if (statSync(to).isFile()) queueOpen(to, 'move_path');
      return `Moved ${relative(WORKSPACE, from)} to ${relative(WORKSPACE, to)}`;
    }

    case 'copy_path': {
      const from = resolveInsideWorkspace(String(args.from || '').trim());
      const to = resolveInsideWorkspace(String(args.to || '').trim());
      if (from === WORKSPACE) return '[error] the workspace root cannot be copied';
      if (!existsSync(from)) return `[error] not found: ${relative(WORKSPACE, from)}`;
      if (existsSync(to)) return `[error] ${relative(WORKSPACE, to)} already exists`;

      mkdirSync(dirname(to), { recursive: true });
      if (statSync(from).isDirectory()) {
        cpSync(from, to, { recursive: true });
      } else {
        copyFileSync(from, to);
        queueOpen(to, 'copy_path');
      }
      return `Copied ${relative(WORKSPACE, from)} to ${relative(WORKSPACE, to)}`;
    }

    case 'delete_path': {
      const abs = resolveInsideWorkspace(String(args.path || '').trim());
      if (abs === WORKSPACE) return '[error] the workspace root cannot be deleted';
      if (!existsSync(abs)) return `[error] not found: ${relative(WORKSPACE, abs)}`;

      const st = statSync(abs);
      rmSync(abs, { recursive: true, force: true });
      return `Deleted ${relative(WORKSPACE, abs)}${st.isDirectory() ? '/' : ''}`;
    }

    case 'write_text': {
      const abs = resolveInsideWorkspace(String(args.path || '').trim());
      const content = args.content == null ? '' : String(args.content);
      const ext = extname(abs).replace(/^\./, '').toLowerCase();
      if (isBinary(ext)) return `[error] ".${ext}" is a binary format and cannot be written as text`;

      mkdirSync(dirname(abs), { recursive: true });
      writeFileSync(abs, content, 'utf8');
      return `Wrote ${relative(WORKSPACE, abs)} (${Buffer.byteLength(content, 'utf8')} bytes)`;
    }

    // ------------------------------------------------------- the three surfaces

    case 'run_command': {
      const command = String(args.command || '').trim();
      if (!command) return '[error] command is required';

      const timeout = Math.max(1000, Math.min(Number(args.timeout_ms) || 60000, 300000));
      const cwd = args.cwd ? resolveInsideWorkspace(String(args.cwd)) : WORKSPACE;

      const answer = await callApp('run_command', { command, cwd, timeout_ms: timeout }, timeout + 10000);
      if (answer) {
        return answer.ok ? answer.text : `[error] ${answer.error}`;
      }

      // The IDE is not open, so there is no terminal to run it in. Run it here
      // instead, in the same folder, so the tool still does the job.
      const direct = await runDirect(command, cwd, timeout);
      return `${direct}\n\n(the notecode app was not running, so this ran outside its terminal)`;
    }

    case 'get_editor_buffer': {
      const answer = await callApp('get_editor_buffer', { path: args.path ?? null }, 15000);
      if (answer) return answer.ok ? answer.text : `[error] ${answer.error}`;
      return '[error] the notecode app is not running, so the editor is not reachable.';
    }

    case 'set_editor_text': {
      const text = args.text == null ? '' : String(args.text);
      const answer = await callApp('set_editor_text', { text, path: args.path ?? null }, 20000);
      if (answer) return answer.ok ? answer.text : `[error] ${answer.error}`;
      return '[error] the notecode app is not running, so the editor is not reachable.';
    }

    case 'post_to_chat': {
      const text = String(args.text || '').trim();
      if (!text) return '[error] text is required';
      const answer = await callApp('post_to_chat', { text, kind: args.kind ?? null }, 15000);
      if (answer) return answer.ok ? answer.text : `[error] ${answer.error}`;
      return '[error] the notecode app is not running, so the transcript is not reachable.';
    }

    case 'read_conversation': {
      const limit = Math.max(1, Math.min(Number(args.limit) || 20, 200));
      const answer = await callApp('read_conversation', { limit }, 15000);
      if (answer) return answer.ok ? answer.text : `[error] ${answer.error}`;
      return '[error] the notecode app is not running, so the transcript is not reachable.';
    }

    // ------------------------------------------------------------------ skills

    case 'list_skills': {
      const skills = listSkillFiles();
      if (!skills.length) {
        return `No skills are installed. The skill folders are in notecode.config.json under "skills".`;
      }
      return skills
        .map((s) => `- **${s.name}** - ${s.description || '(no description)'}\n  ${s.path}`)
        .join('\n');
    }

    case 'read_skill': {
      const found = resolveSkillPath(args.path);
      if (found.error) return `[error] ${found.error}`;
      if (!existsSync(found.abs)) return `[error] not found: ${found.abs}`;
      return readFileSync(found.abs, 'utf8');
    }

    case 'write_skill': {
      const found = resolveSkillPath(args.path);
      if (found.error) return `[error] ${found.error}`;
      const content = args.content == null ? '' : String(args.content);
      mkdirSync(dirname(found.abs), { recursive: true });
      writeFileSync(found.abs, content, 'utf8');
      return `Wrote ${found.abs} (${Buffer.byteLength(content, 'utf8')} bytes)`;
    }

    case 'describe_notecode': {
      return describeNotecode();
    }

    default:
      return `[error] unknown tool: ${name}`;
  }
}

/**
 * The ground truth about notecode, for the skill fixer and for anything else
 * that needs to know what this app can really do.
 */
function describeNotecode() {
  const notecodeTools = TOOLS.map((t) => `- \`${t.name}\` - ${t.title}`).join('\n');
  const builtins = [
    '- `read_file` - read a file',
    '- `file_glob_search` - find files by name',
    '- `grep_search` - search inside files',
    '- `write_file` - write a file',
    '- `edit_file` - change part of a file',
    '- `exec_shell_command` - run a shell command',
    '- `get_info` - information about the workspace'
  ].join('\n');

  const roots = skillRoots();
  const rootList = roots.length
    ? roots.map((r) => `- ${r}`).join('\n')
    : '- (none configured)';

  return `# notecode

notecode is a desktop coding IDE. It runs a local llama.cpp server and can also talk to a
provider you bring yourself. The model drives the app through tools, and the app has three
surfaces it can reach.

## The three surfaces

1. **The terminal** - a real console (ConPTY + xterm.js). \`run_command\` runs one command
   there and returns the output and the exit code. The user watches it happen.
2. **The editor** - CodeMirror, with tabs. \`get_editor_buffer\` reads the live buffer of the
   tab the user is looking at, including unsaved edits. \`set_editor_text\` writes into it.
   \`save_as\`, \`open_in_editor\`, \`read_file\`, \`list_files\`, \`write_text\`, \`make_folder\`,
   \`move_path\`, \`copy_path\` and \`delete_path\` work on files.
3. **The transcript** - the agent conversation panel. \`post_to_chat\` puts a note in it where
   the user reads it. \`read_conversation\` reads what has been said so far.

## The tools

notecode tools:

${notecodeTools}

Built-in tools of the local server:

${builtins}

## How a skill has to be shaped

A skill is a markdown file. It is loaded into the conversation as context, so it is
instructions for the model, not a program.

- It lives in a folder, and the file is called \`SKILL.md\`.
- It starts with front matter, and the front matter has a \`name\` and a \`description\`:

  \`\`\`markdown
  ---
  name: my-skill
  description: One line saying what the skill is for and when to use it.
  ---

  The body, in markdown.
  \`\`\`

- The name is the folder name. The description is what the user sees in \`/skills\`.
- The body is the instruction the model follows. Write it as instructions to the model:
  what to do, in what order, and what to check afterwards.
- Name the tools the way this app names them. If the skill tells the model to call
  \`write_file\`, that tool exists, but for a file the user should see, \`save_as\` is the
  one that opens it in the editor.
- Do not tell the model to do something this app cannot do. There is no browser, no git
  commit tool, and no way to reach a file outside the workspace except through a tool.

## The skill folders

${rootList}
`;
}

// ------------------------------------------------------------- MCP server

const pending = new Map();
let nextId = 1;
let buffer = '';

process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => {
  buffer += chunk;
  let i;
  while ((i = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, i).trim();
    buffer = buffer.slice(i + 1);
    if (!line) continue;
    let msg;
    try { msg = JSON.parse(line); } catch (e) {
      // A line that will not parse is dropped, and the caller waits for an
      // answer that is never going to come. Say so, at least.
      trace(`DROP unparsable request (${line.length} chars): ${e.message}`);
      continue;
    }
    handle(msg);
  }
});

function send(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

function handle(msg) {
  if (msg.id === undefined) return; // notification

  const reply = (result) => send({ jsonrpc: '2.0', id: msg.id, result });

  switch (msg.method) {
    case 'initialize':
      reply({
        protocolVersion: '2024-11-05',
        capabilities: { tools: { listChanged: false } },
        serverInfo: { name: 'notecode', version: '0.1.0' }
      });
      break;

    case 'tools/list':
      reply({ tools: TOOLS });
      break;

    case 'tools/call':
      trace(`call ${msg.params?.name} (${JSON.stringify(msg.params?.arguments ?? {}).length} chars of arguments)`);
      (async () => {
        try {
          const text = await callTool(msg.params?.name, msg.params?.arguments || {});
          reply({ content: [{ type: 'text', text }] });
          trace(`answered ${msg.params?.name}: ${String(text).slice(0, 120)}`);
        } catch (e) {
          reply({ content: [{ type: 'text', text: `[error] ${e.message}` }], isError: true });
          trace(`failed ${msg.params?.name}: ${e.message}`);
        }
      })();
      break;

    case 'ping':
      reply({});
      break;

    default:
      send({ jsonrpc: '2.0', id: msg.id, error: { code: -32601, message: `method not found: ${msg.method}` } });
  }
}
