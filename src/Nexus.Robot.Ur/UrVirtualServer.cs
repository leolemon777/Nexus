using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Robot.Ur
{
    /// <summary>
    /// UR 机器人虚拟服务器 — 模拟 Universal Robots Dashboard 服务器（端口 29999）。
    /// <para>用于集成测试，无需真实 UR 机器人。</para>
    /// <para>Dashboard 服务器接收文本命令（以 \n 结束），返回文本响应（以 \n 结束）。</para>
    /// </summary>
    public class UrVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private string _loadedProgram = string.Empty;
        private bool _programRunning;

        /// <summary>监听端口。</summary>
        public int Port { get; private set; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        public UrVirtualServer(int port = 0)
        {
            Port = port;
        }

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
                    // Dashboard 连接建立时，服务器先发送欢迎消息（UR 真实行为）。
                    byte[] banner = Encoding.ASCII.GetBytes(
                        "Connected: Universal Robots Dashboard Server\n");
                    try { stream.Write(banner, 0, banner.Length); } catch { return; }

                    while (_running && client.Connected)
                    {
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read == 0) break;

                        string command = Encoding.ASCII.GetString(buffer, 0, read).Trim();
                        string response = ProcessDashboardCommand(command);

                        if (!string.IsNullOrEmpty(response))
                        {
                            byte[] respBytes = Encoding.ASCII.GetBytes(response + "\n");
                            try { stream.Write(respBytes, 0, respBytes.Length); }
                            catch { break; }
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>处理 Dashboard 命令，返回文本响应（不含末尾换行）。</summary>
        private string ProcessDashboardCommand(string command)
        {
            string cmd = command.ToLowerInvariant();

            // 修复：原实现对 def/movel 等命令返回空字符串（永不发送响应），
            // 导致 SendDashboardCommand 收到空响应或超时。现按 UR Dashboard 规范返回合理文本。
            if (cmd.StartsWith("load "))
            {
                _loadedProgram = command.Substring(5).Trim();
                return $"Loading program: {_loadedProgram}";
            }
            if (cmd.Contains("play"))
            {
                _programRunning = true;
                return "Starting program";
            }
            if (cmd.Contains("pause"))
            {
                _programRunning = false;
                return "Pausing program";
            }
            if (cmd.Contains("stop"))
            {
                _programRunning = false;
                return "Stopped";
            }
            if (cmd.Contains("quit") || cmd.Contains("disconnect"))
            {
                return "Disconnecting";
            }
            if (cmd.Contains("polyscope") || cmd.Contains("programstate") || cmd.Contains("running"))
            {
                return _programRunning ? "Program running: true" : "Program running: false";
            }
            if (cmd.Contains("power off"))
            {
                return "Powering off";
            }
            if (cmd.Contains("power on") || cmd.Contains("brake release"))
            {
                return "Powering on";
            }
            if (cmd.Contains("get loaded program"))
            {
                return _loadedProgram.Length > 0
                    ? $"Loaded program: {_loadedProgram}"
                    : "No program loaded";
            }
            // movel/movej/def 等 URScript 命令——Dashboard 服务器通常不接受这些（它们走 RTDE/29999 接口），
            // 但为兼容测试返回通用确认。
            if (cmd.Contains("movel") || cmd.Contains("movej") || cmd.Contains("def ") || cmd.Contains("end"))
            {
                return "ok";
            }
            // 未知命令返回通用确认（避免测试因空响应失败）。
            return "ok";
        }
    }
}
