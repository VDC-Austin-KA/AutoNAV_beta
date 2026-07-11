# AutoNAV MCP

AI-driven clash coordination for **Autodesk Navisworks Manage 2024 / 2025 / 2026 / 2027**, built on the [Model Context Protocol (MCP)](https://modelcontextprotocol.io).

Connect Claude (or any MCP client) directly to a live Navisworks session and drive the full clash workflow in natural language:

- **Clash identification** — create clash tests from search sets, run them, and query results with status, distance, grid location, and clashing-element details.
- **Clash assignment** — assign clashes (individually, by group, by status, or in bulk) to trades or people.
- **Clash resolving / updating** — change statuses (New → Active → Reviewed → Approved → Resolved), add comments, rename, and regroup.
- **Clash report creation** — generate HTML / CSV / JSON reports, optionally with embedded clash viewpoint images.

This repository is the successor experiment to [AutoNAV2](https://github.com/VDC-Austin-KA/AutoNAV2) (the ribbon-button plugin). AutoNAV2 remains unchanged and was used as the reference implementation for the Navisworks API patterns here (multi-year Clash API compatibility, search-set traversal, group rebuild transactions).

## How it works

```
┌────────────────┐   MCP (stdio)   ┌──────────────────┐   JSON over TCP    ┌──────────────────────────┐
│  Claude /      │ ◄─────────────► │  AutoNAV MCP     │ ◄────────────────► │  Navisworks Manage        │
│  MCP client    │                 │  server (Node)   │   127.0.0.1:5711   │  + AutoNAVMCP.dll plugin  │
└────────────────┘                 └──────────────────┘                    └──────────────────────────┘
```

Two components, both in this repo:

| Component | Path | Role |
|---|---|---|
| **Bridge plugin** (`AutoNAVMCP.dll`) | `plugin/` | C# Navisworks add-in. Hosts a loopback TCP bridge and executes commands through the Navisworks .NET API on the main thread. |
| **MCP server** | `mcp-server/` | Node.js stdio MCP server exposing the tools below; forwards each call to the bridge. |

The AI never touches your model directly — every operation goes through the official Navisworks .NET API inside the plugin, on the machine where Navisworks runs.

## Install

### 1. Plugin

Build (or download from the Actions artifacts) the DLL for your Navisworks year:

```
dotnet build plugin/AutoNAVMCP.csproj -c Release -p:Platform=x64 -p:NWYear=2026 -p:NWPackageVersion=2026.0.1
```

Supported pairs: `2024 / 2024.0.0`, `2025 / 2025.0.0`, `2026 / 2026.0.1`, `2027 / 2027.0.0`. No Navisworks SDK needed — it builds against the `Speckle.Navisworks.API` NuGet packages (same approach as AutoNAV2).

Then copy `AutoNAVMCP.dll` to (admin rights required):

```
C:\Program Files\Autodesk\Navisworks Manage <year>\Plugins\AutoNAVMCP\AutoNAVMCP.dll
```

Restart Navisworks → **Add-Ins ribbon tab** → click **AutoNAV MCP** → the bridge starts on `127.0.0.1:5711` (override with the `AUTONAV_MCP_PORT` environment variable). Click again to stop.

### 2. MCP server

Requires Node.js 18+.

```
cd mcp-server
npm install
```

Register with your MCP client — e.g. Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "navisworks": {
      "command": "node",
      "args": ["C:\\path\\to\\AutoNAV_beta\\mcp-server\\index.js"]
    }
  }
}
```

or Claude Code:

```
claude mcp add navisworks -- node C:\path\to\AutoNAV_beta\mcp-server\index.js
```

The server speaks **stdio** by default (Claude Desktop, Claude Code, VS Code GitHub Copilot). For remote clients that need an HTTP endpoint — notably **Microsoft Copilot Studio** — start it in streamable-HTTP mode instead:

```
npm run start:http        # serves http://127.0.0.1:3711/mcp
```

See **[docs/COPILOT.md](docs/COPILOT.md)** for a step-by-step walkthrough of connecting VS Code GitHub Copilot and Microsoft Copilot Studio (including the dev-tunnel setup Copilot Studio requires).

## Tools

| Tool | Purpose |
|---|---|
| `navisworks_status` | Verify the bridge is reachable and a model is open. |
| `get_document_info` | Model title, file, units, appended models, clash test count. |
| `list_search_sets` | All saved selection/search sets with paths (inputs for test creation). |
| `list_clash_tests` | Every clash test with per-status result counts. |
| `create_clash_test` | New test from two search sets/folders (Hard/Clearance/Duplicate, tolerance, optional immediate run). |
| `run_clash_test` / `run_all_clash_tests` | Update one test or all of them. |
| `delete_clash_test` | Remove a test. |
| `get_clash_results` | Filterable, paginated results: status, distance, grid location, items, assignment. |
| `get_clash_result` | Deep-dive on one clash: comments, timestamps, element properties of both items. |
| `assign_clashes` | Set *Assigned To* on specific clashes, a group, a status bucket, or the whole test. |
| `set_clash_status` | Move clashes through New / Active / Reviewed / Approved / Resolved. |
| `add_clash_comment` | Append coordination notes visible in Clash Detective. |
| `rename_clash` | Rename a result or group. |
| `group_clashes` | Regroup a test by status, assignee, level, nearest grid intersection, or item. |
| `create_clash_report` | Write an HTML / CSV / JSON report (optionally with embedded clash images) and return its path. |

### AutoNAV automation tools (ported from AutoNAV2)

These bring the complete AutoNAV2 workflow — discipline detection, search-set generation, clash-test generation, and the AutoNAVismate one-button pipeline — directly into MCP:

| Tool | AutoNAV2 equivalent |
|---|---|
| `list_disciplines` | Read the `1. DISCIPLINES` folder. |
| `create_discipline_search_sets` | **Function 1** — auto-detect disciplines from model filenames and create a search set per discipline. Unknown files are surfaced back so the AI can ask you which discipline they are, then retry with `disciplineOverrides` (replaces the old pop-up picker). |
| `list_discipline_properties` / `list_discipline_property_values` | Discover element properties/values for choosing what to split on. |
| `create_property_search_sets` | **Function 2** — element-property (categorized) search sets per discipline. |
| `create_custom_search_sets` | **Function 3** — a search set per distinct value of any property in a discipline. |
| `generate_clash_tests` | **Function 4** — generate every cross-discipline clash test pair and run them (optional Walls/Floors precursor grouping). |
| `group_walls_floors` | **Function 5** — group every test into Walls / Floors buckets. |
| `group_all_tests` | **Functions 6/7** — group all tests by grid/level/selection/model/status/etc. with a naming template. |
| `run_autonavismate` | **AutoNAVismate** — the full 1→2→4→5→6 pipeline in one call. |

### Example sessions

Coordination on an already-set-up model:

> *"Run all clash tests, then group 'Mechanical vs Structural' by grid intersection, assign everything still New to the Mechanical trade, mark the sprinkler-main duplicates Reviewed with a comment, and give me an HTML report of what's still Active."*

Full setup from a freshly federated model:

> *"Run AutoNAVismate on this model."*

The AI calls `run_autonavismate`; if any model files can't be matched to a discipline it pauses and asks you which discipline each is, then continues once you answer — creating discipline + property search sets, generating and running all clash tests, and grouping/naming the results end-to-end.

## Development

- `docs/ARCHITECTURE.md` — bridge protocol, threading model, multi-year API compatibility notes.
- `mcp-server`: `npm test` runs an end-to-end smoke test against a fake bridge (no Navisworks needed).
- `plugin`: builds cross-platform via the SDK-style project; CI compiles all four Navisworks years on every push.

## License

Internal use only. © Keith Acker.
