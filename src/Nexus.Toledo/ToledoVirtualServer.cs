using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Toledo
{
    /// <summary>
    /// 梅特勒-托利多电子秤虚拟服务器 — 模拟 Toledo 秤 TCP 数据输出。
    /// <para>用于集成测试，无需真实托利多电子秤硬件。</para>
    /// <para>标准连续输出模式帧: STX(0x02) + 小数点(1) + 状态(1) + 单位状态(1) + 重量(6 ASCII) + 皮重(6 ASCII) + CR(0x0D)</para>
    /// </summary>
    public class ToledoVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _dataLock = new object();
        private int _connectionCount;

        // 数据模型
        private float _weight = 1.234f;
        private float _tare;
        private bool _isNet;
        private bool _positive = true;
        private bool _isDynamic;
        private int _decimalPlaces = 3; // buffer[1] & 7

        /// <summary>监听端口。</summary>
        public int Port { get; private set; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        /// <summary>已接受的 TCP 连接数。</summary>
        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public ToledoVirtualServer(int port = 8000)
        {
            Port = port;
        }

        // ── 数据设置方法（测试用） ──

        /// <summary>设置重量值（已经过小数处理后的原始值）。</summary>
        public void SetWeight(float weight) { lock (_dataLock) _weight = weight; }

        /// <summary>设置皮重值。</summary>
        public void SetTare(float tare) { lock (_dataLock) _tare = tare; }

        /// <summary>设置净重标志。</summary>
        public void SetNet(bool isNet) { lock (_dataLock) _isNet = isNet; }

        /// <summary>设置正负号。</summary>
        public void SetPositive(bool positive) { lock (_dataLock) _positive = positive; }

        /// <summary>设置动态标志。</summary>
        public void SetDynamic(bool isDynamic) { lock (_dataLock) _isDynamic = isDynamic; }

        /// <summary>设置小数位数（0-7）。</summary>
        public void SetDecimalPlaces(int dp) { lock (_dataLock) _decimalPlaces = dp & 7; }

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
                    // Toledo 秤模式: 连接后按请求发送数据帧
                    // 客户端每次 ReceiveFrame 连接后读取一帧
                    while (_running && client.Connected)
                    {
                        // 发送标准输出帧
                        byte[] frame = BuildStandardFrame();
                        stream.Write(frame, 0, frame.Length);

                        // 等待客户端断开或下一轮读取
                        Thread.Sleep(200);
                        // 检查客户端是否关闭了连接
                        if (!client.Connected || !stream.DataAvailable)
                        {
                            // 等待客户端可能的重连读取
                            Thread.Sleep(300);
                            if (!client.Connected) break;
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 构建标准连续输出模式帧: STX(1) + 小数点(1) + 状态(1) + 单位(1) + 重量(6 ASCII) + 皮重(6 ASCII) + CR(1) = 17 字节。
        /// <para>客户端 ReceiveFrame 等待 CR(0x0D) 且 buffer &gt;= 16。</para>
        /// </summary>
        private byte[] BuildStandardFrame()
        {
            byte[] frame = new byte[17]; // 16 data + CR
            lock (_dataLock)
            {
                // [0] STX — 标准连续输出模式标识
                frame[0] = 0x02;

                // [1] 小数点位置 (dp=2 → ApplyDecimalPlaces 不调整)
                frame[1] = (byte)(_decimalPlaces & 7);

                // [2] 状态位
                byte status = 0;
                if (_isNet) status |= 0x01;
                if (_positive) status |= 0x02;
                if (_isDynamic) status |= 0x08;
                frame[2] = status;

                // [3] 单位/打印状态 (bit3=print, bit4=10x)
                frame[3] = 0x00;

                // [4-9] 重量 (6 ASCII 字符，客户端 float.Parse 直接解析)
                string w = _weight.ToString("F3").PadLeft(6);
                if (w.Length > 6) w = w.Substring(w.Length - 6);
                Encoding.ASCII.GetBytes(w.PadLeft(6)).CopyTo(frame, 4);

                // [10-15] 皮重 (6 ASCII 字符)
                string t = _tare.ToString("F3").PadLeft(6);
                if (t.Length > 6) t = t.Substring(t.Length - 6);
                Encoding.ASCII.GetBytes(t.PadLeft(6)).CopyTo(frame, 10);
            }

            // [16] CR — 帧结束符（客户端 ReceiveFrame 据此判断帧完整）
            frame[16] = 0x0D;
            return frame;
        }
    }
}
