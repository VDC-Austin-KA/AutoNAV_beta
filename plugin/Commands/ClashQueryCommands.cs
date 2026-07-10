using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using AutoNAVMCP.Bridge;

namespace AutoNAVMCP.Commands
{
    internal static class ClashQueryCommands
    {
        public static object ListClashTests(Dictionary<string, object> args)
        {
            Document doc = CommandRouter.ActiveDocument();
            DocumentClash clash = ClashHelpers.GetClashPart(doc);

            var tests = new List<object>();
            foreach (ClashTest test in ClashCompat.EnumerateTests(clash.TestsData))
            {
                tests.Add(new Dictionary<string, object>
                {
                    { "guid", test.Guid.ToString() },
                    { "name", test.DisplayName ?? "" },
                    { "testType", test.TestType.ToString() },
                    { "status", test.Status.ToString() },
                    { "tolerance", test.Tolerance },
                    { "lastRun", test.LastRun },
                    { "resultCounts", ClashHelpers.CountResultsByStatus(test) },
                });
            }
            return new Dictionary<string, object> { { "count", tests.Count }, { "tests", tests } };
        }

        public static object GetClashResults(Dictionary<string, object> args)
        {
            Document doc = CommandRouter.ActiveDocument();
            DocumentClash clash = ClashHelpers.GetClashPart(doc);
            ClashTest test = ClashHelpers.FindTest(clash.TestsData, CommandRouter.RequireString(args, "test"));

            string statusFilter = CommandRouter.GetString(args, "status");
            string assignedFilter = CommandRouter.GetString(args, "assignedTo");
            string groupFilter = CommandRouter.GetString(args, "group");
            int offset = CommandRouter.GetInt(args, "offset", 0);
            int limit = CommandRouter.GetInt(args, "limit", 100);
            bool includeItems = CommandRouter.GetBool(args, "includeItems", true);

            ClashResultStatus? status = null;
            if (!string.IsNullOrEmpty(statusFilter)) status = ClashHelpers.ParseStatus(statusFilter);

            var all = ClashHelpers.IterateResults(test)
                .Where(p => status == null || p.Key.Status == status.Value)
                .Where(p => string.IsNullOrEmpty(assignedFilter) ||
                            ClashCompat.GetAssignedTo(p.Key).Equals(assignedFilter, StringComparison.OrdinalIgnoreCase))
                .Where(p => string.IsNullOrEmpty(groupFilter) ||
                            p.Value.Trim().Equals(groupFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            var page = all.Skip(offset).Take(limit)
                .Select(p => (object)ClashHelpers.SerializeResult(doc, p.Key, p.Value, includeItems))
                .ToList();

            return new Dictionary<string, object>
            {
                { "test", test.DisplayName ?? "" },
                { "totalMatching", all.Count },
                { "offset", offset },
                { "returned", page.Count },
                { "results", page },
            };
        }

        public static object GetClashResult(Dictionary<string, object> args)
        {
            Document doc = CommandRouter.ActiveDocument();
            DocumentClash clash = ClashHelpers.GetClashPart(doc);
            ClashTest test = ClashHelpers.FindTest(clash.TestsData, CommandRouter.RequireString(args, "test"));
            string wanted = CommandRouter.RequireString(args, "clash");

            Guid guid;
            bool isGuid = Guid.TryParse(wanted, out guid);
            foreach (var pair in ClashHelpers.IterateResults(test))
            {
                ClashResult result = pair.Key;
                bool match = (isGuid && result.Guid == guid) ||
                             (result.DisplayName != null &&
                              result.DisplayName.Trim().Equals(wanted.Trim(), StringComparison.OrdinalIgnoreCase));
                if (!match) continue;

                var data = ClashHelpers.SerializeResult(doc, result, pair.Value, true);
                data["approvedTime"] = result.ApprovedTime;
                data["resolvedTime"] = ClashCompat.GetResolvedTime(result);
                data["comments"] = SerializeComments(result);
                data["item1Properties"] = SerializeItemProperties(result.CompositeItem1 ?? result.Item1);
                data["item2Properties"] = SerializeItemProperties(result.CompositeItem2 ?? result.Item2);
                return data;
            }
            throw new CommandException("Clash '" + wanted + "' not found in test '" + test.DisplayName + "'.");
        }

        private static List<object> SerializeComments(ClashResult result)
        {
            var comments = new List<object>();
            if (result.Comments == null) return comments;
            foreach (Comment comment in result.Comments)
            {
                comments.Add(new Dictionary<string, object>
                {
                    { "author", ClashCompat.GetCommentAuthor(comment) },
                    { "body", comment.Body ?? "" },
                    { "status", comment.Status.ToString() },
                    { "created", comment.CreationDate },
                });
            }
            return comments;
        }

        // Item property categories, capped to keep responses bounded.
        private static List<object> SerializeItemProperties(ModelItem item)
        {
            var categories = new List<object>();
            if (item == null || item.PropertyCategories == null) return categories;
            int propertyBudget = 200;
            foreach (PropertyCategory category in item.PropertyCategories)
            {
                var props = new Dictionary<string, object>();
                foreach (DataProperty prop in category.Properties)
                {
                    if (propertyBudget-- <= 0) break;
                    try { props[prop.DisplayName ?? prop.Name] = prop.Value != null ? prop.Value.ToDisplayString() : ""; }
                    catch { }
                }
                categories.Add(new Dictionary<string, object>
                {
                    { "category", category.DisplayName ?? category.Name },
                    { "properties", props },
                });
                if (propertyBudget <= 0) break;
            }
            return categories;
        }
    }
}
