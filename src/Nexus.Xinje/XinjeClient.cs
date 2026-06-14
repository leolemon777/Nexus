using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;

namespace Nexus.Xinje
{
    /// <summary>
    /// 信捷 Xinje 通讯客户端 — 支持 XG/XC/XL 系列。
    /// 信捷 PLC 支持 Modbus RTU/TCP 兼容协议 + 自有 XNet 协议。
    /// 本客户端实现 Modbus TCP 兼容模式 (默认端口 502)。
    /// <para>对标 HSL: XinJETcpNet — Read/Write D/HD/SD/SM/M 区域, 批量位, PLC控制</para>
    /// </summary>
    public class XinjeClient : IBatchReadWrite, ISubscribeDevice
    {
        private readonly object _lock = new object();
        private TcpClient? _tcp;
        private Stream? _stream;
        private bool _isConnected;
        private int _transactionId;
        protected ILogger Log { get; set; }

        public string IpAddress { get; }
        public int Port { get; }
        public byte Station { get; set; }
        public int Timeout { get; set; }

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;
        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected { get { lock (_lock) return _isConnected && _tcp?.Connected == true; } }

        public XinjeClient(string ipAddress, int port = 502, byte station = 1, int timeout = 5000)
        {
            IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            Port = port; Station = station; Timeout = timeout; Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        public OperateResult Connect() { try { lock (_lock) { if (_isConnected) return OperateResult.Success(); _tcp = new TcpClient(); var ar = _tcp.BeginConnect(IpAddress, Port, null, null); if (!ar.AsyncWaitHandle.WaitOne(Timeout, false)) { _tcp.Close(); _tcp = null; return OperateResult.Failed("连接超时"); } _tcp.EndConnect(ar); _stream = _tcp.GetStream(); _stream.ReadTimeout = Timeout; _stream.WriteTimeout = Timeout; _isConnected = true; } OnConnected?.Invoke(this, EventArgs.Empty); return OperateResult.Success(); } catch (Exception ex) { OnError?.Invoke(this, ex.Message); return OperateResult.Failed(ex.Message); } }
        public async Task<OperateResult> ConnectAsync() { try { _tcp = new TcpClient(); await _tcp.ConnectAsync(IpAddress, Port).ConfigureAwait(false); lock (_lock) { _stream = _tcp.GetStream(); _stream.ReadTimeout = Timeout; _stream.WriteTimeout = Timeout; _isConnected = true; } OnConnected?.Invoke(this, EventArgs.Empty); return OperateResult.Success(); } catch (Exception ex) { OnError?.Invoke(this, ex.Message); return OperateResult.Failed(ex.Message); } }
        public void Disconnect() { lock (_lock) { _isConnected = false; try { _stream?.Close(); } catch { } try { _tcp?.Close(); } catch { } _stream = null; _tcp = null; } OnDisconnected?.Invoke(this, EventArgs.Empty); }
        public void Dispose() { Disconnect(); GC.SuppressFinalize(this); }

        // Modbus TCP frame: MBAP(7) + PDU
        private ushort NextTid() => (ushort)(Interlocked.Increment(ref _transactionId) & 0xFFFF);

        private OperateResult<byte[]> SendReceive(byte[] pdu)
        {
            lock (_lock)
            {
                if (_stream == null || !_isConnected) return OperateResult<byte[]>.Failed("未连接");
                try
                {
                    ushort tid = NextTid();
                    int len = pdu.Length + 1;
                    var frame = new byte[7 + pdu.Length];
                    frame[0] = (byte)(tid >> 8); frame[1] = (byte)tid;
                    frame[2] = 0; frame[3] = 0;
                    frame[4] = (byte)(len >> 8); frame[5] = (byte)(len & 0xFF);
                    frame[6] = Station;
                    Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);

                    OnMessageSent?.Invoke(this, BitConverter.ToString(frame));
                    _stream.Write(frame, 0, frame.Length);
                    _stream.Flush();

                    // Read MBAP header (7 bytes)
                    var header = new byte[7];
                    int read = 0;
                    while (read < 7) { int n = _stream.Read(header, read, 7 - read); if (n <= 0) return OperateResult<byte[]>.Failed("无响应"); read += n; }

                    int respLen = (header[4] << 8) | header[5];
                    int payloadLen = respLen - 1;
                    var payload = new byte[payloadLen];
                    if (payloadLen > 0) { read = 0; while (read < payloadLen) { int n = _stream.Read(payload, read, payloadLen - read); if (n <= 0) break; read += n; } }

                    // Check Modbus exception
                    if (payload.Length > 0 && (payload[0] & 0x80) != 0)
                        return OperateResult<byte[]>.Failed($"Modbus exception: 0x{payload[1]:X2}");

                    OnMessageReceived?.Invoke(this, BitConverter.ToString(header));
                    return OperateResult<byte[]>.Success(payload);
                }
                catch (Exception ex) { _isConnected = false; return OperateResult<byte[]>.Failed(ex.Message); }
            }
        }

