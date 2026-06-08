using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Nexus;

namespace Nexus.Siemens
{
    /// <summary>
    /// Siemens Fetch/Write 虚拟 PLC 服务器 — 用于测试。
    /// </summary>
    public class SiemensFetchWriteVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly int _port;

        // ── 数据存储 ─────────────────────────────
        private readonly byte[] _db = new byte[65536];   // DB 块数据
        private readonly byte[] _mk = new byte[65536];   // M 区
        private readonly byte[] _pe = new byte[65536];   // I 区
        private readonly byte[] _pa = new byte[65536];   // Q 区
        private readonly byte[] _tm = new byte[65536];   // T 区
        private readonly byte[] _ct = new byte[65536];   // C 区
        private readonly object _dataLock = new object();

        public SiemensFetchWriteVirtualServer(int port) => _port = port;

        public bool IsRunning => _running;

        // ── 数据设置 API ─────────────────────────

        public void SetDB(int dbOffset, byte[] data)
        {
            lock (_dataLock)
            {
                Array.Copy(data, 0, _db, dbOffset, Math.Min(data.Length, _db.Length - dbOffset));
            }
        }

        public void SetDBWord(int dbOffset, ushort value)
        {
            lock (_dataLock)
            {
                _db[dbOffset] = (byte)(value >> 8);
                _db[dbOffset + 1] = (byte)(value & 0xFF);
            }
        }

        public void SetM(int offset, byte value)
        {
            lock (_dataLock) { _mk[offset] = value; }
        }

        public void SetI(int offset, byte value)
        {
            lock (_dataLock) { _pe[offset] = value; }
        }

        public void SetQ(int offset, byte value)
        {
            lock (_dataLock) { _pa[offset] = value; }
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
                        // 读取 16 字节请求头
                        var header = ReadExact(ns, 16);
                        if (header == null) break;

                        byte[] response;
                        if (header[5] == 0x05) // 读取
                            response = ProcessRead(header);
                        else if (header[5] == 0x06) // 写入
                            response = ProcessWrite(header, ns);
                        else
                            response = BuildErrorResponse(header, 0xFF);

                        ns.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
        }

        private byte[] ProcessRead(byte[] header)
        {
            byte areaCode = header[8];
            ushort dbNumber = header[9];
            int startAddr = (header[10] << 8) | header[11];
            int count = (header[12] << 8) | header[13];

            byte[] src;
            lock (_dataLock)
            {
                src = areaCode switch
                {
                    1 => _db,   // DB
                    2 => _mk,  // M
                    3 => _pe,  // I
                    4 => _pa,  // Q
                    7 => _tm,  // T
                    6 => _ct,  // C
                    _ => _mk,
                };

                // 计算实际字节数
                int byteCount = count;
                if (areaCode == 7 || areaCode == 6) // T/C: count 是字数
                    byteCount = count * 2;

                var resp = new byte[16 + byteCount];
                // 复制请求头前 8 字节（协议标识），然后设置响应字段
                Array.Copy(header, 0, resp, 0, 8);
                resp[8] = 0x00; // 成功：错误码为 0
                resp[9] = header[9]; // DB 编号回传
                resp[10] = header[10]; // 地址回传
                resp[11] = header[11];
                resp[12] = (byte)(byteCount >> 8);
                resp[13] = (byte)(byteCount & 0xFF);
                resp[14] = 0xFF;
                resp[15] = 0x02;
                // 复制数据
                Array.Copy(src, startAddr, resp, 16, Math.Min(byteCount, src.Length - startAddr));
                return resp;
            }
        }

        private byte[] ProcessWrite(byte[] header, NetworkStream ns)
        {
            byte areaCode = header[8];
            int startAddr = (header[10] << 8) | header[11];
            int dataLen = (header[12] << 8) | header[13];

            // 读取数据部分
            var data = dataLen > 0 ? ReadExact(ns, dataLen) : new byte[0];
            if (data == null && dataLen > 0)
                return BuildErrorResponse(header, 0x04);

            byte[] src;
            lock (_dataLock)
            {
                src = areaCode switch
                {
                    1 => _db,
                    2 => _mk,
                    3 => _pe,
                    4 => _pa,
                    7 => _tm,
                    6 => _ct,
                    _ => _mk,
                };

                if (data != null && data.Length > 0)
                {
                    Array.Copy(data, 0, src, startAddr, Math.Min(data.Length, src.Length - startAddr));
                }
            }

            // 写入成功响应（无数据）
            var resp = new byte[16];
            Array.Copy(header, 0, resp, 0, 8);
            resp[8] = 0x00; // 成功：错误码为 0
            resp[9] = header[9];
            resp[10] = header[10];
            resp[11] = header[11];
            resp[12] = 0x00;
            resp[13] = 0x00;
            resp[14] = 0xFF;
            resp[15] = 0x02;
            return resp;
        }

        private static byte[] BuildErrorResponse(byte[] header, byte errorCode)
        {
            var resp = new byte[16];
            Array.Copy(header, 0, resp, 0, 8);
            resp[8] = errorCode;
            resp[14] = 0xFF;
            resp[15] = 0x02;
            return resp;
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
