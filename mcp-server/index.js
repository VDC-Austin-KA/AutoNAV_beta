#!/usr/bin/env node
// AutoNAV MCP server — exposes Autodesk Navisworks Manage clash coordination
// to MCP clients (Claude Desktop, Claude Code, etc.).
//
// Requires the AutoNAV MCP bridge plugin running inside Navisworks Manage
// (Add-Ins ribbon tab -> AutoNAV MCP). See README.md for setup.

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { sendCommand, bridgeAddress } from "./lib/bridge.js";

const server = new McpServer({
  name: "autonav-navisworks",
  version: "1.0.0",
});

const CLASH_STATUSES = ["New", "Active", "Reviewed", "Approved", "Resolved"];

function asText(result) {
  return { content: [{ type: "text", text: JSON.stringify(result, null, 2) }] };
}

function asError(error) {
  return {
    content: [{ type: "text", text: `Error: ${error.message}` }],
    isError: true,
  };
}

function tool(name, description, shape, handler) {
  server.registerTool(name, { description, inputSchema: shape }, async (args) => {
    try {
      return asText(await handler(args ?? {}));
    } catch (error) {
      return asError(error);
    }
  });
}

// ── Status / document ──────────────────────────────────────────────

tool(
  "navisworks_status",
  "Check connectivity to Navisworks Manage and whether a model is open. Use this first to verify the AutoNAV MCP bridge is running.",
  {},
  async () => {
    const result = await sendCommand("ping", {}, 15000);
    return { bridge: bridgeAddress(), ...result };
  }
);

tool(
  "get_document_info",
  "Get information about the model currently open in Navisworks: title, file name, units, appended model files, and clash test count.",
  {},
  (args) => sendCommand("get_document_info", args)
);

tool(
  "list_search_sets",
  "List all saved selection sets, search sets and folders in the open Navisworks model, with their '/'-separated paths. Use these paths as selectionA/selectionB when creating clash tests.",
  {},
  (args) => sendCommand("list_search_sets", args)
);

// ── Clash identification ───────────────────────────────────────────

tool(
  "list_clash_tests",
  "List all clash tests in the open model with their type, status, last-run time, and clash result counts broken down by status (New/Active/Reviewed/Approved/Resolved).",
  {},
  (args) => sendCommand("list_clash_tests", args)
);

tool(
  "create_clash_test",
  "Create a new clash test between two saved selection/search sets or folders (identified by path or name, e.g. '2. CLASH SETS/Mechanical'). Optionally run it immediately.",
  {
    name: z.string().describe("Display name for the new clash test, e.g. 'Mechanical vs Structural'"),
    selectionA: z.string().describe("Path or name of the selection/search set (or folder of sets) for the left side"),
    selectionB: z.string().describe("Path or name of the selection/search set (or folder of sets) for the right side"),
    clashType: z.enum(["Hard", "Clearance", "Duplicate"]).optional().describe("Clash test type (default Hard)"),
    tolerance: z.number().optional().describe("Tolerance in model units (default 0)"),
    run: z.boolean().optional().describe("Run the test immediately after creating it (default false)"),
  },
  (args) => sendCommand("create_clash_test", args)
);

tool(
  "run_clash_test",
  "Run (update) a single clash test by name or GUID and return its result counts.",
  { test: z.string().describe("Clash test name or GUID") },
  (args) => sendCommand("run_clash_test", args)
);

tool(
  "run_all_clash_tests",
  "Run (update) every clash test in the model — equivalent to 'Update All' in Clash Detective — and return per-test result counts.",
  {},
  (args) => sendCommand("run_all_clash_tests", args)
);

tool(
  "delete_clash_test",
  "Delete a clash test (and all of its results) from the model.",
  { test: z.string().describe("Clash test name or GUID") },
  (args) => sendCommand("delete_clash_test", args)
);

tool(
  "get_clash_results",
  "List clash results in a test, with status, distance, nearest grid location, assignment, and the two clashing items. Supports filtering and pagination.",
  {
    test: z.string().describe("Clash test name or GUID"),
    status: z.enum(CLASH_STATUSES).optional().describe("Only return clashes with this status"),
    assignedTo: z.string().optional().describe("Only return clashes assigned to this person/trade"),
    group: z.string().optional().describe("Only return clashes inside this clash group"),
    offset: z.number().int().min(0).optional().describe("Pagination offset (default 0)"),
    limit: z.number().int().min(1).max(500).optional().describe("Maximum results to return (default 100)"),
    includeItems: z.boolean().optional().describe("Include clashing item details (default true)"),
  },
  (args) => sendCommand("get_clash_results", args)
);

