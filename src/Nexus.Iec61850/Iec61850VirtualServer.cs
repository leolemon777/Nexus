using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Iec61850
{
    /// <summary>
    /// IEC 61850 MMS 虚拟服务器 — 模拟 IED MMS 协议通讯。
    /// <para>用于集成测试，无需真实 IEC 61850 IED 硬件。</para>
    /// <para>请求: 服务类型(1) + InvokeId(4) + LD(32) + LN(32) + DataName(32) + FC(1) + [值]</para>
    /// <para>响应: 同请求头前6字节 + 长度(2 BE) + 数据</para>
    /// </summary>
    public class Iec61850VirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _dataLock = new object();
        private int _connectionCount;

        // 数据模型
        private readonly byte[] _dataMemory = new byte[4096];

        /// <summary>监听端口。</summary>
        public int Port { get; private set; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        /// <summary>累计接收的 TCP 连接数量。</summary>
        public int ConnectionCount => _connectionCount;

        public Iec61850VirtualServer(int port = 102)
        {
            Port = port;
        }

        // ── 数据设置方法（测试用） ──

        /// <summary>设置数据存储区域。</summary>
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
                        // IEC 61850 请求格式:
                        // GetDataValues: 102 字节 (ServiceType(1) + InvokeId(4) + LD(32) + LN(32) + DataName(32) + FC(1))
                        // SetDataValues: 102 + value.Length + 1 字节
                        // 先读取第一个字节判断服务类型
                        byte[]? firstByte = ReadExact(stream, 1);
                        if (firstByte == null) break;

                        byte serviceType = firstByte[0];
                        byte[] response;

                        if (serviceType == 0x03) // GetDataValues
                        {
                            // 读取剩余 101 字节
                            byte[]? rest = ReadExact(stream, 101);
                            if (rest == null) break;

                            // 提取 LN + DataName 用于寻址（简化处理）
                            byte[] data = new byte[16];
                            lock (_dataLock)
                            {
                                Buffer.BlockCopy(_dataMemory, 0, data, 0, Math.Min(data.Length, _dataMemory.Length));
                            }

                            response = BuildGetDataValuesResponse(rest, data);
                        }
                        else if (serviceType == 0x04) // SetDataValues
                        {
                            // 请求格式: ServiceType(1) + InvokeId(4) + LD(32) + LN(32) + DataName(32) + FC(1) + ValLen(1) + Value
                            // 已读 1 字节 serviceType，还需读 102 字节到达 ValLen
                            byte[]? rest = ReadExact(stream, 102);
                            if (rest == null) break;

                            // 值长度在 rest[101] (offset 102 in full request)
                            int valLen = rest[101];
                            if (valLen > 0)
                            {
                                byte[]? val = ReadExact(stream, valLen);
                                if (val == null) break;

                                lock (_dataLock)
                                {
                                    int copyLen = Math.Min(valLen, _dataMemory.Length);
                                    Buffer.BlockCopy(val, 0, _dataMemory, 0, copyLen);
                                }
                            }

                            response = BuildSetDataValuesResponse(rest);
                        }
                        else if (serviceType == 0x05) // GetServerDirectory
                        {
                            byte[]? rest = ReadExact(stream, 4); // InvokeId
                            if (rest == null) break;
                            response = BuildDirectoryResponse(0x05, new[] { "LD0", "LD1" });
                        }
                        else if (serviceType == 0x06) // GetLogicalDeviceDirectory
                        {
                            byte[]? rest = ReadExact(stream, 36); // InvokeId(4) + LD(32)
                            if (rest == null) break;
                            response = BuildDirectoryResponse(0x06, new[] { "LLN0", "GGIO1", "MMXU1" });
                        }
                        else if (serviceType == 0x07) // GetLogicalNodeDirectory
                        {
                            byte[]? rest = ReadExact(stream, 68); // InvokeId(4) + LD(32) + LN(32)
                            if (rest == null) break;
                            response = BuildDirectoryResponse(0x07, new[] { "Beh", "Mod", "NamPlt", "Ind1", "AnIn1" });
                        }
                        else if (serviceType == 0x08) // GetDataDirectory
                        {
                            byte[]? invokeId = ReadExact(stream, 4); // InvokeId
                            if (invokeId == null) break;
                            byte[]? refLen = ReadExact(stream, 1);
                            if (refLen == null) break;
                            int len = refLen[0];
                            if (len > 0)
                            {
                                byte[]? refBytes = ReadExact(stream, len);
                                if (refBytes == null) break;
                            }
                            response = BuildDirectoryResponse(0x08, new[] { "stVal", "q", "t", "ctlVal" });
                        }
                        else if (serviceType == 0x09) // EnableReports
                        {
                            byte[]? invokeId = ReadExact(stream, 4); // InvokeId
                            if (invokeId == null) break;
                            byte[]? rcbLenByte = ReadExact(stream, 1);
                            if (rcbLenByte == null) break;
                            int rcbLen = rcbLenByte[0];
                            if (rcbLen > 0) { byte[]? tmp = ReadExact(stream, rcbLen); if (tmp == null) break; }
                            byte[]? dsLenByte = ReadExact(stream, 1);
                            if (dsLenByte == null) break;
                            int dsLen = dsLenByte[0];
                            if (dsLen > 0) { byte[]? tmp = ReadExact(stream, dsLen); if (tmp == null) break; }
                            response = BuildSimpleResponse(0x09, requestRest: invokeId, rest: new byte[] { 0, 0, 0, 1 });
                        }
                        else if (serviceType == 0x0A) // DisableReports
                        {
                            byte[]? invokeId = ReadExact(stream, 4); // InvokeId
                            if (invokeId == null) break;
                            byte[]? rcbLenByte = ReadExact(stream, 1);
                            if (rcbLenByte == null) break;
                            int rcbLen = rcbLenByte[0];
                            if (rcbLen > 0) { byte[]? tmp = ReadExact(stream, rcbLen); if (tmp == null) break; }
                            response = BuildSimpleResponse(0x0A, requestRest: invokeId, rest: new byte[] { 0, 0, 0, 1 });
                        }
                        else if (serviceType == 0x0B) // Select
                        {
                            byte[]? invokeId = ReadExact(stream, 4); // InvokeId
                            if (invokeId == null) break;
                            byte[]? refLenByte = ReadExact(stream, 1);
                            if (refLenByte == null) break;
                            int refLen = refLenByte[0];
                            if (refLen > 0) { byte[]? tmp = ReadExact(stream, refLen); if (tmp == null) break; }
                            response = BuildSimpleResponse(0x0B, requestRest: invokeId, rest: new byte[] { 0, 0, 0, 1 });
                        }
                        else if (serviceType == 0x0C) // Operate
                        {
                            byte[]? invokeId = ReadExact(stream, 4); // InvokeId
                            if (invokeId == null) break;
                            byte[]? refLenByte = ReadExact(stream, 1);
                            if (refLenByte == null) break;
                            int refLen = refLenByte[0];
                            if (refLen > 0) { byte[]? tmp = ReadExact(stream, refLen); if (tmp == null) break; }
                            byte[]? valLenByte = ReadExact(stream, 1);
                            if (valLenByte == null) break;
                            int valLen = valLenByte[0];
                            if (valLen > 0)
                            {
                                byte[]? val = ReadExact(stream, valLen);
                                if (val == null) break;
                                lock (_dataLock) { Buffer.BlockCopy(val, 0, _dataMemory, 0, Math.Min(valLen, _dataMemory.Length)); }
                            }
                            response = BuildSimpleResponse(0x0C, requestRest: invokeId, rest: new byte[] { 0, 0, 0, 1 });
                        }
                        else if (serviceType == 0x0D) // Cancel
                        {
                            byte[]? invokeId = ReadExact(stream, 4); // InvokeId
                            if (invokeId == null) break;
                            byte[]? refLenByte = ReadExact(stream, 1);
                            if (refLenByte == null) break;
                            int refLen = refLenByte[0];
                            if (refLen > 0) { byte[]? tmp = ReadExact(stream, refLen); if (tmp == null) break; }
                            response = BuildSimpleResponse(0x0D, requestRest: invokeId, rest: new byte[] { 0, 0, 0, 1 });
                        }
                        else
                        {
                            // 未知服务 — 跳过
                            break;
                        }

                        stream.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 构建 GetDataValues 响应。
        /// 客户端 SendAndReceive 读 8 字节头，payload = header[6:7] - 6。
        /// 客户端 ParseResponseData 检查 response[0]==0x03，然后从 offset 10 提取数据。
        /// </summary>
        private static byte[] BuildGetDataValuesResponse(byte[] requestRest, byte[] data)
        {
            // 客户端期望:
            // - ResponseHeaderLength = 8 → 读 8 字节
            // - GetResponsePayloadLength: (header[6] << 8) | header[7] - 6
            // - ParseResponseData: response[0] == 0x03 (service type), data from offset 10
            int payloadLen = 2 + data.Length; // 2 bytes extra for client's offset 8-9
            int totalLen = 6 + payloadLen;

            byte[] resp = new byte[8 + payloadLen];

            // 头部
            resp[0] = 0x03; // GetDataValues service (让客户端 ParseResponseData 检查通过)
            resp[1] = requestRest[0]; // InvokeId
            resp[2] = requestRest[1];
            resp[3] = requestRest[2];
            resp[4] = requestRest[3];
            resp[5] = 0x00;
            // 长度字段 (BE)
            resp[6] = (byte)((totalLen >> 8) & 0xFF);
            resp[7] = (byte)(totalLen & 0xFF);

            // 客户端 ParseResponseData: dataLen = response.Length - 10, data from offset 10
            // 响应总长 = 8 + payloadLen, 去掉前 10 字节 = 8 + payloadLen - 10 = payloadLen - 2
            // 我们需要 payloadLen - 2 = data.Length → payloadLen = data.Length + 2 ✓

            // 填充 offset 8-9 (客户端跳过的字节)
            resp[8] = 0x00;
            resp[9] = 0x00;

            // 实际数据从 offset 10 开始
            Buffer.BlockCopy(data, 0, resp, 10, data.Length);

            return resp;
        }

        private static byte[] BuildSetDataValuesResponse(byte[] requestRest)
        {
            // SetDataValues 成功响应: 无数据
            int totalLen = 6;
            byte[] resp = new byte[8];
            resp[0] = 0x04; // SetDataValues service
            resp[1] = requestRest[0];
            resp[2] = requestRest[1];
            resp[3] = requestRest[2];
            resp[4] = requestRest[3];
            resp[5] = 0x00;
            resp[6] = (byte)((totalLen >> 8) & 0xFF);
            resp[7] = (byte)(totalLen & 0xFF);
            return resp;
        }

        private static byte[] BuildDirectoryResponse(byte serviceType, string[] names)
        {
            int dataLen = 2; // count(2)
            foreach (string name in names)
                dataLen += 1 + Encoding.ASCII.GetByteCount(name); // length(1) + name

            int totalLen = 6 + dataLen;
            byte[] resp = new byte[8 + dataLen];

            resp[0] = serviceType;
            resp[1] = 0x00;
            resp[2] = 0x00;
            resp[3] = 0x00;
            resp[4] = 0x00;
            resp[5] = 0x00;
            resp[6] = (byte)((totalLen >> 8) & 0xFF);
            resp[7] = (byte)(totalLen & 0xFF);

            int pos = 8;
            resp[pos++] = (byte)((names.Length >> 8) & 0xFF);
            resp[pos++] = (byte)(names.Length & 0xFF);

            foreach (string name in names)
            {
                byte[] nameBytes = Encoding.ASCII.GetBytes(name);
                resp[pos++] = (byte)nameBytes.Length;
                Buffer.BlockCopy(nameBytes, 0, resp, pos, nameBytes.Length);
                pos += nameBytes.Length;
            }

            return resp;
        }

        private static byte[] BuildSimpleResponse(byte serviceType, byte[]? requestRest = null, byte[]? rest = null)
        {
            int dataLen = rest?.Length ?? 0;
            int totalLen = 6 + dataLen;
            byte[] resp = new byte[8 + dataLen];

            resp[0] = serviceType;
            resp[1] = requestRest?[0] ?? 0x00;
            resp[2] = requestRest?[1] ?? 0x00;
            resp[3] = requestRest?[2] ?? 0x00;
            resp[4] = requestRest?[3] ?? 0x00;
            resp[5] = 0x00;
            resp[6] = (byte)((totalLen >> 8) & 0xFF);
            resp[7] = (byte)(totalLen & 0xFF);

            if (rest != null && rest.Length > 0)
                Buffer.BlockCopy(rest, 0, resp, 8, rest.Length);

            return resp;
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
