using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Autodesk.Navisworks.Api.DocumentParts;

namespace AutoNAVMCP.Commands
{
    internal static class DocumentCommands
    {
        public static object Ping(Dictionary<string, object> args)
        {
            Document doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            return new Dictionary<string, object>
            {
                { "plugin", "AutoNAV MCP" },
                { "documentOpen", doc != null && !doc.IsClear },
            };
        }

        public static object GetDocumentInfo(Dictionary<string, object> args)
        {
            Document doc = CommandRouter.ActiveDocument();

            var models = new List<object>();
            foreach (Model model in doc.Models)
            {
                models.Add(new Dictionary<string, object>
                {
                    { "fileName", model.FileName != null ? Path.GetFileName(model.FileName) : "" },
                    { "sourceFileName", model.SourceFileName ?? "" },
                });
            }

            int testCount = 0;
            DocumentClash clash = doc.GetClash();
            if (clash != null && clash.TestsData != null)
                testCount = ClashCompat.EnumerateTests(clash.TestsData).Count();

            return new Dictionary<string, object>
            {
                { "title", doc.Title ?? "" },
                { "fileName", doc.FileName ?? "" },
                { "units", doc.Units.ToString() },
                { "modelCount", models.Count },
                { "models", models },
                { "clashTestCount", testCount },
                { "clashDetectiveAvailable", clash != null && clash.TestsData != null },
            };
        }

        public static object ListSearchSets(Dictionary<string, object> args)
        {
            Document doc = CommandRouter.ActiveDocument();
            var sets = new List<object>();
            GroupItem root = doc.SelectionSets.RootItem as GroupItem;
            if (root != null) Walk(root, "", sets);
            return new Dictionary<string, object> { { "count", sets.Count }, { "sets", sets } };
        }

        private static void Walk(GroupItem group, string prefix, List<object> output)
        {
            foreach (SavedItem child in group.Children)
            {
                string path = string.IsNullOrEmpty(prefix)
                    ? (child.DisplayName ?? "")
                    : prefix + "/" + (child.DisplayName ?? "");
                var set = child as SelectionSet;
                if (set != null)
                {
                    output.Add(new Dictionary<string, object>
                    {
                        { "path", path },
                        { "type", set.HasSearch ? "search" : "selection" },
                    });
                }
                var childGroup = child as GroupItem;
                if (childGroup != null)
                {
                    output.Add(new Dictionary<string, object> { { "path", path }, { "type", "folder" } });
                    Walk(childGroup, path, output);
                }
            }
        }
    }
}
