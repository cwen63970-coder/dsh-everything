// Protocol smoke test: start the MCP server over stdio and verify it answers
// initialize + tools/list + tools/call(status). Works without Everything
// running (tools/list never touches the bridge), so CI can run it.
//
//   node scripts/smoke.mjs

import { spawn } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const SERVER = join(HERE, "..", "mcp", "everything-mcp.mjs");

const child = spawn(process.execPath, [SERVER], { stdio: ["pipe", "pipe", "pipe"] });
let buffer = "";
const waiters = new Map();
const logs = [];

child.stdout.setEncoding("utf8");
child.stdout.on("data", (chunk) => {
  buffer += chunk;
  let index;
  while ((index = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, index).trim();
    buffer = buffer.slice(index + 1);
    if (!line) continue;
    let message;
    try { message = JSON.parse(line); } catch { continue; }
    if (message.id && waiters.has(message.id)) {
      waiters.get(message.id)(message);
      waiters.delete(message.id);
    }
  }
});
child.stderr.setEncoding("utf8");
child.stderr.on("data", (chunk) => logs.push(chunk.trim()));

const call = (id, method, params) =>
  new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`timeout waiting for ${method}`)), 20000);
    waiters.set(id, (message) => { clearTimeout(timer); resolve(message); });
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
  });

const checks = [];
const check = (name, ok, detail = "") => {
  checks.push({ name, ok, detail });
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? `  (${detail})` : ""}`);
};

try {
  const init = await call(1, "initialize", {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "smoke", version: "0" },
  });
  child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");
  check("initialize returns serverInfo.name=everything", init.result?.serverInfo?.name === "everything");

  const list = await call(2, "tools/list", {});
  const names = (list.result?.tools ?? []).map((tool) => tool.name).sort();
  const expected = ["everything_count", "everything_duplicates", "everything_recent", "everything_search", "everything_status"];
  check("tools/list returns the five tools", JSON.stringify(names) === JSON.stringify(expected), names.join(","));

  const status = await call(3, "tools/call", { name: "everything_status", arguments: {} });
  const text = status.result?.content?.[0]?.text ?? "";
  check("everything_status answers", text.length > 0, text.split("\n")[0]);
} catch (error) {
  check("protocol round-trip", false, String(error.message ?? error));
} finally {
  child.kill();
}

const failed = checks.filter((entry) => !entry.ok);
if (failed.length > 0) {
  if (logs.length) console.error("\nserver stderr:\n" + logs.join("\n"));
  process.exit(1);
}
console.log("\nall smoke checks passed");
