// Smoke test for the streamable-HTTP transport: fake bridge + MCP server
// in --http mode, exercised with plain fetch() JSON-RPC calls.
//
// Run with: node test/http.test.js

import net from "node:net";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const serverEntry = path.join(here, "..", "index.js");

// ── Fake bridge ────────────────────────────────────────────────────

const bridge = net.createServer((socket) => {
  socket.setEncoding("utf8");
  let buf = "";
  socket.on("data", (chunk) => {
    buf += chunk;
    let idx;
    while ((idx = buf.indexOf("\n")) >= 0) {
      const line = buf.slice(0, idx).trim();
      buf = buf.slice(idx + 1);
      if (!line) continue;
      const req = JSON.parse(line);
      const response =
        req.command === "ping"
          ? { id: req.id, ok: true, result: { plugin: "AutoNAV MCP", documentOpen: true } }
          : { id: req.id, ok: false, error: `Unknown command '${req.command}'.` };
      socket.write(JSON.stringify(response) + "\n");
    }
  });
  socket.on("error", () => {});
});
await new Promise((resolve) => bridge.listen(0, "127.0.0.1", resolve));
const bridgePort = bridge.address().port;

// ── MCP server in HTTP mode ────────────────────────────────────────

const httpPort = 3000 + Math.floor(Math.random() * 30000);
const child = spawn(process.execPath, [serverEntry, "--http", String(httpPort)], {
  env: { ...process.env, AUTONAV_MCP_PORT: String(bridgePort) },
  stdio: ["ignore", "ignore", "pipe"],
});

// Wait for the "listening" line on stderr.
await new Promise((resolve, reject) => {
  let err = "";
  const timer = setTimeout(() => reject(new Error(`Server did not start: ${err}`)), 10000);
  child.stderr.setEncoding("utf8");
  child.stderr.on("data", (chunk) => {
    err += chunk;
    if (err.includes("listening")) {
      clearTimeout(timer);
      resolve();
    }
  });
  child.on("exit", (code) => reject(new Error(`Server exited early (${code}): ${err}`)));
});

const endpoint = `http://127.0.0.1:${httpPort}/mcp`;
let rpcId = 0;

async function rpc(method, params) {
  const response = await fetch(endpoint, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Accept: "application/json, text/event-stream",
    },
    body: JSON.stringify({ jsonrpc: "2.0", id: ++rpcId, method, params }),
  });
  const text = await response.text();
  // Streamable HTTP may answer as SSE; extract the JSON data line(s).
  if (text.startsWith("event:") || text.includes("\ndata:") || text.startsWith("data:")) {
    for (const line of text.split("\n")) {
      if (line.startsWith("data:")) return JSON.parse(line.slice(5).trim());
    }
  }
  return JSON.parse(text);
}

function assert(condition, label) {
  if (!condition) throw new Error(`FAILED: ${label}`);
  console.log(`ok - ${label}`);
}

try {
  const init = await rpc("initialize", {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "http-smoke-test", version: "1.0.0" },
  });
  assert(init.result?.serverInfo?.name === "autonav-navisworks", "initialize over HTTP");

  const tools = await rpc("tools/list", {});
  assert(
    tools.result.tools.some((t) => t.name === "create_clash_report"),
    "tools/list over HTTP includes clash tools"
  );

  const status = await rpc("tools/call", { name: "navisworks_status", arguments: {} });
  const body = JSON.parse(status.result.content[0].text);
  assert(body.documentOpen === true, "tools/call reaches the bridge over HTTP");

  const notFound = await fetch(`http://127.0.0.1:${httpPort}/other`, { method: "POST" });
  assert(notFound.status === 404, "unknown paths return 404");

  const wrongMethod = await fetch(endpoint, { method: "GET" });
  assert(wrongMethod.status === 405, "GET /mcp returns 405 in stateless mode");

  console.log("\nAll HTTP transport tests passed.");
} finally {
  child.kill();
  bridge.close();
}
