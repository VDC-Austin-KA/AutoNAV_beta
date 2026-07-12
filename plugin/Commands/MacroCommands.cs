using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AutoNAVMCP.Bridge;
using AutoNAVMCP.Macros;

namespace AutoNAVMCP.Commands
{
    // Record / save / recall / replay of AutoNAV command sequences ("macros").
    internal static class MacroCommands
    {
        // ── Recording ────────────────────────────────────────────────

        public static object StartRecording(Dictionary<string, object> args)
        {
            string name = CommandRouter.GetString(args, "name");
            MacroRecorder.Start(name);
            return new Dictionary<string, object>
            {
                { "recording", true },
                { "session", MacroRecorder.SessionName },
                { "note", "Every AutoNAV action (create/generate/group/assign/report/…) will be journaled until stop_recording. Read-only queries are journaled but flagged non-mutating." },
            };
        }

        public static object StopRecording(Dictionary<string, object> args)
        {
            bool was = MacroRecorder.IsRecording;
            MacroRecorder.Stop();
            return new Dictionary<string, object>
            {
                { "recording", false },
                { "wasRecording", was },
                { "session", MacroRecorder.SessionName },
                { "stepsCaptured", MacroRecorder.Journal.Count },
                { "steps", MacroRecorder.JournalAsObjects() },
                { "hint", "Review the steps, then save_macro to persist them (mutating steps are kept by default)." },
            };
        }

        public static object GetRecording(Dictionary<string, object> args)
        {
            return new Dictionary<string, object>
            {
                { "recording", MacroRecorder.IsRecording },
                { "session", MacroRecorder.SessionName },
                { "stepsCaptured", MacroRecorder.Journal.Count },
                { "steps", MacroRecorder.JournalAsObjects() },
            };
        }

        public static object ClearRecording(Dictionary<string, object> args)
        {
            MacroRecorder.Clear();
            return new Dictionary<string, object> { { "cleared", true } };
        }

        // ── Save / list / inspect / delete ───────────────────────────

        public static object SaveMacro(Dictionary<string, object> args)
        {
            string name = CommandRouter.RequireString(args, "name");
            string description = CommandRouter.GetString(args, "description", "");
            bool includeReadOnly = CommandRouter.GetBool(args, "includeReadOnly", false);
            bool overwrite = CommandRouter.GetBool(args, "overwrite", false);
            List<string> onlyStepsRaw = CommandRouter.GetStringList(args, "steps"); // 1-based indices

            if (MacroStore.Exists(name) && !overwrite)
                throw new CommandException("A macro named '" + name + "' already exists. Pass overwrite=true to replace it.");

            HashSet<int> onlySteps = null;
            if (onlyStepsRaw != null && onlyStepsRaw.Count > 0)
            {
                onlySteps = new HashSet<int>();
                foreach (string s in onlyStepsRaw)
                {
                    int idx;
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
                        onlySteps.Add(idx);
                }
            }

            var steps = new List<MacroStore.MacroStep>();
            foreach (MacroRecorder.Step j in MacroRecorder.Journal)
            {
                if (onlySteps != null) { if (!onlySteps.Contains(j.Index)) continue; }
                else if (!includeReadOnly && !j.Mutating) continue;

                steps.Add(new MacroStore.MacroStep { Command = j.Command, Params = j.Params });
            }

            if (steps.Count == 0)
                throw new CommandException(
                    "Nothing to save — the current recording has no matching steps. " +
                    "Record some actions first (start_recording), or pass includeReadOnly / explicit steps.");

            MacroStore.Save(new MacroStore.Macro
            {
                Name = name, Description = description, Created = DateTime.Now, Steps = steps,
            });

            return new Dictionary<string, object>
            {
                { "saved", name },
                { "steps", steps.Count },
                { "commands", steps.Select(s => (object)s.Command).ToList() },
            };
        }

        public static object ListMacros(Dictionary<string, object> args)
        {
            var macros = MacroStore.List();
            return new Dictionary<string, object>
            {
                { "count", macros.Count },
                { "macros", macros.Select(m => (object)new Dictionary<string, object>
                    {
                        { "name", m.Name },
                        { "description", m.Description },
                        { "steps", m.Steps.Count },
                    }).ToList() },
            };
        }

