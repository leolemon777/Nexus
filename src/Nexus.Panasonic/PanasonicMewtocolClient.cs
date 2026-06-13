using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Nexus.Panasonic
{
    /// <summary>
    /// 松下 Mewtocol 协议客户端 — 支持 FP 系列 PLC (FP0/FP2/FP3/FP5/FP7等)。
    /// <para>帧格式: % [Station] [Command] [Data] [BCC] CR</para>
    /// <para>对标 HSL: PanasonicMewtocol — Read/Write DT/WR/WL/FL 寄存器</para>
    /// </summary>
    public class PanasonicMewtocolClient : IReadWriteDevice, IBatchReadWrite
    {
        private readonly Stream _stream;
        private readonly object _lock = new object();
        protected ILogger Log { get; set; }

        /// <summary>站号 (01-99)。</summary>
        public byte Station { get; set; }
        /// <summary>超时（毫秒）。</summary>
        public int Timeout { get; set; }

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;
        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected => _stream?.CanRead == true && _stream?.CanWrite == true;

        public PanasonicMewtocolClient(Stream stream, byte station = 1, int timeout = 5000)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            Station = station;
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ═══════════════════════════════════════════
        //  BCC 校验 (Block Check Character)
        // ═══════════════════════════════════════════

        /// <summary>BCC = 所有字符 ASCII 异或（从站号开始到数据结束）。</summary>
        private static byte ComputeBcc(string data)
        {
            byte bcc = 0;
            foreach (char c in data)
                bcc ^= (byte)c;
            return bcc;
        }

        // ═══════════════════════════════════════════
        //  帧收发
        // ═══════════════════════════════════════════

        private OperateResult<string> SendAndReceive(string command, string data)
        {
            try
            {
                lock (_lock)
                {
                    // 构建: %SSCCDD...DBCC\r
                    // SS = Station (2 chars), CC = Command (2 chars), DD... = Data, BCC = 2 hex chars
                    string stationStr = Station.ToString("D2");
                    string body = stationStr + command + data;
                    byte bcc = ComputeBcc(body);
                    string frame = "%" + body + bcc.ToString("X2") + "\r";

                    Log.Debug($"TX → {frame.TrimEnd()}");
                    OnMessageSent?.Invoke(this, frame.TrimEnd());

                    byte[] txBytes = Encoding.ASCII.GetBytes(frame);
                    _stream.Write(txBytes, 0, txBytes.Length);

                    // 读取响应: %SSCC...BCC\r
                    string? response = ReadFrame();
                    if (response == null)
                        return OperateResult<string>.Failed("读取响应超时");

                    Log.Debug($"RX ← {response.TrimEnd()}");
                    OnMessageReceived?.Invoke(this, response.TrimEnd());

                    // 验证格式
                    if (response.Length < 6 || !response.StartsWith("%"))
                        return OperateResult<string>.Failed("响应格式错误");

                    // 验证站号
                    string respStation = response.Substring(1, 2);
                    if (respStation != stationStr)
                        return OperateResult<string>.Failed($"响应站号不匹配: 期望={stationStr}, 实际={respStation}");

                    // 检查错误响应: %SS!CC...
                    string respCmd = response.Substring(3, 2);
                    if (respCmd == "!")
                    {
                        string errCode = response.Length > 5 ? response.Substring(5) : "??";
                        return OperateResult<string>.Failed($"PLC 错误: {ParseErrorCode(errCode)}");
                    }

                    // 验证 BCC
                    string respBody = response.Substring(1, response.Length - 4); // 去掉 % 和 BCC+\r
                    byte expectedBcc = ComputeBcc(respBody);
                    string respBcc = response.Substring(response.Length - 3, 2);
                    if (expectedBcc.ToString("X2") != respBcc)
                        return OperateResult<string>.Failed($"BCC 校验失败: 期望={expectedBcc:X2}, 实际={respBcc}");

                    // 提取数据: %SS + CC(2) + Data + BCC(2) + \r
                    // 返回 CC 之后到 BCC 之前的部分
                    int dataStart = 5; // %(1) + SS(2) + CC(2)
                    int dataEnd = response.Length - 3; // BCC(2) + \r(1)
                    if (dataEnd <= dataStart)
                        return OperateResult<string>.Success("");
                    return OperateResult<string>.Success(response.Substring(dataStart, dataEnd - dataStart));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Mewtocol 通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult<string>.Failed($"通讯异常: {ex.Message}");
            }
        }

        private string? ReadFrame()
        {
            int deadline = Environment.TickCount + Timeout;
            using var ms = new MemoryStream();

            while (Environment.TickCount <= deadline)
            {
                int b = ReadByteWithTimeout(deadline);
                if (b < 0) return null;
                ms.WriteByte((byte)b);
                if (b == '\r') return Encoding.ASCII.GetString(ms.ToArray());
            }
            return null;
        }

        private int ReadByteWithTimeout(int deadline)
        {
            while (Environment.TickCount <= deadline)
            {
                try { return _stream.ReadByte(); }
                catch (TimeoutException) { return -1; }
            }
            return -1;
        }

        private static string ParseErrorCode(string code) => code switch
        {
            "00" => "无错误",
            "01" => "未定义命令",
            "02" => "非法地址",
            "03" => "非法数据",
            "04" => "操作失败",
            "05" => "忙碌",
            "20" => "通讯错误",
            "21" => "帧错误",
            "22" => "BCC 错误",
            "23" => "站号不匹配",
            "24" => "超时",
            "40" => "PLC 运行中不允许写入",
            "41" => "地址写保护",
            _ => $"未知错误代码 {code}"
        };

        // ═══════════════════════════════════════════
        //  寄存器地址映射
        // ═══════════════════════════════════════════

        /// <summary>
        /// 解析 Mewtocol 地址。
        /// <para>支持格式: "DT100", "DD100", "WR100", "WL100", "FL100", "SV100", "EV100", "IX100", "IY100"</para>
        /// <para>简写: "100" → DT100, "R100" → WR100, "L100" → WL100</para>
        /// </summary>
        private static (string areaCode, int address) ParseAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("地址不能为空");
            address = address.Trim().ToUpperInvariant();

            // Mewtocol area codes
            if (address.StartsWith("DT")) return ("D", int.Parse(address.Substring(2)));
            if (address.StartsWith("DD")) return ("D", int.Parse(address.Substring(2)));
            if (address.StartsWith("WR")) return ("R", int.Parse(address.Substring(2)));
            if (address.StartsWith("WL")) return ("L", int.Parse(address.Substring(2)));
            if (address.StartsWith("FL")) return ("F", int.Parse(address.Substring(2)));
            if (address.StartsWith("SV")) return ("J", int.Parse(address.Substring(2)));
            if (address.StartsWith("EV")) return ("K", int.Parse(address.Substring(2)));
            if (address.StartsWith("IX")) return ("X", int.Parse(address.Substring(2)));
            if (address.StartsWith("IY")) return ("Y", int.Parse(address.Substring(2)));

            // Shortcuts
            if (address.StartsWith("R")) return ("R", int.Parse(address.Substring(1)));
            if (address.StartsWith("L")) return ("L", int.Parse(address.Substring(1)));
            if (address.StartsWith("F")) return ("F", int.Parse(address.Substring(1)));
            if (address.StartsWith("X")) return ("X", int.Parse(address.Substring(1)));
            if (address.StartsWith("Y")) return ("Y", int.Parse(address.Substring(1)));

            // Default: DT
            return ("D", int.Parse(address));
        }

        // ═══════════════════════════════════════════
        //  读写命令
        // ═══════════════════════════════════════════

        /// <summary>读取寄存器 (RD command)。</summary>
        private OperateResult<string> ReadRegisters(string area, int startAddress, int count)
        {
            // Command: RD + Area(1) + StartAddr(5hex) + EndAddr(5hex)
            int endAddr = startAddress + count - 1;
            string data = area + startAddress.ToString("D5") + endAddr.ToString("D5");
            return SendAndReceive("RD", data);
        }

        /// <summary>读取单个寄存器 (RCS/RD command)。</summary>
        private OperateResult<string> ReadSingleRegister(string area, int address)
        {
            return ReadRegisters(area, address, 1);
        }

        /// <summary>写入寄存器 (WD command)。</summary>
        private OperateResult WriteRegisters(string area, int startAddress, string hexData)
        {
            // Command: WD + Area(1) + StartAddr(5hex) + Data(4hex per register)
            string data = area + startAddress.ToString("D5") + hexData;
            var result = SendAndReceive("WD", data);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message, result.ErrorCode);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 数据类型读写
        // ═══════════════════════════════════════════

        // ── PLC 控制命令 ──────────────────────────

        /// <summary>
        /// 读取 PLC 型号 (RT 命令)。
        /// <para>响应包含 PLC 类型代码，如 "FP2", "FP3", "FP5", "FP10", "FP7" 等。</para>
        /// </summary>
        public OperateResult<string> ReadPlcModel()
        {
            var r = SendAndReceive("RT", "");
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(r.Content.Trim());
        }

        /// <summary>
        /// 运行 PLC (MS 命令, 模式=01 = Run)。
        /// </summary>
        public OperateResult Run()
        {
            var r = SendAndReceive("MS", "01");
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>
        /// 停止 PLC (MS 命令, 模式=02 = Stop)。
        /// </summary>
        public OperateResult Stop()
        {
            var r = SendAndReceive("MS", "02");
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>异步运行 PLC。</summary>
        public Task<OperateResult> RunAsync() => Task.FromResult(Run());

        /// <summary>异步停止 PLC。</summary>
        public Task<OperateResult> StopAsync() => Task.FromResult(Stop());

        /// <summary>异步读取 PLC 型号。</summary>
        public Task<OperateResult<string>> ReadPlcModelAsync() => Task.FromResult(ReadPlcModel());

        // ── 触点（位）读写 ──────────────────────

        /// <summary>
        /// 读取单个触点 (RCS 命令) — 读单个位状态。
        /// <para>触点类型: X=输入, Y=输出, R=内部继电器, T=定时器, C=计数器, L=链接继电器。</para>
        /// </summary>
        /// <param name="address">触点地址，如 "R100", "Y0", "X10"。</param>
        public OperateResult<bool> ReadContact(string address)
        {
            var (type, addr) = ParseContactAddress(address);
            var r = SendAndReceive("RCS", type + addr.ToString("D4"));
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Trim() == "1");
        }

        /// <summary>
        /// 写入单个触点 (WCS 命令) — 写单个位状态。
        /// </summary>
        /// <param name="address">触点地址。</param>
        /// <param name="value">位值。</param>
        public OperateResult WriteContact(string address, bool value)
        {
            var (type, addr) = ParseContactAddress(address);
            string data = type + addr.ToString("D4") + (value ? "1" : "0");
            var r = SendAndReceive("WCS", data);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>
        /// 批量读取触点 (RLS 命令) — 读多个连续位。
        /// </summary>
        /// <param name="address">起始触点地址。</param>
        /// <param name="count">读取位数 (1-256)。</param>
        public OperateResult<bool[]> ReadContacts(string address, ushort count)
        {
            var (type, addr) = ParseContactAddress(address);
            string data = type + addr.ToString("D4") + count.ToString("D4");
            var r = SendAndReceive("RLS", data);
            if (!r.IsSuccess) return OperateResult<bool[]>.Failed(r.Message, r.ErrorCode);

            bool[] bits = new bool[r.Content.Length];
            for (int i = 0; i < r.Content.Length; i++)
                bits[i] = r.Content[i] == '1';
            return OperateResult<bool[]>.Success(bits);
        }

        /// <summary>
        /// 批量写入触点 (WLS 命令) — 写多个连续位。
        /// </summary>
        /// <param name="address">起始触点地址。</param>
        /// <param name="values">位值数组。</param>
        public OperateResult WriteContacts(string address, bool[] values)
        {
            var (type, addr) = ParseContactAddress(address);
            var sb = new System.Text.StringBuilder();
            sb.Append(type);
            sb.Append(addr.ToString("D4"));
            sb.Append(values.Length.ToString("D4"));
            foreach (var v in values)
                sb.Append(v ? "1" : "0");

            var r = SendAndReceive("WLS", sb.ToString());
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>解析触点地址: X0, Y10, R100, T0, C0, L100。</summary>
        private static (string type, int address) ParseContactAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("地址不能为空");
            address = address.Trim().ToUpperInvariant();

            char prefix = address[0];
            int num = int.Parse(address.Substring(1));

            return prefix switch
            {
                'X' => ("X", num),
                'Y' => ("Y", num),
                'R' => ("R", num),
                'T' => ("T", num),
                'C' => ("C", num),
                'L' => ("L", num),
                _ => throw new ArgumentException($"不支持的触点类型: {prefix}")
            };
        }

        // ── 寄存器读写 ────────────────────────────

        public OperateResult<bool> ReadBool(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadSingleRegister(area, addr);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<bool>.Failed("响应数据不足");
            return OperateResult<bool>.Success(r.Content != "0000");
        }

        public OperateResult<short> ReadInt16(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadSingleRegister(area, addr);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success((short)HexToInt16(r.Content));
        }

        public OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult<int> ReadInt32(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadRegisters(area, addr, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<int>.Failed("响应数据不足");
            return OperateResult<int>.Success((int)HexToUInt32(r.Content));
        }

        public OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult<long> ReadInt64(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadRegisters(area, addr, 4);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 16) return OperateResult<long>.Failed("响应数据不足");
            return OperateResult<long>.Success(HexToInt64(r.Content));
        }

        public OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public unsafe OperateResult<float> ReadFloat(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadRegisters(area, addr, 2);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<float>.Failed("响应数据不足");
            int v = (int)HexToUInt32(r.Content);
            return OperateResult<float>.Success(*(float*)&v);
        }

        public unsafe OperateResult<double> ReadDouble(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadRegisters(area, addr, 4);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 16) return OperateResult<double>.Failed("响应数据不足");
            long v = HexToInt64(r.Content);
            return OperateResult<double>.Success(*(double*)&v);
        }

        public OperateResult<string> ReadString(string address, ushort length)
        {
            var (area, addr) = ParseAddress(address);
            int regCount = (length + 1) / 2;
            var r = ReadRegisters(area, addr, regCount);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            // Hex to ASCII
            byte[] bytes = HexToBytes(r.Content);
            string text = Encoding.ASCII.GetString(bytes, 0, Math.Min(length, bytes.Length));
            return OperateResult<string>.Success(text.TrimEnd('\0'));
        }

        public OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var (area, addr) = ParseAddress(address);
            int regCount = (length + 1) / 2;
            var r = ReadRegisters(area, addr, regCount);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] data = HexToBytes(r.Content);
            if (data.Length < length)
                return OperateResult<byte[]>.Failed($"响应数据不足: 期望 {length} 字节，实际 {data.Length} 字节");

            byte[] result = new byte[length];
            Array.Copy(data, result, length);
            return OperateResult<byte[]>.Success(result);
        }

        // ── 写入 ──

        public OperateResult Write(string address, bool value)
        {
            var (area, addr) = ParseAddress(address);
            string hex = value ? "0001" : "0000";
            return WriteRegisters(area, addr, hex);
        }

        public OperateResult Write(string address, short value)
        {
            var (area, addr) = ParseAddress(address);
            string hex = ((ushort)value).ToString("X4");
            return WriteRegisters(area, addr, hex);
        }

        public OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public OperateResult Write(string address, int value)
        {
            var (area, addr) = ParseAddress(address);
            string hex = ((uint)value).ToString("X8");
            return WriteRegisters(area, addr, hex);
        }

        public OperateResult Write(string address, uint value) => Write(address, (int)value);
        public OperateResult Write(string address, long value)
        {
            var (area, addr) = ParseAddress(address);
            string hex = unchecked((ulong)value).ToString("X16");
            return WriteRegisters(area, addr, hex);
        }

        public OperateResult Write(string address, ulong value)
        {
            var (area, addr) = ParseAddress(address);
            string hex = value.ToString("X16");
            return WriteRegisters(area, addr, hex);
        }

        public unsafe OperateResult Write(string address, float value)
        {
            int v = *(int*)&value;
            return Write(address, v);
        }

        public unsafe OperateResult Write(string address, double value)
        {
            ulong v = *(ulong*)&value;
            return Write(address, v);
        }

        public OperateResult Write(string address, string value)
        {
            var (area, addr) = ParseAddress(address);
            byte[] bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
            if (bytes.Length % 2 != 0) Array.Resize(ref bytes, bytes.Length + 1);
            string hex = BytesToHex(bytes);
            return WriteRegisters(area, addr, hex);
        }

        public OperateResult Write(string address, byte[] data)
        {
            if (data == null)
                return OperateResult.Failed("写入数据不能为空");

            var (area, addr) = ParseAddress(address);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            string hex = BytesToHex(data);
            return WriteRegisters(area, addr, hex);
        }

        // ═══════════════════════════════════════════
        //  Hex 转换辅助
        // ═══════════════════════════════════════════

        private static short HexToInt16(string hex) => (short)ushort.Parse(hex.Substring(0, 4), System.Globalization.NumberStyles.HexNumber);
        private static uint HexToUInt32(string hex) => uint.Parse(hex.Substring(0, 8), System.Globalization.NumberStyles.HexNumber);
        private static long HexToInt64(string hex) => unchecked((long)ulong.Parse(hex.Substring(0, 16), System.Globalization.NumberStyles.HexNumber));

        private static byte[] HexToBytes(string hex)
        {
            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = (byte)(HexVal(hex[i * 2]) << 4 | HexVal(hex[i * 2 + 1]));
            return result;
        }

        private static string BytesToHex(byte[] data)
        {
            var sb = new StringBuilder(data.Length * 2);
            foreach (byte b in data) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        private static int HexVal(char c) => c >= '0' && c <= '9' ? c - '0' :
            c >= 'A' && c <= 'F' ? c - 'A' + 10 :
            c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0;

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 连接
        // ═══════════════════════════════════════════

        public OperateResult Connect()
        {
            if (_stream.CanRead && _stream.CanWrite)
            {
                OnConnected?.Invoke(this, EventArgs.Empty);
                return OperateResult.Success();
            }
            return OperateResult.Failed("Stream 不可读写");
        }

        public Task<OperateResult> ConnectAsync() => Task.FromResult(Connect());

        public void Disconnect()
        {
            try { _stream.Close(); } catch { }
            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { if (disposing) try { _stream?.Close(); } catch { } }

        // ── Async ──
        public Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public Task<OperateResult<short>> ReadInt16Async(string address) => Task.Run(() => ReadInt16(address));
        public Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.Run(() => ReadUInt16(address));
        public Task<OperateResult<int>> ReadInt32Async(string address) => Task.Run(() => ReadInt32(address));
        public Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.Run(() => ReadUInt32(address));
        public Task<OperateResult<long>> ReadInt64Async(string address) => Task.Run(() => ReadInt64(address));
        public Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.Run(() => ReadUInt64(address));
        public Task<OperateResult<float>> ReadFloatAsync(string address) => Task.Run(() => ReadFloat(address));
        public Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.Run(() => ReadDouble(address));
        public Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.Run(() => ReadString(address, length));
        public Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.Run(() => ReadBytes(address, length));
        public Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, short value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

        // ═══════════════════════════════════════════
        //  IBatchReadWrite 实现
        // ═══════════════════════════════════════════

        /// <inheritdoc/>
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addressList = addresses.ToList();
            if (addressList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");

            var result = new Dictionary<string, object?>();
            foreach (string addr in addressList)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = (object?)r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        /// <inheritdoc/>
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchRead(addresses), cancellationToken);

        /// <inheritdoc/>
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addressList = addresses.ToList();
            if (addressList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");

            var result = new Dictionary<string, byte[]>();
            foreach (string addr in addressList)
            {
                var r = ReadBytes(addr, 2);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        /// <inheritdoc/>
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => RandomRead(addresses), cancellationToken);

        /// <inheritdoc/>
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0)
                return OperateResult.Failed("写入列表不能为空");

            foreach (var kv in itemList)
            {
                OperateResult r = kv.Value switch
                {
                    bool v => Write(kv.Key, v),
                    short v => Write(kv.Key, v),
                    ushort v => Write(kv.Key, v),
                    int v => Write(kv.Key, v),
                    uint v => Write(kv.Key, v),
                    long v => Write(kv.Key, v),
                    ulong v => Write(kv.Key, v),
                    float v => Write(kv.Key, v),
                    double v => Write(kv.Key, v),
                    string v => Write(kv.Key, v),
                    byte[] v => Write(kv.Key, v),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        /// <inheritdoc/>
        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchWrite(items), cancellationToken);

        // ═══════════════════════════════════════════
        //  ISubscribeDevice — 数据订阅接口
        // ═══════════════════════════════════════════

        private readonly object _monitorLock = new object();
        private readonly Dictionary<string, MonitorEntry> _monitors = new Dictionary<string, MonitorEntry>();
        private bool _monitoring;
        private Timer? _monitorTimer;

        private class MonitorEntry
        {
            public string Address = "";
            public string DataType = "Int16";
            public int IntervalMs = 1000;
            public object? LastValue;
        }

        /// <summary>数据变化事件。</summary>
        public event EventHandler<DataChangeEventArgs>? OnDataChanged;

        /// <summary>订阅指定地址的数据变化。</summary>
        public void Subscribe(string address, int intervalMs = 1000, string dataType = "Int16")
        {
            lock (_monitorLock)
            {
                _monitors[address] = new MonitorEntry
                {
                    Address = address,
                    DataType = dataType,
                    IntervalMs = intervalMs,
                    LastValue = null
                };
            }
        }

        /// <summary>取消订阅。</summary>
        public void Unsubscribe(string address)
        {
            lock (_monitorLock) { _monitors.Remove(address); }
        }

        /// <summary>启动所有订阅。</summary>
        public void StartSubscriptions(int globalIntervalMs = 500)
        {
            if (_monitoring) return;
            _monitoring = true;
            _monitorTimer = new Timer(PollMonitors, null, globalIntervalMs, globalIntervalMs);
        }

        /// <summary>停止所有订阅。</summary>
        public void StopSubscriptions()
        {
            _monitoring = false;
            _monitorTimer?.Dispose();
            _monitorTimer = null;
        }

        private void PollMonitors(object? state)
        {
            if (!_monitoring) return;
            try
            {
                List<MonitorEntry> entries;
                lock (_monitorLock) { entries = new List<MonitorEntry>(_monitors.Values); }

                foreach (var entry in entries)
                {
                    try
                    {
                        object? current = entry.DataType switch
                        {
                            "Int16" => ReadInt16(entry.Address).Content,
                            "UInt16" => ReadUInt16(entry.Address).Content,
                            "Int32" => ReadInt32(entry.Address).Content,
                            "Float" => ReadFloat(entry.Address).Content,
                            "Bool" => ReadBool(entry.Address).Content,
                            "String" => ReadString(entry.Address, 10).Content,
                            _ => null
                        };

                        if (current != null && !Equals(current, entry.LastValue))
                        {
                            if (entry.LastValue == null) { entry.LastValue = current; continue; }
                            var args = new DataChangeEventArgs
                            {
                                Address = entry.Address,
                                OldValue = entry.LastValue,
                                NewValue = current,
                                Timestamp = DateTime.Now,
                                Quality = "Good"
                            };
                            entry.LastValue = current;
                            OnDataChanged?.Invoke(this, args);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
