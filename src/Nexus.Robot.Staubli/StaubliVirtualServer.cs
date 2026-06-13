using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Robot.Staubli
{
    public class StaubliVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        public int Port { get; }
        public bool IsRunning => _running;

        public ConcurrentDictionary<string, string> Variables { get; } = new ConcurrentDictionary<string, string>();

        public StaubliVirtualServer(int port = 59001) { Port = port; }

        public void SetVariable(string name, string value) { Variables[name] = value; }

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop() { _running = false; _listener?.Stop(); }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener!.AcceptTcpClient();
                    var thread = new Thread(() => HandleClient(client)) { IsBackground = true };
                    thread.Start();
                }
                catch { break; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[4096];
                    while (_running && client.Connected)
                    {
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read == 0) break;

                        string cmd = Encoding.ASCII.GetString(buffer, 0, read).Trim();
                        string response = ProcessCommand(cmd);
                        byte[] respBytes = Encoding.ASCII.GetBytes(response + "\r\n");
                        stream.Write(respBytes, 0, respBytes.Length);
                    }
                }
            }
            catch { }
        }

        private string ProcessCommand(string cmd)
        {
            if (cmd.ToLowerInvariant().StartsWith("getvariable("))
            {
                int start = cmd.IndexOf('(') + 1;
                int end = cmd.IndexOf(')');
                if (start > 0 && end > start)
                {
                    string name = cmd.Substring(start, end - start).Trim().Trim('"').Trim('\'');
                    if (Variables.TryGetValue(name, out string val))
                        return val;
                    return "0.0";
                }
            }
            else if (cmd.ToLowerInvariant().StartsWith("setvariable("))
            {
                return "0";
            }
            else if (cmd.ToLowerInvariant().Contains("jointPositions"))
            {
                return "0.0,0.0,0.0,0.0,0.0,0.0";
            }
            else if (cmd.ToLowerInvariant().Contains("move"))
            {
                return "0";
            }
            return "0";
        }

        public void Dispose() { Stop(); }
    }
}
