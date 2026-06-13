using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Nexus.Fuji
{
    /// <summary>
    /// 富士 SPH/SPB 系列 PLC 通讯客户端 — S-BUS 协议。
    /// <para>帧格式: STX(0x02) + StationNo(2hex) + Command(2hex) + Data + ETX(0x03) + BCC(2hex)</para>
    /// <para>对标 HSL: FujiSPH — Read/Write D/C/T/M 寄存器, 批量位, PLC控制</para>
    /// </summary>
    public class FujiSphClient : IBatchReadWrite
    {
        private readonly Stream _stream;
        private readonly object _lock = new object();
        protected ILogger Log { get; set; }

        public byte Station { get; set; }
        public int Timeout { get; set; }

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;
        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected => _stream?.CanRead == true && _stream?.CanWrite == true;

        public FujiSphClient(Stream stream, byte station = 1, int timeout = 5000)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            Station = station;
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ═══════════════════════════════════════════
        //  S-BUS 帧构建
        // ═══════════════════════════════════════════

        private OperateResult<string> SendReceive(string command, string data)
        {
            try
            {
                lock (_lock)
                {
                    // Frame: STX + Station(2) + Command(2) + Data + ETX + BCC(2)
                    string body = Station.ToString("D2") + command + data;
                    string frame = "\x02" + body + "\x03";
                    byte bcc = ComputeBcc(frame.Substring(1)); // from Station to ETX
                    frame += bcc.ToString("X2");

                    Log.Debug($"TX → {frame.Replace("\x02", "[STX]").Replace("\x03", "[ETX]")}");
                    OnMessageSent?.Invoke(this, $"S-BUS Cmd={command}");

                    _stream.Write(Encoding.ASCII.GetBytes(frame), 0, frame.Length);

                    // Read response
                    string? response = ReadFrame();
                    if (response == null)
                        return OperateResult<string>.Failed("读取响应超时");

                    Log.Debug($"RX ← Response [{response.Length} chars]");
                    OnMessageReceived?.Invoke(this, $"S-BUS Response");

                    if (response.Length < 7)
                        return OperateResult<string>.Failed("响应太短");

                    // Verify BCC
                    string respBody = response.Substring(1, response.Length - 3);
                    byte expBcc = ComputeBcc(respBody);
                    string respBcc = response.Substring(response.Length - 2);
                    if (expBcc.ToString("X2") != respBcc)
                        return OperateResult<string>.Failed($"BCC 校验失败");

                    // Check error: command field = "FF"
                    string respCmd = response.Substring(3, 2);
                    if (respCmd == "FF")
                    {
                        string errData = response.Length > 5 ? response.Substring(5, response.Length - 7) : "";
                        return OperateResult<string>.Failed($"PLC 错误: {errData}");
                    }

                    // Extract data: skip STX(1)+Station(2)+Command(2), before ETX+BCC(3)
                    return OperateResult<string>.Success(response.Substring(5, response.Length - 8));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"S-BUS 通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult<string>.Failed($"通讯异常: {ex.Message}");
            }
        }

        private string? ReadFrame()
        {
            int deadline = Environment.TickCount + Timeout;
            using var ms = new MemoryStream();

            // Wait for STX
            while (Environment.TickCount <= deadline)
            {
                int b = ReadByteWithTimeout(deadline);
                if (b < 0) return null;
                if (b == 0x02) { ms.WriteByte((byte)b); break; }
            }

            // Read until ETX
            while (Environment.TickCount <= deadline)
            {
                int b = ReadByteWithTimeout(deadline);
                if (b < 0) return null;
                ms.WriteByte((byte)b);
                if (b == 0x03)
                {
                    // Read BCC (2 chars)
                    for (int i = 0; i < 2; i++)
                    {
                        b = ReadByteWithTimeout(deadline);
                        if (b < 0) return null;
                        ms.WriteByte((byte)b);
                    }
                    return Encoding.ASCII.GetString(ms.ToArray());
                }
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

        private static byte ComputeBcc(string data)
        {
            byte bcc = 0;
            foreach (char c in data)
                bcc ^= (byte)c;
            return bcc;
        }

        // ═══════════════════════════════════════════
        //  地址映射
        // ═══════════════════════════════════════════

        /// <summary>
        /// 富士地址: "D100"(数据寄存器), "M100"(内部继电器), "C100"(计数器),
        /// "T100"(定时器), "X100"(输入), "Y100"(输出)
        /// </summary>
        private static (string areaCode, int address) ParseAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("地址不能为空");
            address = address.Trim().ToUpperInvariant();
            char prefix = address[0];
            int num = int.Parse(address.Substring(1));

            return prefix switch
            {
                'D' => ("01", num),   // Data Register
                'M' => ("02", num),   // Internal Relay
                'X' => ("03", num),   // Input
                'Y' => ("04", num),   // Output
                'T' => ("05", num),   // Timer
                'C' => ("06", num),   // Counter
                'R' => ("07", num),   // File Register
                'L' => ("08", num),   // Link Register
                _ => ("01", int.Parse(address)),
            };
        }

        // ═══════════════════════════════════════════
        //  读写命令
        // ═══════════════════════════════════════════

        // Commands: RR=Read Registers, WR=Write Registers, RS=Read Bit, WS=Write Bit
        private OperateResult<string> ReadRegs(string area, int startAddr, int count)
        {
            string data = area + startAddr.ToString("D4") + count.ToString("D4");
            return SendReceive("RR", data);
        }

        private OperateResult WriteRegs(string area, int startAddr, string hexData)
        {
            string data = area + startAddr.ToString("D4") + hexData;
            var r = SendReceive("WR", data);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        public OperateResult<bool> ReadBool(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadRegs(area, addr, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Trim() != "0000");
        }

        public OperateResult<short> ReadInt16(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadRegs(area, addr, 1);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success((short)Convert.ToUInt16(r.Content.Trim(), 16));
        }

        public OperateResult<ushort> ReadUInt16(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode); }
        public OperateResult<int> ReadInt32(string address) { var (a, o) = ParseAddress(address); var r = ReadRegs(a, o, 2); if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode); return OperateResult<int>.Success((int)Convert.ToUInt32(r.Content.Trim(), 16)); }
        public OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode); }
        public OperateResult<long> ReadInt64(string address) { var r = ReadUInt64(address); return r.IsSuccess ? OperateResult<long>.Success(unchecked((long)r.Content)) : OperateResult<long>.Failed(r.Message, r.ErrorCode); }
        public OperateResult<ulong> ReadUInt64(string address) { var (a, o) = ParseAddress(address); var r = ReadRegs(a, o, 4); if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message, r.ErrorCode); return OperateResult<ulong>.Success(Convert.ToUInt64(r.Content.Trim(), 16)); }
        public unsafe OperateResult<float> ReadFloat(string address) { var r = ReadInt32(address); if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode); int v = r.Content; return OperateResult<float>.Success(*(float*)&v); }
        public unsafe OperateResult<double> ReadDouble(string address) { var r = ReadUInt64(address); if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode); ulong v = r.Content; return OperateResult<double>.Success(*(double*)&v); }
        public OperateResult<string> ReadString(string address, ushort length) { var r = ReadBytes(address, length); if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message); return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content).TrimEnd('\0')); }
        public OperateResult<byte[]> ReadBytes(string address, ushort length) { var (a, o) = ParseAddress(address); int cnt = (length + 1) / 2; var r = ReadRegs(a, o, cnt); if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode); byte[] raw = HexToBytes(r.Content); if (raw.Length < length) return OperateResult<byte[]>.Failed("S-BUS 响应数据不足"); byte[] data = new byte[length]; Array.Copy(raw, data, length); return OperateResult<byte[]>.Success(data); }

        public OperateResult Write(string address, bool value) { var (a, o) = ParseAddress(address); return WriteRegs(a, o, value ? "0001" : "0000"); }
        public OperateResult Write(string address, short value) { var (a, o) = ParseAddress(address); return WriteRegs(a, o, ((ushort)value).ToString("X4")); }
        public OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public OperateResult Write(string address, int value) { var (a, o) = ParseAddress(address); return WriteRegs(a, o, ((uint)value).ToString("X8")); }
        public OperateResult Write(string address, uint value) => Write(address, (int)value);
        public OperateResult Write(string address, long value) => Write(address, unchecked((ulong)value));
        public OperateResult Write(string address, ulong value) { var (a, o) = ParseAddress(address); return WriteRegs(a, o, value.ToString("X16")); }
        public unsafe OperateResult Write(string address, float value) => Write(address, *(int*)&value);
        public unsafe OperateResult Write(string address, double value) => Write(address, *(ulong*)&value);
        public OperateResult Write(string address, string value) { var (a, o) = ParseAddress(address); byte[] bytes = Encoding.ASCII.GetBytes(value ?? ""); if (bytes.Length % 2 != 0) Array.Resize(ref bytes, bytes.Length + 1); return WriteRegs(a, o, BytesToHex(bytes)); }
        public OperateResult Write(string address, byte[] data) { if (data == null) return OperateResult.Failed("写入数据不能为空"); var (a, o) = ParseAddress(address); if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1); return WriteRegs(a, o, BytesToHex(data)); }

        private static byte[] HexToBytes(string hex) { byte[] r = new byte[hex.Length / 2]; for (int i = 0; i < r.Length; i++) r[i] = (byte)(HexV(hex[i * 2]) << 4 | HexV(hex[i * 2 + 1])); return r; }
        private static string BytesToHex(byte[] d) { var sb = new StringBuilder(d.Length * 2); foreach (byte b in d) sb.Append(b.ToString("X2")); return sb.ToString(); }
        private static int HexV(char c) => c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'F' ? c - 'A' + 10 : c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0;

        // ═══════════════════════════════════════════
        //  批量位操作 — ReadBools / WriteBools
        // ═══════════════════════════════════════════

        /// <summary>
        /// 批量读取位地址（S-BUS RS 命令读取位状态）。
        /// </summary>
        public OperateResult<bool[]> ReadBools(string address, ushort count)
        {
            if (count == 0) return OperateResult<bool[]>.Success(Array.Empty<bool>());
            if (count == 1)
            {
                var r = ReadBool(address);
                return r.IsSuccess ? OperateResult<bool[]>.Success(new[] { r.Content }) : OperateResult<bool[]>.Failed(r.Message);
            }

            try
            {
                var (area, addr) = ParseAddress(address);
                // S-BUS RS (Read Bit): read count words, extract bits
                int wordCount = (count + 15) / 16;
                var r = ReadRegs(area, addr, wordCount);
                if (!r.IsSuccess) return OperateResult<bool[]>.Failed(r.Message);

                // 解析 hex 数据为位数组
                byte[] raw = HexToBytes(r.Content.Trim());
                var result = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    int byteIdx = i / 8;
                    int bitIdx = i % 8;
                    if (byteIdx < raw.Length)
                        result[i] = (raw[byteIdx] & (1 << bitIdx)) != 0;
                }
                return OperateResult<bool[]>.Success(result);
            }
            catch (Exception ex) { return OperateResult<bool[]>.Failed(ex.Message); }
        }

        /// <summary>
        /// 批量写入位地址（S-BUS WS 命令）。
        /// </summary>
        public OperateResult WriteBools(string address, bool[] values)
        {
            if (values == null || values.Length == 0) return OperateResult.Success();
            if (values.Length == 1) return Write(address, values[0]);

            try
            {
                var (area, addr) = ParseAddress(address);
                int wordCount = (values.Length + 15) / 16;
                byte[] raw = new byte[wordCount * 2];
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i])
                        raw[i / 8] |= (byte)(1 << (i % 8));
                }
                return WriteRegs(area, addr, BytesToHex(raw));
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  PLC 控制命令
        // ═══════════════════════════════════════════

        /// <summary>读取 PLC 型号信息。</summary>
        public OperateResult<string> ReadPlcModel()
        {
            try
            {
                // 富士 PLC 型号在 SD0-SD4 (系统数据区)
                var r = ReadRegs("00", 0, 4);
                if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
                return OperateResult<string>.Success(r.Content.Trim());
            }
            catch (Exception ex) { return OperateResult<string>.Failed(ex.Message); }
        }

        // ── 批量位异步 ──
        public Task<OperateResult<bool[]>> ReadBoolsAsync(string address, ushort count) => Task.Run(() => ReadBools(address, count));
        public Task<OperateResult> WriteBoolsAsync(string address, bool[] values) => Task.Run(() => WriteBools(address, values));
        public Task<OperateResult<string>> ReadPlcModelAsync() => Task.Run(() => ReadPlcModel());

        // 连接
        public OperateResult Connect() { if (_stream.CanRead && _stream.CanWrite) { OnConnected?.Invoke(this, EventArgs.Empty); return OperateResult.Success(); } return OperateResult.Failed("Stream 不可读写"); }
        public Task<OperateResult> ConnectAsync() => Task.FromResult(Connect());
        public void Disconnect() { try { _stream.Close(); } catch { } OnDisconnected?.Invoke(this, EventArgs.Empty); }
        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { if (disposing) try { _stream?.Close(); } catch { } }

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
