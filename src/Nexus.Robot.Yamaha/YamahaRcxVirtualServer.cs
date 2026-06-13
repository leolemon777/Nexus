using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Robot.Yamaha
{
    /// <summary>
    /// 雅马哈 RCX 控制器虚拟服务器 — 模拟 YAMAHA 机器人 ASCII 协议通讯。
    /// <para>用于集成测试，无需真实 YAMAHA RCX 控制器硬件。</para>
    /// <para>命令以 CRLF 结尾，响应以 OK\r\n 或 NG=错误码\r\n 终止。</para>
    /// </summary>
    public class YamahaRcxVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _dataLock = new object();
        private int _connectionCount;

        // 数据模型
        private int _motorStatus;
        private int _modeStatus = 1;
        private int _emergencyStatus;
        private readonly float[] _joints = new float[6];
        private readonly Dictionary<int, int> _digitalInputs = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _digitalOutputs = new Dictionary<int, int>();

        /// <summary>监听端口。</summary>
        public int Port { get; private set; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        /// <summary>已接受的 TCP 连接数。</summary>
        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public YamahaRcxVirtualServer(int port = 80)
        {
            Port = port;
        }

        // ── 数据设置方法（测试用） ──

        /// <summary>设置马达状态。</summary>
        public void SetMotorStatus(int status) { lock (_dataLock) _motorStatus = status; }

        /// <summary>设置模式状态。</summary>
        public void SetModeStatus(int mode) { lock (_dataLock) _modeStatus = mode; }

        /// <summary>设置急停状态。</summary>
        public void SetEmergencyStatus(int status) { lock (_dataLock) _emergencyStatus = status; }

        /// <summary>设置关节角度。</summary>
        public void SetJoint(int axis, float value)
        {
            if (axis >= 0 && axis < 6) lock (_dataLock) _joints[axis] = value;
        }

        /// <summary>设置数字输入值。</summary>
        public void SetDigitalInput(int index, int value)
        {
            lock (_dataLock) _digitalInputs[index] = value;
        }

        /// <summary>设置数字输出值。</summary>
        public void SetDigitalOutput(int index, int value)
        {
            lock (_dataLock) _digitalOutputs[index] = value;
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
                        // 读取一行命令（以 CRLF 结尾）
                        string? line = ReadLine(stream);
                        if (line == null) break;

                        string response = HandleCommand(line.Trim());
                        byte[] respBytes = Encoding.ASCII.GetBytes(response);
                        stream.Write(respBytes, 0, respBytes.Length);
                    }
                }
            }
            catch { }
        }

        private string HandleCommand(string command)
        {
            // @?MOTOR — 读取马达状态
            if (command.StartsWith("@?MOTOR", StringComparison.OrdinalIgnoreCase))
            {
                lock (_dataLock) return $"{_motorStatus}\r\nOK\r\n";
            }

            // @?MODE — 读取模式
            if (command.StartsWith("@?MODE", StringComparison.OrdinalIgnoreCase))
            {
                lock (_dataLock) return $"{_modeStatus}\r\nOK\r\n";
            }

            // @?EMG — 读取急停状态
            if (command.StartsWith("@?EMG", StringComparison.OrdinalIgnoreCase))
            {
                lock (_dataLock) return $"{_emergencyStatus}\r\nOK\r\n";
            }

            // @?WHERE — 读取关节位置
            if (command.StartsWith("@?WHERE", StringComparison.OrdinalIgnoreCase))
            {
                lock (_dataLock)
                {
                    var parts = new List<string>();
                    for (int i = 0; i < 6; i++)
                        parts.Add(_joints[i].ToString("F3"));
                    return string.Join(" ", parts.ToArray()) + "\r\nOK\r\n";
                }
            }

            // @?DI — 读取数字输入
            if (command.StartsWith("@?DI", StringComparison.OrdinalIgnoreCase))
            {
                int index = ParseIndex(command, "@?DI");
                lock (_dataLock)
                {
                    _digitalInputs.TryGetValue(index, out int val);
                    return $"{val}\r\nOK\r\n";
                }
            }

            // @?DO — 读取数字输出
            if (command.StartsWith("@?DO", StringComparison.OrdinalIgnoreCase))
            {
                int index = ParseIndex(command, "@?DO");
                lock (_dataLock)
                {
                    _digitalOutputs.TryGetValue(index, out int val);
                    return $"{val}\r\nOK\r\n";
                }
            }

            // @ RESET, @ RUN, @ STOP, @ LOAD — 程序控制
            if (command.StartsWith("@ RESET", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("@ RUN", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("@ STOP", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("@ LOAD", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("＠ LOAD", StringComparison.OrdinalIgnoreCase))
            {
                return "OK\r\n";
            }

            return "NG=1\r\n";
        }

        private static int ParseIndex(string command, string prefix)
        {
            string rest = command.Substring(prefix.Length);
            // 格式: "1()" 或 "1" — 索引在括号之前
            int parenOpen = rest.IndexOf('(');
            if (parenOpen > 0)
            {
                // 索引在括号之前
                string idxStr = rest.Substring(0, parenOpen).Trim();
                int.TryParse(idxStr, out int idx);
                return idx;
            }
            // 无括号，直接解析
            int.TryParse(rest.Trim(), out int result);
            return result;
        }

        private static string? ReadLine(NetworkStream stream)
        {
            try
            {
                var buffer = new List<byte>();
                int deadline = Environment.TickCount + 10000;

                while (Environment.TickCount < deadline)
                {
                    if (stream.DataAvailable)
                    {
                        int b = stream.ReadByte();
                        if (b < 0) break;
                        buffer.Add((byte)b);

                        // 检测 CRLF
                        if (buffer.Count >= 2 && buffer[buffer.Count - 2] == '\r' && buffer[buffer.Count - 1] == '\n')
                        {
                            // 移除 CRLF
                            buffer.RemoveAt(buffer.Count - 1);
                            buffer.RemoveAt(buffer.Count - 1);
                            return Encoding.ASCII.GetString(buffer.ToArray());
                        }
                    }
                    else if (buffer.Count > 0)
                    {
                        Thread.Sleep(10);
                    }
                    else
                    {
                        Thread.Sleep(5);
                    }
                }

                if (buffer.Count > 0)
                    return Encoding.ASCII.GetString(buffer.ToArray());
                return null;
            }
            catch { return null; }
        }
    }
}
