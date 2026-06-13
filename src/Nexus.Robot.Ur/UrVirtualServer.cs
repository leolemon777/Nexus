using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Robot.Ur
{
    public class UrVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        public int Port { get; }
        public bool IsRunning => _running;

        public ConcurrentDictionary<string, string> Variables { get; } = new ConcurrentDictionary<string, string>();

        public UrVirtualServer(int port = 30004) { Port = port; }

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
                        // UR sends periodic status data; for simplicity, read commands and respond
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read == 0) break;

                        string cmd = Encoding.ASCII.GetString(buffer, 0, read).Trim();

                        if (cmd.ToLowerInvariant().StartsWith("def") || cmd.ToLowerInvariant().Contains("movel"))
                        {
                            // Script program: respond with acknowledgment
                            string response = "";
                            byte[] respBytes = Encoding.ASCII.GetBytes(response);
                            if (respBytes.Length > 0)
                                stream.Write(respBytes, 0, respBytes.Length);
                        }
                        else if (cmd.ToLowerInvariant().Contains("get"))
                        {
                            string response = "0.0\n";
                            byte[] respBytes = Encoding.ASCII.GetBytes(response);
                            stream.Write(respBytes, 0, respBytes.Length);
                        }
                        // Dashboard commands
                        else if (cmd.ToLowerInvariant().Contains("running"))
                        {
                            string response = "Robotmode: RUNNING\n";
                            byte[] respBytes = Encoding.ASCII.GetBytes(response);
                            stream.Write(respBytes, 0, respBytes.Length);
                        }
                    }
                }
            }
            catch { }
        }

        public void Dispose() { Stop(); }
    }
}
