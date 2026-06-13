using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Nexus.Omron;

namespace Nexus.Omron
{
    /// <summary>
    /// 欧姆龙 HostLink 虚拟 PLC 服务器 — 用于测试。
    /// <para>解析 HostLink ASCII 帧，执行 FINS MemoryAreaRead/Write 命令，返回 ASCII 响应。</para>
    /// <para>响应帧头 15 字节（PLC 响应中 WaitTime 用 2 个 hex 字符，比请求多 1 字节）。</para>
    /// </summary>
    public class OmronHostLinkVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly int _port;
        private int _connectionCount;

        // ── 字存储（大端序，每字 2 字节，byte 偏移 = address * 2）──
        private readonly byte[] _dm  = new byte[16384];
        private readonly byte[] _cio = new byte[16384];
        private readonly byte[] _wr  = new byte[16384];
        private readonly byte[] _hr  = new byte[16384];
        private readonly byte[] _ar  = new byte[16384];

        private readonly object _dataLock = new object();

        // 响应帧头长度：@(1) + unit(2) + FA(2) + wait_hex(2) + ICF(2) + DA2(2) + SA2(2) + SID(2) = 15
        private const int ResponseHeaderLen = 15;

        public OmronHostLinkVirtualServer(int port) => _port = port;
        public bool IsRunning => _running;
        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        // ═══════════════════════════════════════════
        //  数据设置 API（供测试使用）
        // ═══════════════════════════════════════════

        /// <summary>设置 DM 区域字值（大端序）。</summary>
        public void SetDmWord(int addr, ushort value)
        {
            lock (_dataLock)
            {
                int offset = addr * 2;
                if (offset + 1 < _dm.Length)
                {
                    _dm[offset]     = (byte)(value >> 8);
                    _dm[offset + 1] = (byte)(value & 0xFF);
                }
            }
        }

        /// <summary>设置 DM 区域多个字节。</summary>
        public void SetDmBytes(int addr, byte[] data)
        {
            lock (_dataLock)
            {
                int offset = addr * 2;
                Array.Copy(data, 0, _dm, offset, Math.Min(data.Length, _dm.Length - offset));
            }
        }

        /// <summary>设置 CIO 区域字值。</summary>
        public void SetCioWord(int addr, ushort value)
        {
            lock (_dataLock)
            {
                int offset = addr * 2;
                if (offset + 1 < _cio.Length)
                {
                    _cio[offset]     = (byte)(value >> 8);
                    _cio[offset + 1] = (byte)(value & 0xFF);
                }
            }
        }

        /// <summary>设置 WR 区域字值。</summary>
        public void SetWrWord(int addr, ushort value)
        {
            lock (_dataLock)
            {
                int offset = addr * 2;
                if (offset + 1 < _wr.Length)
                {
                    _wr[offset]     = (byte)(value >> 8);
                    _wr[offset + 1] = (byte)(value & 0xFF);
                }
            }
        }

        /// <summary>设置 HR 区域字值。</summary>
        public void SetHrWord(int addr, ushort value)
        {
            lock (_dataLock)
            {
                int offset = addr * 2;
                if (offset + 1 < _hr.Length)
                {
                    _hr[offset]     = (byte)(value >> 8);
                    _hr[offset + 1] = (byte)(value & 0xFF);
                }
            }
        }

        /// <summary>设置 DM 区域位值。</summary>
        public void SetDmBit(int addr, int bitOffset, bool value)
        {
            lock (_dataLock)
            {
                int byteOffset = addr * 2;
                if (byteOffset + 1 < _dm.Length)
                {
                    ushort word = (ushort)((_dm[byteOffset] << 8) | _dm[byteOffset + 1]);
                    if (value)
                        word |= (ushort)(1 << bitOffset);
                    else
                        word &= (ushort)~(1 << bitOffset);
                    _dm[byteOffset]     = (byte)(word >> 8);
                    _dm[byteOffset + 1] = (byte)(word & 0xFF);
                }
            }
        }

