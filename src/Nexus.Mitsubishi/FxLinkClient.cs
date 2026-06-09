using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// 三菱 FX 计算机链接协议客户端 — 支持 FX1N/FX2N/FX3U/FX5U 通过 RS-485 多站通信。
    /// <para>帧格式: ENQ(0x05) + Station(2hex) + CmdAndData + SumCheck(2hex)</para>
    /// <para>命令: 0 = 读, 1 = 写, 7 = 强制 ON, 8 = 强制 OFF</para>
    /// <para>对标 HSL: MitsubishiFxSerial — 计算机链接模式 (带站号)</para>
    /// <para>注意: 本客户端接受 Stream 而非 ISerialPort，以支持 FX-over-TCP/DTU 场景。</para>
    /// </summary>
    public class FxLinkClient : IBatchReadWrite
    {
        private readonly Stream _stream;
        private readonly object _lock = new object();
        protected ILogger Log { get; set; }

        public byte Station { get; set; }
        public int Timeout { get; set; }
        public bool SumCheckEnabled { get; set; } = true;

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;
        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected => _stream?.CanRead == true && _stream?.CanWrite == true;

        public FxLinkClient(Stream stream, byte station = 0, int timeout = 5000)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            Station = station;
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ═══════════════════════════════════════════
        //  FX 计算机链接协议帧收发
        // ═══════════════════════════════════════════

        private OperateResult<string> SendReceive(string cmdAndData)
        {
            try
            {
                lock (_lock)
                {
                    // Frame: ENQ(0x05) + Station(2hex) + CmdAndData + SumCheck(2hex)
                    string body = Station.ToString("D2") + cmdAndData;
                    byte sum = ComputeSum(Encoding.ASCII.GetBytes(body));
                    string frame = "\x05" + body + sum.ToString("X2");

                    Log.Debug($"FX TX → {frame.Replace("\x05", "[ENQ]")}");
                    OnMessageSent?.Invoke(this, "FX Link");

                    _stream.Write(Encoding.ASCII.GetBytes(frame), 0, frame.Length);

                    // Read response
                    int b = ReadByteWithTimeout();
                    if (b < 0) return OperateResult<string>.Failed("读取响应超时");

                    if (b == 0x06) // ACK
                    {
                        OnMessageReceived?.Invoke(this, "FX ACK");
                        return OperateResult<string>.Success("");
                    }

                    if (b == 0x15) // NAK
                    {
                        byte[] errBytes = new byte[2];
                        int deadline = Environment.TickCount + Timeout;
                        if (!ReadExact2(errBytes, deadline))
                            return OperateResult<string>.Failed("NAK 错误码读取超时");
                        string errCode = Encoding.ASCII.GetString(errBytes);
                        return OperateResult<string>.Failed($"FX NAK 错误: {errCode}");
                    }

                    if (b == 0x02) // STX — 读响应带数据
                    {
                        using var ms = new MemoryStream();
                        while (true)
                        {
                            int c = ReadByteWithTimeout();
                            if (c < 0) return OperateResult<string>.Failed("读取数据超时");
                            if (c == 0x03) // ETX
                            {
                                byte[] sumBuf = new byte[2];
                                if (!ReadExact2(sumBuf, Environment.TickCount + Timeout))
                                    return OperateResult<string>.Failed("Sum check 读取超时");
                                break;
                            }
                            ms.WriteByte((byte)c);
                        }
                        string responseData = Encoding.ASCII.GetString(ms.ToArray());
                        OnMessageReceived?.Invoke(this, $"FX Data [{responseData.Length}]");
                        return OperateResult<string>.Success(responseData);
                    }

                    return OperateResult<string>.Failed($"未知响应: 0x{b:X2}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"FX 计算机链接通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult<string>.Failed($"通讯异常: {ex.Message}");
            }
        }

        private int ReadByteWithTimeout()
        {
            int deadline = Environment.TickCount + Timeout;
            while (Environment.TickCount <= deadline)
            {
                try { return _stream.ReadByte(); }
                catch (TimeoutException) { return -1; }
            }
            return -1;
        }

        private bool ReadExact2(byte[] buf, int deadline)
        {
            int offset = 0;
            while (offset < buf.Length && Environment.TickCount <= deadline)
            {
                int n = _stream.Read(buf, offset, buf.Length - offset);
                if (n <= 0) return false;
                offset += n;
            }
            return offset >= buf.Length;
        }

        /// <summary>FX Sum Check: 字节累加取低8位。</summary>
        private static byte ComputeSum(byte[] data)
        {
            byte sum = 0;
            foreach (byte b in data) sum += b;
            return sum;
        }

        // ═══════════════════════════════════════════
        //  FX 地址编码 (计算机链接模式)
        // ═══════════════════════════════════════════

        /// <summary>
        /// FX 地址格式:
        /// "D100" → 数据寄存器 D100 (字)
        /// "M100" → 内部继电器 M100 (位)
        /// "Y0" → 输出 Y0 (位)
        /// "X0" → 输入 X0 (位)
        /// "T100" → 定时器 T100 (位/字)
        /// "C100" → 计数器 C100 (位/字)
        /// </summary>
        private static (char deviceCode, string addressHex, bool isBit) ParseAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("地址不能为空");
            address = address.Trim().ToUpperInvariant();

            char prefix = address[0];
            string numPart = address.Substring(1);
            int num = int.Parse(numPart);

            return prefix switch
            {
                'D' => ('D', num.ToString("X4"), false),   // Data Register (word)
                'M' => ('M', num.ToString("X4"), true),     // Internal Relay (bit)
                'Y' => ('Y', (num / 8).ToString("X2"), true), // Output (bit, octal)
                'X' => ('X', (num / 8).ToString("X2"), true), // Input (bit, octal)
                'T' => ('T', num.ToString("X4"), true),     // Timer (bit)
                'C' => ('C', num.ToString("X4"), true),     // Counter (bit)
                'S' => ('S', num.ToString("X4"), true),     // State (bit)
                'R' => ('R', num.ToString("X4"), false),    // File Register (word)
                'Z' => ('Z', num.ToString("X2"), false),    // Index Register
                'V' => ('V', num.ToString("X2"), false),    // Index Register
                _ => ('D', num.ToString("X4"), false),
            };
        }

        // ═══════════════════════════════════════════
        //  读写命令构建
        // ═══════════════════════════════════════════

        private OperateResult<string> ReadWords(char device, string addrHex, int count)
        {
            string cmd = "0" + device + addrHex + count.ToString("X2");
            return SendReceive(cmd);
        }

        private OperateResult<string> ReadBits(char device, string addrHex, int count)
        {
            string cmd = "0" + device + addrHex + count.ToString("X2");
            return SendReceive(cmd);
        }

        private OperateResult WriteWords(char device, string addrHex, string hexData)
        {
            int count = hexData.Length / 4;
            string cmd = "1" + device + addrHex + count.ToString("X2") + hexData;
            var r = SendReceive(cmd);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        private OperateResult WriteBits(char device, string addrHex, string bitData)
        {
            int count = bitData.Length;
            string cmd = "1" + device + addrHex + count.ToString("X2") + bitData;
            var r = SendReceive(cmd);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 类型化读写
        // ═══════════════════════════════════════════

        public OperateResult<bool> ReadBool(string address)
        {
            var (device, addrHex, isBit) = ParseAddress(address);
            var r = ReadBits(device, addrHex, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Trim() == "1");
        }

        public OperateResult<short> ReadInt16(string address)
        {
            var (device, addrHex, _) = ParseAddress(address);
            var r = ReadWords(device, addrHex, 1);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(unchecked((short)Convert.ToUInt16(r.Content.Trim(), 16)));
        }

        public OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success(unchecked((ushort)r.Content)) : OperateResult<ushort>.Failed(r.Message);
        }

        public OperateResult<int> ReadInt32(string address)
        {
            var (device, addrHex, _) = ParseAddress(address);
            var r = ReadWords(device, addrHex, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success(unchecked((int)Convert.ToUInt32(r.Content.Trim(), 16)));
        }

        public OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success(unchecked((uint)r.Content)) : OperateResult<uint>.Failed(r.Message);
        }

        public OperateResult<long> ReadInt64(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<long>.Success((long)r.Content) : OperateResult<long>.Failed(r.Message);
        }

        public OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success(unchecked((ulong)r.Content)) : OperateResult<ulong>.Failed(r.Message);
        }

        public unsafe OperateResult<float> ReadFloat(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            int v = r.Content;
            return OperateResult<float>.Success(*(float*)&v);
        }

        public OperateResult<double> ReadDouble(string address) => OperateResult<double>.Failed("FX 不支持 Double");

        public OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content).TrimEnd('\0'));
        }

        public OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var (d, a, _) = ParseAddress(address);
            int cnt = (length + 1) / 2;
            var r = ReadWords(d, a, cnt);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message);
            byte[] raw = HexToBytes(r.Content);
            byte[] result = new byte[length];
            Array.Copy(raw, result, Math.Min(length, raw.Length));
            return OperateResult<byte[]>.Success(result);
        }

        // ── 写入 ──────────────────────────────────

        public OperateResult Write(string address, bool value)
        {
            var (d, a, _) = ParseAddress(address);
            return WriteBits(d, a, value ? "1" : "0");
        }

        public OperateResult Write(string address, short value)
        {
            var (d, a, _) = ParseAddress(address);
            return WriteWords(d, a, unchecked((ushort)value).ToString("X4"));
        }

        public OperateResult Write(string address, ushort value) => Write(address, unchecked((short)value));

        public OperateResult Write(string address, int value)
        {
            var (d, a, _) = ParseAddress(address);
            return WriteWords(d, a, unchecked((uint)value).ToString("X8"));
        }

        public OperateResult Write(string address, uint value) => Write(address, unchecked((int)value));
        public OperateResult Write(string address, long value) => Write(address, unchecked((int)value));
        public OperateResult Write(string address, ulong value) => Write(address, unchecked((int)value));
        public unsafe OperateResult Write(string address, float value) => Write(address, *(int*)&value);
        public OperateResult Write(string address, double value) => Write(address, (float)value);

        public OperateResult Write(string address, string value)
        {
            var (d, a, _) = ParseAddress(address);
            byte[] bytes = Encoding.ASCII.GetBytes(value ?? "");
            if (bytes.Length % 2 != 0) Array.Resize(ref bytes, bytes.Length + 1);
            return WriteWords(d, a, BytesToHex(bytes));
        }

        public OperateResult Write(string address, byte[] data)
        {
            var (d, a, _) = ParseAddress(address);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteWords(d, a, BytesToHex(data));
        }

        // ═══════════════════════════════════════════
        //  连接生命周期
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

        // ═══════════════════════════════════════════
        //  异步覆写
        // ═══════════════════════════════════════════

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
        //  工具方法
        // ═══════════════════════════════════════════

        private static byte[] HexToBytes(string hex)
        {
            byte[] r = new byte[hex.Length / 2];
            for (int i = 0; i < r.Length; i++)
                r[i] = (byte)(HexVal(hex[i * 2]) << 4 | HexVal(hex[i * 2 + 1]));
            return r;
        }

        private static string BytesToHex(byte[] d)
        {
            var sb = new StringBuilder(d.Length * 2);
            foreach (byte b in d) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        private static int HexVal(char c) =>
            c >= '0' && c <= '9' ? c - '0' :
            c >= 'A' && c <= 'F' ? c - 'A' + 10 :
            c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0;

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
        // ═══════════════════════════════════════════

        /// <summary>批量读取多个地址的值。</summary>
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, object?>();
            foreach (var addr in addrList)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        /// <summary>批量读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

        /// <summary>随机读取多个不连续地址（返回原始字节）。</summary>
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList)
            {
                var r = ReadBytes(addr, 1);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        /// <summary>随机读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(RandomRead(addresses));

        /// <summary>批量写入多个地址的值。</summary>
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0)
                return OperateResult.Failed("写入列表不能为空");
            foreach (var kv in itemList)
            {
                OperateResult r = kv.Value switch
                {
                    bool b => Write(kv.Key, b),
                    short s => Write(kv.Key, s),
                    ushort us => Write(kv.Key, us),
                    int i => Write(kv.Key, i),
                    uint ui => Write(kv.Key, ui),
                    float f => Write(kv.Key, f),
                    string s => Write(kv.Key, s),
                    byte[] b => Write(kv.Key, b),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        /// <summary>批量写入（异步）。</summary>
        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));
    }
}