        // Address: D100, HD100, SD100, C100, Y0, X0, M100, T0, S100
        private static (ushort startAddr, byte function) ParseAddress(string address)
        {
            string addr = address.Trim().ToUpperInvariant();
            if (addr.Length < 2) throw new ArgumentException($"Invalid Xinje address: {address}");
            char p = addr[0];
            string numStr;
            ushort baseAddr;

            switch (p)
            {
                case 'D': numStr = addr.Substring(1); baseAddr = 0; break;
                case 'H': numStr = addr.Substring(2); baseAddr = 0x8000; break; // HD
                case 'S': numStr = addr.Substring(2); baseAddr = 0xC000; break; // SD
                case 'Y': numStr = addr.Substring(1); baseAddr = 0; return (ParseUShort(numStr), 0x01);
                case 'X': numStr = addr.Substring(1); baseAddr = 0; return (ParseUShort(numStr), 0x02);
                case 'M': numStr = addr.Substring(1); baseAddr = 0x0800; return ((ushort)(baseAddr + ParseUShort(numStr)), 0x01);
                case 'C': numStr = addr.Substring(1); baseAddr = 0x1000; break;
                case 'T': numStr = addr.Substring(1); baseAddr = 0x0600; break;
                default: numStr = addr.Substring(1); baseAddr = 0; break;
            }
            return ((ushort)(baseAddr + ParseUShort(numStr)), 0x03);
        }

        private static ushort ParseUShort(string s) => ushort.TryParse(s, out var v) ? v : throw new FormatException($"Invalid: {s}");

        public OperateResult<bool> ReadBool(string address) { try { var (addr, fc) = ParseAddress(address); var pdu = fc == 0x01 ? new byte[] { 0x01, (byte)(addr >> 8), (byte)addr, 0x00, 0x01 } : new byte[] { 0x02, (byte)(addr >> 8), (byte)addr, 0x00, 0x01 }; var r = SendReceive(pdu); if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message); if (r.Content.Length < 4) return OperateResult<bool>.Failed("响应过短"); return OperateResult<bool>.Success((r.Content[3] & 0x01) != 0); } catch (Exception ex) { return OperateResult<bool>.Failed(ex.Message); } }

