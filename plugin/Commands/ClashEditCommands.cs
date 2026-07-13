using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using AutoNAVMCP.Bridge;

namespace AutoNAVMCP.Commands
{
    internal static class ClashEditCommands
    {
        // ── Test lifecycle ───────────────────────────────────────────

        public static object CreateClashTest(Dictionary<string, object> args)
        {
            Document doc = CommandRouter.ActiveDocument();
            DocumentClash clash = ClashHelpers.GetClashPart(doc);

            string name = CommandRouter.RequireString(args, "name");
            string selectionA = CommandRouter.RequireString(args, "selectionA");
            string selectionB = CommandRouter.RequireString(args, "selectionB");
            double tolerance = CommandRouter.GetDouble(args, "tolerance", 0.0);
            string typeName = CommandRouter.GetString(args, "clashType", "Hard");

            ClashTestType testType;
            try { testType = (ClashTestType)Enum.Parse(typeof(ClashTestType), typeName, true); }
            catch
            {
                throw new CommandException("Invalid clashType '" + typeName + "'. Valid values: " +
                    string.Join(", ", Enum.GetNames(typeof(ClashTestType))));
            }

            foreach (ClashTest existing in ClashCompat.EnumerateTests(clash.TestsData))
                if (existing.DisplayName != null &&
                    existing.DisplayName.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new CommandException("A clash test named '" + name + "' already exists.");

            var sourcesA = ClashHelpers.BuildSelectionSources(doc, ClashHelpers.FindSelectionSetItem(doc, selectionA));
            var sourcesB = ClashHelpers.BuildSelectionSources(doc, ClashHelpers.FindSelectionSetItem(doc, selectionB));
            if (sourcesA.Count == 0) throw new CommandException("'" + selectionA + "' contains no selection/search sets.");
            if (sourcesB.Count == 0) throw new CommandException("'" + selectionB + "' contains no selection/search sets.");

            var test = new ClashTest
            {
                DisplayName = name,
                CustomTestName = name,
                TestType = testType,
                Tolerance = tolerance,
            };
            test.SelectionA.SelfIntersect = false;
            test.SelectionA.PrimitiveTypes = PrimitiveTypes.Triangles;
            foreach (SelectionSource source in sourcesA) test.SelectionA.Selection.SelectionSources.Add(source);
            test.SelectionB.SelfIntersect = false;
            test.SelectionB.PrimitiveTypes = PrimitiveTypes.Triangles;
            foreach (SelectionSource source in sourcesB) test.SelectionB.Selection.SelectionSources.Add(source);

            ClashCompat.TestsAddCopyAtRoot(clash.TestsData, test);

            bool run = CommandRouter.GetBool(args, "run", false);
            if (run)
            {
                ClashTest created = ClashHelpers.FindTest(clash.TestsData, name);
                clash.TestsData.TestsRunTest(created);
            }

            ClashTest final = ClashHelpers.FindTest(clash.TestsData, name);
            return new Dictionary<string, object>
            {
                { "created", name },
                { "guid", final.Guid.ToString() },
                { "ran", run },
                { "resultCounts", ClashHelpers.CountResultsByStatus(final) },
            };
        }

        public static object DeleteClashTest(Dictionary<string, object> args)
        {
            Document doc = CommandRouter.ActiveDocument();
            DocumentClash clash = ClashHelpers.GetClashPart(doc);
            ClashTest test = ClashHelpers.FindTest(clash.TestsData, CommandRouter.RequireString(args, "test"));
            string name = test.DisplayName;
            ClashCompat.TestsRemoveAtRoot(clash.TestsData, test);
            return new Dictionary<string, object> { { "deleted", name } };
        }

        public static object RunClashTest(Dictionary<string, object> args)
        {
            Document doc = CommandRouter.ActiveDocument();
            DocumentClash clash = ClashHelpers.GetClashPart(doc);
            ClashTest test = ClashHelpers.FindTest(clash.TestsData, CommandRouter.RequireString(args, "test"));
            clash.TestsData.TestsRunTest(test);
            // Re-resolve: running replaces the underlying test object.
            ClashTest updated = ClashHelpers.FindTest(clash.TestsData, test.DisplayName);
            return new Dictionary<string, object>
            {
                { "test", updated.DisplayName ?? "" },
                { "status", updated.Status.ToString() },
                { "resultCounts", ClashHelpers.CountResultsByStatus(updated) },
            };
        }

        public static object RunAllClashTests(Dictionary<string, object> args)
        {
            Document doc = CommandRouter.ActiveDocument();
            DocumentClash clash = ClashHelpers.GetClashPart(doc);
            clash.TestsData.TestsRunAllTests();

