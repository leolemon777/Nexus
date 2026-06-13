using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Fatek
{
    /// <summary>
    /// 永宏 Fatek FBs 系列通讯客户端 — 支持 FBs-10MA/MC/MB 等。
    /// <para>支持 TCP (端口 5000) 和串口连接。</para>
    /// <para>帧格式: STX(0x02) + Station(2hex) + Command + Data + ETX(0x03) + Checksum(2hex)</para>
    /// <para>对标 HSL: FatekProgram / FatekServer — Read/Write R/D/T/C/X/Y/M 区域, 批量位, PLC控制</para>
    /// </summary>
    public class FatekClient : IReadWriteDevice, IBatchReadWrite, ISubscribeDevice
    {
        private readonly object _lock = new object();
        private TcpClient? _tcp;
        private Stream? _stream;
        private bool _isConnected;
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

        public bool IsConnected
        {
            get
            {
                lock (_lock)
                    return _isConnected && _tcp?.Connected == true;
            }
        }

        public FatekClient(string ipAddress, int port = 5000, byte station = 1, int timeout = 5000)
        {
            IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            Port = port;
            Station = station;
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        public FatekClient(Stream stream, byte station = 1, int timeout = 5000)
        {
            IpAddress = string.Empty;
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            Station = station;
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
                        _tcp.Close();
                        _tcp = null;
                        return OperateResult.Failed("连接超时 / Connection timeout");
                    }
                    _tcp.EndConnect(ar);
                    _stream = _tcp.GetStream();
                    _stream.ReadTimeout = Timeout;
                    _stream.WriteTimeout = Timeout;
                    _isConnected = true;
                }

                Log.Debug($"Connected to {IpAddress}:{Port}");
                OnConnected?.Invoke(this, EventArgs.Empty);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
                return OperateResult.Failed(ex.Message);
            }
        }

        public async Task<OperateResult> ConnectAsync()
        {
            try
            {
                lock (_lock)
                {
                    if (_isConnected) return OperateResult.Success();
                }

                _tcp = new TcpClient();
                await _tcp.ConnectAsync(IpAddress, Port).ConfigureAwait(false);
                lock (_lock)
                {
                    _stream = _tcp.GetStream();
                    _stream.ReadTimeout = Timeout;
                    _stream.WriteTimeout = Timeout;
                    _isConnected = true;
                }

                Log.Debug($"Connected to {IpAddress}:{Port}");
                OnConnected?.Invoke(this, EventArgs.Empty);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
                return OperateResult.Failed(ex.Message);
            }
        }

        public void Disconnect()
        {
            lock (_lock)
            {
                _isConnected = false;
                try { _stream?.Close(); } catch { }
                try { _tcp?.Close(); } catch { }
                _stream = null;
                _tcp = null;
            }
            Log.Debug("Disconnected");
            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Disconnect();
            GC.SuppressFinalize(this);
        }

        // ═══════════════════════════════════════════
        //  帧收发
        // ═══════════════════════════════════════════

        /// <summary>
        /// Fatek FBs 帧格式:
        /// STX(0x02) + Body + ETX(0x03) + Checksum(2 hex ASCII)
        /// Checksum = sum of all bytes from Station to ETX (inclusive), modulo 256
        /// </summary>
        private byte[] BuildFrame(string body)
        {
            // STX + Station(2) + body + ETX
            var content = Encoding.ASCII.GetBytes(body);
            var frame = new byte[1 + content.Length + 1]; // STX + content + ETX
            frame[0] = 0x02; // STX
            Buffer.BlockCopy(content, 0, frame, 1, content.Length);
            frame[frame.Length - 1] = 0x03; // ETX

            // Calculate checksum over Station + body + ETX (everything between STX and checksum)
            byte sum = 0;
            for (int i = 1; i < frame.Length; i++)
                sum += frame[i];
            string checksum = sum.ToString("X2");
            var csBytes = Encoding.ASCII.GetBytes(checksum);

            var result = new byte[frame.Length + csBytes.Length];
            Buffer.BlockCopy(frame, 0, result, 0, frame.Length);
            Buffer.BlockCopy(csBytes, 0, result, frame.Length, csBytes.Length);
            return result;
        }

        private OperateResult<string> SendAndReceive(string body)
        {
            lock (_lock)
            {
                if (_stream == null || !_isConnected)
                    return OperateResult<string>.Failed("未连接 / Not connected");

                try
                {
                    // Build frame with station prefix
                    string fullBody = Station.ToString("D2") + body;
                    var frame = BuildFrame(fullBody);

                    Log.Debug($"TX → {BitConverter.ToString(frame)}");
                    OnMessageSent?.Invoke(this, BitConverter.ToString(frame));

                    _stream.Write(frame, 0, frame.Length);
                    _stream.Flush();

                    // Read response: STX + data + ETX + Checksum(2)
                    var response = ReadResponse();
                    if (response == null)
                        return OperateResult<string>.Failed("无响应 / No response");

                    Log.Debug($"RX ← {response}");
                    OnMessageReceived?.Invoke(this, response);

                    // Strip STX, checksum, then ETX
                    string resp = response.Trim();
                    if (resp.StartsWith("\x02")) resp = resp.Substring(1);
                    // Remove trailing checksum (2 hex ASCII chars) first
                    if (resp.Length > 2)
                        resp = resp.Substring(0, resp.Length - 2);
                    // Then remove trailing ETX
                    if (resp.EndsWith("\x03")) resp = resp.Substring(0, resp.Length - 1);

                    return OperateResult<string>.Success(resp);
                }
                catch (Exception ex)
                {
                    _isConnected = false;
                    OnError?.Invoke(this, ex.Message);
                    return OperateResult<string>.Failed(ex.Message);
                }
            }
        }

        private string? ReadResponse()
        {
            try
            {
                // Read until we find ETX (0x03) + 2 checksum bytes
                var buf = new List<byte>();
                int maxRead = 512;
                while (buf.Count < maxRead)
                {
                    int b = _stream!.ReadByte();
                    if (b < 0) break;
                    buf.Add((byte)b);
                    if (b == 0x03 && buf.Count > 3)
                    {
                        // Check if we have 2 more bytes for checksum
                        if (buf.Count >= maxRead) break;
                        int cs1 = _stream.ReadByte();
                        if (cs1 < 0) break;
                        buf.Add((byte)cs1);
                        if (buf.Count >= maxRead) break;
                        int cs2 = _stream.ReadByte();
                        if (cs2 < 0) break;
                        buf.Add((byte)cs2);
                        break;
                    }
                }

                if (buf.Count == 0) return null;
                return Encoding.ASCII.GetString(buf.ToArray());
            }
            catch
            {
                return null;
            }
        }

        // ═══════════════════════════════════════════
        //  地址解析
        // ═══════════════════════════════════════════

        /// <summary>
        /// 解析 Fatek 地址为 (区域代码, 起始编号, 是否位操作)。
        /// 支持: R0, D100, Y0, X0, M100, T0, C0 等格式。
        /// </summary>
        private static (string area, int number, bool isBit) ParseAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
                throw new ArgumentException("Address is empty");

            string addr = address.Trim().ToUpperInvariant();
            char prefix = addr[0];
            string numStr = addr.Substring(1);
            if (!int.TryParse(numStr, out int num))
                throw new FormatException($"Invalid address number: {numStr}");

            switch (prefix)
            {
                case 'R': return ("R", num, true);   // Internal Relay
                case 'X': return ("X", num, true);   // Input
                case 'Y': return ("Y", num, true);   // Output
                case 'M': return ("M", num, true);   // Auxiliary Relay
                case 'D': return ("D", num, false);  // Data Register (16-bit)
                case 'T': return ("T", num, false);  // Timer current value
                case 'C': return ("C", num, false);  // Counter current value
                default:
                    throw new ArgumentException(
                        $"Unknown area prefix '{prefix}'. Valid: R/X/Y/M/D/T/C");
            }
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 读取
        // ═══════════════════════════════════════════

        public OperateResult<bool> ReadBool(string address)
        {
            try
            {
                var (area, num, _) = ParseAddress(address);
                var body = $"R{area}{num:D4}";
                var resp = SendAndReceive(body);
                if (!resp.IsSuccess) return OperateResult<bool>.Failed(resp.Message);
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult<bool>.Failed("Fatek error: " + resp.Content);

                string data = resp.Content.Substring(2).Trim();
                bool value = data == "1" || data.Equals("ON", StringComparison.OrdinalIgnoreCase);
                return OperateResult<bool>.Success(value);
            }
            catch (Exception ex)
            {
                return OperateResult<bool>.Failed(ex.Message);
            }
        }

        public OperateResult<short> ReadInt16(string address)
        {
            try
            {
                var (area, num, isBit) = ParseAddress(address);
                var body = $"R{area}{num:D4}";
                var resp = SendAndReceive(body);
                if (!resp.IsSuccess) return OperateResult<short>.Failed(resp.Message);
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult<short>.Failed("Fatek error: " + resp.Content);

                string data = resp.Content.Substring(2).Trim();
                if (short.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out short val))
                    return OperateResult<short>.Success(val);
                return OperateResult<short>.Failed($"Cannot parse '{data}' as Int16");
            }
            catch (Exception ex) { return OperateResult<short>.Failed(ex.Message); }
        }

        public OperateResult<ushort> ReadUInt16(string address)
        {
            try
            {
                var (area, num, isBit) = ParseAddress(address);
                var body = $"R{area}{num:D4}";
                var resp = SendAndReceive(body);
                if (!resp.IsSuccess) return OperateResult<ushort>.Failed(resp.Message);
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult<ushort>.Failed("Fatek error: " + resp.Content);

                string data = resp.Content.Substring(2).Trim();
                if (ushort.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort val))
                    return OperateResult<ushort>.Success(val);
                return OperateResult<ushort>.Failed($"Cannot parse '{data}' as UInt16");
            }
            catch (Exception ex) { return OperateResult<ushort>.Failed(ex.Message); }
        }

        public OperateResult<int> ReadInt32(string address)
        {
            try
            {
                // Read two consecutive registers
                var lo = ReadInt16(address);
                if (!lo.IsSuccess) return OperateResult<int>.Failed(lo.Message);
                var hi = ReadInt16(IncrementAddress(address));
                if (!hi.IsSuccess) return OperateResult<int>.Failed(hi.Message);
                return OperateResult<int>.Success((hi.Content << 16) | (lo.Content & 0xFFFF));
            }
            catch (Exception ex) { return OperateResult<int>.Failed(ex.Message); }
        }

        public OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message);
            return OperateResult<uint>.Success((uint)r.Content);
        }

        public OperateResult<long> ReadInt64(string address)
        {
            try
            {
                var lo = ReadInt32(address);
                if (!lo.IsSuccess) return OperateResult<long>.Failed(lo.Message);
                var hi = ReadInt32(IncrementAddress(IncrementAddress(address)));
                if (!hi.IsSuccess) return OperateResult<long>.Failed(hi.Message);
                return OperateResult<long>.Success(((long)hi.Content << 32) | (uint)lo.Content);
            }
            catch (Exception ex) { return OperateResult<long>.Failed(ex.Message); }
        }

        public OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message);
            return OperateResult<ulong>.Success((ulong)r.Content);
        }

        public OperateResult<float> ReadFloat(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            return OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(r.Content), 0));
        }

        public OperateResult<double> ReadDouble(string address)
        {
            var r = ReadInt64(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return OperateResult<double>.Success(BitConverter.Int64BitsToDouble(r.Content));
        }

        public OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, (ushort)(length * 2));
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content).TrimEnd('\0'));
        }

        public OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            try
            {
                var result = new List<byte>();
                string currentAddr = address;
                for (int i = 0; i < (length + 1) / 2; i++)
                {
                    var r = ReadInt16(currentAddr);
                    if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message);
                    result.AddRange(BitConverter.GetBytes(r.Content));
                    currentAddr = IncrementAddress(currentAddr);
                }
                return OperateResult<byte[]>.Success(result.ToArray());
            }
            catch (Exception ex) { return OperateResult<byte[]>.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 写入
        // ═══════════════════════════════════════════

        public OperateResult Write(string address, bool value)
        {
            try
            {
                var (area, num, _) = ParseAddress(address);
                var body = $"W{area}{num:D4}{(value ? "1" : "0")}";
                var resp = SendAndReceive(body);
                if (!resp.IsSuccess) return resp;
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult.Failed("Fatek write error: " + resp.Content);
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        public OperateResult Write(string address, short value)
            => WriteRegister(address, value.ToString("D5"));

        public OperateResult Write(string address, ushort value)
            => WriteRegister(address, value.ToString("D5"));

        public OperateResult Write(string address, int value)
        {
            var lo = (short)(value & 0xFFFF);
            var hi = (short)((value >> 16) & 0xFFFF);
            var r1 = Write(address, lo);
            if (!r1.IsSuccess) return r1;
            return Write(IncrementAddress(address), hi);
        }

        public OperateResult Write(string address, uint value)
            => Write(address, (int)value);

        public OperateResult Write(string address, long value)
        {
            var r1 = Write(address, (int)(value & 0xFFFFFFFF));
            if (!r1.IsSuccess) return r1;
            return Write(IncrementAddress(IncrementAddress(address)), (int)(value >> 32));
        }

        public OperateResult Write(string address, ulong value)
            => Write(address, (long)value);

        public OperateResult Write(string address, float value)
            => Write(address, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        public OperateResult Write(string address, double value)
            => Write(address, BitConverter.ToInt64(BitConverter.GetBytes(value), 0));

        public OperateResult Write(string address, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            return Write(address, bytes);
        }

        public OperateResult Write(string address, byte[] data)
        {
            for (int i = 0; i < data.Length; i += 2)
            {
                short val = data.Length > i + 1
                    ? (short)(data[i] | (data[i + 1] << 8))
                    : data[i];
                string addr = IncrementAddress(address, i / 2);
                var r = Write(addr, val);
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        private OperateResult WriteRegister(string address, string valueStr)
        {
            try
            {
                var (area, num, _) = ParseAddress(address);
                var body = $"W{area}{num:D4}{valueStr}";
                var resp = SendAndReceive(body);
                if (!resp.IsSuccess) return resp;
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult.Failed("Fatek write error: " + resp.Content);
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  Async 方法
        // ═══════════════════════════════════════════

        public Task<OperateResult<bool>> ReadBoolAsync(string address)
            => Task.Run(() => ReadBool(address));

        public Task<OperateResult<short>> ReadInt16Async(string address)
            => Task.Run(() => ReadInt16(address));

        public Task<OperateResult<ushort>> ReadUInt16Async(string address)
            => Task.Run(() => ReadUInt16(address));

        public Task<OperateResult<int>> ReadInt32Async(string address)
            => Task.Run(() => ReadInt32(address));

        public Task<OperateResult<uint>> ReadUInt32Async(string address)
            => Task.Run(() => ReadUInt32(address));

        public Task<OperateResult<long>> ReadInt64Async(string address)
            => Task.Run(() => ReadInt64(address));

        public Task<OperateResult<ulong>> ReadUInt64Async(string address)
            => Task.Run(() => ReadUInt64(address));

        public Task<OperateResult<float>> ReadFloatAsync(string address)
            => Task.Run(() => ReadFloat(address));

        public Task<OperateResult<double>> ReadDoubleAsync(string address)
            => Task.Run(() => ReadDouble(address));

        public Task<OperateResult<string>> ReadStringAsync(string address, ushort length)
            => Task.Run(() => ReadString(address, length));

        public Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length)
            => Task.Run(() => ReadBytes(address, length));

        public Task<OperateResult> WriteAsync(string address, bool value)
            => Task.Run(() => Write(address, value));

        public Task<OperateResult> WriteAsync(string address, short value)
            => Task.Run(() => Write(address, value));

        public Task<OperateResult> WriteAsync(string address, int value)
            => Task.Run(() => Write(address, value));

        public Task<OperateResult> WriteAsync(string address, float value)
            => Task.Run(() => Write(address, value));

        public Task<OperateResult> WriteAsync(string address, string value)
            => Task.Run(() => Write(address, value));

        public Task<OperateResult> WriteAsync(string address, byte[] data)
            => Task.Run(() => Write(address, data));

        // ═══════════════════════════════════════════
        //  辅助
        // ═══════════════════════════════════════════

        private static string IncrementAddress(string address, int offset = 1)
        {
            var (area, num, _) = ParseAddress(address);
            return $"{area}{num + offset}";
        }

        // ═══════════════════════════════════════════
        //  批量位操作 — ReadBools / WriteBools
        // ═══════════════════════════════════════════

        /// <summary>
        /// 批量读取位地址。
        /// <para>Fatek 命令 "44" = 批量读位, 返回 '0'/'1' 字符序列。</para>
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
                var (area, num, _) = ParseAddress(address);
                // Fatek 批量读位命令: "44" + count(2hex) + area + startAddr(4dec)
                string countHex = count.ToString("X2");
                string body = $"44{countHex}{area}{num:D4}";
                var resp = SendAndReceive(body);
                if (!resp.IsSuccess) return OperateResult<bool[]>.Failed(resp.Message);
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult<bool[]>.Failed("Fatek read error: " + resp.Content);

                // 响应格式: "!0" + data (0/1 字符序列)
                string data = resp.Content.Substring(2);
                var result = new bool[count];
                for (int i = 0; i < count && i < data.Length; i++)
                    result[i] = data[i] == '1';

                return OperateResult<bool[]>.Success(result);
            }
            catch (Exception ex) { return OperateResult<bool[]>.Failed(ex.Message); }
        }

        /// <summary>
        /// 批量写入位地址。
        /// <para>Fatek 命令 "45" = 批量写位。</para>
        /// </summary>
        public OperateResult WriteBools(string address, bool[] values)
        {
            if (values == null || values.Length == 0) return OperateResult.Success();
            if (values.Length == 1) return Write(address, values[0]);

            try
            {
                var (area, num, _) = ParseAddress(address);
                string countHex = values.Length.ToString("X2");
                var dataChars = new char[values.Length];
                for (int i = 0; i < values.Length; i++)
                    dataChars[i] = values[i] ? '1' : '0';

                string body = $"45{countHex}{area}{num:D4}{new string(dataChars)}";
                var resp = SendAndReceive(body);
                if (!resp.IsSuccess) return resp;
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult.Failed("Fatek write error: " + resp.Content);
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  PLC 控制命令
        // ═══════════════════════════════════════════

        /// <summary>读取 PLC 运行状态。</summary>
        public OperateResult<bool> ReadPlcStatus()
        {
            try
            {
                // Fatek 命令 "40" = 读取状态
                var resp = SendAndReceive("40");
                if (!resp.IsSuccess) return OperateResult<bool>.Failed(resp.Message);
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult<bool>.Failed("Fatek error: " + resp.Content);

                string data = resp.Content.Substring(2).Trim();
                // 返回3字节hex: byte0 = run status (0/1)
                bool isRunning = data.Length > 0 && data[0] == '1';
                return OperateResult<bool>.Success(isRunning);
            }
            catch (Exception ex) { return OperateResult<bool>.Failed(ex.Message); }
        }

        /// <summary>启动 PLC（RUN）。</summary>
        public OperateResult Run()
        {
            try
            {
                // Fatek 命令 "41" + "1" = 启动
                var resp = SendAndReceive("411");
                if (!resp.IsSuccess) return resp;
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult.Failed("Fatek RUN error: " + resp.Content);
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        /// <summary>停止 PLC（STOP）。</summary>
        public OperateResult Stop()
        {
            try
            {
                // Fatek 命令 "41" + "0" = 停止
                var resp = SendAndReceive("410");
                if (!resp.IsSuccess) return resp;
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult.Failed("Fatek STOP error: " + resp.Content);
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        // ── 批量位异步 ──
        public Task<OperateResult<bool[]>> ReadBoolsAsync(string address, ushort count) => Task.Run(() => ReadBools(address, count));
        public Task<OperateResult> WriteBoolsAsync(string address, bool[] values) => Task.Run(() => WriteBools(address, values));
        public Task<OperateResult<bool>> ReadPlcStatusAsync() => Task.Run(() => ReadPlcStatus());
        public Task<OperateResult> RunAsync() => Task.Run(() => Run());
        public Task<OperateResult> StopAsync() => Task.Run(() => Stop());

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
