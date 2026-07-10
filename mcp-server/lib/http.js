// Streamable-HTTP transport for the AutoNAV MCP server.
//
// Used by remote MCP clients such as Microsoft Copilot Studio that cannot
// spawn a local stdio process (see docs/COPILOT.md). Runs stateless: each
// POST /mcp request gets a fresh server + transport pair, so no session
// bookkeeping is needed and any load path (direct, dev tunnel, reverse
// proxy) works.
//
// The listener binds 127.0.0.1 by default; expose it beyond the machine
// deliberately (e.g. a Microsoft dev tunnel) — never by binding 0.0.0.0
// on an untrusted network. Set AUTONAV_MCP_HTTP_HOST to override.

import http from "node:http";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";

const HOST = process.env.AUTONAV_MCP_HTTP_HOST || "127.0.0.1";

function readBody(req) {
  return new Promise((resolve, reject) => {
    let data = "";
    req.setEncoding("utf8");
    req.on("data", (chunk) => (data += chunk));
    req.on("end", () => resolve(data));
    req.on("error", reject);
  });
}

/**
 * Starts an HTTP server exposing the MCP endpoint at POST /mcp.
 * `buildServer` is called per request to produce a fresh McpServer.
 */
export function startHttpServer(buildServer, port) {
  const httpServer = http.createServer(async (req, res) => {
    const url = new URL(req.url, `http://${req.headers.host || "localhost"}`);
    if (url.pathname !== "/mcp") {
      res.writeHead(404, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: "Not found. The MCP endpoint is POST /mcp." }));
      return;
    }
    if (req.method !== "POST") {
      // Stateless mode: no SSE stream to resume (GET) and no session to
      // delete (DELETE).
      res.writeHead(405, { "Content-Type": "application/json", Allow: "POST" });
      res.end(JSON.stringify({ error: "Method not allowed. Use POST." }));
      return;
    }

    try {
      const raw = await readBody(req);
      let parsedBody;
      try {
        parsedBody = raw ? JSON.parse(raw) : undefined;
      } catch {
        res.writeHead(400, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: "Invalid JSON body." }));
        return;
      }

      const server = buildServer();
      const transport = new StreamableHTTPServerTransport({
        sessionIdGenerator: undefined, // stateless
      });
      res.on("close", () => {
        transport.close();
        server.close();
      });
      await server.connect(transport);
      await transport.handleRequest(req, res, parsedBody);
    } catch (error) {
      console.error("HTTP request failed:", error);
      if (!res.headersSent) {
        res.writeHead(500, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: "Internal server error." }));
      }
    }
  });

  return new Promise((resolve, reject) => {
    httpServer.once("error", reject);
    httpServer.listen(port, HOST, () => resolve(httpServer));
  });
}
