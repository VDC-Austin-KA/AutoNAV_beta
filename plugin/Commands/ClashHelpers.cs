using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Autodesk.Navisworks.Api.DocumentParts;
using AutoNAVMCP.Bridge;

namespace AutoNAVMCP.Commands
{
    // Shared lookup and serialization helpers for the clash commands.
    internal static class ClashHelpers
    {
        public static DocumentClash GetClashPart(Document doc)
        {
            DocumentClash clash = doc.GetClash();
            if (clash == null || clash.TestsData == null)
                throw new CommandException("Clash Detective is not available (requires Navisworks Manage).");
            return clash;
        }

        // Finds a top-level clash test by display name or GUID string.
        public static ClashTest FindTest(DocumentClashTests dct, string nameOrGuid)
        {
            Guid guid;
            bool isGuid = Guid.TryParse(nameOrGuid, out guid);
            foreach (ClashTest test in ClashCompat.EnumerateTests(dct))
            {
                if (isGuid && test.Guid == guid) return test;
                if (test.DisplayName != null &&
                    test.DisplayName.Trim().Equals(nameOrGuid.Trim(), StringComparison.OrdinalIgnoreCase))
                    return test;
            }
            throw new CommandException("Clash test '" + nameOrGuid + "' not found. Use list_clash_tests to see available tests.");
        }

        // Yields every ClashResult under the test with the name of its
        // containing group ("" for ungrouped, top-level results).
        public static IEnumerable<KeyValuePair<ClashResult, string>> IterateResults(ClashTest test)
        {
            foreach (SavedItem child in test.Children)
            {
                var result = child as ClashResult;
                if (result != null)
                {
                    yield return new KeyValuePair<ClashResult, string>(result, "");
                    continue;
                }
                var group = child as ClashResultGroup;
                if (group != null)
                {
                    string groupName = group.DisplayName ?? "";
                    foreach (SavedItem grandChild in group.Children)
                    {
                        var nested = grandChild as ClashResult;
                        if (nested != null)
                            yield return new KeyValuePair<ClashResult, string>(nested, groupName);
                    }
                }
            }
        }

