# Architecture

## Overview

```
MCP client (Claude)
   │  MCP over stdio (JSON-RPC)
   ▼
mcp-server/index.js          ── tool schemas, validation (zod)
mcp-server/lib/bridge.js     ── lazy TCP client, request/response correlation, reconnect
   │  newline-delimited JSON over 127.0.0.1:5711
   ▼
plugin/Bridge/BridgeServer.cs ── TcpListener, one thread per client
plugin/Bridge/MainThread.cs   ── marshals every command onto the Navisworks UI thread
plugin/Commands/*             ── Navisworks .NET API calls (Clash Detective etc.)
```

## Bridge protocol

One JSON object per line, in both directions:

```json
{"id": 7, "command": "assign_clashes", "params": {"test": "Mech vs Struct", "assignTo": "Mechanical"}}
{"id": 7, "ok": true, "result": {"test": "Mech vs Struct", "assignedTo": "Mechanical", "clashesUpdated": 42}}
{"id": 8, "ok": false, "error": "Clash test 'X' not found. Use list_clash_tests to see available tests."}
```

Design choices:

- **TCP, not HTTP.** `HttpListener` needs URL ACL reservations (or admin rights) on Windows; a loopback `TcpListener` doesn't. The MCP server is the only intended client and runs on the same machine.
- **Loopback only.** The listener binds `127.0.0.1`; nothing is exposed on the network.
- **Dependency-free JSON.** `MiniJson.cs` keeps the plugin a single DLL — nothing else has to be shipped into the Navisworks `Plugins` folder.
- **Errors are responses.** Handler exceptions become `ok:false` messages; the connection stays up, and the MCP server surfaces the text as a tool error so the AI can self-correct (e.g. call `list_clash_tests` after a typo).

## Threading

The Navisworks .NET API must be called from the application's main thread. `MainThread.Initialize()` runs when the user clicks the ribbon button (which executes on the UI thread) and creates a hidden WinForms `Control`; socket threads then funnel every command through `Control.Invoke`. One command executes at a time, serialized on the UI thread — no locking needed in command handlers.

## Multi-year Clash API compatibility

