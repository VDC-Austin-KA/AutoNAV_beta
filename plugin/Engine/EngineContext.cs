using System;
using System.Collections.Generic;

namespace AutoNAVMCP
{
    // Headless plumbing that lets the ported AutoNAV2 engines run under the MCP
    // bridge instead of the WPF UI:
    //
    //   * Notifier messages (Info/Warning/Error/Result) are captured into a log
    //     that the bridge command returns to the AI client.
    //   * The Function 1 discipline-classification "unknown discipline" dialog
    //     is replaced by DisciplineContext: callers pre-seed Overrides (source
    //     filename -> discipline token); anything still unresolved is recorded
    //     and returned so the client can prompt the user and retry.
    //
    // Every engine call runs on the Navisworks main thread, one at a time
    // (BridgeServer marshals + serializes), so process-wide static state is
    // safe here — Capture() resets it at the start of each run.
    internal static class DisciplineContext
    {
        [ThreadStatic] public static Dictionary<string, string> Overrides;
        [ThreadStatic] private static List<KeyValuePair<string, string>> _unresolved;

        public static void RecordUnresolved(string sourceFile, string fallbackName)
        {
            if (_unresolved == null) _unresolved = new List<KeyValuePair<string, string>>();
            _unresolved.Add(new KeyValuePair<string, string>(sourceFile ?? "", fallbackName ?? ""));
        }

        public static List<KeyValuePair<string, string>> DrainUnresolved()
        {
            var list = _unresolved ?? new List<KeyValuePair<string, string>>();
            _unresolved = null;
            return list;
        }
    }

    // Result of running one engine action: the captured Notifier log plus any
    // files whose discipline could not be auto-classified and weren't overridden.
    internal sealed class EngineRun
    {
        public List<string> Log = new List<string>();
        public List<Dictionary<string, object>> UnresolvedDisciplines = new List<Dictionary<string, object>>();

        // Runs `action` with the Notifier wired to capture into this run's log,
        // and Function 1 discipline overrides applied from `overrides`.
        public static EngineRun Run(Action action, Dictionary<string, string> overrides = null)
        {
            var run = new EngineRun();
            var previousSink = Notifier.Sink;
            DisciplineContext.Overrides = overrides;
            DisciplineContext.DrainUnresolved(); // clear any stale state
            Notifier.Sink = (message, level, body) =>
            {
                string line = "[" + level + "] " + (message ?? "");
                if (!string.IsNullOrEmpty(body)) line += "\n" + body;
                run.Log.Add(line);
            };
            try
            {
                action();
            }
            finally
            {
                Notifier.Sink = previousSink;
                DisciplineContext.Overrides = null;
                foreach (var kv in DisciplineContext.DrainUnresolved())
                    run.UnresolvedDisciplines.Add(new Dictionary<string, object>
                    {
                        { "sourceFile", kv.Key },
                        { "suggestedName", kv.Value },
                    });
            }
            return run;
        }

        public Dictionary<string, object> ToResult(Dictionary<string, object> extra = null)
        {
            var result = extra ?? new Dictionary<string, object>();
            result["log"] = Log;
            if (UnresolvedDisciplines.Count > 0)
            {
                result["unresolvedDisciplines"] = UnresolvedDisciplines;
                result["needsDisciplineInput"] = true;
                result["hint"] =
                    "Some model files could not be auto-classified to a discipline. Ask the user which discipline " +
                    "each 'sourceFile' belongs to, then call create_discipline_search_sets again with " +
                    "disciplineOverrides = { \"<sourceFile>\": \"<disciplineToken>\", ... }.";
            }
            return result;
        }
    }
}
