using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Rkc
{
    /// <summary>
    /// RKC CD/CH 温度控制器虚拟服务器 — 模拟 RKC TCP 通讯。
    /// <para>用于集成测试，无需真实 RKC 温度控制器硬件。</para>
    /// <para>读取帧: EOT(0x04) + 站号(2 ASCII) + 地址(ASCII) + ENQ(0x05)</para>
    /// <para>读响应: STX(0x02) + 站号(2 ASCII) + 数据(ASCII) + ETX(0x03) + BCC</para>
    /// <para>写入帧: EOT(0x04) + 站号(2 ASCII) + STX(0x02) + 地址(ASCII) + 值(ASCII) + ETX(0x03) + BCC</para>
    /// <para>写响应: ACK(0x06) 或 NAK(0x15)</para>
    /// </summary>
    public class RkcTemperatureVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _dataLock = new object();

        // 数据模型 — 地址 → 字符串值
        private readonly Dictionary<string, string> _dataValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 帧常量
        private const byte EOT = 0x04;
        private const byte ENQ = 0x05;
        private const byte STX = 0x02;
        private const byte ETX = 0x03;
        private const byte ACK = 0x06;
        private const byte NAK = 0x15;

        /// <summary>监听端口。</summary>
        public int Port { get; private set; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        public RkcTemperatureVirtualServer(int port = 10001)
        {
            Port = port;
        }

        // ── 数据设置方法（测试用） ──

        /// <summary>设置地址数据值。</summary>
        public void SetValue(string address, double value)
        {
            lock (_dataLock) _dataValues[address ?? "M1"] = value.ToString("F1");
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
                        // 读取第一个字节判断帧类型
                        int firstByte = stream.ReadByte();
                        if (firstByte < 0) break;

                        if (firstByte == EOT)
                        {
                            HandleEotFrame(stream);
                        }
                        else
                        {
                            // 未知帧 — 跳过
                            continue;
                        }
                    }
                }
            }
            catch { }
        }

        private void HandleEotFrame(NetworkStream stream)
        {
            // 读取后续字节直到 ENQ(0x05) 或 STX(0x02)
            var buffer = new List<byte>();
            int deadline = Environment.TickCount + 5000;

            while (Environment.TickCount < deadline)
            {
                if (stream.DataAvailable)
                {
                    int b = stream.ReadByte();
                    if (b < 0) return;
                    buffer.Add((byte)b);

                    if (b == ENQ)
                    {
                        // 读请求: EOT + 站号(2) + 地址 + ENQ
                        ProcessReadRequest(stream, buffer);
                        return;
                    }

                    if (b == STX)
                    {
                        // 写请求开始: EOT + 站号(2) + STX + 地址 + 值 + ETX + BCC
                        ProcessWriteRequest(stream, buffer);
                        return;
                    }
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
        }

        private void ProcessReadRequest(NetworkStream stream, List<byte> buffer)
        {
            // buffer: 站号(2) + 地址 + ENQ
            if (buffer.Count < 3) { SendNak(stream); return; }

            // 移除末尾的 ENQ
            string stationAndAddr = Encoding.ASCII.GetString(buffer.ToArray(), 0, buffer.Count - 1);
            string address = stationAndAddr.Substring(2); // 跳过站号

            string value;
            lock (_dataLock)
            {
                _dataValues.TryGetValue(address, out string? val);
                value = val ?? "000.0";
            }

            // 构建响应: STX + 站号(2) + 数据(ASCII) + ETX + BCC
            var response = new List<byte>();
            response.Add(STX);
            response.AddRange(buffer.GetRange(0, 2)); // 站号
            byte[] valueBytes = Encoding.ASCII.GetBytes(value);
            response.AddRange(valueBytes);
            response.Add(ETX);

            // BCC: 从站号第一个字节到 ETX 的异或
            byte bcc = 0;
            for (int i = 1; i < response.Count; i++) // 从站号开始（跳过 STX）
                bcc ^= response[i];
            response.Add(bcc);

            stream.Write(response.ToArray(), 0, response.Count);
        }

        private void ProcessWriteRequest(NetworkStream stream, List<byte> buffer)
        {
            // buffer: 站号(2) + STX
            // 还需读取: 地址 + 值 + ETX + BCC
            var rest = new List<byte>();
            int deadline = Environment.TickCount + 5000;

            while (Environment.TickCount < deadline)
            {
                if (stream.DataAvailable)
                {
                    int b = stream.ReadByte();
                    if (b < 0) { SendNak(stream); return; }
                    rest.Add((byte)b);

                    if (b == ETX && rest.Count >= 2 && rest[rest.Count - 2] != ETX)
                    {
                        // ETX 找到，下一个是 BCC
                        int bccByte = stream.ReadByte();
                        // 简化: 直接接受
                        // 解析地址和值
                        string restStr = Encoding.ASCII.GetString(rest.ToArray(), 0, rest.Count - 1); // 去掉 ETX
                        // 找到地址和值的分界 — 在 RKC 写入中，地址长度固定，值为 6 字符
                        if (restStr.Length > 6)
                        {
                            string address = restStr.Substring(0, restStr.Length - 6);
                            string value = restStr.Substring(restStr.Length - 6);
                            lock (_dataLock) _dataValues[address] = value;
                        }

                        SendAck(stream);
                        return;
                    }
                }
                else
                {
                    Thread.Sleep(5);
                }
            }

            SendNak(stream);
        }

        private static void SendAck(NetworkStream stream)
        {
            stream.WriteByte(ACK);
        }

        private static void SendNak(NetworkStream stream)
        {
            stream.WriteByte(NAK);
        }
    }
}
