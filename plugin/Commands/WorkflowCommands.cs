using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using AutoNAVMCP.Bridge;

namespace AutoNAVMCP.Commands
{
    // Wraps AutoNAV2's clash-test generation (Function 4), Walls/Floors grouping
    // (Function 5), template grouping (Functions 6/7) and the one-shot
    // AutoNAVismate pipeline as headless bridge commands.
    internal static class WorkflowCommands
    {
        // Diagnostic build stamp — incremented on each deploy so the
        // MCP client can confirm which build is executing.
        internal const string BuildStamp = "2026.07.13-fixB";

        // AutoNAVismate's default naming template and Function 6 defaults,
        // mirrored from MainWindow.xaml / RunFunction6WithDefaults.
        public const string DefaultNamingTemplate =
            "{Month}/{Day}_{Level}_{Area} | {TestName} - {SelectionA} vs {SelectionB} {#}";

        // Function 4 — generate every cross-discipline clash test pair and run.
        public static object GenerateClashTests(Dictionary<string, object> args)
        {
            CommandRouter.ActiveDocument();
            bool withPrecursor = CommandRouter.GetBool(args, "wallsFloorsPrecursor", false);

            EngineRun run = EngineRun.Run(() =>
            {
                var engine = new ClashTestGeneratorEngine();
                if (withPrecursor) engine.GenerateClashTestsWithPrecursor();
                else engine.GenerateClashTests();
            });
            return run.ToResult(new Dictionary<string, object>
            {
                { "function", withPrecursor ? "4 — Clash Tests (Walls/Floors precursor grouping)" : "4 — Clash Tests" },
            });
        }

        // Function 5 — group every test's results into Walls / Floors buckets.
        public static object GroupWallsFloors(Dictionary<string, object> args)
        {
            CommandRouter.ActiveDocument();
            string summary = null;
            EngineRun run = EngineRun.Run(() => { summary = ClashGrouper.GroupAllTestsByWallsAndFloors(); });
            return run.ToResult(new Dictionary<string, object>
            {
                { "function", "5 — Walls / Floors Grouping" },
                { "summary", summary ?? "" },
            });
        }

        // Functions 6 / 7 — group every test with the chosen primary (+ optional
        // sub) mode, optionally applying a naming template.
        public static object GroupAllTests(Dictionary<string, object> args)
        {
            System.Console.Error.WriteLine("AUTONAV_DIAG:group_all_tests entered — build " + BuildStamp);

            Document doc = CommandRouter.ActiveDocument();
            DocumentClash clash = ClashHelpers.GetClashPart(doc);

            ClashGrouper.GroupingMode primary = ParseMode(
                CommandRouter.GetString(args, "mode", "GridIntersection"));
            ClashGrouper.GroupingMode sub = ParseMode(
                CommandRouter.GetString(args, "subMode", "None"));
            bool keepExisting = CommandRouter.GetBool(args, "keepExistingGroups", true);

            // namingTemplate: caller value, or the default preset when "default",
            // or "" for legacy auto-naming.
            string templateArg = CommandRouter.GetString(args, "namingTemplate", "default");
            string template =
                templateArg == null ? "" :
                templateArg.Equals("default", StringComparison.OrdinalIgnoreCase) ? DefaultNamingTemplate :
                templateArg;

            var newStatuses = ParseStatusSet(CommandRouter.GetStringList(args, "newStatusFilter"))
                              ?? new HashSet<ClashResultStatus> { ClashResultStatus.New };

            int testCount = 0;
            EngineRun run = EngineRun.Run(() =>
            {
                // Capture GUIDs up front, then re-resolve each test by GUID right
                // before grouping it — grouping a test replaces its object in the
                // collection, so a handle snapshot goes stale after the first one.
                var guids = ClashCompat.EnumerateTests(clash.TestsData).Select(t => t.Guid).ToList();
                foreach (Guid g in guids)
                {
                    DocumentClashTests dct = ClashHelpers.GetClashPart(doc).TestsData;
                    ClashTest fresh = ClashCompat.ResolveTestByGuid(dct, g);
                    if (fresh == null) continue;
                    ClashGrouper.GroupClashes(fresh, primary, sub, keepExisting, template, newStatuses);
                    testCount++;
                }
            });
            return run.ToResult(new Dictionary<string, object>
            {
                { "function", "6/7 — Group All Tests" },
                { "mode", primary.ToString() },
                { "subMode", sub.ToString() },
                { "namingTemplate", template },
                { "testsProcessed", testCount },
            });
        }

        // AutoNAVismate — the full one-shot pipeline: F1 → F2 → F4 → F5 → F6.
        public static object RunAutoNavismate(Dictionary<string, object> args)
        {
            Document doc = CommandRouter.ActiveDocument();

            Dictionary<string, string> overrides = null;
            object raw;
            if (args.TryGetValue("disciplineOverrides", out raw) && raw is Dictionary<string, object>)
            {
                overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in (Dictionary<string, object>)raw)
                    overrides[kv.Key] = kv.Value == null ? "" : Convert.ToString(kv.Value);
            }

            var steps = new List<object>();

            // Step 1 — discipline search sets. If files can't be classified and
            // no override is given, stop and ask the client to gather input.
            EngineRun f1 = EngineRun.Run(() => SearchSetGenerator.GenerateFunction1SearchSets(), overrides);
            var f1Result = f1.ToResult(new Dictionary<string, object> { { "step", "1 — Discipline search sets" } });
            steps.Add(f1Result);
            if (f1.UnresolvedDisciplines.Count > 0)
            {
                return new Dictionary<string, object>
                {
                    { "workflow", "AutoNAVismate" },
                    { "status", "paused" },
                    { "steps", steps },
                    { "needsDisciplineInput", true },
                    { "unresolvedDisciplines", f1.UnresolvedDisciplines },
                    { "hint", "Ask the user which discipline each unresolved 'sourceFile' is, then call " +
                              "run_autonavismate again with disciplineOverrides set." },
                };
            }

