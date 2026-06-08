using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.GeSrtp
{
    /// <summary>
    /// GE SRTP 虚拟 PLC 服务器 — 模拟 SRTP 协议，用于无硬件测试。
    /// <para>支持区域: R(寄存器), AI(模拟输入), AQ(模拟输出), %I(输入), %Q(输出), %M(内存)</para>
    /// </summary>
    public class GeSrtpVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        // 字区域存储
        private readonly byte[] _rArea = new byte[131072];  // R: 65536 字
        private readonly byte[] _aiArea = new byte[65536];  // AI: 32768 字
        private readonly byte[] _aqArea = new byte[65536];  // AQ: 32768 字
        private readonly byte[] _iArea = new byte[65536];   // I: 65536 字节 (位寻址)
        private readonly byte[] _qArea = new byte[65536];   // Q: 65536 字节
        private readonly byte[] _mArea = new byte[65536];   // M: 65536 字节

        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly object _lock = new object();
        private ushort _sessionId;

        public int Port { get; }
        public bool IsRunning => _running;

        public GeSrtpVirtualServer(int port = 18245)
        {
            Port = port;
        }

        // ── 数据设置 API ────────────────────────────

        public void SetRWord(int wordIndex, ushort value)
        {
            lock (_lock)
            {
                int off = wordIndex * 2;
                if (off >= 0 && off + 1 < _rArea.Length)
                {
                    _rArea[off] = (byte)(value >> 8);
                    _rArea[off + 1] = (byte)(value & 0xFF);
                }
            }
        }

        public void SetRBytes(int byteOffset, byte[] data)
        {
            lock (_lock)
            {
                int off = Math.Min(byteOffset, _rArea.Length - data.Length);
                Buffer.BlockCopy(data, 0, _rArea, off, data.Length);
            }
        }

        public void SetMByte(int byteIndex, byte value)
        {
            lock (_lock)
            {
                if (byteIndex >= 0 && byteIndex < _mArea.Length)
                    _mArea[byteIndex] = value;
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
                foreach (var c in _clients) { try { c.Close(); } catch { } }
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
                    // GE SRTP 需要先完成会话建立
                    // 第一次连接收到初始化请求，回复 session 确认
                    while (_running && client.Connected)
                    {
                        var header = ReadExact(stream, 8);
                        if (header == null) break;

                        // SRTP header: ServiceType(1) + Channel(1) + Reserved(2) + SessionId(2) + Length(2)
                        int dataLen = (header[6] << 8) | header[7];
                        byte[]? data = null;
                        if (dataLen > 0) data = ReadExact(stream, dataLen);

                        byte[]? response = ProcessRequest(header, data);
                        if (response != null)
                            stream.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
            finally { lock (_lock) { _clients.Remove(client); } }
        }

        private byte[]? ProcessRequest(byte[] header, byte[]? data)
        {
            byte serviceType = header[0];
            ushort sessionId = (ushort)((header[4] << 8) | header[5]);

            // 首次连接: ServiceType=0 → 返回 session 确认
            if (serviceType == 0x00)
            {
                _sessionId++;
                var resp = new byte[10];
                resp[0] = 0x01; // 确认
                resp[4] = (byte)(_sessionId >> 8);
                resp[5] = (byte)(_sessionId & 0xFF);
                resp[6] = 0; resp[7] = 2;
                resp[8] = 0x0F; // 版本
                return resp;
            }

            if (serviceType == 0x01 && data != null && data.Length >= 6)
            {
                // 读请求
                byte memType = data[0];
                int offset = (data[2] << 8) | data[3];
                int count = (data[4] << 8) | data[5];
                return ProcessRead(header, memType, offset, count);
            }

            if (serviceType == 0x02 && data != null && data.Length >= 4)
            {
                // 写请求
                byte memType = data[0];
                int offset = (data[2] << 8) | data[3];
                byte[] writeData = new byte[data.Length - 4];
                Buffer.BlockCopy(data, 4, writeData, 0, writeData.Length);
                return ProcessWrite(header, memType, offset, writeData);
            }

            return null;
        }

        private byte[]? ProcessRead(byte[] header, byte memType, int offset, int count)
        {
            var storage = GetStorage(memType);
            if (storage == null) return BuildErrorResponse(header);

            lock (_storage)
            {
                int byteLen = count * 2;
                if (offset * 2 + byteLen > storage.Length)
                    byteLen = storage.Length - offset * 2;
                if (byteLen < 0) byteLen = 0;

                byte[] data = new byte[byteLen];
                if (byteLen > 0)
                    Buffer.BlockCopy(storage, offset * 2, data, 0, byteLen);

                return BuildReadResponse(header, data);
            }
        }

        private byte[]? ProcessWrite(byte[] header, byte memType, int offset, byte[] data)
        {
            var storage = GetStorage(memType);
            if (storage == null) return BuildErrorResponse(header);

            lock (_storage)
            {
                int off = offset * 2;
                if (off >= 0 && off + data.Length <= storage.Length)
                    Buffer.BlockCopy(data, 0, storage, off, data.Length);
            }

            // 写成功响应
            var resp = new byte[8];
            resp[4] = header[4]; resp[5] = header[5];
            resp[6] = 0; resp[7] = 0;
            return resp;
        }

        private byte[] BuildReadResponse(byte[] header, byte[] data)
        {
            int totalLen = 8 + data.Length;
            var resp = new byte[totalLen];
            resp[0] = 0x01; // 读响应
            resp[4] = header[4]; resp[5] = header[5]; // SessionId
            resp[6] = (byte)(data.Length >> 8); resp[7] = (byte)(data.Length & 0xFF);
            if (data.Length > 0)
                Buffer.BlockCopy(data, 0, resp, 8, data.Length);
            return resp;
        }

        private byte[] BuildErrorResponse(byte[] header)
        {
            var resp = new byte[8];
            resp[0] = 0xFF; // Error
            resp[4] = header[4]; resp[5] = header[5];
            return resp;
        }

        private byte[]? GetStorage(byte memType) => memType switch
        {
            0x08 => _rArea,   // R
            0x0A => _aiArea,  // AI
            0x0C => _aqArea,  // AQ
            0x10 => _iArea,   // I
            0x12 => _qArea,   // Q
            0x14 => _mArea,   // M
            _ => null
        };

        private readonly object _storage = new object();

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

        public void Dispose() => Stop();
    }
}
