using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Siemens
{
    /// <summary>
    /// 西门子 Fetch/Write 协议客户端。
    /// <para>适用于 S7-300/400/1200/1500，PLC 侧需启用 Fetch/Write 连接（非 S7 通信）。</para>
    /// <para>地址格式：I100, Q100, M100, DB1.100, T100, C100。不支持单独位操作，位操作通过读-改-写实现。</para>
    /// </summary>
    public class SiemensFetchWriteClient : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        // ── FetchWrite 区域码 ───────────────────
        private const byte AreaDB = 1;
        private const byte AreaM  = 2;
        private const byte AreaI  = 3;
        private const byte AreaQ  = 4;
        private const byte AreaT  = 7;
        private const byte AreaC  = 6;

        // ── TcpDeviceBase 抽象实现 ──────────────

        /// <summary>Fetch/Write 响应头固定 16 字节。</summary>
        protected override int ResponseHeaderLength => 16;

        /// <summary>
        /// 从响应头中解析数据长度。
        /// Fetch/Write 响应头无显式长度字段；成功时数据紧跟在 16 字节头之后。
        /// </summary>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 16) return 0;
            // 错误响应无额外数据
            if (header[8] != 0) return 0;
            // 成功响应：字节 [12-13] 为请求的数据字节数（大端）
            return (header[12] << 8) | header[13];
        }

        // ── 构造 ────────────────────────────────

        /// <summary>
        /// 初始化 Fetch/Write 协议客户端。
        /// </summary>
        public SiemensFetchWriteClient(string ip, int port = 102, int timeout = 5000)
            : base(ip, port, timeout) { }

        // ── 原始字节读写 ────────────────────────

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var cmd = BuildReadCommand(address, length);
            if (!cmd.IsSuccess) return OperateResult<byte[]>.Failed(cmd.Message);

            var recv = SendAndReceive(cmd.Content);
            if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

            var check = CheckResponse(recv.Content);
            if (!check.IsSuccess) return OperateResult<byte[]>.Failed(check.Message);

            if (recv.Content.Length <= 16)
                return OperateResult<byte[]>.Success(new byte[0]);

            var data = new byte[recv.Content.Length - 16];
            Array.Copy(recv.Content, 16, data, 0, data.Length);
            return OperateResult<byte[]>.Success(data);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var cmd = BuildWriteCommand(address, data);
            if (!cmd.IsSuccess) return OperateResult.Failed(cmd.Message);

            var recv = SendAndReceive(cmd.Content);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            return CheckResponse(recv.Content);
        }

        // ── 标准类型读取 ────────────────────────

        public override OperateResult<bool> ReadBool(string address)
        {
            string baseAddr = GetWordAddress(address, out int bitOffset);
            var r = ReadBytes(baseAddr, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            return OperateResult<bool>.Success((r.Content[0] & (1 << bitOffset)) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadBytes(address, 2);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1]));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadBytes(address, 4);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            return OperateResult<int>.Success(
                (r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadBytes(address, 8);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message);
            long val = 0;
            for (int i = 0; i < 8; i++) val = (val << 8) | r.Content[i];
            return OperateResult<long>.Success(val);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            return OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(r.Content), 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadInt64(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return OperateResult<double>.Success(BitConverter.ToDouble(BitConverter.GetBytes(r.Content), 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content).TrimEnd('\0'));
        }

        // ── 标准类型写入 ────────────────────────

        public override OperateResult Write(string address, bool value)
        {
            string baseAddr = GetWordAddress(address, out int bitOffset);
            var r = ReadBytes(baseAddr, 1);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message);
            byte b = r.Content[0];
            if (value) b |= (byte)(1 << bitOffset);
            else b &= (byte)~(1 << bitOffset);
            return Write(baseAddr, new byte[] { b });
        }

        public override OperateResult Write(string address, short value)
            => Write(address, new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
            => Write(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, ulong value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, float value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, double value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, string value)
            => Write(address, System.Text.Encoding.ASCII.GetBytes(value ?? string.Empty));

        // ── 协议帧构建 ───────────────────────────

        /// <summary>构建读取命令帧（16 字节）。</summary>
        public static OperateResult<byte[]> BuildReadCommand(string address, ushort count)
        {
            var addr = AnalysisAddress(address);
            if (!addr.Success) return OperateResult<byte[]>.Failed(addr.Message);

            var cmd = new byte[16];
            cmd[0] = 0x53; cmd[1] = 0x35; cmd[2] = 0x10; cmd[3] = 0x01;
            cmd[4] = 0x03; cmd[5] = 0x05; cmd[6] = 0x03; cmd[7] = 0x08;
            cmd[8] = addr.AreaCode;
            cmd[9] = (byte)addr.DbNumber;
            cmd[10] = (byte)(addr.StartAddr >> 8);
            cmd[11] = (byte)(addr.StartAddr & 0xFF);

            // T/C 以字为单位（必须偶数），其余以字节为单位
            if (addr.AreaCode == AreaT || addr.AreaCode == AreaC)
            {
                if (count % 2 != 0) count++;
                ushort wordCount = (ushort)(count / 2);
                cmd[12] = (byte)(wordCount >> 8);
                cmd[13] = (byte)(wordCount & 0xFF);
            }
            else
            {
                cmd[12] = (byte)(count >> 8);
                cmd[13] = (byte)(count & 0xFF);
            }
            cmd[14] = 0xFF; cmd[15] = 0x02;
            return OperateResult<byte[]>.Success(cmd);
        }

        /// <summary>构建写入命令帧（16 字节头 + 数据）。</summary>
        public static OperateResult<byte[]> BuildWriteCommand(string address, byte[] data)
        {
            var addr = AnalysisAddress(address);
            if (!addr.Success) return OperateResult<byte[]>.Failed(addr.Message);

            var cmd = new byte[16 + data.Length];
            cmd[0] = 0x53; cmd[1] = 0x35; cmd[2] = 0x10; cmd[3] = 0x01;
            cmd[4] = 0x03; cmd[5] = 0x06; cmd[6] = 0x03; cmd[7] = 0x08;
            cmd[8] = addr.AreaCode;
            cmd[9] = (byte)addr.DbNumber;
            cmd[10] = (byte)(addr.StartAddr >> 8);
            cmd[11] = (byte)(addr.StartAddr & 0xFF);
            cmd[12] = (byte)(data.Length >> 8);
            cmd[13] = (byte)(data.Length & 0xFF);
            cmd[14] = 0xFF; cmd[15] = 0x02;
            Array.Copy(data, 0, cmd, 16, data.Length);
            return OperateResult<byte[]>.Success(cmd);
        }

        /// <summary>校验 Fetch/Write 响应。</summary>
        public static OperateResult CheckResponse(byte[] content)
        {
            if (content == null || content.Length < 9)
                return OperateResult.Failed($"Fetch/Write 响应过短 ({content?.Length ?? 0} 字节)");
            if (content[8] != 0)
                return OperateResult.Failed($"Fetch/Write 错误码: 0x{content[8]:X2}");
            return OperateResult.Success();
        }

        // ── 地址解析 ─────────────────────────────

        /// <summary>地址解析结果。</summary>
        public readonly struct AddressResult
        {
            public bool Success { get; }
            public string Message { get; }
            public byte AreaCode { get; }
            public int StartAddr { get; }
            public ushort DbNumber { get; }

            private AddressResult(bool success, string message, byte areaCode, int startAddr, ushort dbNumber)
            {
                Success = success; Message = message;
                AreaCode = areaCode; StartAddr = startAddr; DbNumber = dbNumber;
            }

            public static AddressResult Ok(byte areaCode, int startAddr, ushort dbNumber)
                => new AddressResult(true, string.Empty, areaCode, startAddr, dbNumber);

            public static AddressResult Fail(string message)
                => new AddressResult(false, message, 0, 0, 0);
        }

        /// <summary>解析地址字符串。</summary>
        public static AddressResult AnalysisAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
                return AddressResult.Fail("地址不能为空");

            try
            {
                if (address.StartsWith("DB", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = address.Substring(2).Split('.');
                    if (parts.Length < 2)
                        return AddressResult.Fail("DB 地址格式错误，应为 DB1.100");
                    if (!ushort.TryParse(parts[0], out ushort dbNumber) || dbNumber > 255)
                        return AddressResult.Fail("DB 编号必须在 0-255 之间");
                    if (!int.TryParse(parts[1], out int startAddr))
                        return AddressResult.Fail($"DB 偏移地址解析失败: {parts[1]}");
                    return AddressResult.Ok(AreaDB, startAddr, dbNumber);
                }

                char prefix = char.ToUpper(address[0]);
                string rest = address.Substring(1);
                byte areaCode;
                switch (prefix)
                {
                    case 'I': areaCode = AreaI; break;
                    case 'Q': areaCode = AreaQ; break;
                    case 'M': areaCode = AreaM; break;
                    case 'T': areaCode = AreaT; break;
                    case 'C': areaCode = AreaC; break;
                    default: return AddressResult.Fail($"不支持的地址区域: {prefix}");
                }
                if (!int.TryParse(rest, out int addr))
                    return AddressResult.Fail($"地址偏移解析失败: {rest}");
                return AddressResult.Ok(areaCode, addr, 0);
            }
            catch (Exception ex)
            {
                return AddressResult.Fail($"地址解析失败: {ex.Message}");
            }
        }

        /// <summary>从位地址提取字地址和位偏移。</summary>
        private static string GetWordAddress(string address, out int bitOffset)
        {
            int dot = address.IndexOf('.');
            if (dot > 0 && int.TryParse(address.Substring(dot + 1), out bitOffset)
                && bitOffset >= 0 && bitOffset < 8)
            {
                return address.Substring(0, dot);
            }
            bitOffset = 0;
            return address;
        }

        public override string ToString() => $"SiemensFetchWriteClient[{Ip}:{Port}]";

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

        /// <inheritdoc/>
        protected override byte[] BuildHeartbeat()
        {
            try { return BuildReadCommand("DB1.0", 1).Content; }
            catch { return null; }
        }
    }
}
