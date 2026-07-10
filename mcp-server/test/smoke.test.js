// End-to-end smoke test: starts a fake Navisworks bridge on a loopback
// port, launches the MCP server against it, and exercises the MCP
// handshake plus a few tool calls over stdio. No Navisworks required.
//
// Run with: npm test

import net from "node:net";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const serverEntry = path.join(here, "..", "index.js");

// ── Fake bridge ────────────────────────────────────────────────────

const cannedResults = {
  ping: { plugin: "AutoNAV MCP", documentOpen: true },
  list_clash_tests: {
    count: 1,
    tests: [
      {
        guid: "11111111-2222-3333-4444-555555555555",
        name: "Mechanical vs Structural",
        testType: "Hard",
        status: "Ok",
        tolerance: 0,
        resultCounts: { New: 3, Active: 1, Reviewed: 0, Approved: 0, Resolved: 2, Total: 6 },
      },
    ],
  },
  assign_clashes: { test: "Mechanical vs Structural", assignedTo: "Mechanical", clashesUpdated: 3 },
};

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
      const result = cannedResults[req.command];
      const response = result
        ? { id: req.id, ok: true, result }
        : { id: req.id, ok: false, error: `Unknown command '${req.command}'.` };
      socket.write(JSON.stringify(response) + "\n");
    }
  });
  socket.on("error", () => {});
});

await new Promise((resolve) => bridge.listen(0, "127.0.0.1", resolve));
const port = bridge.address().port;

// ── MCP client over stdio ──────────────────────────────────────────

const child = spawn(process.execPath, [serverEntry], {
  env: { ...process.env, AUTONAV_MCP_PORT: String(port) },
  stdio: ["pipe", "pipe", "inherit"],
});

let stdoutBuf = "";
const waiters = new Map();
child.stdout.setEncoding("utf8");
child.stdout.on("data", (chunk) => {
  stdoutBuf += chunk;
  let idx;
  while ((idx = stdoutBuf.indexOf("\n")) >= 0) {
    const line = stdoutBuf.slice(0, idx).trim();
    stdoutBuf = stdoutBuf.slice(idx + 1);
    if (!line) continue;
    const msg = JSON.parse(line);
    if (msg.id !== undefined && waiters.has(msg.id)) {
      waiters.get(msg.id)(msg);
      waiters.delete(msg.id);
    }
  }
});

let rpcId = 0;
function rpc(method, params) {
  const id = ++rpcId;
  const msg = { jsonrpc: "2.0", id, method, params };
  child.stdin.write(JSON.stringify(msg) + "\n");
  return new Promise((resolve, reject) => {
    waiters.set(id, resolve);
    setTimeout(() => {
      if (waiters.delete(id)) reject(new Error(`Timed out waiting for ${method}`));
    }, 15000);
  });
}

function notify(method, params) {
  child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method, params }) + "\n");
}

function assert(condition, label) {
  if (!condition) throw new Error(`FAILED: ${label}`);
  console.log(`ok - ${label}`);
}

try {
  const init = await rpc("initialize", {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "smoke-test", version: "1.0.0" },
  });
  assert(init.result?.serverInfo?.name === "autonav-navisworks", "initialize returns server info");
  notify("notifications/initialized", {});

  const tools = await rpc("tools/list", {});
  const names = tools.result.tools.map((t) => t.name);
  for (const expected of [
    "navisworks_status",
    "list_clash_tests",
    "create_clash_test",
    "run_clash_test",
    "get_clash_results",
    "assign_clashes",
    "set_clash_status",
    "add_clash_comment",
    "group_clashes",
    "create_clash_report",
  ]) {
    assert(names.includes(expected), `tools/list includes ${expected}`);
  }

  const status = await rpc("tools/call", { name: "navisworks_status", arguments: {} });
  const statusBody = JSON.parse(status.result.content[0].text);
  assert(statusBody.documentOpen === true, "navisworks_status reaches the bridge");

  const list = await rpc("tools/call", { name: "list_clash_tests", arguments: {} });
  const listBody = JSON.parse(list.result.content[0].text);
  assert(listBody.tests[0].name === "Mechanical vs Structural", "list_clash_tests round-trips");

  const assign = await rpc("tools/call", {
    name: "assign_clashes",
    arguments: { test: "Mechanical vs Structural", assignTo: "Mechanical" },
  });
  const assignBody = JSON.parse(assign.result.content[0].text);
  assert(assignBody.clashesUpdated === 3, "assign_clashes round-trips");

  const bad = await rpc("tools/call", {
    name: "delete_clash_test",
    arguments: { test: "nope" },
  });
  assert(bad.result.isError === true, "bridge errors surface as tool errors");

  console.log("\nAll smoke tests passed.");
} finally {
  child.kill();
  bridge.close();
}