            // Step 2 — element-property search sets, first option per discipline.
            var disciplines = SearchSetGenerator.GetAvailableDisciplines();
            var f2Log = new List<string>();
            foreach (string discipline in disciplines)
            {
                string canonical;
                SearchSetGenerator.DisciplineRegistry.TryGetValue(discipline, out canonical);
                var options = SearchSetGenerator.PropertyOptionsFor(canonical);
                if (options.Length == 0) continue;
                var opt = options[0];
                EngineRun r = EngineRun.Run(() => SearchSetGenerator.GenerateFunction2SearchSets(
                    new List<string> { discipline }, opt.Category, opt.Property));
                f2Log.AddRange(r.Log);
            }
            steps.Add(new Dictionary<string, object> { { "step", "2 — Element-property search sets" }, { "log", f2Log } });

            // Step 3 — generate + run clash tests (Function 4).
            EngineRun f4 = EngineRun.Run(() => new ClashTestGeneratorEngine().GenerateClashTests());
            steps.Add(f4.ToResult(new Dictionary<string, object> { { "step", "4 — Generate + run clash tests" } }));

            // Step 4 — Walls / Floors grouping (Function 5).
            string f5Summary = null;
            EngineRun f5 = EngineRun.Run(() => { f5Summary = ClashGrouper.GroupAllTestsByWallsAndFloors(); });
            steps.Add(f5.ToResult(new Dictionary<string, object>
                { { "step", "5 — Walls / Floors grouping" }, { "summary", f5Summary ?? "" } }));

            // Step 5 — Function 6: grid-intersection grouping + template naming,
            // preserving the Walls/Floors groups from step 4.
            var newStatuses = new HashSet<ClashResultStatus> { ClashResultStatus.New };
            int f6Count = 0;
            EngineRun f6 = EngineRun.Run(() =>
            {
                var guids = ClashCompat.EnumerateTests(ClashHelpers.GetClashPart(doc).TestsData)
                    .Select(t => t.Guid).ToList();
                foreach (Guid g in guids)
                {
                    DocumentClashTests dct = ClashHelpers.GetClashPart(doc).TestsData;
                    ClashTest fresh = ClashCompat.ResolveTestByGuid(dct, g);
                    if (fresh == null) continue;
                    ClashGrouper.GroupClashes(fresh, ClashGrouper.GroupingMode.GridIntersection,
                        ClashGrouper.GroupingMode.None, true, "", newStatuses);

                    ClashTest afterGroup = ClashCompat.ResolveTestByGuid(
                        ClashHelpers.GetClashPart(doc).TestsData, g);
                    if (afterGroup != null)
                        RenameGroupsExcludingWallsFloors(ClashHelpers.GetClashPart(doc), afterGroup, DefaultNamingTemplate);
                    f6Count++;
                }
            });
            steps.Add(f6.ToResult(new Dictionary<string, object>
                { { "step", "6 — Grid grouping + naming" }, { "testsProcessed", f6Count } }));

            return new Dictionary<string, object>
            {
                { "workflow", "AutoNAVismate" },
                { "status", "complete" },
                { "steps", steps },
                { "note", "All five steps ran. Open Clash Detective to review results." },
            };
        }

        // Rename every group in a test with the template, except the literal
        // "Walls"/"Floors" groups (Function 5's output). Mirrors AutoNAV2's
        // RenameGroupsExcludingWallsFloors.
        private static void RenameGroupsExcludingWallsFloors(DocumentClash clash, ClashTest test, string template)
        {
            if (test == null || string.IsNullOrWhiteSpace(template)) return;

            // Re-resolve the test in the document (grouping replaced the object).
            ClashTest live = ClashCompat.EnumerateTests(clash.TestsData)
                .FirstOrDefault(t => t.Guid == test.Guid) ?? test;

            var toRename = new List<ClashResultGroup>();
            foreach (SavedItem child in live.Children)
            {
                var grp = child as ClashResultGroup;
                if (grp == null) continue;
                string n = (grp.DisplayName ?? "").Trim();
                if (n.Equals("Walls", StringComparison.OrdinalIgnoreCase)) continue;
                if (n.Equals("Floors", StringComparison.OrdinalIgnoreCase)) continue;
                toRename.Add(grp);
            }
            if (toRename.Count > 0)
                ClashGrouper.RenameGroupsWithTemplate(toRename, live, template);
        }

        private static ClashGrouper.GroupingMode ParseMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return ClashGrouper.GroupingMode.None;
            ClashGrouper.GroupingMode parsed;
            if (Enum.TryParse(mode, true, out parsed)) return parsed;
            throw new CommandException("Invalid grouping mode '" + mode + "'. Valid modes: " +
                string.Join(", ", Enum.GetNames(typeof(ClashGrouper.GroupingMode))));
        }

        private static HashSet<ClashResultStatus> ParseStatusSet(List<string> statuses)
        {
            if (statuses == null || statuses.Count == 0) return null;
            var set = new HashSet<ClashResultStatus>();
            foreach (string s in statuses) set.Add(ClashHelpers.ParseStatus(s));
            return set;
        }
    }
}
