using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Nexus;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// 三菱 A1E 虚拟 PLC 服务器 — 用于测试。
    /// <para>支持字读写（D/R/W/TN/CN）、位读写（M/X/Y/S/B/F/TS/TC/CS/CC）、
    /// 以及位类型区域的字读取（M 区按字读取时自动打包 16 位为 1 字）。</para>
    /// </summary>
    public class MelsecA1EVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly int _port;
        private int _connectionCount;

        // ── 字类型存储（大端序，每字 2 字节，byte 偏移 = address * 2）──
        private readonly byte[] _d  = new byte[8192]; // D 数据寄存器
        private readonly byte[] _r  = new byte[8192]; // R 文件寄存器
        private readonly byte[] _w  = new byte[8192]; // W 链接寄存器
        private readonly byte[] _tn = new byte[8192]; // TN 定时器当前值
        private readonly byte[] _cn = new byte[8192]; // CN 计数器当前值

        // ── 位类型存储（每个字节代表一个位，0 或 1）──
        private readonly byte[] _m  = new byte[8192]; // M 中间继电器
        private readonly byte[] _x  = new byte[1024]; // X 输入
        private readonly byte[] _y  = new byte[1024]; // Y 输出
        private readonly byte[] _s  = new byte[8192]; // S 状态
        private readonly byte[] _b  = new byte[8192]; // B 连接继电器
        private readonly byte[] _f  = new byte[8192]; // F 报警器
        private readonly byte[] _ts = new byte[8192]; // TS 定时器触点
        private readonly byte[] _tc = new byte[8192]; // TC 定时器线圈
        private readonly byte[] _cs = new byte[8192]; // CS 计数器触点
        private readonly byte[] _cc = new byte[8192]; // CC 计数器线圈

        private readonly object _dataLock = new object();

        public MelsecA1EVirtualServer(int port) => _port = port;
        public bool IsRunning => _running;
        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        // ═══════════════════════════════════════════
        //  数据设置 API（供测试使用）
        // ═══════════════════════════════════════════

        /// <summary>设置 D 区域字值（大端序）。</summary>
        public void SetDWord(int dAddr, ushort value)
        {
            lock (_dataLock)
            {
                int offset = dAddr * 2;
                if (offset + 1 < _d.Length)
                {
                    _d[offset]     = (byte)(value >> 8);
                    _d[offset + 1] = (byte)(value & 0xFF);
                }
            }
        }

        /// <summary>设置 D 区域多个字节。</summary>
        public void SetDBytes(int dAddr, byte[] data)
        {
            lock (_dataLock)
            {
                int offset = dAddr * 2;
                Array.Copy(data, 0, _d, offset, Math.Min(data.Length, _d.Length - offset));
            }
        }

        /// <summary>设置 M 区域位值（0 或 1）。</summary>
        public void SetM(int addr, byte value)
        {
            lock (_dataLock) { if (addr < _m.Length) _m[addr] = value; }
        }

        /// <summary>设置 X 区域位值。</summary>
        public void SetX(int addr, byte value)
        {
            lock (_dataLock) { if (addr < _x.Length) _x[addr] = value; }
        }

        /// <summary>设置 Y 区域位值。</summary>
        public void SetY(int addr, byte value)
        {
            lock (_dataLock) { if (addr < _y.Length) _y[addr] = value; }
        }

        /// <summary>设置 R 区域字值。</summary>
        public void SetRWord(int rAddr, ushort value)
        {
            lock (_dataLock)
            {
                int offset = rAddr * 2;
                if (offset + 1 < _r.Length)
                {
                    _r[offset]     = (byte)(value >> 8);
                    _r[offset + 1] = (byte)(value & 0xFF);
                }
            }
        }

        // ═══════════════════════════════════════════
        //  服务器生命周期
        // ═══════════════════════════════════════════

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
                        // 读取 12 字节命令头
                        var header = ReadExact(ns, 12);
                        if (header == null) break;

                        byte[] response = ProcessCommand(header, ns);
                        ns.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════
        //  命令处理
        // ═══════════════════════════════════════════

        private byte[] ProcessCommand(byte[] cmd, NetworkStream ns)
        {
            byte subCmd = cmd[0];
            int address = cmd[4] | (cmd[5] << 8) | (cmd[6] << 16) | (cmd[7] << 24);
            ushort dataCode = (ushort)(cmd[8] | (cmd[9] << 8));
            int length = cmd[10] | (cmd[11] << 8);
            if (length == 0 && (subCmd == 0 || subCmd == 1)) length = 256; // 0 = 256

            var (storage, isWordType) = GetStorage(dataCode);
            if (storage == null) return BuildErrorResponse(subCmd, 0x02);

            switch (subCmd)
            {
                case 0: // 位读取
                    return ProcessBitRead(storage, address, length, subCmd);
                case 1: // 字读取
                    return isWordType
                        ? ProcessWordRead(storage, address, length, subCmd)
                        : ProcessWordReadFromBits(storage, address, length, subCmd);
                case 2: // 位写入
                    int packedLen = (length + 1) / 2;
                    var bitData = ReadExact(ns, packedLen);
                    if (bitData == null) return BuildErrorResponse(subCmd, 0x04);
                    return ProcessBitWrite(storage, address, length, bitData, subCmd);
                case 3: // 字写入
                    int dataBytes = length * 2;
                    var wordData = dataBytes > 0 ? ReadExact(ns, dataBytes) : new byte[0];
                    if (wordData == null && dataBytes > 0) return BuildErrorResponse(subCmd, 0x04);
                    return isWordType
                        ? ProcessWordWrite(storage, address, length, wordData ?? new byte[0], subCmd)
                        : ProcessWordWriteToBits(storage, address, length, wordData ?? new byte[0], subCmd);
                default:
                    return BuildErrorResponse(subCmd, 0x01);
            }
        }

        // ── 字读取（字类型存储）────────────────────

        private byte[] ProcessWordRead(byte[] storage, int address, int wordCount, byte subCmd)
        {
            int byteOffset = address * 2;
            int byteCount = wordCount * 2;
            lock (_dataLock)
            {
                var resp = new byte[2 + byteCount];
                resp[0] = subCmd;
                resp[1] = 0; // 成功
                int copyLen = Math.Min(byteCount, Math.Max(0, storage.Length - byteOffset));
                if (copyLen > 0)
                    Array.Copy(storage, byteOffset, resp, 2, copyLen);
                return resp;
            }
        }

        // ── 字读取（位类型存储 → 16 位打包为 1 字）──

        private byte[] ProcessWordReadFromBits(byte[] storage, int address, int wordCount, byte subCmd)
        {
            int byteCount = wordCount * 2;
            var resp = new byte[2 + byteCount];
            resp[0] = subCmd;
            resp[1] = 0;

            lock (_dataLock)
            {
                for (int w = 0; w < wordCount; w++)
                {
                    ushort word = 0;
                    for (int b = 0; b < 16; b++)
                    {
                        int bitAddr = address + w * 16 + b;
                        if (bitAddr < storage.Length && storage[bitAddr] != 0)
                            word |= (ushort)(1 << b);
                    }
                    // 大端序：高字节在前
                    resp[2 + w * 2]     = (byte)(word >> 8);
                    resp[2 + w * 2 + 1] = (byte)(word & 0xFF);
                }
            }
            return resp;
        }

        // ── 位读取（2 位打包为 1 字节）──────────────

        private byte[] ProcessBitRead(byte[] storage, int address, int bitCount, byte subCmd)
        {
            int packedBytes = (bitCount + 1) / 2;
            var resp = new byte[2 + packedBytes];
            resp[0] = subCmd;
            resp[1] = 0;

            lock (_dataLock)
            {
                for (int i = 0; i < bitCount; i++)
                {
                    int bitAddr = address + i;
                    byte bitVal = bitAddr < storage.Length ? storage[bitAddr] : (byte)0;
                    if (bitVal != 0)
                    {
                        int respIdx = i / 2;
                        if (i % 2 == 0)
                            resp[2 + respIdx] |= 0x10; // 偶数位 → 高半字节
                        else
                            resp[2 + respIdx] |= 0x01; // 奇数位 → 低半字节
                    }
                }
            }
            return resp;
        }

        // ── 字写入（字类型存储）────────────────────

        private byte[] ProcessWordWrite(byte[] storage, int address, int wordCount, byte[] data, byte subCmd)
        {
            lock (_dataLock)
            {
                int byteOffset = address * 2;
                if (data != null && data.Length > 0)
                    Array.Copy(data, 0, storage, byteOffset, Math.Min(data.Length, storage.Length - byteOffset));
            }
            return BuildSuccessResponse(subCmd);
        }

        // ── 字写入（位类型存储）────────────────────

        private byte[] ProcessWordWriteToBits(byte[] storage, int address, int wordCount, byte[] data, byte subCmd)
        {
            lock (_dataLock)
            {
                for (int w = 0; w < wordCount; w++)
                {
                    ushort word = (ushort)((data[w * 2] << 8) | data[w * 2 + 1]);
                    for (int b = 0; b < 16; b++)
                    {
                        int bitAddr = address + w * 16 + b;
                        if (bitAddr < storage.Length)
                            storage[bitAddr] = (byte)((word >> b) & 1);
                    }
                }
            }
            return BuildSuccessResponse(subCmd);
        }

        // ── 位写入（解包后写入）────────────────────

        private byte[] ProcessBitWrite(byte[] storage, int address, int bitCount, byte[] packedData, byte subCmd)
        {
            lock (_dataLock)
            {
                for (int i = 0; i < bitCount; i++)
                {
                    int packedIdx = i / 2;
                    bool bitVal;
                    if (i % 2 == 0)
                        bitVal = (packedData[packedIdx] & 0x10) != 0;
                    else
                        bitVal = (packedData[packedIdx] & 0x01) != 0;

                    int bitAddr = address + i;
                    if (bitAddr < storage.Length)
                        storage[bitAddr] = bitVal ? (byte)1 : (byte)0;
                }
            }
            return BuildSuccessResponse(subCmd);
        }

        // ═══════════════════════════════════════════
        //  存储/辅助方法
        // ═══════════════════════════════════════════

        private (byte[]? storage, bool isWordType) GetStorage(ushort dataCode)
        {
            switch (dataCode)
            {
                // 字类型
                case 0x4440: return (_d, true);   // D
                case 0x5220: return (_r, true);   // R
                case 0x5740: return (_w, true);   // W
                case 0x544E: return (_tn, true);  // TN
                case 0x434E: return (_cn, true);  // CN
                // 位类型
                case 0x4D20: return (_m, false);  // M
                case 0x5820: return (_x, false);  // X
                case 0x5920: return (_y, false);  // Y
                case 0x5320: return (_s, false);  // S
                case 0x4220: return (_b, false);  // B
                case 0x4620: return (_f, false);  // F
                case 0x5453: return (_ts, false); // TS
                case 0x5443: return (_tc, false); // TC
                case 0x4353: return (_cs, false); // CS
                case 0x4343: return (_cc, false); // CC
                default: return (null, false);
            }
        }

        private static byte[] BuildSuccessResponse(byte subCmd)
            => new byte[] { subCmd, 0 };

        private static byte[] BuildErrorResponse(byte subCmd, byte errorCode)
            => new byte[] { subCmd, errorCode };

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
