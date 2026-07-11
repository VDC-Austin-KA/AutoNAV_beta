# Connecting Microsoft Copilot to the Navisworks MCP

The AutoNAV MCP server speaks two transports, and which Microsoft product you use decides which one you need:

| Client | Works? | Transport |
|---|---|---|
| **GitHub Copilot in VS Code** (agent mode) | ✅ Yes | stdio (local, default) |
| **Microsoft Copilot Studio** (copilotstudio.microsoft.com) | ✅ Yes | streamable HTTP + a tunnel |
| Consumer **Copilot app** (Windows taskbar / copilot.microsoft.com) | ❌ No | it cannot use custom MCP servers |

Everything below assumes the basics from the [README](../README.md) are done: `AutoNAVMCP.dll` installed for your Navisworks year, and Node.js 18+ installed on the same PC.

---

## Option A — GitHub Copilot in VS Code (simplest)

1. In Navisworks: **Add-Ins tab → AutoNAV MCP** → bridge starts.
2. In VS Code, create `.vscode/mcp.json` in your workspace:

   ```json
   {
     "servers": {
       "navisworks": {
         "type": "stdio",
         "command": "node",
         "args": ["C:\\path\\to\\AutoNAV_beta\\mcp-server\\index.js"]
       }
     }
   }
   ```

3. Open Copilot Chat, switch to **Agent** mode, click the tools icon and confirm the `navisworks` tools are listed.
4. Ask: *"Check Navisworks status."* You should see `documentOpen: true`.

---

## Option B — Microsoft Copilot Studio (step by step)

Copilot Studio runs in Microsoft's cloud, so it can't launch a local process or see `127.0.0.1`. You therefore (1) run the MCP server in **HTTP mode**, (2) publish it through a **dev tunnel**, and (3) point Copilot Studio at the tunnel URL.

> ⚠️ **Before you start:** a tunnel makes a live, model-editing endpoint reachable from the internet for anyone who has the URL. Only run it while you're actively using it, stop it when done, and clear this with your IT department on a work PC. Your Microsoft 365 admin may also need to allow Copilot Studio connectors/tools for your tenant.

### Step 1 — Start the bridge in Navisworks

Open your model in Navisworks Manage → **Add-Ins tab → AutoNAV MCP**. You'll see *"bridge is running on 127.0.0.1:5711"*.

### Step 2 — Start the MCP server in HTTP mode

```bat
cd C:\path\to\AutoNAV_beta\mcp-server
npm install        (first time only)
npm run start:http
```

You should see:

```
AutoNAV MCP server listening on http://127.0.0.1:3711/mcp (bridge target 127.0.0.1:5711).
```

(Any port works: `node index.js --http 8080`.)

**Verify it locally** — in a second terminal:

```bat
curl -s -X POST http://127.0.0.1:3711/mcp -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"curl\",\"version\":\"1.0\"}}}"
```

A response containing `"autonav-navisworks"` means the HTTP transport is up.

### Step 3 — Publish it with a Microsoft dev tunnel

Dev tunnels are Microsoft's supported way to expose a local port over HTTPS (ngrok works too if your org prefers it).

```bat
winget install Microsoft.devtunnel
devtunnel user login          (sign in with your Microsoft/Entra account)
devtunnel host -p 3711 --allow-anonymous
```

The output includes a public URL like:

```
https://a1b2c3d4-3711.usw2.devtunnels.ms
```

Your **MCP endpoint** is that URL **plus `/mcp`**:

```
https://a1b2c3d4-3711.usw2.devtunnels.ms/mcp
```

Leave both the tunnel window and the MCP server window running. (`--allow-anonymous` means anyone with the URL can call it — treat the URL like a password. Without the flag, dev tunnels require Microsoft login, but Copilot Studio then needs matching auth configured on the tool.)

### Step 4 — Add the MCP server to a Copilot Studio agent

1. Go to **https://copilotstudio.microsoft.com** and open (or create) an agent.
2. Open the **Tools** page for the agent → **+ Add a tool** → **+ New tool** → **Model Context Protocol**.
3. Fill in:
   - **Server name:** `Navisworks`
   - **Server description:** `Clash identification, assignment, resolution and reporting in Autodesk Navisworks Manage`
   - **Server URL:** your tunnel endpoint, e.g. `https://a1b2c3d4-3711.usw2.devtunnels.ms/mcp`
   - **Authentication:** *None* (the anonymous tunnel handles transport)
4. **Create**, then **Add to agent**.

All 15 Navisworks tools (list/run clash tests, get results, assign, set status, comment, group, create reports…) now appear under the agent's tools.

> Menu names shift between Copilot Studio releases; on older tenants the same thing is done via a *custom connector* with the MCP protocol option. If you don't see "Model Context Protocol" under New tool, ask your admin to enable MCP tools / generative orchestration for the environment.

### Step 5 — Test it

In the agent's **Test** pane, try:

> *Check the Navisworks status and tell me what model is open.*

Approve the connection/tool prompt the first time. Then go bigger:

> *Run all clash tests. Group "Mechanical vs Structural" by grid intersection, assign every New clash to the Mechanical trade, then create an HTML report of Active clashes and tell me the file path.*

The agent chains `run_all_clash_tests` → `group_clashes` → `assign_clashes` → `create_clash_report`; the report lands on the Navisworks PC (default `Documents\AutoNAV Reports`).

### Step 6 — (Optional) Publish to Microsoft Teams for your company

To make the agent a Teams app your whole company can add (not just you), follow **[TEAMS.md](TEAMS.md)** — it covers stable hosting for the endpoint, publishing the Teams channel, and the org-wide **admin-approval** step that's easy to miss. Remember it only works while the Navisworks PC has the bridge, the MCP server, and a reachable endpoint running.

### Troubleshooting

| Symptom | Fix |
|---|---|
| Tool errors mentioning *"Lost connection to the Navisworks bridge"* | The bridge isn't running (Add-Ins → AutoNAV MCP) or Navisworks was closed. |
| Copilot Studio can't reach the server URL | Tunnel window closed, or the URL changed (dev tunnel URLs change per `devtunnel host` run — create a persistent tunnel with `devtunnel create` / `devtunnel port create -p 3711` to keep one URL). Update the tool's URL after any change. |
| "Model Context Protocol" option missing | Tenant admin needs to enable MCP/custom tools for Copilot Studio. |
| Long clash runs time out in Copilot Studio | Run heavy `run_all_clash_tests` calls first (or from VS Code/Claude), then let the agent do queries/assignment/reporting. |
| Works with curl locally, 404 from the tunnel | Make sure you appended `/mcp` to the tunnel URL. |

### Security notes

- The bridge itself never leaves `127.0.0.1` — only the MCP HTTP server is tunneled, and only while you host the tunnel.
- Stop `devtunnel` (Ctrl+C) as soon as you're done; the endpoint dies with it.
- Prefer authenticated tunnels (`devtunnel host` without `--allow-anonymous`, plus matching auth on the Copilot Studio tool) if your organization supports it.
