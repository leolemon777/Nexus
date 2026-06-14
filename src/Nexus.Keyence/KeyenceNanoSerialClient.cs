using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Keyence
{
    /// <summary>
    /// 基恩士 Nano 系列串口客户端 — 支持 KV-10/24 等。
    /// <para>文本协议 over 串口，帧格式: [站号] + 命令 + \r</para>
    /// <para>响应: OK + 数据（读取）/ OK（写入）/ E0/E1/E2 + 错误码</para>
    /// </summary>
    public class KeyenceNanoSerialClient : IReadWriteDevice, IBatchReadWrite
    {
        private readonly object _lock = new object();
        private ISerialPort? _serialPort;
        private Stream? _stream;
        protected ILogger Log { get; set; }

        public byte Station { get; set; }
        public int Timeout { get; set; }

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;
        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected => _serialPort?.IsOpen == true || (_stream != null && _serialPort == null);

        public KeyenceNanoSerialClient(ISerialPort serialPort, byte station = 0, int timeout = 5000)
        {
            _serialPort = serialPort ?? throw new ArgumentNullException(nameof(serialPort));
            Station = station;
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        public KeyenceNanoSerialClient(Stream stream, byte station = 0, int timeout = 5000)
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

        private OperateResult<string> SendCommand(string command)
        {
            try
            {
                lock (_lock)
                {
                    if (_stream == null)
                        return OperateResult<string>.Failed("未连接");

                    string frame = Station.ToString("D2") + command + "\r";
                    Log.Debug($"TX → {frame.TrimEnd()}");
                    OnMessageSent?.Invoke(this, frame.TrimEnd());

                    byte[] txBytes = Encoding.ASCII.GetBytes(frame);
                    _stream.Write(txBytes, 0, txBytes.Length);

                    string? response = ReadLine();
                    if (response == null)
                        return OperateResult<string>.Failed("读取响应超时");

                    Log.Debug($"RX ← {response.TrimEnd()}");
                    OnMessageReceived?.Invoke(this, response.TrimEnd());

                    if (response.StartsWith("E0"))
                        return OperateResult<string>.Failed($"Nano 错误: 未定义命令 ({response})");
                    if (response.StartsWith("E1"))
                        return OperateResult<string>.Failed($"Nano 错误: 非法数据 ({response})");
                    if (response.StartsWith("E2"))
                        return OperateResult<string>.Failed($"Nano 错误: 地址越界 ({response})");
                    if (response.StartsWith("E"))
                        return OperateResult<string>.Failed($"Nano 错误: {response}");

                    if (!response.StartsWith("OK"))
                        return OperateResult<string>.Failed($"未知响应: {response}");

                    string data = response.Length > 2 ? response.Substring(2) : "";
                    return OperateResult<string>.Success(data);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Nano 通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult<string>.Failed($"通讯异常: {ex.Message}");
            }
        }

        private string? ReadLine()
        {
            var sb = new StringBuilder(64);
            int start = Environment.TickCount;

            while (unchecked(Environment.TickCount - start) <= Timeout)
            {
                int remaining = Timeout - unchecked(Environment.TickCount - start);
                if (remaining < 0) return null;
                int b = ReadByteWithTimeout(remaining);
                if (b < 0) return null;
                if (b == '\r' || b == '\n')
                {
                    if (b == '\r')
                    {
                        int rem2 = Timeout - unchecked(Environment.TickCount - start);
                        int next = ReadByteWithTimeout(Math.Min(rem2 < 0 ? 0 : rem2, 200));
                        if (next >= 0 && next != '\n')
                            sb.Append((char)next);
                    }
                    return sb.ToString();
                }
                sb.Append((char)b);
            }
            return null;
        }

        private int ReadByteWithTimeout(int remainingMs)
        {
            int start = Environment.TickCount;
            while (unchecked(Environment.TickCount - start) <= remainingMs)
            {
                try
                {
                    if (_serialPort != null)
                    {
                        byte[] buf = new byte[1];
                        int read = _serialPort.Read(buf, 0, 1);
                        if (read > 0) return buf[0];
                    }
                    else if (_stream != null)
                    {
                        return _stream.ReadByte();
                    }
                }
                catch (TimeoutException) { return -1; }
            }
            return -1;
        }

        // ═══════════════════════════════════════════
        //  内部读写
        // ═══════════════════════════════════════════

        private OperateResult<string> ReadWord(KeyenceNanoAddress addr)
            => SendCommand($"RD {addr.AreaCode}{addr.Address}.{addr.SubAddress}");

        private OperateResult<string> ReadBit(KeyenceNanoAddress addr)
            => SendCommand($"RDS {addr.AreaCode}{addr.Address}.{addr.SubAddress}");

        private OperateResult WriteWord(KeyenceNanoAddress addr, string data)
            => SendCommand($"WD {addr.AreaCode}{addr.Address}.{addr.SubAddress} {data}");

        private OperateResult WriteBit(KeyenceNanoAddress addr, string data)
            => SendCommand($"WRS {addr.AreaCode}{addr.Address}.{addr.SubAddress} {data}");

        private bool IsBitArea(string areaCode)
            => areaCode == "R" || areaCode == "B" || areaCode == "T" || areaCode == "C" ||
               areaCode == "MR" || areaCode == "LR" || areaCode == "CR";

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 数据类型读写
        // ═══════════════════════════════════════════

        public OperateResult<bool> ReadBool(string address)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            if (addr.IsBitArea || addr.SubAddress > 0)
            {
                var r = ReadBit(addr);
                if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
                return OperateResult<bool>.Success(r.Content.Trim() != "0");
            }
            else
            {
                var r = ReadWord(addr);
                if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
                return OperateResult<bool>.Success(Convert.ToInt16(r.Content.Trim(), 16) != 0);
            }
        }

        public OperateResult<short> ReadInt16(string address)
        {
            var r = ReadWord(KeyenceNanoAddress.Parse(address));
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(Convert.ToInt16(r.Content.Trim(), 16));
        }

        public OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess
                ? OperateResult<ushort>.Success((ushort)r.Content)
                : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult<int> ReadInt32(string address)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            var r1 = ReadWord(addr);
            if (!r1.IsSuccess) return OperateResult<int>.Failed(r1.Message, r1.ErrorCode);

            var nextAddr = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + 1}.{addr.SubAddress}");
            var r2 = ReadWord(nextAddr);
            if (!r2.IsSuccess) return OperateResult<int>.Failed(r2.Message, r2.ErrorCode);

            ushort hi = Convert.ToUInt16(r1.Content.Trim(), 16);
            ushort lo = Convert.ToUInt16(r2.Content.Trim(), 16);
            return OperateResult<int>.Success((hi << 16) | lo);
        }

        public OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess
                ? OperateResult<uint>.Success((uint)r.Content)
                : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult<long> ReadInt64(string address)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            long value = 0;
            for (int i = 0; i < 4; i++)
            {
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i}.{addr.SubAddress}");
                var r = ReadWord(a);
                if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
                value = (value << 16) | Convert.ToUInt16(r.Content.Trim(), 16);
            }
            return OperateResult<long>.Success(value);
        }

        public OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess
                ? OperateResult<ulong>.Success((ulong)r.Content)
                : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
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
            var addr = KeyenceNanoAddress.Parse(address);
            int regCount = (length + 1) / 2;
            var bytes = new List<byte>();

            for (int i = 0; i < regCount; i++)
            {
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i}.{addr.SubAddress}");
                var r = ReadWord(a);
                if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
                ushort val = Convert.ToUInt16(r.Content.Trim(), 16);
                bytes.Add((byte)(val >> 8));
                bytes.Add((byte)(val & 0xFF));
            }

            string text = Encoding.ASCII.GetString(bytes.ToArray(), 0, Math.Min(length, bytes.Count));
            return OperateResult<string>.Success(text.TrimEnd('\0'));
        }

        public OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            int regCount = (length + 1) / 2;
            var bytes = new List<byte>();

            for (int i = 0; i < regCount; i++)
            {
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i}.{addr.SubAddress}");
                var r = ReadWord(a);
                if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
                ushort val = Convert.ToUInt16(r.Content.Trim(), 16);
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
            var addr = KeyenceNanoAddress.Parse(address);
            if (addr.IsBitArea || addr.SubAddress > 0)
                return WriteBit(addr, value ? "1" : "0");
            return WriteWord(addr, value ? "0001" : "0000");
        }

        public OperateResult Write(string address, short value)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            return WriteWord(addr, ((ushort)value).ToString("X4"));
        }

        public OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public OperateResult Write(string address, int value)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            var r1 = WriteWord(addr, ((ushort)((uint)value >> 16)).ToString("X4"));
            if (!r1.IsSuccess) return r1;
            var nextAddr = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + 1}.{addr.SubAddress}");
            return WriteWord(nextAddr, ((ushort)(value & 0xFFFF)).ToString("X4"));
        }

        public OperateResult Write(string address, uint value) => Write(address, (int)value);

        public OperateResult Write(string address, long value) => Write(address, unchecked((ulong)value));

        public OperateResult Write(string address, ulong value)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            for (int i = 0; i < 4; i++)
            {
                ushort word = (ushort)(value >> (48 - i * 16));
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i}.{addr.SubAddress}");
                var r = WriteWord(a, word.ToString("X4"));
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
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
            var addr = KeyenceNanoAddress.Parse(address);
            byte[] strBytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
            if (strBytes.Length % 2 != 0) Array.Resize(ref strBytes, strBytes.Length + 1);

            for (int i = 0; i < strBytes.Length; i += 2)
            {
                ushort word = (ushort)((strBytes[i] << 8) | strBytes[i + 1]);
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i / 2}.{addr.SubAddress}");
                var r = WriteWord(a, word.ToString("X4"));
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        public OperateResult Write(string address, byte[] data)
        {
            if (data == null)
                return OperateResult.Failed("写入数据不能为空");

            var addr = KeyenceNanoAddress.Parse(address);
            byte[] padded = data;
            if (padded.Length % 2 != 0) { padded = new byte[data.Length + 1]; Array.Copy(data, padded, data.Length); }

            for (int i = 0; i < padded.Length; i += 2)
            {
                ushort word = (ushort)((padded[i] << 8) | padded[i + 1]);
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i / 2}.{addr.SubAddress}");
                var r = WriteWord(a, word.ToString("X4"));
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 连接
        // ═══════════════════════════════════════════

        public OperateResult Connect()
        {
            if (_serialPort != null)
            {
                try
                {
                    _serialPort.ReadTimeout = Timeout;
                    _serialPort.WriteTimeout = Timeout;
                    _serialPort.Open();
                    _stream = null;
                    Log.Info($"串口已打开 {_serialPort.PortName}");
                    OnConnected?.Invoke(this, EventArgs.Empty);
                    return OperateResult.Success();
                }
                catch (Exception ex)
                {
                    Log.Error($"串口打开失败 — {ex.Message}");
                    OnError?.Invoke(this, ex.Message);
                    return OperateResult.Failed($"串口打开失败: {ex.Message}");
                }
            }

            if (_stream != null)
            {
                OnConnected?.Invoke(this, EventArgs.Empty);
                return OperateResult.Success();
            }

            return OperateResult.Failed("未配置串口或流");
        }

        public Task<OperateResult> ConnectAsync() => Task.FromResult(Connect());

        public void Disconnect()
        {
            try { _serialPort?.Close(); } catch { }
            if (_serialPort == null)
            {
                try { _stream?.Close(); } catch { }
                _stream = null;
            }
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

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchRead(addresses), cancellationToken);

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

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => RandomRead(addresses), cancellationToken);

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

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchWrite(items), cancellationToken);
    }
}
