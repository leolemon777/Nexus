using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Nexus;

namespace Nexus.Siemens
{
    /// <summary>
    /// Siemens PPI 虚拟 PLC 服务器 — 模拟 S7-200 PLC 的 PPI 协议通信。
    /// 用于集成测试，无需真实硬件。
    /// </summary>
    public class SiemensPpiVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly int _port;
        private int _connectionCount;

        // ── 数据存储 ─────────────────────────────
        private readonly byte[] _v  = new byte[65536];  // V 区 (0x85) — 通用存储
        private readonly byte[] _i  = new byte[256];    // I 区 (0x81) — 输入
        private readonly byte[] _q  = new byte[256];    // Q 区 (0x82) — 输出
        private readonly byte[] _m  = new byte[256];    // M 区 (0x83) — 标志位
        private readonly byte[] _sm = new byte[256];    // SM 区 (0x86) — 特殊标志
        private readonly byte[] _c  = new byte[256];    // C 区 (0x1C) — 计数器
        private readonly byte[] _s  = new byte[256];    // S 区 (0x84) — SCR
        private readonly object _dataLock = new object();

        public SiemensPpiVirtualServer(int port) => _port = port;

        public bool IsRunning => _running;

        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        // ── 数据设置 API ─────────────────────────

        public void SetV(int offset, byte[] data)
        {
            lock (_dataLock)
            {
                Array.Copy(data, 0, _v, offset, Math.Min(data.Length, _v.Length - offset));
            }
        }

        public void SetVWord(int offset, ushort value)
        {
            lock (_dataLock)
            {
                _v[offset] = (byte)(value >> 8);
                _v[offset + 1] = (byte)(value & 0xFF);
            }
        }

        public void SetI(int offset, byte value)
        {
            lock (_dataLock) { _i[offset] = value; }
        }

        public void SetQ(int offset, byte value)
        {
            lock (_dataLock) { _q[offset] = value; }
        }

        public void SetM(int offset, byte value)
        {
            lock (_dataLock) { _m[offset] = value; }
        }

