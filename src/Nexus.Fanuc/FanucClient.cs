using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;

namespace Nexus.Fanuc
{
    /// <summary>
    /// FANUC FOCAS/Ethernet 通讯客户端 — 支持 0i/16i/18i/21i/30i/31i/32i/35i 系列。
    /// <para>FOCAS Ethernet 协议 over TCP (默认端口 8193)。</para>
    /// <para>帧格式: Header(10) + Data</para>
    /// <para>对标 HSL: FANUC driver — Read/Write PMC/G 参数, CNC状态/轴位置/报警/刀具</para>
    /// </summary>
    public class FanucClient : IBatchReadWrite, ISubscribeDevice
    {
        private readonly object _lock = new object();
        private TcpClient? _tcp;
        private Stream? _stream;
        private bool _isConnected;
        protected ILogger Log { get; set; }

        public string IpAddress { get; }
        public int Port { get; }
        public int Timeout { get; set; }

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;
        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected
        {
            get { lock (_lock) return _isConnected && _tcp?.Connected == true; }
        }

        public FanucClient(string ipAddress, int port = 8193, int timeout = 5000)
        {
            IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            Port = port;
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ═══════════════════════════════════════════
        //  连接管理
        // ═══════════════════════════════════════════

        public OperateResult Connect()
        {
            try
            {
                lock (_lock)
                {
                    if (_isConnected) return OperateResult.Success();
                    _tcp = new TcpClient();
                    var ar = _tcp.BeginConnect(IpAddress, Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(Timeout, false))
                    {
                        _tcp.Close(); _tcp = null;
                        return OperateResult.Failed("连接超时");
                    }
                    _tcp.EndConnect(ar);
                    _stream = _tcp.GetStream();
                    _stream.ReadTimeout = Timeout;
                    _stream.WriteTimeout = Timeout;
                    _isConnected = true;
                }
                OnConnected?.Invoke(this, EventArgs.Empty);
                return OperateResult.Success();
            }
            catch (Exception ex) { OnError?.Invoke(this, ex.Message); return OperateResult.Failed(ex.Message); }
        }

        public async Task<OperateResult> ConnectAsync()
        {
            try
            {
                _tcp = new TcpClient();
                await _tcp.ConnectAsync(IpAddress, Port).ConfigureAwait(false);
                lock (_lock) { _stream = _tcp.GetStream(); _stream.ReadTimeout = Timeout; _stream.WriteTimeout = Timeout; _isConnected = true; }
                OnConnected?.Invoke(this, EventArgs.Empty);
                return OperateResult.Success();
            }
            catch (Exception ex) { OnError?.Invoke(this, ex.Message); return OperateResult.Failed(ex.Message); }
        }

        public void Disconnect()
        {
            lock (_lock) { _isConnected = false; try { _stream?.Close(); } catch { } try { _tcp?.Close(); } catch { } _stream = null; _tcp = null; }
            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() { Disconnect(); GC.SuppressFinalize(this); }

        // ═══════════════════════════════════════════
        //  FANUC FOCAS 帧
        // ═══════════════════════════════════════════
        // FOCAS2 Ethernet 帧头 (10 bytes):
        //   Identifier(2) + Reserved(2) + BlockLength(2) + Reserved(2) + HeaderCode(2)

        private byte[] BuildFocasFrame(ushort function, ushort subFunction, byte[] data)
        {
            int dataLen = data?.Length ?? 0;
            int totalLen = 10 + 4 + dataLen; // header + func/subfunc + data
            var frame = new byte[totalLen];

            // Header
            frame[0] = 0x00; frame[1] = 0x00; // Identifier
            frame[2] = 0x00; frame[3] = 0x00; // Reserved
            int blockLen = totalLen - 10;
            frame[4] = (byte)(blockLen >> 8); frame[5] = (byte)(blockLen & 0xFF);
            frame[6] = 0x00; frame[7] = 0x00; // Reserved
            // Header code
            frame[8] = (byte)(function >> 8); frame[9] = (byte)(function & 0xFF);

            // Function + SubFunction
            frame[10] = (byte)(subFunction >> 8); frame[11] = (byte)(subFunction & 0xFF);
            frame[12] = 0x00; frame[13] = 0x00; // padding

            if (data != null && dataLen > 0)
                Buffer.BlockCopy(data, 0, frame, 14, dataLen);

            return frame;
        }

        private OperateResult<byte[]> SendAndReceive(ushort function, ushort subFunction, byte[]? data)
        {
            lock (_lock)
            {
                if (_stream == null || !_isConnected) return OperateResult<byte[]>.Failed("未连接");
                try
                {
                    var frame = BuildFocasFrame(function, subFunction, data);
                    OnMessageSent?.Invoke(this, BitConverter.ToString(frame));
                    _stream.Write(frame, 0, frame.Length);
                    _stream.Flush();

                    // Read response header (10 bytes)
                    var headerBuf = new byte[10];
                    int read = 0;
                    while (read < 10) { int n = _stream.Read(headerBuf, read, 10 - read); if (n <= 0) return OperateResult<byte[]>.Failed("无响应"); read += n; }

                    int respLen = (headerBuf[4] << 8) | headerBuf[5];
                    int payloadLen = respLen - 4; // subtract func/subfunc
                    if (payloadLen < 0) payloadLen = 0;

                    var payload = new byte[payloadLen];
                    if (payloadLen > 0)
                    {
                        read = 0;
                        while (read < payloadLen) { int n = _stream.Read(payload, read, payloadLen - read); if (n <= 0) break; read += n; }
                    }

                    OnMessageReceived?.Invoke(this, BitConverter.ToString(headerBuf));
                    // Check FOCAS completion code (first 2 bytes of payload after func/sub)
                    if (payloadLen >= 4)
                    {
                        short completionCode = (short)((payload[0] << 8) | payload[1]);
                        if (completionCode != 0)
                            return OperateResult<byte[]>.Failed($"FOCAS error: {completionCode}");
                    }

                    return OperateResult<byte[]>.Success(payload);
                }
                catch (Exception ex) { _isConnected = false; return OperateResult<byte[]>.Failed(ex.Message); }
            }
        }

        // ═══════════════════════════════════════════
        //  地址解析 — FANUC 使用 R[num], D[num], G[num], F[num], A[num], C[num], T[num], K[num]
        // ═══════════════════════════════════════════

        private static (string area, int number) ParseAddress(string address)
        {
            string addr = address.Trim().ToUpperInvariant();
            if (addr.Length < 2) throw new ArgumentException($"Invalid FANUC address: {address}");
            char prefix = addr[0];
            string numStr = addr.Substring(1);
            if (!int.TryParse(numStr, out int num)) throw new FormatException($"Invalid number: {numStr}");
            return (prefix.ToString(), num);
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 读取
        // ═══════════════════════════════════════════

        public OperateResult<bool> ReadBool(string address)
        {
            try
            {
                var (area, num) = ParseAddress(address);
                // PMC read bit: function=0x73, sub=0x00
                var data = new byte[6];
                data[0] = (byte)(num >> 8); data[1] = (byte)(num & 0xFF);
                data[2] = (byte)area[0]; data[3] = 0x00;
                var resp = SendAndReceive(0x73, 0x00, data);
                if (!resp.IsSuccess) return OperateResult<bool>.Failed(resp.Message);
                return OperateResult<bool>.Success(resp.Content.Length > 4 && resp.Content[4] != 0);
            }
            catch (Exception ex) { return OperateResult<bool>.Failed(ex.Message); }
        }

        public OperateResult<short> ReadInt16(string address)
        {
            try
            {
                var (area, num) = ParseAddress(address);
                var data = new byte[6];
                data[0] = (byte)(num >> 8); data[1] = (byte)(num & 0xFF);
                data[2] = (byte)area[0]; data[3] = 0x00;
                var resp = SendAndReceive(0x73, 0x01, data);
                if (!resp.IsSuccess) return OperateResult<short>.Failed(resp.Message);
                if (resp.Content.Length < 6) return OperateResult<short>.Failed("响应数据过短");
                return OperateResult<short>.Success((short)((resp.Content[4] << 8) | resp.Content[5]));
            }
            catch (Exception ex) { return OperateResult<short>.Failed(ex.Message); }
        }

        public OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            return OperateResult<ushort>.Success((ushort)r.Content);
        }

        public OperateResult<int> ReadInt32(string address)
        {
            try
            {
                var lo = ReadInt16(address); if (!lo.IsSuccess) return OperateResult<int>.Failed(lo.Message);
                var hi = ReadInt16(address + "+1"); if (!hi.IsSuccess) return OperateResult<int>.Failed(hi.Message);
                return OperateResult<int>.Success((hi.Content << 16) | (lo.Content & 0xFFFF));
            }
            catch (Exception ex) { return OperateResult<int>.Failed(ex.Message); }
        }

        public OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message); }
        public OperateResult<long> ReadInt64(string address) { var r = ReadInt32(address); if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message); var r2 = ReadInt32(address + "+2"); if (!r2.IsSuccess) return OperateResult<long>.Failed(r2.Message); return OperateResult<long>.Success(((long)r2.Content << 32) | (uint)r.Content); }
        public OperateResult<ulong> ReadUInt64(string address) { var r = ReadInt64(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message); }
        public OperateResult<float> ReadFloat(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(r.Content), 0)) : OperateResult<float>.Failed(r.Message); }
        public OperateResult<double> ReadDouble(string address) { var r = ReadInt64(address); return r.IsSuccess ? OperateResult<double>.Success(BitConverter.Int64BitsToDouble(r.Content)) : OperateResult<double>.Failed(r.Message); }
        public OperateResult<string> ReadString(string address, ushort length) { var r = ReadBytes(address, (ushort)(length * 2)); return r.IsSuccess ? OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content).TrimEnd('\0')) : OperateResult<string>.Failed(r.Message); }
        public OperateResult<byte[]> ReadBytes(string address, ushort length) { try { var result = new List<byte>(); for (int i = 0; i < (length + 1) / 2; i++) { var r = ReadInt16(IncrAddr(address, i)); if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message); result.AddRange(BitConverter.GetBytes(r.Content)); } return OperateResult<byte[]>.Success(result.ToArray()); } catch (Exception ex) { return OperateResult<byte[]>.Failed(ex.Message); } }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 写入
        // ═══════════════════════════════════════════

        public OperateResult Write(string address, bool value) { try { var (area, num) = ParseAddress(address); var data = new byte[6]; data[0] = (byte)(num >> 8); data[1] = (byte)(num & 0xFF); data[2] = (byte)area[0]; data[3] = 0x00; data[4] = (byte)(value ? 1 : 0); var resp = SendAndReceive(0x73, 0x10, data); return resp.IsSuccess ? OperateResult.Success() : OperateResult.Failed(resp.Message); } catch (Exception ex) { return OperateResult.Failed(ex.Message); } }
        public OperateResult Write(string address, short value) { try { var (area, num) = ParseAddress(address); var data = new byte[8]; data[0] = (byte)(num >> 8); data[1] = (byte)(num & 0xFF); data[2] = (byte)area[0]; data[3] = 0x00; data[4] = (byte)(value >> 8); data[5] = (byte)(value & 0xFF); var resp = SendAndReceive(0x73, 0x11, data); return resp.IsSuccess ? OperateResult.Success() : OperateResult.Failed(resp.Message); } catch (Exception ex) { return OperateResult.Failed(ex.Message); } }
        public OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public OperateResult Write(string address, int value) { var r1 = Write(address, (short)(value & 0xFFFF)); if (!r1.IsSuccess) return r1; return Write(IncrAddr(address, 1), (short)((value >> 16) & 0xFFFF)); }
        public OperateResult Write(string address, uint value) => Write(address, (int)value);
        public OperateResult Write(string address, long value) => Write(address, (int)value);
        public OperateResult Write(string address, ulong value) => Write(address, (long)value);
        public OperateResult Write(string address, float value) => Write(address, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));
        public OperateResult Write(string address, double value) => Write(address, (long)BitConverter.DoubleToInt64Bits(value));
        public OperateResult Write(string address, string value) => Write(address, Encoding.ASCII.GetBytes(value));
        public OperateResult Write(string address, byte[] data) { for (int i = 0; i < data.Length; i += 2) { short v = data.Length > i + 1 ? (short)(data[i] | (data[i + 1] << 8)) : data[i]; var r = Write(IncrAddr(address, i / 2), v); if (!r.IsSuccess) return r; } return OperateResult.Success(); }

        // Async wrappers
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

        private static string IncrAddr(string address, int offset)
        {
            var (area, num) = ParseAddress(address);
            return $"{area}{num + offset}";
        }

        // ═══════════════════════════════════════════
        //  CNC 专用命令
        // ═══════════════════════════════════════════

        /// <summary>
        /// 读取 CNC 状态信息。
        /// <para>FOCAS cnc_sysinfo 函数: function=0x0061, sub=0x00。</para>
        /// </summary>
        public OperateResult<FanucCncInfo> ReadCncInfo()
        {
            try
            {
                var resp = SendAndReceive(0x0061, 0x00, null);
                if (!resp.IsSuccess) return OperateResult<FanucCncInfo>.Failed(resp.Message);
                if (resp.Content.Length < 32) return OperateResult<FanucCncInfo>.Failed("响应数据不足");

                // FOCAS CNC info: max axis, CNC type, MT type, series, version
                var info = new FanucCncInfo
                {
                    MaxAxis = resp.Content[2],
                    CncType = Encoding.ASCII.GetString(resp.Content, 4, 2).TrimEnd('\0'),
                    MtType = Encoding.ASCII.GetString(resp.Content, 6, 2).TrimEnd('\0'),
                    Series = Encoding.ASCII.GetString(resp.Content, 8, 4).TrimEnd('\0'),
                    Version = Encoding.ASCII.GetString(resp.Content, 12, 4).TrimEnd('\0')
                };
                return OperateResult<FanucCncInfo>.Success(info);
            }
            catch (Exception ex) { return OperateResult<FanucCncInfo>.Failed(ex.Message); }
        }

        /// <summary>
        /// 读取 CNC 运行状态。
        /// <para>FOCAS cnc_statinfo 函数: function=0x0063, sub=0x00。</para>
        /// </summary>
        public OperateResult<FanucCncStatus> ReadCncStatus()
        {
            try
            {
                var resp = SendAndReceive(0x0063, 0x00, null);
                if (!resp.IsSuccess) return OperateResult<FanucCncStatus>.Failed(resp.Message);
                if (resp.Content.Length < 8) return OperateResult<FanucCncStatus>.Failed("响应数据不足");

                return OperateResult<FanucCncStatus>.Success(new FanucCncStatus
                {
                    Run = (resp.Content[4] >> 0) & 0x07,
                    Motion = (resp.Content[4] >> 3) & 0x07,
                    Mstb = (resp.Content[4] >> 6) & 0x07,
                    Emergency = resp.Content[5] != 0
                });
            }
            catch (Exception ex) { return OperateResult<FanucCncStatus>.Failed(ex.Message); }
        }

        /// <summary>
        /// 读取指定轴的位置（绝对坐标）。
        /// <para>FOCAS cnc_absolute 函数: function=0x0067, sub=0x00。</para>
        /// </summary>
        public OperateResult<double> ReadAxisPosition(int axis)
        {
            try
            {
                var data = new byte[4];
                data[0] = (byte)(axis >> 24); data[1] = (byte)(axis >> 16);
                data[2] = (byte)(axis >> 8); data[3] = (byte)axis;

                var resp = SendAndReceive(0x0067, 0x00, data);
                if (!resp.IsSuccess) return OperateResult<double>.Failed(resp.Message);
                if (resp.Content.Length < 12) return OperateResult<double>.Failed("响应数据不足");

                // FOCAS 返回 ODBAXIS 结构: data(4) + (4*axis)
                int dataOffset = 4 + axis * 4;
                if (dataOffset + 4 > resp.Content.Length) return OperateResult<double>.Failed("轴号超出范围");

                int rawValue = (resp.Content[dataOffset] << 24) | (resp.Content[dataOffset + 1] << 16) |
                               (resp.Content[dataOffset + 2] << 8) | resp.Content[dataOffset + 3];
                // 位置值以 0.001mm 为单位
                return OperateResult<double>.Success(rawValue / 1000.0);
            }
            catch (Exception ex) { return OperateResult<double>.Failed(ex.Message); }
        }

        /// <summary>
        /// 读取 CNC 报警信息。
        /// <para>FOCAS cnc_alarm 函数: function=0x0070, sub=0x00。</para>
        /// </summary>
        public OperateResult<FanucAlarm[]> ReadAlarms()
        {
            try
            {
                var resp = SendAndReceive(0x0070, 0x00, null);
                if (!resp.IsSuccess) return OperateResult<FanucAlarm[]>.Failed(resp.Message);
                if (resp.Content.Length < 8) return OperateResult<FanucAlarm[]>.Success(Array.Empty<FanucAlarm>());

                // 简化: 最多解析 10 个报警
                var alarms = new List<FanucAlarm>();
                for (int i = 4; i + 4 <= resp.Content.Length && alarms.Count < 10; i += 4)
                {
                    int code = (resp.Content[i] << 8) | resp.Content[i + 1];
                    if (code == 0) break;
                    alarms.Add(new FanucAlarm
                    {
                        Code = code,
                        Axis = resp.Content[i + 2],
                        Type = resp.Content[i + 3]
                    });
                }
                return OperateResult<FanucAlarm[]>.Success(alarms.ToArray());
            }
            catch (Exception ex) { return OperateResult<FanucAlarm[]>.Failed(ex.Message); }
        }

        /// <summary>读取当前刀具号。</summary>
        public OperateResult<int> ReadToolNumber()
        {
            try
            {
                // FOCAS cnc_modal: 读取刀具号
                var resp = SendAndReceive(0x0074, 0x00, new byte[] { 0, 0, 0, 0 });
                if (!resp.IsSuccess) return OperateResult<int>.Failed(resp.Message);
                if (resp.Content.Length < 8) return OperateResult<int>.Failed("响应数据不足");

                int toolNo = (resp.Content[4] << 8) | resp.Content[5];
                return OperateResult<int>.Success(toolNo);
            }
            catch (Exception ex) { return OperateResult<int>.Failed(ex.Message); }
        }

        // ── CNC 异步 ──
        public Task<OperateResult<FanucCncInfo>> ReadCncInfoAsync() => Task.Run(() => ReadCncInfo());
        public Task<OperateResult<FanucCncStatus>> ReadCncStatusAsync() => Task.Run(() => ReadCncStatus());
        public Task<OperateResult<double>> ReadAxisPositionAsync(int axis) => Task.Run(() => ReadAxisPosition(axis));
        public Task<OperateResult<FanucAlarm[]>> ReadAlarmsAsync() => Task.Run(() => ReadAlarms());
        public Task<OperateResult<int>> ReadToolNumberAsync() => Task.Run(() => ReadToolNumber());

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

    /// <summary>FANUC CNC 系统信息。</summary>
    public class FanucCncInfo
    {
        public int MaxAxis { get; set; }
        public string CncType { get; set; } = "";
        public string MtType { get; set; } = "";
        public string Series { get; set; } = "";
        public string Version { get; set; } = "";
        public override string ToString() => $"FANUC {CncType} {Series} v{Version} ({MaxAxis} axes)";
    }

    /// <summary>FANUC CNC 运行状态。</summary>
    public class FanucCncStatus
    {
        public int Run { get; set; }
        public int Motion { get; set; }
        public int Mstb { get; set; }
        public bool Emergency { get; set; }
        public string RunDescription => Run switch
        {
            0 => "RESET", 1 => "STOP", 2 => "HOLD", 3 => "START", 4 => "MSTR",
            _ => $"UNKNOWN({Run})"
        };
        public override string ToString() => $"Run={RunDescription} Emergency={Emergency}";
    }

    /// <summary>FANUC CNC 报警信息。</summary>
    public class FanucAlarm
    {
        public int Code { get; set; }
        public int Axis { get; set; }
        public int Type { get; set; }
        public override string ToString() => $"Alarm #{Code} Axis={Axis} Type={Type}";
    }
}
