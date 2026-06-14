using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Keyence
{
    /// <summary>
    /// 基恩士 KV 系列上位通讯客户端 — 支持 KV-3000/5000/7000 等。
    /// <para>二进制协议 over TCP (端口 3000) 或 串口。</para>
    /// <para>帧格式: STX(0x02) + Station(2hex) + Command(2hex) + Data + ETX(0x03) + CRC(2hex)</para>
    /// <para>对标 HSL: KeyenceNanoSerial — Read/Write DM/EM/WR/WL 寄存器</para>
    /// </summary>
    public class KeyenceKvClient : IReadWriteDevice, IBatchReadWrite, ISubscribeDevice
    {
        private readonly object _lock = new object();
        private TcpClient? _tcp;
        private Stream? _stream;
        private bool _isConnected;
        protected ILogger Log { get; set; }

        public string IpAddress { get; } = "";
        public int Port { get; }
        public byte Station { get; set; }
        public int Timeout { get; set; }

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;
        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected => _isConnected && _tcp?.Connected == true;

        public KeyenceKvClient(string ipAddress, int port = 3000, byte station = 0, int timeout = 5000)
        {
            IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            Port = port;
            Station = station;
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        public KeyenceKvClient(Stream stream, byte station = 0, int timeout = 5000)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            Station = station;
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ═══════════════════════════════════════════
        //  帧收发
        // ═══════════════════════════════════════════

        /// <summary>
        /// 发送命令并接收响应。
        /// KV 上位通讯命令格式（文本模式，更通用）:
        /// 命令字符串 + CR(0x0D) 或 LF(0x0A) 或 CR+LF
        /// 响应: 数据 + CR+LF  或  错误码 + CR+LF
        /// </summary>
        private OperateResult<string> SendCommand(string command)
        {
            try
            {
                lock (_lock)
                {
                    if (_stream == null) return OperateResult<string>.Failed("未连接");

                    string frame = Station.ToString("D2") + command + "\r";
                    Log.Debug($"TX → {frame.TrimEnd()}");
                    OnMessageSent?.Invoke(this, frame.TrimEnd());

                    byte[] txBytes = Encoding.ASCII.GetBytes(frame);
                    _stream.Write(txBytes, 0, txBytes.Length);

                    // 读取响应直到 \r 或 \n
                    string? response = ReadLine();
                    if (response == null)
                        return OperateResult<string>.Failed("读取响应超时");

                    Log.Debug($"RX ← {response.TrimEnd()}");
                    OnMessageReceived?.Invoke(this, response.TrimEnd());

                    // 检查错误: 首字符为 '!' 表示错误
                    if (response.StartsWith("?"))
                    {
                        string errCode = response.Length > 1 ? response.Substring(1).Trim() : "??";
                        return OperateResult<string>.Failed($"KV 错误: {ParseErrorCode(errCode)}");
                    }

                    return OperateResult<string>.Success(response.TrimEnd('\r', '\n'));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"KV 通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult<string>.Failed($"通讯异常: {ex.Message}");
            }
        }

        private string? ReadLine()
        {
            int start = Environment.TickCount;
            using var ms = new MemoryStream();

            while (unchecked(Environment.TickCount - start) <= Timeout)
            {
                int remaining = Timeout - unchecked(Environment.TickCount - start);
                if (remaining < 0) return null;
                int b = ReadByteWithTimeout(remaining);
                if (b < 0) return null;
                if (b == '\r' || b == '\n')
                {
                    // Consume optional \r\n pair
                    if (b == '\r')
                    {
                        int rem2 = Timeout - unchecked(Environment.TickCount - start);
                        int next = ReadByteWithTimeout(Math.Min(rem2 < 0 ? 0 : rem2, 200));
                        if (next == '\n') { /* consumed */ }
                        else if (next >= 0) ms.WriteByte((byte)next);
                    }
                    return Encoding.ASCII.GetString(ms.ToArray());
                }
                ms.WriteByte((byte)b);
            }
            return null;
        }

        private int ReadByteWithTimeout(int remainingMs)
        {
            int start = Environment.TickCount;
            while (unchecked(Environment.TickCount - start) <= remainingMs)
            {
                try { return _stream!.ReadByte(); }
                catch (TimeoutException) { return -1; }
            }
            return -1;
        }

        private static string ParseErrorCode(string code) => code.Trim() switch
        {
            "0" => "无错误",
            "1" => "未定义命令",
            "2" => "非法数据",
            "3" => "地址越界",
            "4" => "写保护",
            "5" => "通讯错误",
            "6" => "忙碌",
            "7" => "超时",
            _ => $"未知错误 {code}"
        };

        // ═══════════════════════════════════════════
        //  地址解析
        // ═══════════════════════════════════════════

        /// <summary>
        /// KV 地址格式: "DM100", "EM100", "WR100", "WL100", "R100", "B100", "MR100"
        /// 简写: "D100" → DM100
        /// </summary>
        private static (string type, int address) ParseAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("地址不能为空");
            address = address.Trim().ToUpperInvariant();

            if (address.StartsWith("DM")) return ("DM", int.Parse(address.Substring(2)));
            if (address.StartsWith("EM")) return ("EM", int.Parse(address.Substring(2)));
            if (address.StartsWith("WR")) return ("WR", int.Parse(address.Substring(2)));
            if (address.StartsWith("WL")) return ("WL", int.Parse(address.Substring(2)));
            if (address.StartsWith("MR")) return ("MR", int.Parse(address.Substring(2)));
            if (address.StartsWith("CR")) return ("CR", int.Parse(address.Substring(2)));
            if (address.StartsWith("VR")) return ("VR", int.Parse(address.Substring(2)));
            if (address.StartsWith("ZR")) return ("ZR", int.Parse(address.Substring(2)));
            if (address.StartsWith("R")) return ("WR", int.Parse(address.Substring(1)));
            if (address.StartsWith("B")) return ("B", int.Parse(address.Substring(1)));
            if (address.StartsWith("D")) return ("DM", int.Parse(address.Substring(1)));
            if (address.StartsWith("W")) return ("WR", int.Parse(address.Substring(1)));

            // Default: DM
            return ("DM", int.Parse(address));
        }

        // ═══════════════════════════════════════════
        //  读写命令
        // ═══════════════════════════════════════════

        /// <summary>读单个寄存器: RD type address</summary>
        private OperateResult<string> ReadSingle(string type, int address)
        {
            return SendCommand($"RD {type}{address}");
        }

        /// <summary>读多个寄存器: RDS type address count</summary>
        private OperateResult<string[]> ReadMultiple(string type, int startAddress, int count)
        {
            var r = SendCommand($"RDS {type}{startAddress} {count}");
            if (!r.IsSuccess) return OperateResult<string[]>.Failed(r.Message, r.ErrorCode);
            // Response: space-separated hex values
            string[] values = r.Content.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return OperateResult<string[]>.Success(values);
        }

        /// <summary>写单个寄存器: WR type address value</summary>
        private OperateResult WriteSingle(string type, int address, string value)
        {
            var r = SendCommand($"WR {type}{address} {value}");
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>写多个寄存器: WRS type address count values...</summary>
        private OperateResult WriteMultiple(string type, int startAddress, string[] values)
        {
            string valStr = string.Join(" ", values);
            var r = SendCommand($"WRS {type}{startAddress} {values.Length} {valStr}");
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  PLC 控制命令
        // ═══════════════════════════════════════════

        /// <summary>
        /// 读取 PLC 运行状态 (STS 命令)。
        /// <para>返回: 0=停止, 1=运行, 2=调试, 3=错误。</para>
        /// </summary>
        public OperateResult<byte> ReadStatus()
        {
            var r = SendCommand("STS");
            if (!r.IsSuccess) return OperateResult<byte>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 1) return OperateResult<byte>.Failed("状态响应不足");
            if (!byte.TryParse(r.Content.Trim(), out byte status))
                return OperateResult<byte>.Failed($"无法解析状态: {r.Content}");
            return OperateResult<byte>.Success(status);
        }

        /// <summary>
        /// 运行 PLC (MODE 命令)。
        /// </summary>
        public OperateResult Run()
        {
            var r = SendCommand("MODE 0");
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>
        /// 停止 PLC (MODE 命令)。
        /// </summary>
        public OperateResult Stop()
        {
            var r = SendCommand("MODE 1");
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>
        /// 读取 PLC 型号 (UNIT 命令)。
        /// </summary>
        public OperateResult<string> ReadPlcModel()
        {
            var r = SendCommand("UNIT");
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(r.Content.Trim());
        }

        /// <summary>异步运行 PLC。</summary>
        public Task<OperateResult> RunAsync() => Task.FromResult(Run());

        /// <summary>异步停止 PLC。</summary>
        public Task<OperateResult> StopAsync() => Task.FromResult(Stop());

        /// <summary>异步读取 PLC 状态。</summary>
        public Task<OperateResult<byte>> ReadStatusAsync() => Task.FromResult(ReadStatus());

        /// <summary>异步读取 PLC 型号。</summary>
        public Task<OperateResult<string>> ReadPlcModelAsync() => Task.FromResult(ReadPlcModel());

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 数据类型读写
        // ═══════════════════════════════════════════

        public OperateResult<bool> ReadBool(string address)
        {
            var (type, addr) = ParseAddress(address);
            var r = ReadSingle(type, addr);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Trim() != "0");
        }

        public OperateResult<short> ReadInt16(string address)
        {
            var (type, addr) = ParseAddress(address);
            var r = ReadSingle(type, addr);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(Convert.ToInt16(r.Content.Trim(), 16));
        }

        public OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult<int> ReadInt32(string address)
        {
            var (type, addr) = ParseAddress(address);
            var r = ReadMultiple(type, addr, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<int>.Failed("响应数据不足");
            // 两个16位寄存器拼接
            ushort hi = Convert.ToUInt16(r.Content[0].Trim(), 16);
            ushort lo = Convert.ToUInt16(r.Content[1].Trim(), 16);
            return OperateResult<int>.Success((int)((hi << 16) | lo));
        }

        public OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult<long> ReadInt64(string address)
        {
            var (type, addr) = ParseAddress(address);
            var r = ReadMultiple(type, addr, 4);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<long>.Failed("响应数据不足");
            long v = 0;
            for (int i = 0; i < 4; i++)
                v = (v << 16) | Convert.ToUInt16(r.Content[i].Trim(), 16);
            return OperateResult<long>.Success(v);
        }

        public OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public unsafe OperateResult<float> ReadFloat(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            int v = r.Content;
            return OperateResult<float>.Success(*(float*)&v);
        }

        public unsafe OperateResult<double> ReadDouble(string address)
        {
            var r = ReadInt64(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            long v = r.Content;
            return OperateResult<double>.Success(*(double*)&v);
        }

        public OperateResult<string> ReadString(string address, ushort length)
        {
            var (type, addr) = ParseAddress(address);
            int regCount = (length + 1) / 2;
            var r = ReadMultiple(type, addr, regCount);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            // 每个寄存器2字节，hex → bytes → ASCII
            var bytes = new System.Collections.Generic.List<byte>();
            foreach (string hex in r.Content)
            {
                ushort val = Convert.ToUInt16(hex.Trim(), 16);
                bytes.Add((byte)(val >> 8));
                bytes.Add((byte)(val & 0xFF));
            }
            string text = Encoding.ASCII.GetString(bytes.ToArray(), 0, Math.Min(length, bytes.Count));
            return OperateResult<string>.Success(text.TrimEnd('\0'));
        }

        public OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var (type, addr) = ParseAddress(address);
            int regCount = (length + 1) / 2;
            var r = ReadMultiple(type, addr, regCount);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            var bytes = new System.Collections.Generic.List<byte>();
            foreach (string hex in r.Content)
            {
                ushort val = Convert.ToUInt16(hex.Trim(), 16);
                bytes.Add((byte)(val >> 8));
                bytes.Add((byte)(val & 0xFF));
            }
            if (bytes.Count < length)
                return OperateResult<byte[]>.Failed($"响应数据不足: 期望 {length} 字节，实际 {bytes.Count} 字节");

            byte[] result = new byte[length];
            Array.Copy(bytes.ToArray(), result, length);
            return OperateResult<byte[]>.Success(result);
        }

        // ── 写入 ──

        public OperateResult Write(string address, bool value)
        {
            var (type, addr) = ParseAddress(address);
            return WriteSingle(type, addr, value ? "1" : "0");
        }

        public OperateResult Write(string address, short value)
        {
            var (type, addr) = ParseAddress(address);
            return WriteSingle(type, addr, ((ushort)value).ToString("X4"));
        }

        public OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public OperateResult Write(string address, int value)
        {
            var (type, addr) = ParseAddress(address);
            string[] vals = {
                ((ushort)((uint)value >> 16)).ToString("X4"),
                ((ushort)(value & 0xFFFF)).ToString("X4")
            };
            return WriteMultiple(type, addr, vals);
        }

        public OperateResult Write(string address, uint value) => Write(address, (int)value);
        public OperateResult Write(string address, long value) => Write(address, unchecked((ulong)value));
        public OperateResult Write(string address, ulong value)
        {
            var (type, addr) = ParseAddress(address);
            string[] vals = {
                ((ushort)(value >> 48)).ToString("X4"),
                ((ushort)(value >> 32)).ToString("X4"),
                ((ushort)(value >> 16)).ToString("X4"),
                ((ushort)value).ToString("X4")
            };
            return WriteMultiple(type, addr, vals);
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
            var (type, addr) = ParseAddress(address);
            byte[] bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
            if (bytes.Length % 2 != 0) Array.Resize(ref bytes, bytes.Length + 1);
            var vals = new System.Collections.Generic.List<string>();
            for (int i = 0; i < bytes.Length; i += 2)
            {
                ushort v = (ushort)((bytes[i] << 8) | bytes[i + 1]);
                vals.Add(v.ToString("X4"));
            }
            return WriteMultiple(type, addr, vals.ToArray());
        }

        public OperateResult Write(string address, byte[] data)
        {
            if (data == null)
                return OperateResult.Failed("写入数据不能为空");

            var (type, addr) = ParseAddress(address);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            var vals = new System.Collections.Generic.List<string>();
            for (int i = 0; i < data.Length; i += 2)
            {
                ushort v = (ushort)((data[i] << 8) | data[i + 1]);
                vals.Add(v.ToString("X4"));
            }
            return WriteMultiple(type, addr, vals.ToArray());
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 连接
        // ═══════════════════════════════════════════

        public OperateResult Connect()
        {
            if (_stream != null && _tcp == null)
            {
                // Stream-based (serial)
                OnConnected?.Invoke(this, EventArgs.Empty);
                _isConnected = true;
                return OperateResult.Success();
            }

            try
            {
                _tcp = new TcpClient(IpAddress, Port);
                _tcp.SendTimeout = Timeout;
                _tcp.ReceiveTimeout = Timeout;
                _stream = _tcp.GetStream();
                _isConnected = true;
                OnConnected?.Invoke(this, EventArgs.Empty);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"连接失败: {ex.Message}");
            }
        }

        public Task<OperateResult> ConnectAsync() => Task.Run(() => Connect());

        public void Disconnect()
        {
            _isConnected = false;
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _tcp = null;
            _stream = null;
            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { if (disposing) Disconnect(); }

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
        public Task<OperateResult> WriteAsync(string address, ushort value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, uint value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, long value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, ulong value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, double value) => Task.Run(() => Write(address, value));

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