        // ── 服务器生命周期 ──────────────────────

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            _listener?.Stop();
            _acceptThread?.Join(2000);
        }

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
                catch { if (!_running) break; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var ns = client.GetStream())
                {
                    ns.ReadTimeout = 5000;
                    ns.WriteTimeout = 5000;

                    while (_running && client.Connected)
                    {
                        var frame = ReadPpiFrame(ns);
                        if (frame == null) break;

                        if (!VerifyBcc(frame))
                        {
                            // BCC 校验失败，发送错误响应
                            byte[] errResp = BuildErrorPpiResponse(frame, 0x03);
                            ns.Write(errResp, 0, errResp.Length);
                            continue;
                        }

                        byte functionCode = frame[7];
                        byte[] data = ExtractPpiData(frame);

                        byte[] response;
                        if (functionCode == 0x01) // 读取
                            response = ProcessRead(frame, data);
                        else if (functionCode == 0x02) // 写入
                            response = ProcessWrite(frame, data);
                        else
                            response = BuildErrorPpiResponse(frame, 0x03);

                        ns.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
        }

        // ── PPI 帧解析 ──────────────────────────

        private static byte[]? ReadPpiFrame(NetworkStream ns)
        {
            // 读取帧头 [0x68][Len][Len][0x68]
            byte[]? header = ReadExact(ns, 4);
            if (header == null) return null;
            if (header[0] != 0x68 || header[3] != 0x68) return null;

            int lenField = header[1];
            if (header[2] != lenField) return null;

            // 读取剩余部分: 控制+地址+功能码+数据+BCC+0x16
            int remaining = lenField + 2; // +BCC+0x16
            byte[]? rest = ReadExact(ns, remaining);
            if (rest == null) return null;

            byte[] frame = new byte[4 + remaining];
            Array.Copy(header, 0, frame, 0, 4);
            Array.Copy(rest, 0, frame, 4, remaining);
            return frame;
        }

        private static bool VerifyBcc(byte[] frame)
        {
            if (frame.Length < 9) return false;
            if (frame[frame.Length - 1] != 0x16) return false;
            byte bcc = 0;
            for (int i = 4; i < frame.Length - 2; i++) bcc ^= frame[i];
            return bcc == frame[frame.Length - 2];
        }

        private static byte[] ExtractPpiData(byte[] frame)
        {
            int lenField = frame[1];
            int dataLen = lenField - 4; // 减去控制+从站+主站+功能码
            if (dataLen <= 0) return Array.Empty<byte>();
            byte[] data = new byte[dataLen];
            Array.Copy(frame, 8, data, 0, dataLen);
            return data;
        }

        // ── 读取处理 ──────────────────────────────

        private byte[] ProcessRead(byte[] request, byte[] data)
        {
            // 请求数据: [0x01,0x00,length,areaCode,addrHi,addrLo,bitOffset]
            if (data.Length < 7)
                return BuildErrorPpiResponse(request, 0x03);

            int length = data[2];
            byte areaCode = data[3];
            int addr = (data[4] << 8) | data[5];
            int bitOffset = data[6];

            if (length == 0 || length > 255)
                return BuildErrorPpiResponse(request, 0x03);

            byte[]? src = GetAreaByCode(areaCode);
            if (src == null)
                return BuildErrorPpiResponse(request, 0x03);

            // 边界检查
            if (addr + length > src.Length)
                return BuildErrorPpiResponse(request, 0x03);

            // 构建响应数据: [0xFF, data...]
            byte[] respData = new byte[1 + length];
            respData[0] = 0xFF; // 成功标志
            lock (_dataLock)
            {
                Array.Copy(src, addr, respData, 1, length);
            }

            return BuildPpiResponse(request, 0x00, respData);
        }

        // ── 写入处理 ──────────────────────────────

        private byte[] ProcessWrite(byte[] request, byte[] data)
        {
            // 请求数据: [0x01,0x00,length,areaCode,addrHi,addrLo,bitOffset,data...]
            if (data.Length < 7)
                return BuildErrorPpiResponse(request, 0x03);

            int length = data[2];
            byte areaCode = data[3];
            int addr = (data[4] << 8) | data[5];
            int bitOffset = data[6];

            if (length == 0 || data.Length < 7 + length)
                return BuildErrorPpiResponse(request, 0x03);

            byte[]? src = GetAreaByCode(areaCode);
            if (src == null)
                return BuildErrorPpiResponse(request, 0x03);

            // 边界检查
            if (addr + length > src.Length)
                return BuildErrorPpiResponse(request, 0x03);

            lock (_dataLock)
            {
                Array.Copy(data, 7, src, addr, length);
            }

            // 写入成功响应: [0xFF]
            byte[] respData = new byte[] { 0xFF };
            return BuildPpiResponse(request, 0x00, respData);
        }

        // ── 区域映射 ──────────────────────────────

        private byte[]? GetAreaByCode(byte areaCode)
        {
            switch (areaCode)
            {
                case 0x85: return _v;   // V 区
                case 0x81: return _i;   // I 区
                case 0x82: return _q;   // Q 区
                case 0x83: return _m;   // M 区
                case 0x86: return _sm;  // SM 区
                case 0x1C: return _c;   // C 区
                case 0x84: return _s;   // S 区
                default: return null;
            }
        }

        // ── PPI 帧构建 ──────────────────────────

        private static byte[] BuildPpiResponse(byte[] request, byte functionCode, byte[] data)
        {
            byte control = 0x00;
            byte slaveAddr = request[5];  // 请求中的从站地址
            byte masterAddr = request[6]; // 请求中的主站地址
            int dataLen = data?.Length ?? 0;
            int lenField = 4 + dataLen;
            byte[] frame = new byte[4 + lenField + 2];
            frame[0] = 0x68;
            frame[1] = (byte)lenField;
            frame[2] = (byte)lenField;
            frame[3] = 0x68;
            frame[4] = control;
            frame[5] = slaveAddr;
            frame[6] = masterAddr;
            frame[7] = functionCode;
            if (dataLen > 0 && data != null) Buffer.BlockCopy(data, 0, frame, 8, dataLen);
            byte bcc = 0;
            for (int i = 4; i < 8 + dataLen; i++) bcc ^= frame[i];
            frame[8 + dataLen] = bcc;
            frame[9 + dataLen] = 0x16;
            return frame;
        }

        private static byte[] BuildErrorPpiResponse(byte[] request, byte errorCode)
        {
            return BuildPpiResponse(request, errorCode, Array.Empty<byte>());
        }

        private static byte[]? ReadExact(NetworkStream ns, int count)
        {
            var buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = ns.Read(buf, read, count - read);
                if (n == 0) return null;
                read += n;
            }
            return buf;
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