        public OperateResult<short> ReadInt16(string address) { try { var (addr, _) = ParseAddress(address); var pdu = new byte[] { 0x03, (byte)(addr >> 8), (byte)addr, 0x00, 0x01 }; var r = SendReceive(pdu); if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message); if (r.Content.Length < 4) return OperateResult<short>.Failed("响应过短"); return OperateResult<short>.Success((short)((r.Content[3] << 8) | r.Content[4])); } catch (Exception ex) { return OperateResult<short>.Failed(ex.Message); } }

        public OperateResult<ushort> ReadUInt16(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message); }
        public OperateResult<int> ReadInt32(string address) { var lo = ReadInt16(address); if (!lo.IsSuccess) return OperateResult<int>.Failed(lo.Message); var hi = ReadInt16(Incr(address)); return OperateResult<int>.Success((hi.Content << 16) | (lo.Content & 0xFFFF)); }
        public OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message); }
        public OperateResult<long> ReadInt64(string address) { var lo = ReadInt32(address); if (!lo.IsSuccess) return OperateResult<long>.Failed(lo.Message); var hi = ReadInt32(Incr(Incr(address))); return OperateResult<long>.Success(((long)hi.Content << 32) | (uint)lo.Content); }
        public OperateResult<ulong> ReadUInt64(string address) { var r = ReadInt64(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message); }
        public OperateResult<float> ReadFloat(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(r.Content), 0)) : OperateResult<float>.Failed(r.Message); }
        public OperateResult<double> ReadDouble(string address) { var r = ReadInt64(address); return r.IsSuccess ? OperateResult<double>.Success(BitConverter.Int64BitsToDouble(r.Content)) : OperateResult<double>.Failed(r.Message); }
        public OperateResult<string> ReadString(string address, ushort length) { var r = ReadBytes(address, (ushort)(length * 2)); return r.IsSuccess ? OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content).TrimEnd('\0')) : OperateResult<string>.Failed(r.Message); }
        public OperateResult<byte[]> ReadBytes(string address, ushort length) { var result = new List<byte>(); for (int i = 0; i < (length + 1) / 2; i++) { var r = ReadInt16(Incr(address, i)); if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message); result.AddRange(BitConverter.GetBytes(r.Content)); } return OperateResult<byte[]>.Success(result.ToArray()); }

        public OperateResult Write(string address, bool value) { try { var (addr, fc) = ParseAddress(address); var pdu = new byte[] { 0x05, (byte)(addr >> 8), (byte)addr, (byte)(value ? 0xFF : 0x00), value ? (byte)0x00 : (byte)0x00 }; var r = SendReceive(pdu); return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message); } catch (Exception ex) { return OperateResult.Failed(ex.Message); } }
        public OperateResult Write(string address, short value) { try { var (addr, _) = ParseAddress(address); var pdu = new byte[] { 0x06, (byte)(addr >> 8), (byte)addr, (byte)(value >> 8), (byte)(value & 0xFF) }; var r = SendReceive(pdu); return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message); } catch (Exception ex) { return OperateResult.Failed(ex.Message); } }
        public OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public OperateResult Write(string address, int value) { var r1 = Write(address, (short)(value & 0xFFFF)); if (!r1.IsSuccess) return r1; return Write(Incr(address), (short)((value >> 16) & 0xFFFF)); }
        public OperateResult Write(string address, uint value) => Write(address, (int)value);
        public OperateResult Write(string address, long value) { var r1 = Write(address, (int)(value & 0xFFFFFFFF)); if (!r1.IsSuccess) return r1; return Write(Incr(address, 2), (int)((value >> 32) & 0xFFFFFFFF)); }
        public OperateResult Write(string address, ulong value) => Write(address, (long)value);
        public OperateResult Write(string address, float value) => Write(address, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));
        public OperateResult Write(string address, double value) => Write(address, (long)BitConverter.DoubleToInt64Bits(value));
        public OperateResult Write(string address, string value) => Write(address, Encoding.ASCII.GetBytes(value));
        public OperateResult Write(string address, byte[] data) { if (data == null) return OperateResult.Failed("写入数据不能为空"); for (int i = 0; i < data.Length; i += 2) { short v = data.Length > i + 1 ? (short)(data[i] | (data[i + 1] << 8)) : data[i]; var r = Write(Incr(address, i / 2), v); if (!r.IsSuccess) return r; } return OperateResult.Success(); }

        public Task<OperateResult<bool>> ReadBoolAsync(string a) => Task.Run(() => ReadBool(a));
        public Task<OperateResult<short>> ReadInt16Async(string a) => Task.Run(() => ReadInt16(a));
        public Task<OperateResult<ushort>> ReadUInt16Async(string a) => Task.Run(() => ReadUInt16(a));
        public Task<OperateResult<int>> ReadInt32Async(string a) => Task.Run(() => ReadInt32(a));
        public Task<OperateResult<uint>> ReadUInt32Async(string a) => Task.Run(() => ReadUInt32(a));
        public Task<OperateResult<long>> ReadInt64Async(string a) => Task.Run(() => ReadInt64(a));
        public Task<OperateResult<ulong>> ReadUInt64Async(string a) => Task.Run(() => ReadUInt64(a));
        public Task<OperateResult<float>> ReadFloatAsync(string a) => Task.Run(() => ReadFloat(a));
        public Task<OperateResult<double>> ReadDoubleAsync(string a) => Task.Run(() => ReadDouble(a));
        public Task<OperateResult<string>> ReadStringAsync(string a, ushort l) => Task.Run(() => ReadString(a, l));
        public Task<OperateResult<byte[]>> ReadBytesAsync(string a, ushort l) => Task.Run(() => ReadBytes(a, l));
        public Task<OperateResult> WriteAsync(string a, bool v) => Task.Run(() => Write(a, v));
        public Task<OperateResult> WriteAsync(string a, short v) => Task.Run(() => Write(a, v));
        public Task<OperateResult> WriteAsync(string a, int v) => Task.Run(() => Write(a, v));
        public Task<OperateResult> WriteAsync(string a, float v) => Task.Run(() => Write(a, v));
        public Task<OperateResult> WriteAsync(string a, string v) => Task.Run(() => Write(a, v));
        public Task<OperateResult> WriteAsync(string a, byte[] v) => Task.Run(() => Write(a, v));
        public Task<OperateResult> WriteAsync(string a, ushort v) => Task.Run(() => Write(a, v));
        public Task<OperateResult> WriteAsync(string a, uint v) => Task.Run(() => Write(a, v));
        public Task<OperateResult> WriteAsync(string a, long v) => Task.Run(() => Write(a, v));
        public Task<OperateResult> WriteAsync(string a, ulong v) => Task.Run(() => Write(a, v));
        public Task<OperateResult> WriteAsync(string a, double v) => Task.Run(() => Write(a, v));

        private static string Incr(string address, int offset = 1) { var (a, _) = ParseAddress(address); return $"D{a + offset}"; }

        // ═══════════════════════════════════════════
        //  批量位操作 — ReadBools / WriteBools
        // ═══════════════════════════════════════════

        /// <summary>
        /// 批量读取位地址（FC01/FC02）。
        /// <para>支持 Y/X/M/S/T/C 区域，自动分包（每包最多 2000 位）。</para>
        /// </summary>
        public OperateResult<bool[]> ReadBools(string address, ushort count)
        {
            if (count == 0) return OperateResult<bool[]>.Success(Array.Empty<bool>());
            if (count == 1) { var r = ReadBool(address); return r.IsSuccess ? OperateResult<bool[]>.Success(new[] { r.Content }) : OperateResult<bool[]>.Failed(r.Message); }

            try
            {
                var (addr, fc) = ParseAddress(address);
                byte readFc = fc == 0x01 ? (byte)0x01 : (byte)0x02;
                const int maxPerRequest = 2000;
                var result = new bool[count];
                int offset = 0;

                while (offset < count)
                {
                    int batch = Math.Min(count - offset, maxPerRequest);
                    ushort batchAddr = (ushort)(addr + offset);
                    byte[] pdu = { readFc, (byte)(batchAddr >> 8), (byte)batchAddr, (byte)(batch >> 8), (byte)(batch & 0xFF) };
                    var r = SendReceive(pdu);
                    if (!r.IsSuccess) return OperateResult<bool[]>.Failed(r.Message);
                    if (r.Content.Length < 2) return OperateResult<bool[]>.Failed("响应过短");

                    byte byteCount = r.Content[1];
                    for (int i = 0; i < batch; i++)
                    {
                        int byteIdx = 2 + i / 8;
                        int bitIdx = i % 8;
                        if (byteIdx < r.Content.Length)
                            result[offset + i] = (r.Content[byteIdx] & (1 << bitIdx)) != 0;
                    }
                    offset += batch;
                }
                return OperateResult<bool[]>.Success(result);
            }
            catch (Exception ex) { return OperateResult<bool[]>.Failed(ex.Message); }
        }

        /// <summary>
        /// 批量写入位地址（FC0F Write Multiple Coils）。
        /// </summary>
        public OperateResult WriteBools(string address, bool[] values)
        {
            if (values == null || values.Length == 0) return OperateResult.Success();
            if (values.Length == 1) return Write(address, values[0]);

            try
            {
                var (addr, _) = ParseAddress(address);
                const int maxPerRequest = 1968;
                int offset = 0;

                while (offset < values.Length)
                {
                    int batch = Math.Min(values.Length - offset, maxPerRequest);
                    int byteCount = (batch + 7) / 8;
                    byte[] bytes = new byte[byteCount];
                    for (int i = 0; i < batch; i++) { if (values[offset + i]) bytes[i / 8] |= (byte)(1 << (i % 8)); }

                    ushort batchAddr = (ushort)(addr + offset);
                    // FC0F: Write Multiple Coils (Modbus TCP)
                    int pduLen = 6 + byteCount;
                    byte[] pdu = new byte[1 + 5 + byteCount]; // fc + addr(2) + count(2) + bytecount(1) + data
                    pdu[0] = 0x0F;
                    pdu[1] = (byte)(batchAddr >> 8); pdu[2] = (byte)batchAddr;
                    pdu[3] = (byte)(batch >> 8); pdu[4] = (byte)(batch & 0xFF);
                    pdu[5] = (byte)byteCount;
                    Buffer.BlockCopy(bytes, 0, pdu, 6, byteCount);

                    var r = SendReceive(pdu);
                    if (!r.IsSuccess) return OperateResult.Failed(r.Message);
                    offset += batch;
                }
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  PLC 控制命令
        // ═══════════════════════════════════════════

        /// <summary>读取 PLC 型号（信捷 SD0 寄存器存储型号信息）。</summary>
        public OperateResult<string> ReadPlcModel()
        {
            try
            {
                // 信捷 XC/XG 系列 PLC 型号在特殊寄存器 SD0
                ushort modelAddr = 0xC000; // SD0 → ParseAddress "SD0" → 0xC000
                byte[] pdu = { 0x03, (byte)(modelAddr >> 8), (byte)modelAddr, 0x00, 0x10 };
                var r = SendReceive(pdu);
                if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
                if (r.Content.Length < 4) return OperateResult<string>.Failed("响应过短");
                // 跳过 FC + byteCount 前缀, 读取寄存器数据
                int dataStart = r.Content.Length > 3 ? 2 : 0;
                string model = System.Text.Encoding.ASCII.GetString(r.Content, dataStart, Math.Min(r.Content.Length - dataStart, 32)).TrimEnd('\0', ' ');
                return OperateResult<string>.Success(string.IsNullOrEmpty(model) ? "Unknown Xinje" : model);
            }
            catch (Exception ex) { return OperateResult<string>.Failed(ex.Message); }
        }

        // ── 批量位异步 ──
        public Task<OperateResult<bool[]>> ReadBoolsAsync(string address, ushort count) => Task.Run(() => ReadBools(address, count));
        public Task<OperateResult> WriteBoolsAsync(string address, bool[] values) => Task.Run(() => WriteBools(address, values));
        public Task<OperateResult<string>> ReadPlcModelAsync() => Task.Run(() => ReadPlcModel());

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
                    long l => Write(kv.Key, l),
                    ulong ul => Write(kv.Key, ul),
                    float f => Write(kv.Key, f),
                    double d => Write(kv.Key, d),
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
