// Measures what the tool schemas cost in the prompt.
//
// The app sends the tool schemas to the model on every turn. On a model that
// is already slow on CPU the schemas are not a rounding error: they are the
// difference between a turn that starts and a turn that waits. This asks the
// server the same question twice - once with the schemas and once without -
// and prints what the difference is.
//
//   node measure-prompt-cost.mjs [baseUrl]
//
// The base URL defaults to the local server on 127.0.0.1:8080.

const base = process.argv[2] || 'http://127.0.0.1:8080';
const openai = base.replace(/\/+$/, '') + '/v1';

const catalogue = await (await fetch(`${base}/tools`)).json();

// The default set is what the app sends: llama-server's own built-ins and the
// notecode MCP tools. The 170 REA tools are opt-in.
const isDefault = (t) => t.type === 'server' || (t.tool || '').startsWith('notecode_');
const defaults = catalogue.filter(isDefault);

const definition = (item) => item.definition;

async function ask(tools) {
  const body = {
    model: 'notecode-minicpm',
    messages: [{ role: 'user', content: 'hi' }],
    max_tokens: 1,
    stream: false,
  };
  if (tools) {
    body.tools = tools.map(definition);
    body.tool_choice = 'auto';
  }

  const started = Date.now();
  const res = await fetch(`${openai}/chat/completions`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
  const json = await res.json();
  if (!res.ok) throw new Error(`${res.status}: ${JSON.stringify(json).slice(0, 400)}`);

  const usage = json.usage || {};
  return {
    prompt: usage.prompt_tokens ?? 0,
    seconds: ((Date.now() - started) / 1000).toFixed(1),
  };
}

console.log(`catalogue: ${catalogue.length} tools in total`);
console.log(`default set: ${defaults.length} tools`);

const withTools = await ask(defaults);
console.log(`with the schemas:   ${withTools.prompt} prompt tokens (${withTools.seconds}s)`);

const without = await ask(null);
console.log(`without the schemas: ${without.prompt} prompt tokens (${without.seconds}s)`);

console.log('');
console.log(`the tool schemas cost ${withTools.prompt - without.prompt} prompt tokens per turn`);
