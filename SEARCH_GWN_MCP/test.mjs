// End-to-end MCP test: spawns server.mjs like a client would and speaks MCP to it.
//   node test.mjs                (uses SEARCH_GWN_URL / key settings from the environment)
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";

const srv = spawn(process.execPath, [fileURLToPath(new URL("./server.mjs", import.meta.url))], { stdio: ["pipe", "pipe", "inherit"], env: process.env });
const pending = new Map();
createInterface({ input: srv.stdout }).on("line", (l) => {
  const m = JSON.parse(l);
  pending.get(m.id)?.(m);
});
let next = 1;
const rpc = (method, params) => new Promise((res) => { const id = next++; pending.set(id, res); srv.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n"); });
const check = (ok, what) => { console.log(`${ok ? "PASS" : "FAIL"}  ${what}`); if (!ok) process.exitCode = 1; };

const init = await rpc("initialize", { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "test", version: "1" } });
check(init.result?.serverInfo?.name === "search-gwn" && init.result.protocolVersion === "2025-06-18", "initialize");
srv.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");
const list = await rpc("tools/list", {});
check(["web_search", "search_health", "search_usage"].every((n) => list.result.tools.some((t) => t.name === n)), "tools/list: web_search, search_health, search_usage");
const s = await rpc("tools/call", { name: "web_search", arguments: { query: "python asyncio gather return_exceptions", category: "it", limit: 3 } });
const text = s.result?.content?.[0]?.text || "";
check(!s.result.isError && /https?:\/\//.test(text), "web_search (it) returns results");
console.log(text.split("\n").slice(0, 8).join("\n"));
const h = await rpc("tools/call", { name: "search_health", arguments: {} });
check(!h.result.isError && h.result.content[0].text.includes("working:"), "search_health");
console.log(h.result.content[0].text);
const u = await rpc("tools/call", { name: "search_usage", arguments: { limit: 3 } });
check(!u.result.isError && u.result.content[0].text.startsWith("key: "), "search_usage");
const bad = await rpc("tools/call", { name: "nope", arguments: {} });
check(bad.error?.code === -32602, "unknown tool → error");
const nm = await rpc("does/not/exist", {});
check(nm.error?.code === -32601, "unknown method → -32601");
srv.stdin.end();