        // Resolves the target results for an edit command. 'clashes' may hold
        // result display names, result GUIDs, or group names (which expand to
        // the group's results). Null/empty means every result in the test,
        // optionally narrowed by status.
        public static List<ClashResult> ResolveResults(ClashTest test, List<string> clashes, string statusFilter)
        {
            var all = IterateResults(test).ToList();
            var matched = new List<ClashResult>();

            if (clashes == null || clashes.Count == 0)
            {
                matched.AddRange(all.Select(p => p.Key));
            }
            else
            {
                foreach (string wanted in clashes)
                {
                    Guid guid;
                    bool isGuid = Guid.TryParse(wanted, out guid);
                    var hits = all.Where(p =>
                        (isGuid && p.Key.Guid == guid) ||
                        (p.Key.DisplayName != null && p.Key.DisplayName.Trim().Equals(wanted.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                        (p.Value.Trim().Equals(wanted.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
                    if (hits.Count == 0)
                        throw new CommandException("Clash '" + wanted + "' not found in test '" + test.DisplayName + "'.");
                    matched.AddRange(hits.Select(p => p.Key));
                }
            }

            if (!string.IsNullOrEmpty(statusFilter))
            {
                ClashResultStatus status = ParseStatus(statusFilter);
                matched = matched.Where(r => r.Status == status).ToList();
            }
            return matched.Distinct().ToList();
        }

        public static ClashResultStatus ParseStatus(string status)
        {
            try
            {
                return (ClashResultStatus)Enum.Parse(typeof(ClashResultStatus), status, true);
            }
            catch
            {
                throw new CommandException(
                    "Invalid clash status '" + status + "'. Valid values: " +
                    string.Join(", ", Enum.GetNames(typeof(ClashResultStatus))));
            }
        }

        // ── Serialization ────────────────────────────────────────────

        public static Dictionary<string, object> SerializeResult(
            Document doc, ClashResult result, string groupName, bool includeItems)
        {
            var data = new Dictionary<string, object>
            {
                { "guid", result.Guid.ToString() },
                { "name", result.DisplayName ?? "" },
                { "group", groupName ?? "" },
                { "status", result.Status.ToString() },
                { "distance", result.Distance },
                { "description", result.Description ?? "" },
                { "assignedTo", ClashCompat.GetAssignedTo(result) },
                { "approvedBy", ClashCompat.GetApprovedBy(result) },
                { "resolvedBy", ClashCompat.GetResolvedBy(result) },
                { "createdTime", result.CreatedTime },
                { "gridLocation", DescribeGridLocation(doc, result.Center) },
                { "center", SerializePoint(result.Center) },
                { "commentCount", result.Comments != null ? result.Comments.Count : 0 },
            };
            if (includeItems)
            {
                data["item1"] = SerializeItem(result.CompositeItem1 ?? result.Item1);
                data["item2"] = SerializeItem(result.CompositeItem2 ?? result.Item2);
            }
            return data;
        }

        public static Dictionary<string, object> SerializePoint(Point3D point)
        {
            if (point == null) return null;
            return new Dictionary<string, object> { { "x", point.X }, { "y", point.Y }, { "z", point.Z } };
        }

        public static Dictionary<string, object> SerializeItem(ModelItem item)
        {
            if (item == null) return null;
            return new Dictionary<string, object>
            {
                { "name", DescribeItem(item) },
                { "path", BuildItemPath(item) },
                { "sourceFile", GetFileAncestorName(item) },
            };
        }

        public static string DescribeItem(ModelItem item)
        {
            if (item == null) return "";
            if (!string.IsNullOrEmpty(item.DisplayName)) return item.DisplayName;
            if (!string.IsNullOrEmpty(item.ClassDisplayName)) return item.ClassDisplayName;
            return "Unnamed";
        }

        public static string BuildItemPath(ModelItem item)
        {
            var parts = new List<string>();
            for (ModelItem current = item; current != null; current = current.Parent)
            {
                string name = current.DisplayName;
                if (string.IsNullOrEmpty(name)) name = current.ClassDisplayName;
                if (!string.IsNullOrEmpty(name)) parts.Add(name);
            }
            parts.Reverse();
            return string.Join(" > ", parts);
        }

        public static string GetFileAncestorName(ModelItem item)
        {
            for (ModelItem current = item; current != null; current = current.Parent)
                if (current.HasModel && current.Model != null)
                    return current.Model.FileName != null
                        ? System.IO.Path.GetFileName(current.Model.FileName)
                        : (current.DisplayName ?? "");
            return "";
        }

        // Nearest grid intersection + level, e.g. "C-5 @ Level 03".
        public static string DescribeGridLocation(Document doc, Point3D center)
        {
            try
            {
                if (center == null || doc.Grids == null || doc.Grids.ActiveSystem == null) return "";
                GridIntersection gi = doc.Grids.ActiveSystem.ClosestIntersection(center);
                if (gi == null) return "";
                string name = gi.DisplayName ?? "";
                string level = gi.Level != null ? (gi.Level.DisplayName ?? "") : "";
                return string.IsNullOrEmpty(level) ? name : name + " @ " + level;
            }
            catch
            {
                return "";
            }
        }

        public static Dictionary<string, object> CountResultsByStatus(ClashTest test)
        {
            var counts = new Dictionary<string, object>();
            foreach (string name in Enum.GetNames(typeof(ClashResultStatus))) counts[name] = 0;
            int total = 0;
            foreach (var pair in IterateResults(test))
            {
                string key = pair.Key.Status.ToString();
                counts[key] = Convert.ToInt32(counts[key], CultureInfo.InvariantCulture) + 1;
                total++;
            }
            counts["Total"] = total;
            return counts;
        }

        // ── Search-set resolution (for create_clash_test) ────────────

        // Finds a saved selection/search set or folder by "/"-separated path
        // (e.g. "2. CLASH SETS/Mechanical") or by plain display name.
        public static SavedItem FindSelectionSetItem(Document doc, string path)
        {
            GroupItem root = doc.SelectionSets.RootItem as GroupItem;
            if (root == null) throw new CommandException("No saved selection/search sets in this document.");

            string[] segments = path.Split('/').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (segments.Length == 0) throw new CommandException("Empty selection set path.");

            // Exact path walk first.
            SavedItem current = root;
            bool walked = true;
            foreach (string segment in segments)
            {
                var group = current as GroupItem;
                SavedItem next = null;
                if (group != null)
                    next = group.Children.FirstOrDefault(c =>
                        c.DisplayName != null && c.DisplayName.Trim().Equals(segment, StringComparison.OrdinalIgnoreCase));
                if (next == null) { walked = false; break; }
                current = next;
            }
            if (walked) return current;

            // Fall back to a recursive search on the final segment's name.
            SavedItem found = FindByNameRecursive(root, segments[segments.Length - 1]);
            if (found == null)
                throw new CommandException("Selection/search set '" + path + "' not found. Use list_search_sets to see available sets.");
            return found;
        }

        private static SavedItem FindByNameRecursive(GroupItem group, string name)
        {
            foreach (SavedItem child in group.Children)
            {
                if (child.DisplayName != null &&
                    child.DisplayName.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return child;
                var childGroup = child as GroupItem;
                if (childGroup != null)
                {
                    SavedItem nested = FindByNameRecursive(childGroup, name);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        // Expands a saved item (set or folder) into clash SelectionSources.
        public static List<SelectionSource> BuildSelectionSources(Document doc, SavedItem item)
        {
            var sources = new List<SelectionSource>();
            var set = item as SelectionSet;
            if (set != null)
            {
                sources.Add(doc.SelectionSets.CreateSelectionSource(set));
                return sources;
            }
            var group = item as GroupItem;
            if (group != null)
                foreach (SavedItem child in group.Children)
                    sources.AddRange(BuildSelectionSources(doc, child));
            return sources;
        }
    }
}
