using System;
using System.Collections.Generic;

namespace AutoNAVMCP.Macros
{
    // Journals the AutoNAV commands that flow through CommandRouter so a
    // sequence can be reviewed, saved as a macro, and replayed later.
    //
    // Scope note: this records *AutoNAV/MCP commands* (create clash test,
    // group, assign, run AutoNAVismate, …) — the deterministic, replayable
    // actions. It does NOT (and cannot, via the Navisworks API) capture
    // arbitrary manual UI actions like camera moves or ribbon clicks.
    //
    // All commands run one-at-a-time on the Navisworks main thread, so static
    // state needs no locking.
    internal static class MacroRecorder
    {
        // Control/recorder commands are never themselves journaled or replayed,
        // so recording a session and replaying a macro don't capture the act of
        // recording/replaying (and can't recurse).
        public static readonly HashSet<string> ControlCommands =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "start_recording", "stop_recording", "get_recording", "clear_recording",
            "save_macro", "list_macros", "get_macro", "delete_macro", "replay_macro",
            // Pure health checks add noise; skip them.
            "ping",
        };

        // Read-only query commands: journaled, but flagged non-mutating so
        // replay/analysis can skip them by default.
        private static readonly HashSet<string> ReadOnlyCommands =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get_document_info", "list_search_sets", "list_clash_tests",
            "get_clash_results", "get_clash_result", "list_disciplines",
            "list_discipline_properties", "list_discipline_property_values",
            "suggest_search_set_properties",
        };

        public sealed class Step
        {
            public int Index;
            public string Command;
            public Dictionary<string, object> Params;
            public bool Mutating;
            public bool Ok;
            public string Outcome;      // short human summary or error message
            public DateTime Timestamp;
        }

        public static bool IsRecording { get; private set; }
        public static string SessionName { get; private set; }
        public static DateTime StartedAt { get; private set; }

        private static readonly List<Step> _journal = new List<Step>();

        public static void Start(string name)
        {
            SessionName = string.IsNullOrWhiteSpace(name)
                ? "Recording " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") : name.Trim();
            StartedAt = DateTime.Now;
            _journal.Clear();
            IsRecording = true;
        }

        public static void Stop() { IsRecording = false; }

        public static void Clear() { _journal.Clear(); }

        public static bool IsReplayable(string command)
        {
            return !ControlCommands.Contains(command);
        }

        public static bool IsMutating(string command)
        {
            return !ControlCommands.Contains(command) && !ReadOnlyCommands.Contains(command);
        }

        // Called by CommandRouter after each command runs while recording.
        public static void Record(string command, Dictionary<string, object> args, bool ok, string outcome)
        {
            if (!IsRecording) return;
            if (ControlCommands.Contains(command)) return;

            _journal.Add(new Step
            {
                Index = _journal.Count + 1,
                Command = command,
                Params = args ?? new Dictionary<string, object>(),
                Mutating = IsMutating(command),
                Ok = ok,
                Outcome = outcome ?? "",
                Timestamp = DateTime.Now,
            });
        }

        public static IReadOnlyList<Step> Journal { get { return _journal; } }

        public static List<object> JournalAsObjects()
        {
            var list = new List<object>();
            foreach (Step s in _journal)
                list.Add(new Dictionary<string, object>
                {
                    { "index", s.Index },
                    { "command", s.Command },
                    { "params", s.Params },
                    { "mutating", s.Mutating },
                    { "ok", s.Ok },
                    { "outcome", s.Outcome },
                    { "timestamp", s.Timestamp },
                });
            return list;
        }
    }
}
