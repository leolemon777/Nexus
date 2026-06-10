using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.GeSrtp
{
    /// <summary>
    /// GE SRTP (Service Request Transport Protocol) 通讯客户端 — 支持 GE 90-30/90-70/PACSystems。
    /// <para>SRTP over TCP (默认端口 18245)。</para>
    /// <para>对标 HSL: GE driver — Read/Write R/AI/AQ/%I/%Q/%M/%T 区域, 批量位, PLC控制</para>
    /// </summary>
    public class GeSrtpClient : IBatchReadWrite, ISubscribeDevice
    {
        private readonly object _lock = new object();
        private TcpClient? _tcp;
        private Stream? _stream;
        private bool _isConnected;
        private ushort _transactionId;
        protected ILogger Log { get; set; }

        public string IpAddress { get; }
        public int Port { get; }
        public int Timeout { get; set; }

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;
        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected { get { lock (_lock) return _isConnected && _tcp?.Connected == true; } }

        public GeSrtpClient(string ipAddress, int port = 18245, int timeout = 5000)
        {
            IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            Port = port;
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        public OperateResult Connect()
        {
            try
            {
                lock (_lock) { if (_isConnected) return OperateResult.Success(); _tcp = new TcpClient(); var ar = _tcp.BeginConnect(IpAddress, Port, null, null); if (!ar.AsyncWaitHandle.WaitOne(Timeout, false)) { _tcp.Close(); _tcp = null; return OperateResult.Failed("连接超时"); } _tcp.EndConnect(ar); _stream = _tcp.GetStream(); _stream.ReadTimeout = Timeout; _stream.WriteTimeout = Timeout; _isConnected = true; }
                OnConnected?.Invoke(this, EventArgs.Empty);
                return OperateResult.Success();
            }
            catch (Exception ex) { OnError?.Invoke(this, ex.Message); return OperateResult.Failed(ex.Message); }
        }

        public async Task<OperateResult> ConnectAsync() { try { _tcp = new TcpClient(); await _tcp.ConnectAsync(IpAddress, Port).ConfigureAwait(false); lock (_lock) { _stream = _tcp.GetStream(); _stream.ReadTimeout = Timeout; _stream.WriteTimeout = Timeout; _isConnected = true; } OnConnected?.Invoke(this, EventArgs.Empty); return OperateResult.Success(); } catch (Exception ex) { OnError?.Invoke(this, ex.Message); return OperateResult.Failed(ex.Message); } }

        public void Disconnect() { lock (_lock) { _isConnected = false; try { _stream?.Close(); } catch { } try { _tcp?.Close(); } catch { } _stream = null; _tcp = null; } OnDisconnected?.Invoke(this, EventArgs.Empty); }
        public void Dispose() { Disconnect(); GC.SuppressFinalize(this); }

        // ═══════════════════════════════════════════
        //  SRTP 帧格式
        // ═══════════════════════════════════════════
        // SRTP: ServiceType(1) + Channel(1) + Reserved(2) + TransactionId(2) + Length(2) + Data...

        private OperateResult<byte[]> SendAndReceive(byte[] request)
        {
            lock (_lock)
            {
                if (_stream == null || !_isConnected) return OperateResult<byte[]>.Failed("未连接");
                try
                {
                    OnMessageSent?.Invoke(this, BitConverter.ToString(request));
                    _stream.Write(request, 0, request.Length);
                    _stream.Flush();

                    // Read response header (8 bytes)
                    var header = new byte[8];
                    int read = 0;
                    while (read < 8) { int n = _stream.Read(header, read, 8 - read); if (n <= 0) return OperateResult<byte[]>.Failed("无响应"); read += n; }

                    int dataLen = (header[6] << 8) | header[7];
                    // Check SRTP status
                    byte status = header[4];
                    if (status != 0) return OperateResult<byte[]>.Failed($"SRTP error: 0x{status:X2}");

                    var data = new byte[dataLen];
                    if (dataLen > 0) { read = 0; while (read < dataLen) { int n = _stream.Read(data, read, dataLen - read); if (n <= 0) break; read += n; } }

                    OnMessageReceived?.Invoke(this, BitConverter.ToString(header));
                    return OperateResult<byte[]>.Success(data);
                }
                catch (Exception ex) { _isConnected = false; return OperateResult<byte[]>.Failed(ex.Message); }
            }
        }

        private byte[] BuildReadFrame(byte memoryType, ushort offset, ushort count)
        {
            var req = new byte[14];
            req[0] = 0x01; // ServiceType = Read
            req[1] = 0x00; // Channel
            req[2] = 0x00; req[3] = 0x00; // Reserved
            ushort tid = ++_transactionId;
            req[4] = (byte)(tid >> 8); req[5] = (byte)(tid & 0xFF);
            req[6] = 0x00; req[7] = 0x06; // Length of payload
            req[8] = memoryType;
            req[9] = 0x00;
            req[10] = (byte)(offset >> 8); req[11] = (byte)(offset & 0xFF);
            req[12] = (byte)(count >> 8); req[13] = (byte)(count & 0xFF);
            return req;
        }

        private byte[] BuildWriteFrame(byte memoryType, ushort offset, byte[] values)
        {
            int payloadLen = 4 + values.Length;
            var req = new byte[8 + payloadLen];
            req[0] = 0x02; // ServiceType = Write
            req[1] = 0x00;
            req[2] = 0x00; req[3] = 0x00;
            ushort tid = ++_transactionId;
            req[4] = (byte)(tid >> 8); req[5] = (byte)(tid & 0xFF);
            req[6] = (byte)(payloadLen >> 8); req[7] = (byte)(payloadLen & 0xFF);
            req[8] = memoryType;
            req[9] = 0x00;
            req[10] = (byte)(offset >> 8); req[11] = (byte)(offset & 0xFF);
            Buffer.BlockCopy(values, 0, req, 12, values.Length);
            return req;
        }

        // ═══════════════════════════════════════════
        //  地址解析 — R100, AI10, AQ10, %I10, %Q10, %M10, %T10
        // ═══════════════════════════════════════════

        private static (byte memType, int offset) ParseAddress(string address)
        {
            string addr = address.Trim().ToUpperInvariant().Replace("%", "");
            if (addr.Length < 2) throw new ArgumentException($"Invalid GE address: {address}");

            char prefix = addr[0];
            string numStr = addr.Substring(1);
            if (!int.TryParse(numStr, out int num)) throw new FormatException($"Invalid number: {numStr}");

            byte memType = prefix switch
            {
                'R' => 0x08, // Register (%R)
                'A' => (byte)(addr[1] == 'I' ? 0x0A : 0x0C), // AI=%AI, AQ=%AQ
                'I' => 0x10, // Input (%I)
                'Q' => 0x12, // Output (%Q)
                'M' => 0x14, // Memory (%M)
                'T' => 0x16, // Timer (%T)
                _ => 0x08
            };

            // For AI/AQ, skip the second char in number parsing
            if (prefix == 'A' && addr.Length > 1 && (addr[1] == 'I' || addr[1] == 'Q'))
            {
                numStr = addr.Substring(2);
                if (!int.TryParse(numStr, out num)) throw new FormatException($"Invalid number: {numStr}");
            }

            return (memType, num);
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        public OperateResult<short> ReadInt16(string address)
        {
            try
            {
                var (memType, offset) = ParseAddress(address);
                var resp = SendAndReceive(BuildReadFrame(memType, (ushort)offset, 1));
                if (!resp.IsSuccess) return OperateResult<short>.Failed(resp.Message);
                if (resp.Content.Length < 2) return OperateResult<short>.Failed("响应过短");
                return OperateResult<short>.Success((short)((resp.Content[0] << 8) | resp.Content[1]));
            }
            catch (Exception ex) { return OperateResult<short>.Failed(ex.Message); }
        }

        public OperateResult<bool> ReadBool(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<bool>.Success(r.Content != 0) : OperateResult<bool>.Failed(r.Message); }
        public OperateResult<ushort> ReadUInt16(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message); }
        public OperateResult<int> ReadInt32(string address) { try { var lo = ReadInt16(address); if (!lo.IsSuccess) return OperateResult<int>.Failed(lo.Message); var hi = ReadInt16(Incr(address)); return OperateResult<int>.Success((hi.Content << 16) | (lo.Content & 0xFFFF)); } catch (Exception ex) { return OperateResult<int>.Failed(ex.Message); } }
        public OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message); }
        public OperateResult<long> ReadInt64(string address) { var lo = ReadInt32(address); if (!lo.IsSuccess) return OperateResult<long>.Failed(lo.Message); var hi = ReadInt32(Incr(Incr(address))); return OperateResult<long>.Success(((long)hi.Content << 32) | (uint)lo.Content); }
        public OperateResult<ulong> ReadUInt64(string address) { var r = ReadInt64(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message); }
        public OperateResult<float> ReadFloat(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(r.Content), 0)) : OperateResult<float>.Failed(r.Message); }
        public OperateResult<double> ReadDouble(string address) { var r = ReadInt64(address); return r.IsSuccess ? OperateResult<double>.Success(BitConverter.Int64BitsToDouble(r.Content)) : OperateResult<double>.Failed(r.Message); }
        public OperateResult<string> ReadString(string address, ushort length) { var r = ReadBytes(address, (ushort)(length * 2)); return r.IsSuccess ? OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content).TrimEnd('\0')) : OperateResult<string>.Failed(r.Message); }
        public OperateResult<byte[]> ReadBytes(string address, ushort length) { try { var result = new List<byte>(); for (int i = 0; i < (length + 1) / 2; i++) { var r = ReadInt16(Incr(address, i)); if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message); result.AddRange(BitConverter.GetBytes(r.Content)); } return OperateResult<byte[]>.Success(result.ToArray()); } catch (Exception ex) { return OperateResult<byte[]>.Failed(ex.Message); } }

        public OperateResult Write(string address, bool value) => Write(address, (short)(value ? 1 : 0));
        public OperateResult Write(string address, short value) { try { var (mt, off) = ParseAddress(address); var data = new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) }; var resp = SendAndReceive(BuildWriteFrame(mt, (ushort)off, data)); return resp.IsSuccess ? OperateResult.Success() : OperateResult.Failed(resp.Message); } catch (Exception ex) { return OperateResult.Failed(ex.Message); } }
        public OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public OperateResult Write(string address, int value) { var r1 = Write(address, (short)(value & 0xFFFF)); if (!r1.IsSuccess) return r1; return Write(Incr(address), (short)((value >> 16) & 0xFFFF)); }
        public OperateResult Write(string address, uint value) => Write(address, (int)value);
        public OperateResult Write(string address, long value) => Write(address, (int)value);
        public OperateResult Write(string address, ulong value) => Write(address, (long)value);
        public OperateResult Write(string address, float value) => Write(address, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));
        public OperateResult Write(string address, double value) => Write(address, (long)BitConverter.DoubleToInt64Bits(value));
        public OperateResult Write(string address, string value) => Write(address, Encoding.ASCII.GetBytes(value));
        public OperateResult Write(string address, byte[] data) { for (int i = 0; i < data.Length; i += 2) { short v = data.Length > i + 1 ? (short)(data[i] | (data[i + 1] << 8)) : data[i]; var r = Write(Incr(address, i / 2), v); if (!r.IsSuccess) return r; } return OperateResult.Success(); }

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

        private static string Incr(string address, int offset = 1) { var (mt, num) = ParseAddress(address); return $"R{num + offset}"; }

        // ═══════════════════════════════════════════
        //  批量位操作 — ReadBools / WriteBools
        // ═══════════════════════════════════════════

        /// <summary>
        /// 批量读取位地址。
        /// <para>支持 I/Q/M/T 区域的位读取。</para>
        /// </summary>
        public OperateResult<bool[]> ReadBools(string address, ushort count)
        {
            if (count == 0) return OperateResult<bool[]>.Success(Array.Empty<bool>());
            if (count == 1) { var r = ReadBool(address); return r.IsSuccess ? OperateResult<bool[]>.Success(new[] { r.Content }) : OperateResult<bool[]>.Failed(r.Message); }

            try
            {
                var (memType, offset) = ParseAddress(address);
                // SRTP 位读取: 读取包含这些位的寄存器，然后提取位
                int wordCount = (offset % 8 + count + 15) / 16;
                var resp = SendAndReceive(BuildReadFrame(memType, (ushort)(offset / 8 * 2), (ushort)wordCount));
                if (!resp.IsSuccess) return OperateResult<bool[]>.Failed(resp.Message);

                var result = new bool[count];
                int bitStart = offset % 8;
                for (int i = 0; i < count; i++)
                {
                    int totalBit = bitStart + i;
                    int byteIdx = totalBit / 8;
                    int bitIdx = totalBit % 8;
                    if (byteIdx < resp.Content.Length)
                        result[i] = (resp.Content[byteIdx] & (1 << bitIdx)) != 0;
                }
                return OperateResult<bool[]>.Success(result);
            }
            catch (Exception ex) { return OperateResult<bool[]>.Failed(ex.Message); }
        }

        /// <summary>
        /// 批量写入位地址。
        /// </summary>
        public OperateResult WriteBools(string address, bool[] values)
        {
            if (values == null || values.Length == 0) return OperateResult.Success();
            if (values.Length == 1) return Write(address, values[0]);

            try
            {
                // 简化实现: 逐位写入（SRTP 没有批量位写入命令）
                for (int i = 0; i < values.Length; i++)
                {
                    var (mt, off) = ParseAddress(address);
                    string addr = $"{(char)('A' + (mt - 0x08))}{off + i}";
                    var r = Write(addr, values[i]);
                    if (!r.IsSuccess) return r;
                }
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  PLC 控制命令
        // ═══════════════════════════════════════════

        /// <summary>读取 PLC 当前时间。</summary>
        public OperateResult<DateTime> ReadDateTime()
        {
            try
            {
                // SRTP 命令: 读取 PLC 时钟 (ServiceType=0, SubCommand=37)
                var req = new byte[14];
                req[0] = 0x01; // Read
                req[1] = 0x00;
                req[2] = 0x00; req[3] = 0x00;
                ushort tid = ++_transactionId;
                req[4] = (byte)(tid >> 8); req[5] = (byte)(tid & 0xFF);
                req[6] = 0x00; req[7] = 0x06;
                req[8] = 0x25; // SubCommand = 37 (Read DateTime)
                req[9] = 0x00;

                var resp = SendAndReceive(req);
                if (!resp.IsSuccess) return OperateResult<DateTime>.Failed(resp.Message);
                if (resp.Content.Length < 6) return OperateResult<DateTime>.Failed("响应数据不足");

                // BCD编码: 秒 分 时 日 月 年(后2位)
                var d = resp.Content;
                int sec = BcdToDec(d[0]);
                int min = BcdToDec(d[1]);
                int hour = BcdToDec(d[2]);
                int day = BcdToDec(d[3]);
                int month = BcdToDec(d[4]);
                int year = 2000 + BcdToDec(d[5]);
                return OperateResult<DateTime>.Success(new DateTime(year, month, day, hour, min, sec));
            }
            catch (Exception ex) { return OperateResult<DateTime>.Failed(ex.Message); }
        }

        /// <summary>读取 PLC 程序名称。</summary>
        public OperateResult<string> ReadProgramName()
        {
            try
            {
                var req = new byte[14];
                req[0] = 0x01;
                req[1] = 0x00;
                req[2] = 0x00; req[3] = 0x00;
                ushort tid = ++_transactionId;
                req[4] = (byte)(tid >> 8); req[5] = (byte)(tid & 0xFF);
                req[6] = 0x00; req[7] = 0x06;
                req[8] = 0x01; // SubCommand = 1 (Read Program Name)
                req[9] = 0x00;

                var resp = SendAndReceive(req);
                if (!resp.IsSuccess) return OperateResult<string>.Failed(resp.Message);
                if (resp.Content.Length < 8) return OperateResult<string>.Failed("响应数据不足");

                // 程序名在响应的特定偏移处
                string name = Encoding.ASCII.GetString(resp.Content, 0, Math.Min(resp.Content.Length, 32)).TrimEnd('\0', ' ');
                return OperateResult<string>.Success(name);
            }
            catch (Exception ex) { return OperateResult<string>.Failed(ex.Message); }
        }

        /// <summary>读取 PLC 状态。</summary>
        public OperateResult<string> ReadPlcStatus()
        {
            try
            {
                // 读取 R0 寄存器作为状态指示
                var r = ReadInt16("R0");
                if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
                return OperateResult<string>.Success(r.Content == 0 ? "STOP" : "RUN");
            }
            catch (Exception ex) { return OperateResult<string>.Failed(ex.Message); }
        }

        private static int BcdToDec(byte bcd)
        {
            return ((bcd >> 4) & 0x0F) * 10 + (bcd & 0x0F);
        }

        // ── 批量位/控制异步 ──
        public Task<OperateResult<bool[]>> ReadBoolsAsync(string address, ushort count) => Task.Run(() => ReadBools(address, count));
        public Task<OperateResult> WriteBoolsAsync(string address, bool[] values) => Task.Run(() => WriteBools(address, values));
        public Task<OperateResult<DateTime>> ReadDateTimeAsync() => Task.Run(() => ReadDateTime());
        public Task<OperateResult<string>> ReadProgramNameAsync() => Task.Run(() => ReadProgramName());
        public Task<OperateResult<string>> ReadPlcStatusAsync() => Task.Run(() => ReadPlcStatus());

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
        // ═══════════════════════════════════════════

        /// <summary>批量读取多个地址的值（按内存类型分组，连续地址合并读取）。</summary>
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, object?>();
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");

            // 按内存类型分组
            var groups = addrList.GroupBy(a => ParseAddress(a).memType);

            foreach (var group in groups)
            {
                byte memType = group.Key;
                var sorted = group.Select(a => new { Address = a, Parsed = ParseAddress(a) })
                                  .OrderBy(a => a.Parsed.offset)
                                  .ToList();

                ushort minOff = (ushort)sorted[0].Parsed.offset;
                ushort maxOff = (ushort)sorted.Last().Parsed.offset;
                ushort range = (ushort)(maxOff - minOff + 1);

                var resp = SendAndReceive(BuildReadFrame(memType, minOff, range));
                if (!resp.IsSuccess)
                    return OperateResult<Dictionary<string, object?>>.Failed(resp.Message);

                if (resp.Content == null || resp.Content.Length < 2)
                    return OperateResult<Dictionary<string, object?>>.Failed("响应数据不足");

                foreach (var item in sorted)
                {
                    int byteOffset = (item.Parsed.offset - minOff) * 2;
                    if (byteOffset >= 0 && byteOffset + 2 <= resp.Content.Length)
                        result[item.Address] = (short)((resp.Content[byteOffset] << 8) | resp.Content[byteOffset + 1]);
                }
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
                    return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message);
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
