using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Secs
{
    /// <summary>
    /// SECS II / HSMS 虚拟设备服务器 — 模拟半导体设备 HSMS 通讯。
    /// <para>用于集成测试，无需真实半导体设备硬件。</para>
    /// <para>帧格式: Length(4 BE) + Header(10) + Data</para>
    /// </summary>
    public class SecsHsmsVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _dataLock = new object();
        private int _connectionCount;

        // 数据模型
        private readonly byte[] _dataMemory = new byte[4096];
        private ushort _deviceId;

        // HSMS 消息类型
        private const byte PType_SECS2 = 0x00;
        private const byte PType_Select = 0x01;
        private const byte PType_Linktest = 0x05;

        private const byte SType_SelectReq = 0x01;
        private const byte SType_SelectRsp = 0x02;
        private const byte SType_LinktestReq = 0x05;
        private const byte SType_LinktestRsp = 0x06;
        private const byte SType_SeparateReq = 0x09;

        private const int LENGTH_FIELD_SIZE = 4;
        private const int HEADER_LENGTH = 10;

        /// <summary>监听端口。</summary>
        public int Port { get; private set; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        /// <summary>已接受的 TCP 连接数。</summary>
        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public SecsHsmsVirtualServer(int port = 5000)
        {
            Port = port;
        }

        // ── 数据设置方法（测试用） ──

        /// <summary>设置设备 ID。</summary>
        public void SetDeviceId(ushort id) { lock (_dataLock) _deviceId = id; }

        /// <summary>设置数据内存区域。</summary>
        public void SetDataMemory(int offset, byte[] data)
        {
            lock (_dataLock)
            {
                if (offset >= 0 && offset + data.Length <= _dataMemory.Length)
                    Buffer.BlockCopy(data, 0, _dataMemory, offset, data.Length);
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
                        // 读取 4 字节长度字段
                        byte[]? lengthField = ReadExact(stream, LENGTH_FIELD_SIZE);
                        if (lengthField == null) break;

                        int msgLen = (lengthField[0] << 24) | (lengthField[1] << 16) |
                                     (lengthField[2] << 8) | lengthField[3];

                        if (msgLen < HEADER_LENGTH || msgLen > 4096) break;

                        // 读取 header + data
                        byte[]? rest = ReadExact(stream, msgLen);
                        if (rest == null) break;

                        byte[] header = new byte[HEADER_LENGTH];
                        Array.Copy(rest, 0, header, 0, HEADER_LENGTH);

                        byte[]? data = null;
                        int dataLen = msgLen - HEADER_LENGTH;
                        if (dataLen > 0)
                        {
                            data = new byte[dataLen];
                            Array.Copy(rest, HEADER_LENGTH, data, 0, dataLen);
                        }

                        // 处理消息
                        byte[] response = HandleMessage(header, data);
                        stream.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
        }

        private byte[] HandleMessage(byte[] header, byte[]? data)
        {
            byte sType = header[2];
            byte pType = header[9];

            // 回显 SystemBytes
            byte sysByte0 = header[5];
            byte sysByte1 = header[6];
            byte sysByte2 = header[7];
            byte sysByte3 = header[8];

            ushort recvDeviceId = (ushort)((header[0] << 8) | header[1]);

            if (pType == PType_Linktest)
            {
                return BuildHsmsFrame(SType_LinktestRsp, PType_Linktest, recvDeviceId, sysByte0, sysByte1, sysByte2, sysByte3, null);
            }

            if (pType == PType_Select)
            {
                return BuildHsmsFrame(SType_SelectRsp, PType_Select, recvDeviceId, sysByte0, sysByte1, sysByte2, sysByte3, null);
            }

            // SECS Primary Message — 生成 Reply
            if (pType == PType_SECS2)
            {
                byte sfByte = header[3];
                byte stream = (byte)(sfByte >> 1);
                byte function = header[4];

                // 构建 Reply header
                byte[] replyHeader = new byte[HEADER_LENGTH];
                replyHeader[0] = header[0]; // DeviceId Hi
                replyHeader[1] = header[1]; // DeviceId Lo
                replyHeader[2] = 0x00;      // SType = 0 for data message
                replyHeader[3] = (byte)((stream << 1) & 0xFE); // Reply: even function, no W-bit
                replyHeader[4] = (byte)(function + 1); // Reply function = request + 1
                replyHeader[5] = sysByte0;
                replyHeader[6] = sysByte1;
                replyHeader[7] = sysByte2;
                replyHeader[8] = sysByte3;
                replyHeader[9] = PType_SECS2;

                // 回显数据（或返回默认数据）
                return BuildFrame(replyHeader, data);
            }

            // 默认: 回显
            return BuildHsmsFrame(sType, pType, recvDeviceId, sysByte0, sysByte1, sysByte2, sysByte3, data);
        }

        private static byte[] BuildHsmsFrame(byte sType, byte pType, ushort deviceId,
            byte sys0, byte sys1, byte sys2, byte sys3, byte[]? data)
        {
            byte[] header = new byte[HEADER_LENGTH];
            header[0] = (byte)((deviceId >> 8) & 0xFF);
            header[1] = (byte)(deviceId & 0xFF);
            header[2] = sType;
            header[3] = 0x00;
            header[4] = 0x00;
            header[5] = sys0;
            header[6] = sys1;
            header[7] = sys2;
            header[8] = sys3;
            header[9] = pType;
            return BuildFrame(header, data);
        }

        /// <summary>构建 HSMS 帧: Length(4 BE) + Header(10) + Data。</summary>
        public static byte[] BuildFrame(byte[] header, byte[]? data)
        {
            int dataLen = data?.Length ?? 0;
            int msgLen = HEADER_LENGTH + dataLen;
            byte[] frame = new byte[LENGTH_FIELD_SIZE + msgLen];

            frame[0] = (byte)((msgLen >> 24) & 0xFF);
            frame[1] = (byte)((msgLen >> 16) & 0xFF);
            frame[2] = (byte)((msgLen >> 8) & 0xFF);
            frame[3] = (byte)(msgLen & 0xFF);

            Array.Copy(header, 0, frame, LENGTH_FIELD_SIZE, HEADER_LENGTH);
            if (data != null && data.Length > 0)
                Array.Copy(data, 0, frame, LENGTH_FIELD_SIZE + HEADER_LENGTH, data.Length);

            return frame;
        }

        private static byte[]? ReadExact(NetworkStream stream, int count)
        {
            try
            {
                byte[] buffer = new byte[count];
                int offset = 0;
                while (offset < count)
                {
                    int read = stream.Read(buffer, offset, count - offset);
                    if (read == 0) return null;
                    offset += read;
                }
                return buffer;
            }
            catch { return null; }
        }
    }
}
