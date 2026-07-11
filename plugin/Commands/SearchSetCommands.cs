using System;
using System.Collections.Generic;
using System.Linq;
using AutoNAVMCP.Bridge;

namespace AutoNAVMCP.Commands
{
    // Wraps AutoNAV2's SearchSetGenerator (Functions 1-3) as headless bridge
    // commands. The engines report progress through Notifier; EngineRun.Run
    // captures those messages into the returned "log".
    internal static class SearchSetCommands
    {
        // Function 1 — discipline search sets from loaded model filenames.
        public static object CreateDisciplineSearchSets(Dictionary<string, object> args)
        {
            CommandRouter.ActiveDocument();

            // Optional overrides: { "<sourceFile>": "<disciplineToken>" }.
            Dictionary<string, string> overrides = null;
            object raw;
            if (args.TryGetValue("disciplineOverrides", out raw) && raw is Dictionary<string, object>)
            {
                overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in (Dictionary<string, object>)raw)
                    overrides[kv.Key] = kv.Value == null ? "" : Convert.ToString(kv.Value);
            }

            EngineRun run = EngineRun.Run(() => SearchSetGenerator.GenerateFunction1SearchSets(), overrides);
            return run.ToResult(new Dictionary<string, object>
            {
                { "function", "1 — Discipline Search Sets" },
                { "disciplines", SearchSetGenerator.GetAvailableDisciplines() },
            });
        }

        // Function 2 — element-property (categorized) search sets per discipline.
        public static object CreatePropertySearchSets(Dictionary<string, object> args)
        {
            CommandRouter.ActiveDocument();

            List<string> disciplines = CommandRouter.GetStringList(args, "disciplines");
            if (disciplines == null || disciplines.Count == 0)
                disciplines = SearchSetGenerator.GetAvailableDisciplines();
            if (disciplines.Count == 0)
                throw new CommandException("No disciplines found. Run create_discipline_search_sets (Function 1) first.");

            string propCategory = CommandRouter.GetString(args, "propertyCategory");
            string propName = CommandRouter.GetString(args, "propertyName");

            // Default to each discipline's first recommended property option when
            // the caller doesn't specify one (mirrors AutoNAVismate's behavior).
            if (string.IsNullOrEmpty(propCategory) || string.IsNullOrEmpty(propName))
            {
                var perDiscipline = new List<object>();
                var overallLog = new List<string>();
                foreach (string discipline in disciplines)
                {
                    string canonical;
                    SearchSetGenerator.DisciplineRegistry.TryGetValue(discipline, out canonical);
                    var options = SearchSetGenerator.PropertyOptionsFor(canonical);
                    if (options.Length == 0) continue;
                    var opt = options[0];
                    EngineRun r = EngineRun.Run(() => SearchSetGenerator.GenerateFunction2SearchSets(
                        new List<string> { discipline }, opt.Category, opt.Property));
                    overallLog.AddRange(r.Log);
                    perDiscipline.Add(new Dictionary<string, object>
                    {
                        { "discipline", discipline },
                        { "property", opt.Category + " / " + opt.Property },
                    });
                }
                return new Dictionary<string, object>
                {
                    { "function", "2 — Element-Property Search Sets (auto property per discipline)" },
                    { "applied", perDiscipline },
                    { "log", overallLog },
                };
            }

            EngineRun run = EngineRun.Run(() =>
                SearchSetGenerator.GenerateFunction2SearchSets(disciplines, propCategory, propName));
            return run.ToResult(new Dictionary<string, object>
            {
                { "function", "2 — Element-Property Search Sets" },
                { "disciplines", disciplines },
                { "property", propCategory + " / " + propName },
            });
        }

        // Function 3 — custom search sets from any property value in a discipline.
        public static object CreateCustomSearchSets(Dictionary<string, object> args)
        {
            CommandRouter.ActiveDocument();
            string discipline = CommandRouter.RequireString(args, "discipline");
            string propCategory = CommandRouter.RequireString(args, "propertyCategory");
            string propName = CommandRouter.RequireString(args, "propertyName");

            EngineRun run = EngineRun.Run(() =>
                SearchSetGenerator.GenerateCustomSearchSets(discipline, propCategory, propName));
            return run.ToResult(new Dictionary<string, object>
            {
                { "function", "3 — Custom Search Sets" },
                { "discipline", discipline },
                { "property", propCategory + " / " + propName },
            });
        }

        // Discovery helpers (read-only) for the create_* commands above.

        public static object ListDisciplines(Dictionary<string, object> args)
        {
            CommandRouter.ActiveDocument();
            return new Dictionary<string, object>
            {
                { "disciplines", SearchSetGenerator.GetAvailableDisciplines() },
            };
        }

        public static object ListDisciplineProperties(Dictionary<string, object> args)
        {
            CommandRouter.ActiveDocument();
            string discipline = CommandRouter.RequireString(args, "discipline");
            var categories = SearchSetGenerator.GetPropertyCategoriesForDiscipline(discipline);
            return new Dictionary<string, object>
            {
                { "discipline", discipline },
                { "categories", categories.Select(c => (object)new Dictionary<string, object>
                    {
                        { "category", c.DisplayName },
                        { "properties", c.Properties.Select(p => (object)p.DisplayName).ToList() },
                    }).ToList() },
            };
        }

        public static object ListDisciplinePropertyValues(Dictionary<string, object> args)
        {
            CommandRouter.ActiveDocument();
            string discipline = CommandRouter.RequireString(args, "discipline");
            string propCategory = CommandRouter.RequireString(args, "propertyCategory");
            string propName = CommandRouter.RequireString(args, "propertyName");
            var values = SearchSetGenerator.GetPropertyValuesForDiscipline(discipline, propCategory, propName);
            return new Dictionary<string, object>
            {
                { "discipline", discipline },
                { "property", propCategory + " / " + propName },
                { "values", values.Cast<object>().ToList() },
            };
        }
    }
}
