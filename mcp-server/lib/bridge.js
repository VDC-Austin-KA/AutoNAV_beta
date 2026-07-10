// TCP client for the AutoNAV MCP bridge running inside Navisworks.
//
// Protocol: newline-delimited JSON over a loopback socket.
//   request:  {"id": 1, "command": "list_clash_tests", "params": {...}}
//   response: {"id": 1, "ok": true, "result": {...}} | {"id": 1, "ok": false, "error": "..."}
//
// The connection is opened lazily on the first command and re-opened
// automatically if Navisworks restarts or the bridge is toggled.

import net from "node:net";

const HOST = process.env.AUTONAV_MCP_HOST || "127.0.0.1";
const PORT = Number(process.env.AUTONAV_MCP_PORT || 5711);

// Clash runs on large federated models can take a while.
const DEFAULT_TIMEOUT_MS = Number(process.env.AUTONAV_MCP_TIMEOUT_MS || 10 * 60 * 1000);

let socket = null;
let buffer = "";
let nextId = 1;
const pending = new Map();

function failAllPending(error) {
  for (const { reject, timer } of pending.values()) {
    clearTimeout(timer);
    reject(error);
  }
  pending.clear();
}

function disconnect() {
  if (socket) {
    socket.destroy();
    socket = null;
  }
  buffer = "";
}

function connect() {
  return new Promise((resolve, reject) => {
    const sock = net.createConnection({ host: HOST, port: PORT }, () => {
      sock.setNoDelay(true);
      socket = sock;
      resolve();
    });
    sock.setEncoding("utf8");
    sock.on("data", (chunk) => {
      buffer += chunk;
      let idx;
      while ((idx = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, idx).trim();
        buffer = buffer.slice(idx + 1);
        if (!line) continue;
        let message;
        try {
          message = JSON.parse(line);
        } catch {
          continue;
        }
        const entry = pending.get(message.id);
        if (!entry) continue;
        pending.delete(message.id);
        clearTimeout(entry.timer);
        if (message.ok) entry.resolve(message.result);
        else entry.reject(new Error(message.error || "Bridge command failed."));
      }
    });
    const onGone = (err) => {
      const error = new Error(
        `Lost connection to the Navisworks bridge (${HOST}:${PORT}). ` +
          `Make sure Navisworks Manage is running and the AutoNAV MCP bridge is started ` +
          `(Add-Ins ribbon tab -> AutoNAV MCP).` +
          (err && err.message ? ` [${err.message}]` : "")
      );
      if (socket === sock) disconnect();
      failAllPending(error);
      reject(error);
    };
    sock.on("error", onGone);
    sock.on("close", () => onGone());
  });
}

/**
 * Sends a command to the Navisworks plugin and resolves with its result.
 */
export async function sendCommand(command, params = {}, timeoutMs = DEFAULT_TIMEOUT_MS) {
  if (!socket || socket.destroyed) {
    disconnect();
    await connect();
  }
  const id = nextId++;
  const payload = JSON.stringify({ id, command, params }) + "\n";
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`Bridge command '${command}' timed out after ${timeoutMs / 1000}s.`));
    }, timeoutMs);
    pending.set(id, { resolve, reject, timer });
    socket.write(payload, (err) => {
      if (err) {
        pending.delete(id);
        clearTimeout(timer);
        reject(err);
      }
    });
  });
}

export function bridgeAddress() {
  return `${HOST}:${PORT}`;
}
