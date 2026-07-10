using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AutoNAVMCP.Bridge
{
    // Loopback TCP server the MCP server process connects to.
    //
    // Protocol: newline-delimited JSON (one request / one response per line).
    //   request:  {"id": 1, "command": "list_clash_tests", "params": {...}}
    //   response: {"id": 1, "ok": true, "result": {...}}
    //             {"id": 1, "ok": false, "error": "message"}
    //
    // A plain TcpListener (rather than HttpListener) is used deliberately:
    // it binds to 127.0.0.1 without requiring URL ACL reservations or
    // administrator rights.
    internal static class BridgeServer
    {
        public const int DefaultPort = 5711;

        private static TcpListener _listener;
        private static Thread _acceptThread;
        private static volatile bool _running;
        private static int _port;
        private static readonly object _lock = new object();

        public static bool IsRunning { get { return _running; } }
        public static int Port { get { return _port; } }

        public static int ResolvePort()
        {
            string env = Environment.GetEnvironmentVariable("AUTONAV_MCP_PORT");
            int port;
            if (!string.IsNullOrEmpty(env) && int.TryParse(env, out port) && port > 0 && port < 65536)
                return port;
            return DefaultPort;
        }

        public static void Start(int port)
        {
            lock (_lock)
            {
                if (_running) return;
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                _port = port;
                _running = true;
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "AutoNAVMCP-Accept" };
                _acceptThread.Start();
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                try { _listener.Stop(); } catch { }
                _listener = null;
            }
        }

        private static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { break; } // listener stopped
                var t = new Thread(() => ServeClient(client)) { IsBackground = true, Name = "AutoNAVMCP-Client" };
                t.Start();
            }
        }

        private static void ServeClient(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
                {
                    string line;
                    while (_running && (line = reader.ReadLine()) != null)
                    {
                        if (line.Trim().Length == 0) continue;
                        string response = HandleLine(line);
                        writer.WriteLine(response);
                    }
                }
            }
            catch
            {
                // Client disconnects and socket resets are routine; the
                // MCP server reconnects on its next request.
            }
        }

        private static string HandleLine(string line)
        {
            object id = null;
            try
            {
                var request = MiniJson.Parse(line) as Dictionary<string, object>;
                if (request == null) throw new CommandException("Request must be a JSON object.");
                object idVal;
                request.TryGetValue("id", out idVal);
                id = idVal;

                object cmdVal;
                if (!request.TryGetValue("command", out cmdVal) || !(cmdVal is string))
                    throw new CommandException("Missing 'command' field.");
                string command = (string)cmdVal;

                object paramsVal;
                request.TryGetValue("params", out paramsVal);
                var args = paramsVal as Dictionary<string, object> ?? new Dictionary<string, object>();

                object result = MainThread.Run(() => Commands.CommandRouter.Execute(command, args));

                return MiniJson.Serialize(new Dictionary<string, object>
                {
                    { "id", id }, { "ok", true }, { "result", result }
                });
            }
            catch (Exception ex)
            {
                string message = ex.Message;
                for (Exception inner = ex.InnerException; inner != null; inner = inner.InnerException)
                    if (!string.IsNullOrEmpty(inner.Message)) message = inner.Message;
                return MiniJson.Serialize(new Dictionary<string, object>
                {
                    { "id", id }, { "ok", false }, { "error", message }
                });
            }
        }
    }
}
