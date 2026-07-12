using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutoNAVMCP.Bridge;

namespace AutoNAVMCP.Macros
{
    // Persists macros as JSON files under %AppData%/AutoNAV/macros on the
    // Navisworks machine, so they survive restarts and can be recalled through
    // the MCP. One macro == one file; a macro is an ordered list of
    // (command, params) steps.
    internal static class MacroStore
    {
        private static string MacrosDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AutoNAV", "macros");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string SafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        private static string PathFor(string name)
        {
            return Path.Combine(MacrosDir, SafeFileName(name) + ".json");
        }

        public sealed class MacroStep
        {
            public string Command;
            public Dictionary<string, object> Params;
        }

        public sealed class Macro
        {
            public string Name;
            public string Description = "";
            public DateTime Created;
            public DateTime Modified;
            public List<MacroStep> Steps = new List<MacroStep>();
        }

        public static void Save(Macro macro)
        {
            var stepObjs = macro.Steps.Select(s => (object)new Dictionary<string, object>
            {
                { "command", s.Command },
                { "params", s.Params ?? new Dictionary<string, object>() },
            }).ToList();

            var payload = new Dictionary<string, object>
            {
                { "name", macro.Name },
                { "description", macro.Description ?? "" },
                { "created", macro.Created == default(DateTime) ? DateTime.Now : macro.Created },
                { "modified", DateTime.Now },
                { "steps", stepObjs },
            };
            File.WriteAllText(PathFor(macro.Name), MiniJson.Serialize(payload));
        }

        public static bool Exists(string name)
        {
            return File.Exists(PathFor(name));
        }

        public static Macro Load(string name)
        {
            string path = PathFor(name);
            if (!File.Exists(path))
                throw new CommandException("Macro '" + name + "' not found. Use list_macros to see saved macros.");

            var root = MiniJson.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
            if (root == null) throw new CommandException("Macro '" + name + "' is corrupt.");

            var macro = new Macro
            {
                Name = GetStr(root, "name", name),
                Description = GetStr(root, "description", ""),
            };
            object stepsObj;
            if (root.TryGetValue("steps", out stepsObj) && stepsObj is List<object>)
            {
                foreach (object o in (List<object>)stepsObj)
                {
                    var sd = o as Dictionary<string, object>;
                    if (sd == null) continue;
                    macro.Steps.Add(new MacroStep
                    {
                        Command = GetStr(sd, "command", null),
                        Params = (sd.TryGetValue("params", out var p) ? p as Dictionary<string, object> : null)
                                 ?? new Dictionary<string, object>(),
                    });
                }
            }
            return macro;
        }

        public static List<Macro> List()
        {
            var macros = new List<Macro>();
            foreach (string file in Directory.GetFiles(MacrosDir, "*.json"))
            {
                try
                {
                    var root = MiniJson.Parse(File.ReadAllText(file)) as Dictionary<string, object>;
                    if (root == null) continue;
                    int stepCount = 0;
                    object stepsObj;
                    if (root.TryGetValue("steps", out stepsObj) && stepsObj is List<object>)
                        stepCount = ((List<object>)stepsObj).Count;
                    macros.Add(new Macro
                    {
                        Name = GetStr(root, "name", Path.GetFileNameWithoutExtension(file)),
                        Description = GetStr(root, "description", ""),
                        Steps = Enumerable.Repeat<MacroStep>(null, stepCount).ToList(), // count only
                    });
                }
                catch { /* skip unreadable files */ }
            }
            return macros.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool Delete(string name)
        {
            string path = PathFor(name);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        private static string GetStr(Dictionary<string, object> d, string key, string fallback)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return fallback;
        }
    }
}
