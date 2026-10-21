#!/usr/bin/env node
/**
 * mcp-stdio-bridge.mjs
 *
 * llama.cpp's --mcp-servers-config parser is stdio-only: it reads `command`,
 * `cwd`, `args`, `env` and `timeout_ms`, and silently skips any entry that has
 * no `command`. It cannot talk to a remote HTTP MCP server.
 *
 * This bridge closes that gap. It speaks MCP over stdio on the side llama.cpp
 * expects, and forwards every JSON-RPC frame to a streamable-http MCP endpoint.
 *
 * Usage:  node mcp-stdio-bridge.mjs <url> [sessionHeader]
 *
 * The session id returned by `initialize` is captured and replayed on later
 * requests, because streamable-http servers bind the session to that header.
 */

const url = process.argv[2];
if (!url) {
  process.stderr.write('usage: mcp-stdio-bridge.mjs <http-mcp-url>\n');
  process.exit(2);
}

let sessionId = process.argv[3] || null;

/** Serialise frames onto stdout, one JSON object per line. */
function send(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

/** Unwrap an SSE body down to the first JSON data frame. */
function unwrapSse(text) {
  const lines = text.split('\n');
  for (const line of lines) {
    if (line.startsWith('data:')) {
      const payload = line.slice(5).trim();
      if (payload) return payload;
    }
  }
  return null;
}

async function forward(message) {
  const headers = {
    'content-type': 'application/json',
    accept: 'application/json, text/event-stream',
  };
  if (sessionId) headers['mcp-session-id'] = sessionId;

  const res = await fetch(url, {
    method: 'POST',
    headers,
    body: JSON.stringify(message),
  });

  const sid = res.headers.get('mcp-session-id');
  if (sid) sessionId = sid;

  const text = await res.text();
  if (!text) return null;

  const json = unwrapSse(text);
  if (json) {
    try {
      return JSON.parse(json);
    } catch {
      return null;
    }
  }
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

let buffer = '';
process.stdin.setEncoding('utf8');

process.stdin.on('data', (chunk) => {
  buffer += chunk;
  let index;
  while ((index = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, index).trim();
    buffer = buffer.slice(index + 1);
    if (!line) continue;

    let message;
    try {
      message = JSON.parse(line);
    } catch {
      continue;
    }

    // Notifications get no reply.
    if (message.id === undefined) {
      forward(message).catch(() => {});
      continue;
    }

    forward(message)
      .then((result) => {
        if (result === null) {
          send({ jsonrpc: '2.0', id: message.id, error: { code: -32603, message: 'bridge: no response from upstream' } });
        } else {
          send(result);
        }
      })
      .catch((err) => {
        send({ jsonrpc: '2.0', id: message.id, error: { code: -32603, message: `bridge: ${err.message}` } });
      });
  }
});

process.stdin.on('end', () => process.exit(0));
process.on('SIGTERM', () => process.exit(0));