            var summary = new List<object>();
            foreach (ClashTest test in ClashCompat.EnumerateTests(clash.TestsData))
            {
                summary.Add(new Dictionary<string, object>
                {
                    { "test", test.DisplayName ?? "" },
                    { "status", test.Status.ToString() },
                    { "resultCounts", ClashHelpers.CountResultsByStatus(test) },
                });
            }
            return new Dictionary<string, object> { { "testsRun", summary.Count }, { "tests", summary } };
        }

        // ── Result edits: assignment / status / comments / naming ────

        public static object AssignClashes(Dictionary<string, object> args)
        {
            Document doc = CommandRouter.ActiveDocument();
            DocumentClash clash = ClashHelpers.GetClashPart(doc);
            ClashTest test = ClashHelpers.FindTest(clash.TestsData, CommandRouter.RequireString(args, "test"));

            // Empty string unassigns.
            string assignTo = CommandRouter.GetString(args, "assignTo", "");
            var targets = ClashHelpers.ResolveResults(test,
                CommandRouter.GetStringList(args, "clashes"),
                CommandRouter.GetString(args, "statusFilter"));

            int edited = 0;
            foreach (ClashResult result in targets)
            {
                ClashCompat.EditResultAssignedTo(clash.TestsData, result, assignTo);
                edited++;
            }
            return new Dictionary<string, object>
            {
                { "test", test.DisplayName ?? "" },
                { "assignedTo", assignTo },
                { "clashesUpdated", edited },
            };
        }

        public static object SetClashStatus(Dictionary<string, object> args)
        {
            Document doc = CommandRouter.ActiveDocument();
            DocumentClash clash = ClashHelpers.GetClashPart(doc);
            ClashTest test = ClashHelpers.FindTest(clash.TestsData, CommandRouter.RequireString(args, "test"));

            ClashResultStatus status = ClashHelpers.ParseStatus(CommandRouter.RequireString(args, "status"));
            string user = CommandRouter.GetString(args, "user", "");
            var targets = ClashHelpers.ResolveResults(test,
                CommandRouter.GetStringList(args, "clashes"),
                CommandRouter.GetString(args, "statusFilter"));

            int edited = 0;
            foreach (ClashResult result in targets)
            {
                ClashCompat.EditResultStatus(clash.TestsData, result, status, user);
                edited++;
            }
            return new Dictionary<string, object>
            {
                { "test", test.DisplayName ?? "" },
                { "status", status.ToString() },
                { "clashesUpdated", edited },
            };
        }

        public static object AddClashComment(Dictionary<string, object> args)
        {
            Document doc = CommandRouter.ActiveDocument();
            DocumentClash clash = ClashHelpers.GetClashPart(doc);
            ClashTest test = ClashHelpers.FindTest(clash.TestsData, CommandRouter.RequireString(args, "test"));

            string comment = CommandRouter.RequireString(args, "comment");
            string author = CommandRouter.GetString(args, "author", "");
            var targets = ClashHelpers.ResolveResults(test,
                CommandRouter.GetStringList(args, "clashes"), null);
            if (targets.Count == 0) throw new CommandException("No clashes matched.");

            foreach (ClashResult result in targets)
                ClashCompat.AddResultComment(clash.TestsData, result, comment, author);

            return new Dictionary<string, object>
            {
                { "test", test.DisplayName ?? "" },
                { "clashesCommented", targets.Count },
            };
        }

        public static object RenameClash(Dictionary<string, object> args)
        {
            Document doc = CommandRouter.ActiveDocument();
            DocumentClash clash = ClashHelpers.GetClashPart(doc);
            ClashTest test = ClashHelpers.FindTest(clash.TestsData, CommandRouter.RequireString(args, "test"));
            string wanted = CommandRouter.RequireString(args, "clash");
            string newName = CommandRouter.RequireString(args, "newName");

            var targets = ClashHelpers.ResolveResults(test, new List<string> { wanted }, null);
            if (targets.Count != 1)
                throw new CommandException("'" + wanted + "' matched " + targets.Count + " clashes; renaming requires exactly one (use its GUID).");

            clash.TestsData.TestsEditDisplayName(targets[0], newName);
            return new Dictionary<string, object> { { "renamed", wanted }, { "newName", newName } };
        }

        // ── Grouping ─────────────────────────────────────────────────

        // Rebuilds the test's children as groups keyed by the chosen mode.
        // Uses the add-empty-shell-then-fill pattern from AutoNAV2: adding a
        // pre-populated in-memory ClashResultGroup via TestsAddCopy does NOT
        // deep-copy its children, so each group is added empty and its
        // results appended against the live, document-bound group reference.
        public static object GroupClashes(Dictionary<string, object> args)
        {
            System.Console.Error.WriteLine("AUTONAV_DIAG:group_clashes entered — build " + WorkflowCommands.BuildStamp);
            Document doc = CommandRouter.ActiveDocument();
            DocumentClash clash = ClashHelpers.GetClashPart(doc);
            ClashTest test = ClashHelpers.FindTest(clash.TestsData, CommandRouter.RequireString(args, "test"));
            string mode = CommandRouter.GetString(args, "mode", "gridIntersection");