        public static object GetMacro(Dictionary<string, object> args)
        {
            string name = CommandRouter.RequireString(args, "name");
            var macro = MacroStore.Load(name);
            return new Dictionary<string, object>
            {
                { "name", macro.Name },
                { "description", macro.Description },
                { "steps", macro.Steps.Select((s, i) => (object)new Dictionary<string, object>
                    {
                        { "index", i + 1 },
                        { "command", s.Command },
                        { "params", s.Params },
                    }).ToList() },
            };
        }

        public static object DeleteMacro(Dictionary<string, object> args)
        {
            string name = CommandRouter.RequireString(args, "name");
            bool deleted = MacroStore.Delete(name);
            if (!deleted) throw new CommandException("Macro '" + name + "' not found.");
            return new Dictionary<string, object> { { "deleted", name } };
        }

        // ── Replay ───────────────────────────────────────────────────

        // Re-executes a saved macro's steps in order. Options:
        //   dryRun       — list the steps that would run, don't execute.
        //   stopOnError  — abort on the first failing step (default true).
        //   overrides    — { "<stepIndex>": { paramKey: value, ... } } merged
        //                  into that step's params (e.g. same macro, new model).
        public static object ReplayMacro(Dictionary<string, object> args)
        {
            string name = CommandRouter.RequireString(args, "name");
            bool dryRun = CommandRouter.GetBool(args, "dryRun", false);
            bool stopOnError = CommandRouter.GetBool(args, "stopOnError", true);

            var overrides = new Dictionary<int, Dictionary<string, object>>();
            object ov;
            if (args.TryGetValue("overrides", out ov) && ov is Dictionary<string, object>)
                foreach (var kv in (Dictionary<string, object>)ov)
                {
                    int idx;
                    if (int.TryParse(kv.Key, out idx) && kv.Value is Dictionary<string, object>)
                        overrides[idx] = (Dictionary<string, object>)kv.Value;
                }

            var macro = MacroStore.Load(name);
            var results = new List<object>();
            int stepNo = 0, ran = 0, failed = 0;

            foreach (MacroStore.MacroStep step in macro.Steps)
            {
                stepNo++;

                // Never let a macro invoke recorder/replay control commands.
                if (!MacroRecorder.IsReplayable(step.Command))
                {
                    results.Add(StepResult(stepNo, step.Command, "skipped", "control command not allowed inside a macro", null));
                    continue;
                }

                // Merge per-step overrides onto a copy of the saved params.
                var effectiveParams = new Dictionary<string, object>(step.Params ?? new Dictionary<string, object>());
                Dictionary<string, object> stepOverride;
                if (overrides.TryGetValue(stepNo, out stepOverride))
                    foreach (var kv in stepOverride) effectiveParams[kv.Key] = kv.Value;

                if (dryRun)
                {
                    results.Add(StepResult(stepNo, step.Command, "planned", null, effectiveParams));
                    continue;
                }

                try
                {
                    object r = CommandRouter.Execute(step.Command, effectiveParams);
                    ran++;
                    results.Add(StepResult(stepNo, step.Command, "ok", null, r));
                }
                catch (Exception ex)
                {
                    failed++;
                    string msg = ex.Message;
                    for (Exception inner = ex.InnerException; inner != null; inner = inner.InnerException)
                        if (!string.IsNullOrEmpty(inner.Message)) msg = inner.Message;
                    results.Add(StepResult(stepNo, step.Command, "error", msg, null));
                    if (stopOnError) break;
                }
            }

            return new Dictionary<string, object>
            {
                { "macro", macro.Name },
                { "dryRun", dryRun },
                { "totalSteps", macro.Steps.Count },
                { "executed", ran },
                { "failed", failed },
                { "results", results },
            };
        }

        private static object StepResult(int index, string command, string status, string error, object result)
        {
            var d = new Dictionary<string, object>
            {
                { "index", index }, { "command", command }, { "status", status },
            };
            if (error != null) d["error"] = error;
            if (result != null) d["result"] = result;
            return d;
        }
    }
}
