using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Kuka
{
    /// <summary>
    /// KUKA EKI 虚拟机器人服务器 — 模拟 XML over TCP 协议。
    /// <para>用于集成测试，无需真实 KUKA 控制器。</para>
    /// </summary>
    public class KukaEkiVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _varLock = new object();
        private readonly System.Collections.Generic.Dictionary<string, string> _variables = new System.Collections.Generic.Dictionary<string, string>();

        public int Port { get; }
        public bool IsRunning => _running;

        public KukaEkiVirtualServer(int port = 54601) { Port = port; }

        /// <summary>设置变量值。</summary>
        public void SetVariable(string name, string value) { lock (_varLock) _variables[name] = value; }
        /// <summary>获取变量值。</summary>
        public string? GetVariable(string name) { lock (_varLock) return _variables.TryGetValue(name, out var v) ? v : null; }

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop() { _running = false; try { _listener?.Stop(); } catch { } _listener = null; }

        private void AcceptLoop()
        {
            while (_running)
            {
                try { var c = _listener!.AcceptTcpClient(); new Thread(() => HandleClient(c)) { IsBackground = true }.Start(); }
                catch { if (!_running) break; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buf = new byte[4096];
                while (_running && client.Connected)
                {
                    try
                    {
                        // EKI 使用简单的 XML 帧分隔，以 &lt;Robot&gt; 开头
                        int bytesRead = 0;
                        var sb = new StringBuilder();
                        while (stream.DataAvailable || bytesRead == 0)
                        {
                            int n = stream.Read(buf, 0, buf.Length);
                            if (n <= 0) return;
                            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                            bytesRead += n;
                            if (sb.ToString().Contains("</Robot>")) break;
                        }

                        string request = sb.ToString();
                        string response = ProcessXml(request);

                        byte[] respBytes = Encoding.UTF8.GetBytes(response);
                        stream.Write(respBytes, 0, respBytes.Length);
                    }
                    catch { break; }
                }
            }
        }

        private string ProcessXml(string request)
        {
            // 解析 <Read Var="..." />
            int readIdx = request.IndexOf("<Read Var=\"");
            if (readIdx >= 0)
            {
                int start = readIdx + "<Read Var=\"".Length;
                int end = request.IndexOf("\"", start);
                if (end > start)
                {
                    string varName = request.Substring(start, end - start);
                    string value;
                    lock (_varLock)
                    {
                        value = _variables.TryGetValue(varName, out var v) ? v : "0";
                    }
                    return $"<Robot><Read Var=\"{varName}\">{value}</Read></Robot>";
                }
            }

            // 解析 <Write Var="...">VALUE</Write>
            int writeIdx = request.IndexOf("<Write Var=\"");
            if (writeIdx >= 0)
            {
                int start = writeIdx + "<Write Var=\"".Length;
                int end = request.IndexOf("\"", start);
                if (end > start)
                {
                    string varName = request.Substring(start, end - start);
                    int valStart = request.IndexOf(">", end) + 1;
                    int valEnd = request.IndexOf("</Write>", valStart);
                    if (valEnd > valStart)
                    {
                        string value = request.Substring(valStart, valEnd - valStart);
                        lock (_varLock) _variables[varName] = value;
                        return $"<Robot><Write Var=\"{varName}\">{value}</Write></Robot>";
                    }
                }
            }

            return "<Robot><Error>Unknown command</Error></Robot>";
        }

        public void Dispose() { Stop(); GC.SuppressFinalize(this); }
    }
}
