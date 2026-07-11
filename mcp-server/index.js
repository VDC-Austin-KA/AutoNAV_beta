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
import { startHttpServer } from "./lib/http.js";

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

// Builds a fresh McpServer with every tool registered. A factory (rather
// than a singleton) so the HTTP transport can serve stateless requests,
// each of which needs its own server instance.
export function buildServer() {
  const server = new McpServer({
    name: "autonav-navisworks",
    version: "1.0.0",
  });

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

  // ── AutoNAV2 workflow: search-set generation (Functions 1-3) ────────

  tool(
    "list_disciplines",
    "List the discipline search sets currently under the '1. DISCIPLINES' folder (created by create_discipline_search_sets). These feed create_property_search_sets and clash-test generation.",
    {},
    (args) => sendCommand("list_disciplines", args)
  );

  tool(
    "create_discipline_search_sets",
    "AutoNAV Function 1: auto-detect disciplines from the loaded model filenames and create one search set per discipline under '1. DISCIPLINES'. If some files can't be classified, the result has needsDisciplineInput=true and lists them under unresolvedDisciplines — ask the user which discipline each file is, then call again with disciplineOverrides.",
    {
      disciplineOverrides: z
        .record(z.string())
        .optional()
        .describe("Map of model source filename -> discipline token (e.g. {\"Level 3 - HVAC.rvt\": \"Mechanical\"}) for files that couldn't be auto-classified"),
    },
    (args) => sendCommand("create_discipline_search_sets", args)
  );

  tool(
    "list_discipline_properties",
    "List the element-property categories and properties available for a discipline's models — use to choose propertyCategory/propertyName for create_property_search_sets or create_custom_search_sets.",
    { discipline: z.string().describe("Discipline search-set name (from list_disciplines)") },
    (args) => sendCommand("list_discipline_properties", args)
  );

  tool(
    "list_discipline_property_values",
    "List the distinct values of a given element property within a discipline's models (e.g. all 'System Abbreviation' values). Each value becomes its own search set in create_custom_search_sets.",
    {
      discipline: z.string().describe("Discipline search-set name"),
      propertyCategory: z.string().describe("Property category, e.g. 'Element'"),
      propertyName: z.string().describe("Property name, e.g. 'System Abbreviation'"),
    },
    (args) => sendCommand("list_discipline_property_values", args)
  );

  tool(
    "suggest_search_set_properties",
    "Probe a discipline's models and rank the property locations that actually hold a usable system identifier — a duct's system code may live under Element/System Classification, Element/System Abbreviation, Element Properties/System Abbreviation, etc. Returns each candidate with coverage %, distinct-value count, average value length, and example values, plus a recommendation. Values are scored to favor SHORTER identifiers because the value is embedded in every clash-group name (less is more) — trades recognize their own system codes even when a value isn't self-explanatory. Use this before create_property_search_sets / create_custom_search_sets, and present the ranked options to the user.",
    {
      discipline: z.string().describe("Discipline search-set name (from list_disciplines)"),
      maxItemsToScan: z.number().int().min(500).max(200000).optional().describe("Element scan cap per discipline (default 15000)"),
    },
    (args) => sendCommand("suggest_search_set_properties", args)
  );

  tool(
    "create_property_search_sets",
    "AutoNAV Function 2: create element-property (categorized) search sets under '2. CLASH SETS' for the given disciplines. If propertyCategory/propertyName are omitted, each discipline uses its recommended default property (as AutoNAVismate does).",
    {
      disciplines: z.array(z.string()).optional().describe("Discipline names to process (default: all disciplines)"),
      propertyCategory: z.string().optional().describe("Property category to split on, e.g. 'Element'"),
      propertyName: z.string().optional().describe("Property name to split on, e.g. 'System Abbreviation'"),
    },
    (args) => sendCommand("create_property_search_sets", args)
  );

  tool(
    "create_custom_search_sets",
    "AutoNAV Function 3: create a custom search set for every distinct value of a chosen property within one discipline's models (advanced refinement).",
    {
      discipline: z.string().describe("Discipline search-set name"),
      propertyCategory: z.string().describe("Property category, e.g. 'Element'"),
      propertyName: z.string().describe("Property name, e.g. 'Workset'"),
    },
    (args) => sendCommand("create_custom_search_sets", args)
  );

  // ── AutoNAV2 workflow: clash generation, grouping, full pipeline ────

  tool(
    "generate_clash_tests",
    "AutoNAV Function 4: generate every cross-discipline clash test pair from the discipline search sets and run them. Set wallsFloorsPrecursor=true for Function 5-style Walls/Floors precursor grouping during generation.",
    {
      wallsFloorsPrecursor: z.boolean().optional().describe("Generate Walls/Floors precursor-grouped tests (default false)"),
    },
    (args) => sendCommand("generate_clash_tests", args)
  );

  tool(
    "group_walls_floors",
    "AutoNAV Function 5: group every clash test's results into 'Walls' and 'Floors' buckets per discipline pair.",
    {},
    (args) => sendCommand("group_walls_floors", args)
  );

  tool(
    "group_all_tests",
    "AutoNAV Functions 6/7: group every clash test's results by the chosen mode (default gridIntersection = nearest grid + level) with an optional sub-grouping mode, and apply a naming template. namingTemplate supports tokens {Month}{Day}{Year}{Level}{Area}{TestName}{SelectionA}{SelectionB}{#}; pass 'default' for the standard preset or '' for legacy auto-naming.",
    {
      mode: z
        .enum(["None", "Level", "GridIntersection", "SelectionA", "SelectionB", "ModelA", "ModelB", "AssignedTo", "ApprovedBy", "Status", "File", "Layer", "First", "Last", "LastUnique", "WallsAndFloors"])
        .optional()
        .describe("Primary grouping mode (default GridIntersection)"),
      subMode: z
        .enum(["None", "Level", "GridIntersection", "SelectionA", "SelectionB", "ModelA", "ModelB", "AssignedTo", "ApprovedBy", "Status", "File", "Layer", "First", "Last", "LastUnique"])
        .optional()
        .describe("Optional sub-grouping mode within each primary group (default None)"),
      namingTemplate: z.string().optional().describe("'default' for the standard preset, '' for legacy names, or a custom template with {tokens}"),
      keepExistingGroups: z.boolean().optional().describe("Preserve existing groups such as Walls/Floors (default true)"),
      newStatusFilter: z.array(z.enum(CLASH_STATUSES)).optional().describe("Only freshly group clashes in these statuses (default [New])"),
    },
    (args) => sendCommand("group_all_tests", args)
  );

  tool(
    "run_autonavismate",
    "AutoNAVismate: run the entire AutoNAV pipeline end-to-end — Function 1 (discipline search sets) → 2 (property search sets) → 4 (generate + run clash tests) → 5 (Walls/Floors grouping) → 6 (grid grouping + naming). If Function 1 can't classify some files it pauses with needsDisciplineInput=true; gather the disciplines from the user and call again with disciplineOverrides.",
    {
      disciplineOverrides: z
        .record(z.string())
        .optional()
        .describe("Map of model source filename -> discipline token for files that couldn't be auto-classified"),
    },
    (args) => sendCommand("run_autonavismate", args)
  );

  return server;
}

// ── Startup ────────────────────────────────────────────────────────

// Default transport is stdio (Claude Desktop / Claude Code / VS Code
// GitHub Copilot). Pass --http [port] or set AUTONAV_MCP_HTTP_PORT to
// serve streamable HTTP instead (required for Microsoft Copilot Studio;
// see docs/COPILOT.md).
const httpFlagIndex = process.argv.indexOf("--http");
const httpPort =
  httpFlagIndex >= 0
    ? Number(process.argv[httpFlagIndex + 1] || 3711)
    : process.env.AUTONAV_MCP_HTTP_PORT
      ? Number(process.env.AUTONAV_MCP_HTTP_PORT)
      : null;

if (httpPort) {
  await startHttpServer(buildServer, httpPort);
  console.error(
    `AutoNAV MCP server listening on http://127.0.0.1:${httpPort}/mcp (bridge target ${bridgeAddress()}).`
  );
} else {
  const transport = new StdioServerTransport();
  await buildServer().connect(transport);
  console.error(`AutoNAV MCP server ready (bridge target ${bridgeAddress()}).`);
}