            var results = ClashHelpers.IterateResults(test).Select(p => p.Key).ToList();
            if (results.Count == 0)
                throw new CommandException("Test '" + test.DisplayName + "' has no clash results. Run it first.");

            // Bucket copies of every result by the grouping key.
            var buckets = new Dictionary<string, List<ClashResult>>();
            foreach (ClashResult result in results)
            {
                string key = GroupKey(doc, result, mode);
                List<ClashResult> bucket;
                if (!buckets.TryGetValue(key, out bucket))
                {
                    bucket = new List<ClashResult>();
                    buckets[key] = bucket;
                }
                bucket.Add((ClashResult)result.CreateCopy());
            }

            DocumentClashTests dct = clash.TestsData;
            // Resolve/track by GUID, never by dereferencing the test handle
            // (which may be stale after an earlier grouping op such as F5).
            Guid testGuid = test.Guid;
            ClashTest emptyCopy = (ClashTest)test.CreateCopyWithoutChildren();
            int index = ClashCompat.IndexOfTestByGuid(dct, testGuid);
            if (index < 0) throw new CommandException("Could not locate test in the document.");

            Transaction tx = doc.BeginTransaction("AutoNAV MCP group clashes");
            try
            {
                // Clear the test's children, then repopulate with groups.
                ClashCompat.TestsReplaceAtRoot(dct, index, emptyCopy);
                index = ClashCompat.IndexOfTestByGuid(dct, testGuid);
                if (index < 0) throw new CommandException("Test disappeared during grouping.");

                foreach (var bucket in buckets.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
                {
                    // Re-resolve test by GUID before every bucket — the
                    // previous bucket's TestsAddCopy may have invalidated
                    // the handle.
                    index = ClashCompat.IndexOfTestByGuid(dct, testGuid);
                    if (index < 0) continue;

                    string groupName = bucket.Key;
                    dct.TestsAddCopy((GroupItem)ClashCompat.TestAt(dct, index),
                        new ClashResultGroup { DisplayName = groupName });

                    // Re-resolve test again after adding the group shell.
                    index = ClashCompat.IndexOfTestByGuid(dct, testGuid);
                    if (index < 0) continue;
                    ClashTest liveTest = (ClashTest)ClashCompat.TestAt(dct, index);
                    ClashResultGroup liveGroup = null;
                    for (int i = 0; i < liveTest.Children.Count; i++)
                    {
                        if (liveTest.Children[i] is ClashResultGroup crg &&
                            string.Equals(crg.DisplayName, groupName, StringComparison.Ordinal))
                        {
                            liveGroup = crg;
                            break;
                        }
                    }
                    if (liveGroup == null) continue;

                    foreach (ClashResult copy in bucket.Value)
                    {
                        ClashResult detached = (ClashResult)copy.CreateCopy();
                        dct.TestsAddCopy(liveGroup, detached);
                    }
                }
                tx.Commit();
            }
            finally
            {
                tx.Dispose();
            }

            return new Dictionary<string, object>
            {
                { "test", test.DisplayName ?? "" },
                { "mode", mode },
                { "groupsCreated", buckets.Count },
                { "clashesGrouped", results.Count },
                { "groups", buckets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList() },
            };
        }

        private static string GroupKey(Document doc, ClashResult result, string mode)
        {
            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case "status":
                    return result.Status.ToString();
                case "assignedto":
                    {
                        string assigned = ClashCompat.GetAssignedTo(result);
                        return string.IsNullOrEmpty(assigned) ? "Unassigned" : assigned;
                    }
                case "level":
                    {
                        try
                        {
                            GridIntersection gi = doc.Grids.ActiveSystem != null
                                ? doc.Grids.ActiveSystem.ClosestIntersection(result.Center) : null;
                            if (gi != null && gi.Level != null && !string.IsNullOrEmpty(gi.Level.DisplayName))
                                return gi.Level.DisplayName;
                        }
                        catch { }
                        return "No Level";
                    }
                case "grid":
                case "gridintersection":
                    {
                        string location = ClashHelpers.DescribeGridLocation(doc, result.Center);
                        return string.IsNullOrEmpty(location) ? "No Grid Intersection" : location;
                    }
                case "item":
                    {
                        ModelItem item = result.CompositeItem1 ?? result.Item1;
                        return item != null ? ClashHelpers.DescribeItem(item) : "Unknown Item";
                    }
                default:
                    throw new CommandException(
                        "Invalid grouping mode '" + mode + "'. Valid modes: status, assignedTo, level, gridIntersection, item.");
            }
        }
    }
}
