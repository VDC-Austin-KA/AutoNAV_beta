using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Navisworks.Api;
using AutoNAVMCP.Bridge;

namespace AutoNAVMCP.Commands
{
    // Maps bridge command names to handlers. Every handler runs on the
    // Navisworks main thread (BridgeServer marshals before dispatching).
    internal static class CommandRouter
    {
        private static readonly Dictionary<string, Func<Dictionary<string, object>, object>> Handlers =
            new Dictionary<string, Func<Dictionary<string, object>, object>>(StringComparer.OrdinalIgnoreCase)
        {
            // Health / document
            { "ping",                 DocumentCommands.Ping },
            { "get_document_info",    DocumentCommands.GetDocumentInfo },
            { "list_search_sets",     DocumentCommands.ListSearchSets },

            // Clash identification
            { "list_clash_tests",     ClashQueryCommands.ListClashTests },
            { "get_clash_results",    ClashQueryCommands.GetClashResults },
            { "get_clash_result",     ClashQueryCommands.GetClashResult },
            { "create_clash_test",    ClashEditCommands.CreateClashTest },
            { "delete_clash_test",    ClashEditCommands.DeleteClashTest },
            { "run_clash_test",       ClashEditCommands.RunClashTest },
            { "run_all_clash_tests",  ClashEditCommands.RunAllClashTests },

            // Clash assignment / resolution
            { "assign_clashes",       ClashEditCommands.AssignClashes },
            { "set_clash_status",     ClashEditCommands.SetClashStatus },
            { "add_clash_comment",    ClashEditCommands.AddClashComment },
            { "rename_clash",         ClashEditCommands.RenameClash },
            { "group_clashes",        ClashEditCommands.GroupClashes },

            // Reporting
            { "create_clash_report",  ReportCommands.CreateClashReport },

            // Search-set generation (AutoNAV2 Functions 1-3)
            { "create_discipline_search_sets",       SearchSetCommands.CreateDisciplineSearchSets },
            { "create_property_search_sets",         SearchSetCommands.CreatePropertySearchSets },
            { "create_custom_search_sets",           SearchSetCommands.CreateCustomSearchSets },
            { "list_disciplines",                    SearchSetCommands.ListDisciplines },
            { "list_discipline_properties",          SearchSetCommands.ListDisciplineProperties },
            { "list_discipline_property_values",     SearchSetCommands.ListDisciplinePropertyValues },
            { "suggest_search_set_properties",       SearchSetCommands.SuggestSearchSetProperties },

            // Automated clash-test generation, grouping & full workflow (Functions 4-7 + AutoNAVismate)
            { "generate_clash_tests",     WorkflowCommands.GenerateClashTests },
            { "group_walls_floors",       WorkflowCommands.GroupWallsFloors },
            { "group_all_tests",          WorkflowCommands.GroupAllTests },
            { "run_autonavismate",        WorkflowCommands.RunAutoNavismate },
        };

        public static object Execute(string command, Dictionary<string, object> args)
        {
            Func<Dictionary<string, object>, object> handler;
            if (!Handlers.TryGetValue(command, out handler))
                throw new CommandException("Unknown command '" + command + "'.");
            return handler(args);
        }

        // ── Parameter helpers ────────────────────────────────────────

        public static string GetString(Dictionary<string, object> args, string key, string fallback = null)
        {
            object v;
            if (args.TryGetValue(key, out v) && v != null)
                return Convert.ToString(v, CultureInfo.InvariantCulture);
            return fallback;
        }

        public static string RequireString(Dictionary<string, object> args, string key)
        {
            string v = GetString(args, key);
            if (string.IsNullOrEmpty(v))
                throw new CommandException("Missing required parameter '" + key + "'.");
            return v;
        }

        public static int GetInt(Dictionary<string, object> args, string key, int fallback)
        {
            object v;
            if (args.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
                catch { throw new CommandException("Parameter '" + key + "' must be an integer."); }
            }
            return fallback;
        }

        public static double GetDouble(Dictionary<string, object> args, string key, double fallback)
        {
            object v;
            if (args.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
                catch { throw new CommandException("Parameter '" + key + "' must be a number."); }
            }
            return fallback;
        }

        public static bool GetBool(Dictionary<string, object> args, string key, bool fallback)
        {
            object v;
            if (args.TryGetValue(key, out v) && v != null)
            {
                if (v is bool) return (bool)v;
                bool parsed;
                if (bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out parsed)) return parsed;
            }
            return fallback;
        }

        public static List<string> GetStringList(Dictionary<string, object> args, string key)
        {
            object v;
            if (!args.TryGetValue(key, out v) || v == null) return null;
            var list = v as List<object>;
            if (list == null)
            {
                // Allow a single string as shorthand for a one-item list.
                string s = v as string;
                if (s != null) return new List<string> { s };
                throw new CommandException("Parameter '" + key + "' must be an array of strings.");
            }
            var result = new List<string>();
            foreach (object item in list)
                if (item != null) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
            return result;
        }

        public static Document ActiveDocument()
        {
            Document doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null || doc.IsClear)
                throw new CommandException("No model is open in Navisworks.");
            return doc;
        }
    }
}