tool(
  "get_clash_result",
  "Get full details of a single clash result: comments, timestamps, and the property categories of both clashing items (useful for deciding trade assignment and resolution).",
  {
    test: z.string().describe("Clash test name or GUID"),
    clash: z.string().describe("Clash result name or GUID"),
  },
  (args) => sendCommand("get_clash_result", args)
);

// ── Clash assignment ───────────────────────────────────────────────

tool(
  "assign_clashes",
  "Assign clash results to a person or trade (sets the Clash Detective 'Assigned To' field). Targets specific clashes, a whole group, all clashes with a given status, or every clash in the test.",
  {
    test: z.string().describe("Clash test name or GUID"),
    assignTo: z.string().describe("Person or trade to assign to; empty string to unassign"),
    clashes: z.array(z.string()).optional().describe("Clash result names/GUIDs or group names to target (default: all clashes in the test)"),
    statusFilter: z.enum(CLASH_STATUSES).optional().describe("Only touch clashes currently in this status"),
  },
  (args) => sendCommand("assign_clashes", args)
);

// ── Clash resolution / updating ────────────────────────────────────

tool(
  "set_clash_status",
  "Update the status of clash results (New, Active, Reviewed, Approved or Resolved). Use this to mark clashes reviewed/approved/resolved as coordination progresses.",
  {
    test: z.string().describe("Clash test name or GUID"),
    status: z.enum(CLASH_STATUSES).describe("New status to apply"),
    clashes: z.array(z.string()).optional().describe("Clash result names/GUIDs or group names to target (default: all clashes in the test)"),
    statusFilter: z.enum(CLASH_STATUSES).optional().describe("Only touch clashes currently in this status"),
    user: z.string().optional().describe("Acting user recorded as Approved/Resolved by (Navisworks 2026+)"),
  },
  (args) => sendCommand("set_clash_status", args)
);

tool(
  "add_clash_comment",
  "Append a comment to one or more clash results (visible in Clash Detective's Comments panel) — e.g. a proposed resolution or coordination note.",
  {
    test: z.string().describe("Clash test name or GUID"),
    comment: z.string().describe("Comment text"),
    clashes: z.array(z.string()).optional().describe("Clash result names/GUIDs or group names to target (default: all clashes in the test)"),
    author: z.string().optional().describe("Comment author (defaults to the Windows user)"),
  },
  (args) => sendCommand("add_clash_comment", args)
);

tool(
  "rename_clash",
  "Rename a single clash result or clash group.",
  {
    test: z.string().describe("Clash test name or GUID"),
    clash: z.string().describe("Current clash result/group name or GUID (must match exactly one)"),
    newName: z.string().describe("New display name"),
  },
  (args) => sendCommand("rename_clash", args)
);

tool(
  "group_clashes",
  "Rebuild a clash test's results into groups by status, assignedTo, level, gridIntersection (nearest grid + level), or item. Existing grouping in the test is replaced.",
  {
    test: z.string().describe("Clash test name or GUID"),
    mode: z
      .enum(["status", "assignedTo", "level", "gridIntersection", "item"])
      .optional()
      .describe("Grouping key (default gridIntersection)"),
  },
  (args) => sendCommand("group_clashes", args)
);

// ── Reporting ──────────────────────────────────────────────────────

tool(
  "create_clash_report",
  "Generate a clash report file (HTML, CSV or JSON) on the Navisworks machine, covering selected tests or all of them. HTML reports include summary and per-test detail tables, optionally with embedded clash images. Returns the saved file path.",
  {
    tests: z.array(z.string()).optional().describe("Clash test names/GUIDs to include (default: all tests)"),
    format: z.enum(["html", "csv", "json"]).optional().describe("Report format (default html)"),
    status: z.enum(CLASH_STATUSES).optional().describe("Only include clashes with this status"),
    outputPath: z.string().optional().describe("Explicit output file path (default: Documents\\AutoNAV Reports\\<model>_ClashReport_<timestamp>)"),
    includeImages: z.boolean().optional().describe("HTML only: embed a viewpoint image per clash (slower on large tests)"),
  },
  (args) => sendCommand("create_clash_report", args)
);

// ── Startup ────────────────────────────────────────────────────────

const transport = new StdioServerTransport();
await server.connect(transport);
console.error(`AutoNAV MCP server ready (bridge target ${bridgeAddress()}).`);
