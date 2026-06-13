using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Fatek
{
    /// <summary>
    /// 永宏 Fatek FBs 虚拟 PLC 服务器 — 模拟 Fatek ASCII 编程口协议，用于无硬件测试。
    /// <para>帧格式: STX(0x02) + Station(2) + Command(2) + Data + ETX(0x03) + Checksum(2)</para>
    /// <para>支持区域: X(位), Y(位), M(位), S(位), T(位), C(位), D(字), R(字), RT(字), CT(字)</para>
    /// </summary>
    public class FatekVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        // 位区域存储 (每个位 1 字节，0 或 1)
        private readonly byte[] _xArea = new byte[256];  // X: 256 点
        private readonly byte[] _yArea = new byte[256];  // Y: 256 点
        private readonly byte[] _mArea = new byte[2048]; // M: 2048 点
        private readonly byte[] _sArea = new byte[1024]; // S: 1024 点
        private readonly byte[] _tArea = new byte[256];  // T: 256 点 (线圈)
        private readonly byte[] _cArea = new byte[256];  // C: 256 点 (线圈)

        // 字区域存储 (每个字 2 字节，大端)
        private readonly byte[] _drArea = new byte[131072]; // D: 65536 字
        private readonly byte[] _hrArea = new byte[65536];  // R: 32768 字
        private readonly byte[] _tmrArea = new byte[512];   // RT: 256 字
        private readonly byte[] _ctrArea = new byte[512];   // CT: 256 字

        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly object _lock = new object();

        private bool _plcRunning;

        public byte Station { get; set; } = 1;
        public int Port { get; }
        public bool IsRunning => _running;

        public FatekVirtualServer(int port = 5000)
        {
            Port = port;
        }

        // ── 数据设置 API ────────────────────────────

        public void SetDWord(int wordIndex, ushort value)
        {
            lock (_lock)
            {
                int off = wordIndex * 2;
                if (off >= 0 && off + 1 < _drArea.Length)
                {
                    _drArea[off] = (byte)(value >> 8);
                    _drArea[off + 1] = (byte)(value & 0xFF);
                }
            }
        }

        public void SetDBytes(int byteOffset, byte[] data)
        {
            lock (_lock)
            {
                int off = Math.Min(byteOffset, _drArea.Length - data.Length);
                Buffer.BlockCopy(data, 0, _drArea, off, data.Length);
            }
        }

        public void SetBit(char area, int bitIndex, bool value)
        {
            var buf = GetBitBuffer(area);
            if (buf == null || bitIndex < 0 || bitIndex >= buf.Length) return;
            lock (_lock) { buf[bitIndex] = (byte)(value ? 1 : 0); }
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
                    while (_running && client.Connected)
                    {
                        // 读取直到 ETX (0x03) + 2字节校验
                        var buf = new List<byte>();
                        int deadline = Environment.TickCount + 5000;

                        while (Environment.TickCount <= deadline)
                        {
                            int b = stream.ReadByte();
                            if (b < 0) return;
                            buf.Add((byte)b);
                            if (b == 0x03 && buf.Count > 5)
                            {
                                // 读取2字节校验
                                for (int i = 0; i < 2; i++)
                                {
                                    b = stream.ReadByte();
                                    if (b < 0) return;
                                    buf.Add((byte)b);
                                }
                                break;
                            }
                        }

                        if (buf.Count < 5) continue;

                        byte[]? response = ProcessFrame(buf.ToArray());
                        if (response != null)
                            stream.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
            finally { lock (_lock) { _clients.Remove(client); } }
        }

        // ── 帧处理 ──────────────────────────────────

        private byte[]? ProcessFrame(byte[] frame)
        {
            // 帧格式: STX(1) + Station(2) + Command(1-2) + Data + ETX(1) + Checksum(2)
            if (frame[0] != 0x02) return null;
            if (frame.Length < 7) return null;

            // 验证站号 (十进制站号)
            string stationStr = Encoding.ASCII.GetString(frame, 1, 2);
            if (!byte.TryParse(stationStr, out byte station))
                return null;
            if (station != Station) return null;

            // 判断命令类型: R/W 为简化读写, 44-47/40-41 为标准 Fatek 命令
            char cmdChar = (char)frame[3];

            return cmdChar switch
            {
                'R' => ProcessSimpleRead(frame),   // 简化读 (R + area + addr)
                'W' => ProcessSimpleWrite(frame),  // 简化写 (W + area + addr + data)
                _ => ProcessStandardCommand(frame)  // 标准 Fatek 命令
            };
        }

        private byte[]? ProcessStandardCommand(byte[] frame)
        {
            string command = Encoding.ASCII.GetString(frame, 3, 2);

            return command switch
            {
                "44" => ProcessReadBit(frame),
                "45" => ProcessWriteBit(frame),
                "46" => ProcessReadWord(frame),
                "47" => ProcessWriteWord(frame),
                "40" => ProcessReadStatus(frame),
                "41" => ProcessRunStop(frame),
                _ => PackSimpleResponse("2", null)
            };
        }

        // ── 简化 R/W 命令处理 (客户端 ReadInt16/Write 等使用) ──

        private byte[]? ProcessSimpleRead(byte[] frame)
        {
            // 帧格式: STX + Station(2) + 'R' + Area(1) + Addr(4) + ETX + Checksum(2)
            // 最小长度: 1 + 2 + 1 + 1 + 4 + 1 + 2 = 12
            if (frame.Length < 10) return PackSimpleResponse("2", null);

            char area = (char)frame[4];
            int addr = int.Parse(Encoding.ASCII.GetString(frame, 5, 4));

            var bitBuf = GetBitBuffer(area);
            if (bitBuf != null)
            {
                // 位区域读取
                bool value;
                lock (_lock) { value = addr < bitBuf.Length && bitBuf[addr] != 0; }
                return PackSimpleResponse("0", value ? "1" : "0");
            }

            var wordBuf = GetWordBuffer(area);
            if (wordBuf != null)
            {
                // 字区域读取 — 返回十进制值
                int off = addr * 2;
                ushort val = 0;
                lock (_lock)
                {
                    if (off + 1 < wordBuf.Length)
                        val = (ushort)((wordBuf[off] << 8) | wordBuf[off + 1]);
                }
                return PackSimpleResponse("0", val.ToString());
            }

            return PackSimpleResponse("2", null);
        }

        private byte[]? ProcessSimpleWrite(byte[] frame)
        {
            // 帧格式: STX + Station(2) + 'W' + Area(1) + Addr(4) + Data + ETX + Checksum(2)
            if (frame.Length < 10) return PackSimpleResponse("2", null);

            char area = (char)frame[4];
            int addr = int.Parse(Encoding.ASCII.GetString(frame, 5, 4));

            // 数据部分: 从 frame[9] 到 ETX(frame[Len-3])
            int dataLen = frame.Length - 9 - 3; // subtract header(9) + ETX(1) + checksum(2)
            if (dataLen < 1) return PackSimpleResponse("2", null);
            string dataStr = Encoding.ASCII.GetString(frame, 9, dataLen);

            var bitBuf = GetBitBuffer(area);
            if (bitBuf != null)
            {
                // 位写入
                bool value = dataStr.TrimStart('0') == "1";
                lock (_lock)
                {
                    if (addr < bitBuf.Length)
                        bitBuf[addr] = (byte)(value ? 1 : 0);
                }
                return PackSimpleResponse("0", null);
            }

            var wordBuf = GetWordBuffer(area);
            if (wordBuf != null)
            {
                // 字写入 — 十进制值
                if (int.TryParse(dataStr, out int val))
                {
                    int off = addr * 2;
                    lock (_lock)
                    {
                        if (off + 1 < wordBuf.Length)
                        {
                            wordBuf[off] = (byte)((val >> 8) & 0xFF);
                            wordBuf[off + 1] = (byte)(val & 0xFF);
                        }
                    }
                }
                return PackSimpleResponse("0", null);
            }

            return PackSimpleResponse("2", null);
        }

        private byte[] ProcessReadBit(byte[] frame)
        {
            // "44" + count(2hex) + area(1) + addr(4dec)
            if (frame.Length < 11) return PackResponse(frame, "2", null);

            string countStr = Encoding.ASCII.GetString(frame, 5, 2);
            int count = int.Parse(countStr, System.Globalization.NumberStyles.HexNumber);
            if (count == 0) count = 256;

            char area = (char)frame[7];
            int addr = int.Parse(Encoding.ASCII.GetString(frame, 8, 4));

            var buf = GetBitBuffer(area);
            if (buf == null) return PackResponse(frame, "2", null);

            var data = new char[count];
            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                {
                    int idx = addr + i;
                    data[i] = (idx < buf.Length && buf[idx] != 0) ? '1' : '0';
                }
            }

            return PackResponse(frame, "0", new string(data));
        }

        private byte[] ProcessWriteBit(byte[] frame)
        {
            if (frame.Length < 11) return PackResponse(frame, "2", null);

            string countStr = Encoding.ASCII.GetString(frame, 5, 2);
            int count = int.Parse(countStr, System.Globalization.NumberStyles.HexNumber);
            if (count == 0) count = 256;

            char area = (char)frame[7];
            int addr = int.Parse(Encoding.ASCII.GetString(frame, 8, 4));

            var buf = GetBitBuffer(area);
            if (buf == null) return PackResponse(frame, "2", null);

            lock (_lock)
            {
                for (int i = 0; i < count && (12 + i) < frame.Length - 3; i++)
                {
                    int idx = addr + i;
                    if (idx < buf.Length)
                        buf[idx] = (byte)(frame[12 + i] == '1' ? 1 : 0);
                }
            }

            return PackResponse(frame, "0", null);
        }

        private byte[] ProcessReadWord(byte[] frame)
        {
            if (frame.Length < 11) return PackResponse(frame, "2", null);

            string countStr = Encoding.ASCII.GetString(frame, 5, 2);
            int count = int.Parse(countStr, System.Globalization.NumberStyles.HexNumber);
            if (count > 64) return PackResponse(frame, "2", null);

            // 解析区域和地址
            string areaStr = Encoding.ASCII.GetString(frame, 7, 2);
            byte[]? wordBuf;
            int addr;

            if (areaStr == "RT") { wordBuf = _tmrArea; addr = int.Parse(Encoding.ASCII.GetString(frame, 9, 4)); }
            else if (areaStr == "CT") { wordBuf = _ctrArea; addr = int.Parse(Encoding.ASCII.GetString(frame, 9, 4)); }
            else if (areaStr[0] == 'D') { wordBuf = _drArea; addr = int.Parse(Encoding.ASCII.GetString(frame, 8, 5)); }
            else if (areaStr[0] == 'R') { wordBuf = _hrArea; addr = int.Parse(Encoding.ASCII.GetString(frame, 8, 5)); }
            else return PackResponse(frame, "2", null);

            var data = new StringBuilder(count * 4);
            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                {
                    int off = (addr + i) * 2;
                    if (off + 1 < wordBuf.Length)
                    {
                        data.Append(wordBuf[off].ToString("X2"));
                        data.Append(wordBuf[off + 1].ToString("X2"));
                    }
                    else
                    {
                        data.Append("0000");
                    }
                }
            }

            return PackResponse(frame, "0", data.ToString());
        }

        private byte[] ProcessWriteWord(byte[] frame)
        {
            if (frame.Length < 13) return PackResponse(frame, "2", null);

            string countStr = Encoding.ASCII.GetString(frame, 5, 2);
            int count = int.Parse(countStr, System.Globalization.NumberStyles.HexNumber);
            if (count > 64) return PackResponse(frame, "2", null);

            string areaStr = Encoding.ASCII.GetString(frame, 7, 2);
            byte[]? wordBuf;
            int addr;

            if (areaStr == "RT") { wordBuf = _tmrArea; addr = int.Parse(Encoding.ASCII.GetString(frame, 9, 4)); }
            else if (areaStr == "CT") { wordBuf = _ctrArea; addr = int.Parse(Encoding.ASCII.GetString(frame, 9, 4)); }
            else if (areaStr[0] == 'D') { wordBuf = _drArea; addr = int.Parse(Encoding.ASCII.GetString(frame, 8, 5)); }
            else if (areaStr[0] == 'R') { wordBuf = _hrArea; addr = int.Parse(Encoding.ASCII.GetString(frame, 8, 5)); }
            else return PackResponse(frame, "2", null);

            // 数据从帧的偏移处开始（跳过命令头）
            int dataOffset = areaStr == "RT" || areaStr == "CT" ? 13 : 13;
            string hexData = Encoding.ASCII.GetString(frame, dataOffset, count * 4);

            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                {
                    int off = (addr + i) * 2;
                    if (off + 1 < wordBuf.Length)
                    {
                        wordBuf[off] = (byte)(HexV(hexData[i * 4]) << 4 | HexV(hexData[i * 4 + 1]));
                        wordBuf[off + 1] = (byte)(HexV(hexData[i * 4 + 2]) << 4 | HexV(hexData[i * 4 + 3]));
                    }
                }
            }

            return PackResponse(frame, "0", null);
        }

        private byte[] ProcessReadStatus(byte[] frame)
        {
            // 返回3字节hex: run(1) + 00 + 00
            string data = (_plcRunning ? "1" : "0") + "0000";
            return PackResponse(frame, "0", data);
        }

        private byte[] ProcessRunStop(byte[] frame)
        {
            // frame[5] == '1' → RUN, '0' → STOP
            if (frame.Length > 5 && frame[5] == '1')
                _plcRunning = true;
            else
                _plcRunning = false;
            return PackResponse(frame, "0", null);
        }

        private byte[] PackResponse(byte[] request, string errCode, string? data)
        {
            // 客户端期望响应格式: STX + "!0" / "!2" + data + ETX + Checksum(2)
            return PackSimpleResponse(errCode, data);
        }

        /// <summary>
        /// 统一响应打包: STX + "!" + errCode + data + ETX + Checksum(2)
        /// </summary>
        private byte[] PackSimpleResponse(string errCode, string? data)
        {
            string body = "!" + errCode;
            if (data != null) body += data;

            byte sum = 0;
            var bodyBytes = Encoding.ASCII.GetBytes(body);
            foreach (byte b in bodyBytes) sum += b;
            string checksum = sum.ToString("X2");

            string response = "\x02" + body + "\x03" + checksum;
            return Encoding.ASCII.GetBytes(response);
        }

        private byte[]? GetBitBuffer(char area) => area switch
        {
            'X' => _xArea,
            'Y' => _yArea,
            'M' => _mArea,
            'S' => _sArea,
            'T' => _tArea,
            'C' => _cArea,
            _ => null
        };

        private byte[]? GetWordBuffer(char area) => area switch
        {
            'D' => _drArea,
            'R' => _hrArea,
            _ => null
        };

        private static int HexV(char c) => c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'F' ? c - 'A' + 10 : c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0;

        public void Dispose() => Stop();
    }
}
