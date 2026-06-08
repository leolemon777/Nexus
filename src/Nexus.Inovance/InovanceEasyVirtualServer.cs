using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.Inovance
{
    /// <summary>
    /// 汇川 EasyNet 虚拟 PLC 服务器 — 模拟 Easy 系列协议，用于无硬件测试。
    /// 内存模型: D 区 (8192 字) + M 区 (8192 点) + X 区 (512 点) + Y 区 (512 点)。
    /// </summary>
    public class InovanceEasyVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        // 内存区域 — 按字存储
        private readonly byte[] _dArea = new byte[16384]; // D 区 8192 字 = 16384 字节
        private readonly byte[] _mArea = new byte[1024];  // M 区 8192 点 = 1024 字节
        private readonly byte[] _xArea = new byte[64];    // X 区 512 点 = 64 字节
        private readonly byte[] _yArea = new byte[64];    // Y 区 512 点 = 64 字节
        private readonly byte[] _rArea = new byte[16384]; // R 区 8192 字
        private readonly byte[] _wArea = new byte[16384]; // W 区 8192 字
        private readonly byte[] _bArea = new byte[1024];  // B 区 8192 点

        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly object _lock = new object();

        public int Port { get; }
        public bool IsRunning => _running;

        /// <summary>EasyNet 协议固定帧头长度。</summary>
        private const int FrameHeader = 22;

        public InovanceEasyVirtualServer(int port = 5020)
        {
            Port = port;
        }

        // ── 数据设置 API ────────────────────────────

        /// <summary>设置 D 区指定字的值（小端序）。</summary>
        public void SetDWord(int wordIndex, ushort value)
        {
            if (wordIndex < 0 || wordIndex * 2 + 1 >= _dArea.Length) return;
            lock (_lock)
            {
                _dArea[wordIndex * 2] = (byte)(value & 0xFF);
                _dArea[wordIndex * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }
        }

        /// <summary>设置 D 区指定字节。</summary>
        public void SetDBytes(int byteOffset, byte[] data)
        {
            lock (_lock)
            {
                Buffer.BlockCopy(data, 0, _dArea, Math.Min(byteOffset, _dArea.Length - data.Length), data.Length);
            }
        }

        /// <summary>设置 M 区指定位。</summary>
        public void SetMBit(int bitIndex, bool value)
        {
            lock (_lock)
            {
                if (bitIndex >= 0 && bitIndex < 8192)
                {
                    if (value)
                        _mArea[bitIndex / 8] |= (byte)(1 << (bitIndex % 8));
                    else
                        _mArea[bitIndex / 8] &= (byte)~(1 << (bitIndex % 8));
                }
            }
        }

        /// <summary>设置 X 区指定位。</summary>
        public void SetXBit(int bitIndex, bool value)
        {
            lock (_lock)
            {
                if (bitIndex >= 0 && bitIndex < 512)
                {
                    if (value)
                        _xArea[bitIndex / 8] |= (byte)(1 << (bitIndex % 8));
                    else
                        _xArea[bitIndex / 8] &= (byte)~(1 << (bitIndex % 8));
                }
            }
        }

        /// <summary>设置 Y 区指定位。</summary>
        public void SetYBit(int bitIndex, bool value)
        {
            lock (_lock)
            {
                if (bitIndex >= 0 && bitIndex < 512)
                {
                    if (value)
                        _yArea[bitIndex / 8] |= (byte)(1 << (bitIndex % 8));
                    else
                        _yArea[bitIndex / 8] &= (byte)~(1 << (bitIndex % 8));
                }
            }
        }

        // ── 服务器控制 ────────────────────────────

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            lock (_lock)
            {
                foreach (var c in _clients)
                {
                    try { c.Close(); } catch { }
                }
                _clients.Clear();
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener!.AcceptTcpClient();
                    lock (_lock) _clients.Add(client);
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
                        // 读取长度头 (2 字节)
                        byte[] lenBuf = ReadExact(stream, 2);
                        if (lenBuf == null) break;

                        int totalLen = lenBuf[0] | (lenBuf[1] << 8);
                        if (totalLen < FrameHeader) break;

                        // 读取剩余数据
                        byte[] restBuf = ReadExact(stream, totalLen - 2);
                        if (restBuf == null) break;

                        // 组合完整帧
                        byte[] frame = new byte[totalLen];
                        frame[0] = lenBuf[0];
                        frame[1] = lenBuf[1];
                        Buffer.BlockCopy(restBuf, 0, frame, 2, restBuf.Length);

                        byte[]? response = ProcessFrame(frame);
                        if (response != null)
                        {
                            stream.Write(response, 0, response.Length);
                        }
                    }
                }
            }
            catch { }
            finally
            {
                lock (_lock) { _clients.Remove(client); }
            }
        }

        // ── 帧处理 ──────────────────────────────────

        private byte[]? ProcessFrame(byte[] frame)
        {
            if (frame.Length < FrameHeader) return null;

            byte command = frame[8];

            // 提取地址编码 (4 字节)
            byte[] addrBytes = new byte[4];
            Buffer.BlockCopy(frame, 14, addrBytes, 0, 4);

            // 请求 bit 数
            int bitCount = frame[18] | (frame[19] << 8) | (frame[20] << 16);

            switch (command)
            {
                case 0x01: // 读取
                    return ProcessRead(addrBytes, bitCount);
                case 0x02: // 写入
                    return ProcessWrite(addrBytes, bitCount, frame);
                default:
                    return BuildErrorResponse(10001);
            }
        }

        private byte[] ProcessRead(byte[] addrBytes, int bitCount)
        {
            byte[]? storage = ResolveStorage(addrBytes, out int bitOffset);
            if (storage == null)
                return BuildErrorResponse(10002);

            // 将 bit 数转换为字节数
            int byteCount = (bitCount + 7) / 8;

            byte[] response = new byte[FrameHeader + byteCount];
            // 成功响应头
            response[0] = (byte)(response.Length & 0xFF);
            response[1] = (byte)((response.Length >> 8) & 0xFF);
            response[2] = 0x01; response[3] = 0x03;
            response[4] = 0x01;
            response[8] = 0x00; // 成功标志

            lock (_lock)
            {
                // 读取数据
                int srcByteOffset = bitOffset / 8;
                int copyLen = Math.Min(byteCount, storage.Length - srcByteOffset);
                if (copyLen > 0 && srcByteOffset < storage.Length)
                {
                    Buffer.BlockCopy(storage, srcByteOffset, response, FrameHeader, copyLen);
                }
            }

            return response;
        }

        private byte[] ProcessWrite(byte[] addrBytes, int bitCount, byte[] frame)
        {
            byte[]? storage = ResolveStorage(addrBytes, out int bitOffset);
            if (storage == null)
                return BuildErrorResponse(10002);

            int dataOffset = FrameHeader;
            if (frame.Length <= dataOffset)
                return BuildErrorResponse(10003);

            int dataLen = frame.Length - dataOffset;

            lock (_lock)
            {
                int dstByteOffset = bitOffset / 8;
                int copyLen = Math.Min(dataLen, storage.Length - dstByteOffset);
                if (copyLen > 0 && dstByteOffset < storage.Length)
                {
                    Buffer.BlockCopy(frame, dataOffset, storage, dstByteOffset, copyLen);
                }
            }

            // 成功写入响应
            byte[] response = new byte[FrameHeader];
            response[0] = (byte)(response.Length & 0xFF);
            response[1] = (byte)((response.Length >> 8) & 0xFF);
            response[2] = 0x01; response[3] = 0x03;
            response[4] = 0x01;
            response[8] = 0x00; // 成功

            return response;
        }

        /// <summary>
        /// 根据地址编码解析存储区域和位偏移。
        /// </summary>
        private byte[]? ResolveStorage(byte[] addrBytes, out int bitOffset)
        {
            byte typeCode = (byte)(addrBytes[2] & 0xF0);
            int addrValue = addrBytes[0] | (addrBytes[1] << 8) | ((addrBytes[2] & 0x0F) << 16);

            switch (typeCode)
            {
                case 0x40: // D 区
                    bitOffset = addrValue; // addrValue = wordNo * 16 + bitNo
                    return _dArea;

                case 0x10: // M 或 S 区
                    if (addrBytes[2] >= 0x80)
                    {
                        // S 区 — 复用 M 区存储
                        bitOffset = addrValue - 0x80000;
                        return _mArea;
                    }
                    bitOffset = addrValue;
                    return _mArea;

                case 0x00: // X 或 Y 区
                    if (addrValue >= 0x80000)
                    {
                        bitOffset = addrValue - 0x80000;
                        return _yArea;
                    }
                    bitOffset = addrValue;
                    return _xArea;

                case 0x50: // R 区
                    bitOffset = addrValue;
                    return _rArea;

                case 0x60: // W 区
                    bitOffset = addrValue;
                    return _wArea;

                case 0x20: // B 区
                    bitOffset = addrValue;
                    return _bArea;

                case 0xF0: // U 系列（扩展地址）— 复用 D 区
                    bitOffset = addrValue;
                    return _dArea;

                default:
                    bitOffset = 0;
                    return null;
            }
        }

        /// <summary>构建错误响应帧。</summary>
        private byte[] BuildErrorResponse(int errorCode)
        {
            byte[] response = new byte[FrameHeader];
            response[0] = (byte)(response.Length & 0xFF);
            response[1] = (byte)((response.Length >> 8) & 0xFF);
            response[2] = 0x01; response[3] = 0x03;
            response[4] = 0x01;
            response[8] = 0x0F; // 错误标志
            response[10] = (byte)(errorCode & 0xFF);
            response[11] = (byte)((errorCode >> 8) & 0xFF);
            return response;
        }

        private static byte[]? ReadExact(NetworkStream stream, int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buf, offset, count - offset);
                if (read == 0) return null;
                offset += read;
            }
            return buf;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
