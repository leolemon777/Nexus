using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Robot.Kuka
{
    /// <summary>
    /// KUKA 机器人 TCP 虚拟服务器 — 模拟 KUKA TCP 通讯。
    /// <para>用于集成测试，无需真实 KUKA 控制器硬件。</para>
    /// <para>协议为纯 ASCII 文本：读取 "00"+变量名，写入 "01"+变量名=值。</para>
    /// </summary>
    public class KukaTcpVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _dataLock = new object();
        private int _connectionCount;

        // 数据模型 — 变量名 → 字符串值
        private readonly Dictionary<string, string> _variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>监听端口。</summary>
        public int Port { get; private set; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        /// <summary>已接受的 TCP 连接数。</summary>
        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public KukaTcpVirtualServer(int port = 9999)
        {
            Port = port;
        }

        // ── 数据设置方法（测试用） ──

        /// <summary>设置变量值。</summary>
        public void SetVariable(string name, string value)
        {
            lock (_dataLock) _variables[name ?? ""] = value ?? "";
        }

        /// <summary>获取变量值。</summary>
        public string? GetVariable(string name)
        {
            lock (_dataLock)
            {
                _variables.TryGetValue(name ?? "", out string? val);
                return val;
            }
        }

        // ── 服务器控制 ──

        /// <summary>启动虚拟服务器。</summary>
        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        /// <summary>停止虚拟服务器。</summary>
        public void Stop()
        {
            _running = false;
            _listener?.Stop();
            _acceptThread?.Join(2000);
        }

        public void Dispose()
        {
            Stop();
            _listener = null;
        }

        // ── 内部实现 ──

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener!.AcceptTcpClient();
                    Interlocked.Increment(ref _connectionCount);
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
                    while (_running && client.Connected)
                    {
                        // 读取 ASCII 命令直到流空闲
                        string? command = ReadTextCommand(stream);
                        if (command == null) break;

                        // 处理命令
                        string response = HandleCommand(command);
                        byte[] respBytes = Encoding.UTF8.GetBytes(response);
                        stream.Write(respBytes, 0, respBytes.Length);
                    }
                }
            }
            catch { }
        }

        private string HandleCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return "";

            // 命令前缀: "00"=读取, "01"=写入, "03"=启动程序, "06"=停止/复位
            string prefix = command.Substring(0, Math.Min(2, command.Length));

            switch (prefix)
            {
                case "00":
                    return HandleRead(command.Substring(2));

                case "01":
                    return HandleWrite(command.Substring(2));

                case "03":
                    // 启动程序 — 返回成功
                    return "OK";

                case "06":
                    // 停止/复位 — 返回成功
                    return "OK";

                default:
                    return "ERR:UNKNOWN_COMMAND";
            }
        }

        private string HandleRead(string varList)
        {
            // 变量列表以逗号分隔
            string[] names = varList.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (names.Length == 0) return "";

            var results = new List<string>();
            lock (_dataLock)
            {
                foreach (string name in names)
                {
                    string trimmed = name.Trim();
                    if (_variables.TryGetValue(trimmed, out string? val))
                        results.Add(val);
                    else
                        results.Add("0"); // 未定义变量返回 0
                }
            }

            return string.Join(",", results.ToArray());
        }

        private string HandleWrite(string assignmentList)
        {
            // 赋值列表以逗号分隔，每个为 name=value
            string[] assignments = assignmentList.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            lock (_dataLock)
            {
                foreach (string assignment in assignments)
                {
                    int eqPos = assignment.IndexOf('=');
                    if (eqPos > 0)
                    {
                        string name = assignment.Substring(0, eqPos).Trim();
                        string value = assignment.Substring(eqPos + 1).Trim();
                        _variables[name] = value;
                    }
                }
            }

            return "OK";
        }

        private static string? ReadTextCommand(NetworkStream stream)
        {
            try
            {
                var buffer = new List<byte>();
                byte[] buf = new byte[4096];
                int deadline = Environment.TickCount + 5000;

                while (Environment.TickCount < deadline)
                {
                    if (stream.DataAvailable)
                    {
                        int read = stream.Read(buf, 0, buf.Length);
                        if (read > 0)
                        {
                            byte[] chunk = new byte[read];
                            Array.Copy(buf, chunk, read);
                            buffer.AddRange(chunk);
                        }

                        // 短暂等待看是否有更多数据
                        Thread.Sleep(30);
                        if (!stream.DataAvailable) break;
                    }
                    else if (buffer.Count > 0)
                    {
                        break;
                    }
                    else
                    {
                        Thread.Sleep(10);
                    }
                }

                if (buffer.Count == 0) return null;
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
            catch { return null; }
        }
    }
}
