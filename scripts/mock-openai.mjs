// A tiny OpenAI-compatible server, used to test the BYOK path.
//
// The app has to send a turn to a provider the user configured by hand: the right
// URL, the right headers, the right body, and the answer has to come back into
// the transcript. None of that can be checked against a real vendor without a
// real key, so the test points a provider at this instead.
//
//   node mock-openai.mjs --port 8799 --record <file>
//
// It answers POST /v1/chat/completions with a canned reply, serves a model list
// on GET /v1/models, and appends every request it received to the record file as
// one JSON object per line, so the test can pick the chat request out of the
// model probe that comes first.

import { createServer } from 'node:http';
import { appendFileSync } from 'node:fs';

const args = process.argv.slice(2);
const opt = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};

const port = Number(opt('--port', '8799'));
const record = opt('--record', '');
const style = opt('--style', 'openai');     // openai | anthropic
const reply = opt('--reply', 'Hello from the mock provider.');
// The reasoning a provider thinks out loud before it answers. The app shows it
// on the line under the blinker, so the mock can produce it to prove that path.
const reasoning = opt('--reasoning', '');

const server = createServer((req, res) => {
  let body = '';
  req.on('data', (c) => { body += c; });
  req.on('end', () => {
    if (record) {
      appendFileSync(record, JSON.stringify({
        method: req.method,
        url: req.url,
        headers: req.headers,
        body: body ? JSON.parse(body) : null,
      }) + '\n');
    }

    if (req.method === 'GET' && req.url.includes('/models')) {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({
        object: 'list',
        data: [{ id: 'mock-model', object: 'model', owned_by: 'mock' }],
      }));
      return;
    }

    if (!req.url.includes('/chat/completions') && !req.url.includes('/messages')) {
      res.writeHead(404, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ error: { message: 'not found' } }));
      return;
    }

    if (style === 'anthropic') {
      const wantsStream = body && JSON.parse(body).stream;
      if (!wantsStream) {
        res.writeHead(200, { 'content-type': 'application/json' });
        res.end(JSON.stringify({
          id: 'msg_mock',
          type: 'message',
          role: 'assistant',
          model: 'mock-model',
          content: [{ type: 'text', text: reply }],
          stop_reason: 'end_turn',
          usage: { input_tokens: 10, output_tokens: 5 },
        }));
        return;
      }
      res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache' });
      res.write(`event: message_start\ndata: ${JSON.stringify({
        type: 'message_start',
        message: { id: 'msg_mock', type: 'message', role: 'assistant', model: 'mock-model', content: [], stop_reason: null, usage: { input_tokens: 10, output_tokens: 0 } },
      })}\n\n`);
      res.write(`event: content_block_start\ndata: ${JSON.stringify({
        type: 'content_block_start', index: 0, content_block: { type: 'text', text: '' },
      })}\n\n`);
      if (reasoning) {
        // Anthropic sends the thinking as its own delta type.
        res.write(`event: content_block_delta\ndata: ${JSON.stringify({
          type: 'content_block_delta', index: 0, delta: { type: 'thinking_delta', thinking: reasoning },
        })}\n\n`);
      }
      res.write(`event: content_block_delta\ndata: ${JSON.stringify({
        type: 'content_block_delta', index: 0, delta: { type: 'text_delta', text: reply },
      })}\n\n`);
      res.write(`event: content_block_stop\ndata: ${JSON.stringify({ type: 'content_block_stop', index: 0 })}\n\n`);
      res.write(`event: message_delta\ndata: ${JSON.stringify({
        type: 'message_delta', delta: { stop_reason: 'end_turn' }, usage: { output_tokens: 5 },
      })}\n\n`);
      res.write(`event: message_stop\ndata: ${JSON.stringify({ type: 'message_stop' })}\n\n`);
      res.end();
      return;
    }

    const wantsStream = body && JSON.parse(body).stream;
    if (!wantsStream) {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({
        id: 'chatcmpl-mock',
        object: 'chat.completion',
        created: Math.floor(Date.now() / 1000),
        model: 'mock-model',
        choices: [{
          index: 0,
          message: { role: 'assistant', content: reply, reasoning_content: reasoning || undefined },
          finish_reason: 'stop',
        }],
        usage: { prompt_tokens: 10, completion_tokens: 5, total_tokens: 15 },
      }));
      return;
    }

    // The app asks for a stream, so the mock answers in server sent events.
    res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache' });
    res.write(`data: ${JSON.stringify({
      id: 'chatcmpl-mock', object: 'chat.completion.chunk', model: 'mock-model',
      choices: [{ index: 0, delta: { role: 'assistant' }, finish_reason: null }],
    })}\n\n`);
    if (reasoning) {
      // OpenAI-shaped providers carry the thinking in reasoning_content.
      res.write(`data: ${JSON.stringify({
        id: 'chatcmpl-mock', object: 'chat.completion.chunk', model: 'mock-model',
        choices: [{ index: 0, delta: { reasoning_content: reasoning }, finish_reason: null }],
      })}\n\n`);
    }
    res.write(`data: ${JSON.stringify({
      id: 'chatcmpl-mock', object: 'chat.completion.chunk', model: 'mock-model',
      choices: [{ index: 0, delta: { content: reply }, finish_reason: null }],
    })}\n\n`);
    res.write(`data: ${JSON.stringify({
      id: 'chatcmpl-mock', object: 'chat.completion.chunk', model: 'mock-model',
      choices: [{ index: 0, delta: {}, finish_reason: 'stop' }],
    })}\n\n`);
    res.write('data: [DONE]\n\n');
    res.end();
  });
});

server.listen(port, '127.0.0.1', () => {
  console.log(`mock provider on http://127.0.0.1:${port} (${style})`);
});