`plugin/ClashCompat.cs` (extended from AutoNAV2's shim) papers over API changes between Navisworks 2024–2027, switched at compile time via `NW<year>` define constants:

| Area | 2024 / 2025 | 2026 | 2027 |
|---|---|---|---|
| `ClashResult.AssignedTo` / `ApprovedBy` | `string` | `Assignee` | `Assignee` |
| `TestsEditResultAssignedTo` | `(IClashResult, string)` | `(IClashResult, Assignee)` | `(IClashResult, Assignee)` |
| `TestsEditResultStatus` | `(IClashResult, ClashResultStatus)` | `(…, Assignee currentUser)` | `(…, Assignee currentUser)` |
| `ClashResult.ResolvedBy/ResolvedTime` | absent | present | present |
| Top-level tests | `dct.Tests` | `dct.Tests` | `dct.Value.TestsRoot.Children` |
| Add/replace at root | implicit-root overloads | implicit-root overloads | explicit `TestsRoot` parent |
| `Comment.Author` | `string` | `string` | `Assignee` |

These signatures were verified directly against the `Speckle.Navisworks.API` reference assemblies for each year, and CI compiles all four variants on every push.

## Grouping rebuild pattern

`group_clashes` reuses the pattern proven in AutoNAV2's `ClashGrouper.ProcessClashGroup`: `TestsAddCopy` does **not** deep-copy an in-memory `ClashResultGroup`'s children, so the test is replaced with a childless copy inside a transaction, each group is added as an empty shell, the live document-bound group reference is re-fetched, and results are appended one by one via `TestsAddCopy`.

### Native-handle safety (the F5 → F6 fix)

Grouping replaces a `ClashTest` in the tests collection (`TestsReplaceWithCopy`), which **invalidates any `ClashTest` / `ClashResultGroup` handle captured before the replace** — a later dereference throws *"Object has been Disposed (WeakRef) NativeHandle"*. Two rules keep the pipeline stable when Function 5 runs before Function 6 in the same session:

1. **Never dereference a stale handle to locate a test.** `ClashCompat.IndexOfTestByGuid` / `ResolveTestByGuid` find the test by GUID against the *current* collection; `IndexOfTest` (which calls `.IndexOf(handle)` and touches the passed handle) is avoided in the grouping paths. GUIDs are captured while the handle is valid, then everything re-resolves by GUID — after the clearing replace, and on every iteration of the multi-test loops (`group_all_tests`, AutoNAVismate F6).

2. **Add preserved groups whole; build fresh groups by shell+fill.** Feeding a `CreateCopy()`'d existing group — whose children are already *owned* by the copy — through the shell+fill loop throws *"Cannot transfer ownership from argument 'child'"*. `ProcessClashGroup` takes `preserveGroups` separately and adds each whole in a single `TestsAddCopy` (a document-derived group deep-copies intact), while only freshly-built in-memory groups go through shell+fill.

Because CI can't run Navisworks, this path is guarded by the manual QA checklist below.

### QA regression checklist (F5 → F6, requires live Navisworks)

On a model with ≥2 clash tests that each have ≥100 clashes:
1. `generate_clash_tests` (or otherwise run tests with results).
2. `group_walls_floors` (Function 5) — succeeds.
3. **In the same session**, `group_all_tests` (`GridIntersection`, `keepExistingGroups=true`, `namingTemplate=default`).
4. Assert: no exception; group count increased on each test.
5. Assert: the `Walls` / `Floors` groups from step 2 are still present.
6. Repeat step 3 via `group_clashes` per test in modes `gridIntersection`, `item`, `level` — no exception.
7. Run `run_autonavismate` end-to-end; F6 completes after F5 with no save/close/reopen.

## Reports

Reports are generated by the plugin (not the MCP server) so element data never has to round-trip in bulk. HTML reports are self-contained (inline CSS, base64 images). Clash images use `TestsImageForResult`, located via reflection because its signature varies across releases; when unavailable the image cell is simply omitted.

## Macros (record / save / recall / replay)

Because every AutoNAV action funnels through `CommandRouter.Execute`, that single choke point is where recording is wired in. When a session is being recorded, `Execute` runs the handler and then appends `(command, params, ok, one-line outcome)` to `MacroRecorder`'s in-memory journal; control commands (`start_recording`, `replay_macro`, …) self-exclude so recording and replay never capture themselves or recurse. Read-only queries are journaled but flagged non-mutating, so `save_macro` keeps just the mutating steps by default.

`MacroStore` persists each macro as a JSON file under `%AppData%/AutoNAV/macros` on the Navisworks machine (one macro = an ordered list of `(command, params)` steps), so macros survive restarts and are recalled through the MCP. `replay_macro` re-invokes `CommandRouter.Execute` for each step in order, supporting `dryRun` (preview), `stopOnError`, and per-step parameter `overrides` (keyed by 1-based index) so the same macro can be retargeted — e.g. run a recorded discipline workflow against a different discipline or test.

**Scope boundary:** this records *AutoNAV/MCP commands*, which are deterministic and replayable. It cannot record arbitrary manual Navisworks UI interaction (camera, ribbon, manual selection) — the Navisworks .NET API exposes no global command/input event stream to hook, so there is nothing to capture or replay for those.

## Ported AutoNAV2 engines

The full AutoNAV2 feature set (Functions 1–7 + AutoNAVismate) is ported verbatim into `plugin/Engine/` — `SearchSetGenerator`, `ClashTestGeneratorEngine`, `ClashGrouper`, `ClashResultGrouper`, `Notifier` — under the `AutoNAVMCP` namespace. The engine logic is unchanged; only the presentation layer is adapted for headless, AI-driven operation:

- **`Notifier`** kept as-is (a static `Sink` shim). `EngineContext.EngineRun.Run` swaps in a sink that captures each `Info/Warning/Error/Result` message into a `log` array returned to the client, so progress and summaries surface as tool output instead of WPF status text.
- **The `UnknownDisciplineDialog`** (WPF) is removed. Function 1's classifier now reads caller-supplied overrides from `DisciplineContext.Overrides` (source filename → discipline token); any file it still can't classify is recorded and returned as `unresolvedDisciplines` with `needsDisciplineInput: true`. The MCP client asks the user and re-invokes with `disciplineOverrides`. This turns a modal Windows dialog into a normal AI prompt.
- **No WPF/WinForms UI** is compiled in — the plugin references `System.Windows.Forms` only for the main-thread marshalling control and message boxes in `PluginMain`.

### Property suggestion (`suggest_search_set_properties`)

A new capability with no AutoNAV2 equivalent (`SearchSetGenerator.Suggest.cs`, a `partial` extension of the ported class). System identifiers are inconsistently located across authoring tools — a duct's system code may sit under `Element / System Classification`, `Element / System Abbreviation`, `Element Properties / System Abbreviation`, and so on. Rather than making the user know where, the suggester does a single bounded walk (default 15,000 items) of the discipline's models, accumulating per-`(category, property)` stats for a curated candidate list plus any property whose name matches identifier keywords (`system`, `abbrev`, `classif`, `workset`, …).

Each candidate is scored `0.45·coverage + 0.35·brevity + 0.20·granularity`. **Brevity is weighted heavily and deliberately** — the chosen value is embedded into every clash-group name, so shorter identifiers (a system abbreviation) are preferred over long descriptive names, on the premise that the assigned trade recognizes its own codes. The tool returns the ranked candidates with coverage %, distinct-value count, value-length stats, and shortest-first example values, plus a single recommendation; the AI presents these for the user to choose before `create_property_search_sets` / `create_custom_search_sets`.

`WorkflowCommands` reproduces `MainWindow`'s AutoNAVismate sequence (F1 → F2-with-default-property-per-discipline → F4 → F5 → F6-grid-grouping-plus-template-naming, preserving Walls/Floors groups via `RenameGroupsExcludingWallsFloors`). Because grouping replaces the live `ClashTest` object, tests are re-resolved by `Guid` between steps.

All four Navisworks years compile with the ported engines; the engines call the same `ClashCompat` shim documented above.

## Relationship to AutoNAV2

AutoNAV2 (`VDC-Austin-KA/AutoNAV2`) is untouched and remains the interactive ribbon plugin. This repo reuses its build approach (SDK-style csproj + Speckle NuGet packages, per-year `DefineConstants`), its Clash API idioms, and now its complete engine code — re-fronted from a ribbon UI onto MCP tools driven by an AI client.
