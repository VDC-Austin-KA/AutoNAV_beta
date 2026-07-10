using System;
using System.Windows.Forms;

namespace AutoNAVMCP.Bridge
{
    // The Navisworks .NET API may only be called from the application's main
    // (UI) thread. Bridge requests arrive on socket threads, so they are
    // marshalled through a hidden WinForms control whose handle is created
    // on the main thread when the plugin button is first clicked.
    internal static class MainThread
    {
        private static Control _marshal;

        // Must be called on the Navisworks main thread (e.g. from the
        // AddInPlugin Execute handler).
        public static void Initialize()
        {
            if (_marshal != null) return;
            _marshal = new Control();
            // Force native handle creation so Invoke works from other threads.
            var _ = _marshal.Handle;
        }

        public static bool IsInitialized
        {
            get { return _marshal != null; }
        }

        // Runs fn on the main thread and returns its result. Exceptions
        // thrown by fn are rethrown on the calling thread.
        public static object Run(Func<object> fn)
        {
            if (_marshal == null)
                throw new InvalidOperationException("Bridge not initialized on the main thread.");

            if (!_marshal.InvokeRequired)
                return fn();

            object result = null;
            Exception error = null;
            _marshal.Invoke((MethodInvoker)delegate
            {
                try { result = fn(); }
                catch (Exception ex) { error = ex; }
            });
            if (error != null) throw new CommandException(error.Message, error);
            return result;
        }
    }

    // Wraps a failure inside a command handler so the bridge can report the
    // message to the MCP client without tearing the connection down.
    internal class CommandException : Exception
    {
        public CommandException(string message) : base(message) { }
        public CommandException(string message, Exception inner) : base(message, inner) { }
    }
}
