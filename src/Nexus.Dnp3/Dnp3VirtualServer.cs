using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.Dnp3
{
    /// <summary>
    /// DNP3 虚拟从站服务器 — 模拟 DNP3 TCP 协议通讯。
    /// <para>用于集成测试，无需真实 DNP3 RTU/IED 硬件。</para>
    /// <para>链路层帧: Start(2: 0x05 0x64) + Length(1) + Control(1) + Dest(2) + Src(2) + CRC(2) + UserData</para>
    /// </summary>
    public class Dnp3VirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _dataLock = new object();
        private int _connectionCount;

        // 数据模型
        private readonly float[] _analogInputs = new float[256];
        private readonly bool[] _binaryInputs = new bool[256];

        /// <summary>监听端口。</summary>
        public int Port { get; private set; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        /// <summary>已接受的 TCP 连接数。</summary>
        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public Dnp3VirtualServer(int port = 20000)
        {
            Port = port;
        }

        // ── 数据设置方法（测试用） ──

        /// <summary>设置模拟输入浮点值。</summary>
        public void SetAnalogInput(int index, float value)
        {
            if (index >= 0 && index < 256) lock (_dataLock) _analogInputs[index] = value;
        }

        /// <summary>设置二进制输入。</summary>
        public void SetBinaryInput(int index, bool value)
        {
            if (index >= 0 && index < 256) lock (_dataLock) _binaryInputs[index] = value;
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
                        // 读取 10 字节链路层帧头
                        byte[]? header = ReadExact(stream, 10);
                        if (header == null) break;

                        // 验证起始字节
                        if (header[0] != Dnp3Constants.StartByte1 || header[1] != Dnp3Constants.StartByte2)
                            continue;

                        // 解析请求帧: BuildLinkHeader 中 frame[2] = userDataLength + 10
                        // 已读 10 字节固定头，还需读 userDataLength = length - 10 字节
                        int length = header[2];
                        int userDataLen = length - 10;

                        byte[]? requestData = null;
                        if (userDataLen > 0)
                        {
                            requestData = ReadExact(stream, userDataLen);
                            if (requestData == null) break;
                        }

                        // 构建并发送响应
                        byte[] response = BuildResponse(header, requestData);
                        stream.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
        }

        private byte[] BuildResponse(byte[] requestHeader, byte[]? requestData)
        {
            // 客户端 ResponseHeaderLength = 10, GetResponsePayloadLength = header[2] - 8
            // 响应格式: Start(2) + Length(1) + Control(1) + Dest(2) + Src(2) + CRC(2) + AppData

            byte[] appData = BuildApplicationData(requestData);
            int appDataLen = appData.Length;

            byte[] frame = new byte[10 + appDataLen];

            // Start bytes
            frame[0] = Dnp3Constants.StartByte1;
            frame[1] = Dnp3Constants.StartByte2;

            // Length = 5 + userDataLen; userDataLen = 5 + appDataLen - actually:
            // GetResponsePayloadLength returns header[2] - 8
            // We want payload = appDataLen
            // So header[2] - 8 = appDataLen → header[2] = appDataLen + 8
            frame[2] = (byte)(appDataLen + 8);

            // Control (response from outstation)
            frame[3] = 0x00; // response

            // Dest = swap of request's source
            frame[4] = requestHeader[6]; // request src lo
            frame[5] = requestHeader[7]; // request src hi

            // Src = swap of request's dest
            frame[6] = requestHeader[4]; // request dest lo
            frame[7] = requestHeader[5]; // request dest hi

            // CRC placeholder
            frame[8] = 0x00;
            frame[9] = 0x00;

            if (appDataLen > 0)
                Buffer.BlockCopy(appData, 0, frame, 10, appDataLen);

            return frame;
        }

        private byte[] BuildApplicationData(byte[]? requestData)
        {
            if (requestData == null || requestData.Length < 4)
                return BuildAnalogInputData(0, 4);

            byte functionCode = requestData[1];
            if (functionCode == (byte)Dnp3FunctionCode.DirectOperate)
                return new byte[0];

            var group = (Dnp3Group)requestData[2];
            ushort start = requestData.Length > 6
                ? (ushort)(requestData[5] | (requestData[6] << 8))
                : (ushort)0;
            ushort stop = requestData.Length > 9
                ? (ushort)(requestData[8] | (requestData[9] << 8))
                : (ushort)3;
            ushort count = stop >= start ? (ushort)(stop - start + 1) : (ushort)1;

            if (group == Dnp3Group.BinaryInput)
                return BuildBinaryInputData(start, count);

            return BuildAnalogInputData(start, count);
        }

        private byte[] BuildAnalogInputData(ushort start, ushort count)
        {
            int valueCount = Math.Min(count, (ushort)Math.Max(0, 256 - start));
            if (valueCount <= 0) valueCount = 1;

            byte[] data = new byte[valueCount * 4];
            lock (_dataLock)
            {
                for (int i = 0; i < valueCount; i++)
                {
                    byte[] val = BitConverter.GetBytes(_analogInputs[start + i]);
                    Buffer.BlockCopy(val, 0, data, i * 4, 4);
                }
            }

            return data;
        }

        private byte[] BuildBinaryInputData(ushort start, ushort count)
        {
            int valueCount = Math.Min(count, (ushort)Math.Max(0, 256 - start));
            if (valueCount <= 0) valueCount = 1;

            int byteCount = (valueCount + 7) / 8;
            byte[] data = new byte[2 + byteCount];
            lock (_dataLock)
            {
                for (int i = 0; i < valueCount; i++)
                {
                    if (_binaryInputs[start + i])
                        data[2 + i / 8] |= (byte)(1 << (i % 8));
                }
            }

            return data;
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