        /// <summary>设置 CIO 区域位值。</summary>
        public void SetCioBit(int addr, int bitOffset, bool value)
        {
            lock (_dataLock)
            {
                int byteOffset = addr * 2;
                if (byteOffset + 1 < _cio.Length)
                {
                    ushort word = (ushort)((_cio[byteOffset] << 8) | _cio[byteOffset + 1]);
                    if (value)
                        word |= (ushort)(1 << bitOffset);
                    else
                        word &= (ushort)~(1 << bitOffset);
                    _cio[byteOffset]     = (byte)(word >> 8);
                    _cio[byteOffset + 1] = (byte)(word & 0xFF);
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
                        var ms = new MemoryStream();
                        int b;
                        while ((b = ns.ReadByte()) != -1)
                        {
                            ms.WriteByte((byte)b);
                            if (b == 0x0D) break;
                        }

                        if (ms.Length == 0) break;
                        byte[] request = ms.ToArray();

                        byte[] response = ProcessHostLinkFrame(request);
                        ns.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════
        //  HostLink 帧处理
        // ═══════════════════════════════════════════

        private byte[] ProcessHostLinkFrame(byte[] frame)
        {
            // 最小请求帧: @ + unit(2) + FA(2) + wait(1) + ICF(2) + DA2(2) + SA2(2) + SID(2) + cmd(4) + FCS(2) + * + CR = 24
            if (frame.Length < 24) return BuildErrorResponse(frame, 0x01);

            try
            {
                // 提取 FINS 命令数据：请求帧头 14 字节，帧尾 4 字节（FCS+*+CR）
                string finsHex = Encoding.ASCII.GetString(frame, 14, frame.Length - 18);
                byte[] finsCmd = OmronHostLinkClient.AsciiHexToBytes(finsHex);

                if (finsCmd.Length < 2) return BuildErrorResponse(frame, 0x01);

                ushort cmdCode = (ushort)((finsCmd[0] << 8) | finsCmd[1]);

                // 提取请求中的命令码 ASCII（用于响应回显）
                string cmdCodeStr = Encoding.ASCII.GetString(frame, 14, 4);

                switch (cmdCode)
                {
                    case 0x0101: // MemoryAreaRead
                        var readData = ProcessFinsRead(finsCmd);
                        if (readData == null) return BuildErrorResponse(frame, 0x03);
                        return BuildSuccessResponse(frame, cmdCodeStr, readData);

                    case 0x0102: // MemoryAreaWrite
                        var writeOk = ProcessFinsWrite(finsCmd);
                        if (!writeOk) return BuildErrorResponse(frame, 0x03);
                        return BuildSuccessResponse(frame, cmdCodeStr, Array.Empty<byte>());

                    default:
                        return BuildErrorResponse(frame, 0x01);
                }
            }
            catch
            {
                return BuildErrorResponse(frame, 0xFF);
            }
        }

        // ═══════════════════════════════════════════
        //  FINS 命令处理
        // ═══════════════════════════════════════════

        private byte[]? ProcessFinsRead(byte[] cmd)
        {
            if (cmd.Length < 9) return null;

            var storage = GetStorage(cmd[2]);
            if (storage == null) return null;

            ushort wordAddr = (ushort)((cmd[4] << 8) | cmd[5]);
            ushort length   = (ushort)((cmd[7] << 8) | cmd[8]);
            bool isBit = cmd[3] == 0x01;

            lock (_dataLock)
            {
                if (isBit)
                {
                    // 位读取：返回 1 字节
                    int byteOffset = wordAddr * 2;
                    if (byteOffset + 1 >= storage.Length) return null;
                    ushort word = (ushort)((storage[byteOffset] << 8) | storage[byteOffset + 1]);
                    bool bitVal = (word & (1 << cmd[6])) != 0;
                    return new byte[] { (byte)(bitVal ? 0x01 : 0x00) };
                }
                else
                {
                    // 字读取：返回 length * 2 字节
                    int byteOffset = wordAddr * 2;
                    int byteCount  = length * 2;
                    var data = new byte[byteCount];
                    int copyLen = Math.Min(byteCount, Math.Max(0, storage.Length - byteOffset));
                    if (copyLen > 0)
                        Array.Copy(storage, byteOffset, data, 0, copyLen);
                    return data;
                }
            }
        }

        private bool ProcessFinsWrite(byte[] cmd)
        {
            if (cmd.Length < 9) return false;

            var storage = GetStorage(cmd[2]);
            if (storage == null) return false;

            ushort wordAddr = (ushort)((cmd[4] << 8) | cmd[5]);
            bool isBit = cmd[3] == 0x01;

            lock (_dataLock)
            {
                if (isBit && cmd.Length > 9)
                {
                    int byteOffset = wordAddr * 2;
                    if (byteOffset + 1 >= storage.Length) return false;
                    ushort word = (ushort)((storage[byteOffset] << 8) | storage[byteOffset + 1]);
                    bool bitVal = cmd[9] != 0;
                    if (bitVal)
                        word |= (ushort)(1 << cmd[6]);
                    else
                        word &= (ushort)~(1 << cmd[6]);
                    storage[byteOffset]     = (byte)(word >> 8);
                    storage[byteOffset + 1] = (byte)(word & 0xFF);
                }
                else if (!isBit && cmd.Length > 9)
                {
                    int byteOffset = wordAddr * 2;
                    int dataLen    = cmd.Length - 9;
                    Array.Copy(cmd, 9, storage, byteOffset, Math.Min(dataLen, storage.Length - byteOffset));
                }
            }
            return true;
        }

        private byte[]? GetStorage(byte areaCode)
        {
            switch (areaCode)
            {
                case (byte)FinsMemoryArea.DM:  return _dm;
                case (byte)FinsMemoryArea.CIO: return _cio;
                case (byte)FinsMemoryArea.WR:  return _wr;
                case (byte)FinsMemoryArea.HR:  return _hr;
                case (byte)FinsMemoryArea.AR:  return _ar;
                default: return null;
            }
        }

        // ═══════════════════════════════════════════
        //  响应帧构建
        // ═══════════════════════════════════════════
        //
        //  响应帧结构（PLC 行为）：
        //  @(1) + unit(2) + FA(2) + wait_hex(2) + ICF(2) + DA2(2) + SA2(2) + SID(2) = 15 字节头
        //  + cmdCode(4) + endCode(4) + data_ascii_hex + FCS(2) + *(1) + CR(1)
        //
        //  客户端 ParseResponse 期望：cmdCode 在 [15..18], endCode 在 [19..22], data 在 [23..len-4]
        // ═══════════════════════════════════════════

        private byte[] BuildSuccessResponse(byte[] request, string cmdCodeStr, byte[] finsData)
        {
            // 构建 FINS 响应体（ASCII hex）：cmdCode + "0000"(endCode) + data
            string dataHex = BitConverter.ToString(finsData).Replace("-", "");
            string body = cmdCodeStr + "0000" + dataHex;
            byte[] bodyAscii = Encoding.ASCII.GetBytes(body);

            return BuildResponseFrame(request, bodyAscii);
        }

        private byte[] BuildErrorResponse(byte[] request, byte errorCode)
        {
            string cmdCodeStr = request.Length >= 18
                ? Encoding.ASCII.GetString(request, 14, 4)
                : "0101";

            // 结束码：高字节=错误码，低字节=01
            string endCode = $"{errorCode:D2}01";
            string body = cmdCodeStr + endCode;
            byte[] bodyAscii = Encoding.ASCII.GetBytes(body);

            return BuildResponseFrame(request, bodyAscii);
        }

        private byte[] BuildResponseFrame(byte[] request, byte[] bodyAscii)
        {
            // 响应帧 = 头(15) + body + FCS(2) + *(1) + CR(1)
            int totalLen = ResponseHeaderLen + bodyAscii.Length + 4;
            var frame = new byte[totalLen];

            // ── 构建响应头（15 字节）──
            frame[0] = (byte)'@';
            // unit: 直接复制请求中的 unit
            frame[1] = request[1];
            frame[2] = request[2];
            // FA
            frame[3] = (byte)'F';
            frame[4] = (byte)'A';
            // wait: 请求中是 1 字节 ASCII（如 '0'=0x30），响应中展开为 2 字节 hex（如 "30"）
            byte waitByte = request[5];
            frame[5] = OmronHostLinkClient.ToAsciiHexHigh(waitByte);
            frame[6] = OmronHostLinkClient.ToAsciiHexLow(waitByte);
            // ICF
            frame[7]  = request[6];
            frame[8]  = request[7];
            // DA2
            frame[9]  = request[8];
            frame[10] = request[9];
            // SA2
            frame[11] = request[10];
            frame[12] = request[11];
            // SID
            frame[13] = request[12];
            frame[14] = request[13];

            // ── 复制 body ──
            Array.Copy(bodyAscii, 0, frame, ResponseHeaderLen, bodyAscii.Length);

            // ── 帧尾 ──
            frame[totalLen - 2] = (byte)'*';
            frame[totalLen - 1] = 0x0D; // CR

            // ── FCS：XOR [0..totalLen-5] ──
            byte fcs = 0;
            for (int i = 0; i < totalLen - 4; i++)
                fcs ^= frame[i];
            frame[totalLen - 4] = OmronHostLinkClient.ToAsciiHexHigh(fcs);
            frame[totalLen - 3] = OmronHostLinkClient.ToAsciiHexLow(fcs);

            return frame;
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
