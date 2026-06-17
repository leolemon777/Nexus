using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Mitsubishi
{
    public class FxSerialClient : SerialDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        public FxSerialClient(ISerialPort port, int timeout = 5000) : base(port, timeout) { }

        // FX 使用自定义 ENQ/ACK/STX+ETX 帧协议，不走 SendAndReceive 基类路径
        protected override int ResponseHeaderLength => 1;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        protected async Task<OperateResult<byte[]>> SendFxAsync(byte[] commandFrame, CancellationToken ct)
        {
            bool lockTaken = false;
            try
            {
                await _asyncLock.WaitAsync(ct).ConfigureAwait(false);
                lockTaken = true;

                if (!Port.IsOpen) return OperateResult<byte[]>.Failed("串口未打开");

                Port.Write(new byte[] { 0x05 }, 0, 1);
                byte[] ackBuf = new byte[1];
                if (await ReadExactSerialAsync(ackBuf, 0, 1, ct).ConfigureAwait(false) < 1) return OperateResult<byte[]>.Failed("等待 FX ACK 超时");
                if (ackBuf[0] == 0x15) return OperateResult<byte[]>.Failed("FX PLC 返回 NAK");
                if (ackBuf[0] != 0x06) return OperateResult<byte[]>.Failed($"FX 握手失败: 0x{ackBuf[0]:X2}");

                Port.Write(commandFrame, 0, commandFrame.Length);
                byte[] respHeader = new byte[1];
                if (await ReadExactSerialAsync(respHeader, 0, 1, ct).ConfigureAwait(false) < 1) return OperateResult<byte[]>.Failed("读取 FX 响应头超时");
                if (respHeader[0] == 0x06) return OperateResult<byte[]>.Success(Array.Empty<byte>());
                if (respHeader[0] != 0x02) return OperateResult<byte[]>.Failed($"FX 响应格式错误: 0x{respHeader[0]:X2}");

                using var ms = new System.IO.MemoryStream();
                ms.WriteByte(0x02);
                bool etxFound = false;
                while (!etxFound)
                {
                    byte[] buf = new byte[1];
                    if (await ReadExactSerialAsync(buf, 0, 1, ct).ConfigureAwait(false) < 1) return OperateResult<byte[]>.Failed("读取 FX 响应数据超时");
                    ms.WriteByte(buf[0]);
                    if (buf[0] == 0x03) etxFound = true;
                }
                byte[] sumBuf = new byte[2];
                if (await ReadExactSerialAsync(sumBuf, 0, 2, ct).ConfigureAwait(false) < 2) return OperateResult<byte[]>.Failed("读取 FX SUM 校验和超时");
                ms.Write(sumBuf, 0, 2);

                if (!FxFrameBuilder.VerifyResponse(ms.ToArray(), out byte[] data)) return OperateResult<byte[]>.Failed("FX 响应 SUM 校验失败");
                return OperateResult<byte[]>.Success(data);
            }
            catch (OperationCanceledException)
            {
                return OperateResult<byte[]>.Failed("FX 串口通讯已取消");
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed($"FX 串口通讯异常: {ex.Message}");
            }
            finally
            {
                if (lockTaken)
                    _asyncLock.Release();
            }
        }

        private async Task<int> ReadExactSerialAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int totalRead = 0;
            using var timeoutToken = new CancellationTokenSource(Timeout);
            using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutToken.Token);
            while (totalRead < count)
            {
                try { int read = await Task.Run(() => Port.Read(buffer, offset + totalRead, count - totalRead), linkedToken.Token).ConfigureAwait(false); if (read == 0) return totalRead; totalRead += read; }
                catch (OperationCanceledException) { return totalRead; }
            }
            return totalRead;
        }

        private static readonly Regex _fxAddrRegex = new Regex(@"^([DMXYTSRC])(\d+)$", RegexOptions.IgnoreCase);
        private class FxAddress { public char DeviceCode; public int Address; public bool IsBitDevice; }
        private static OperateResult<FxAddress> TryParseAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return OperateResult<FxAddress>.Failed("FX 地址不能为空");

            string normalized = address.Trim().ToUpperInvariant();
            var match = _fxAddrRegex.Match(normalized);
            if (!match.Success)
                return OperateResult<FxAddress>.Failed($"无效的 FX 地址格式: {address}");

            if (!int.TryParse(match.Groups[2].Value, out int parsedAddress))
                return OperateResult<FxAddress>.Failed($"无效的 FX 地址编号: {address}");

            char deviceCode = match.Groups[1].Value[0];
            return OperateResult<FxAddress>.Success(new FxAddress
            {
                DeviceCode = deviceCode,
                Address = parsedAddress,
                IsBitDevice = IsBitDevice(deviceCode)
            });
        }

        private static bool IsBitDevice(char deviceCode)
        {
            switch (char.ToUpperInvariant(deviceCode))
            {
                case 'M':
                case 'X':
                case 'Y':
                case 'T':
                case 'S':
                case 'C':
                    return true;
                default:
                    return false;
            }
        }

        private static OperateResult<byte[]> BuildReadFrame(string address, int words)
        {
            var addr = TryParseAddress(address);
            if (!addr.IsSuccess) return OperateResult<byte[]>.Failed(addr.Message, addr.ErrorCode);
            if (addr.Content.IsBitDevice) return OperateResult<byte[]>.Failed($"FX Serial 字/字节读取暂仅支持 D/R 字设备地址: {address}");

            try
            {
                return OperateResult<byte[]>.Success(FxFrameBuilder.BuildReadCommand(addr.Content.DeviceCode, addr.Content.Address, words));
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed(ex.Message);
            }
        }

        private static OperateResult<byte[]> BuildWriteFrame(string address, byte[] data)
        {
            var addr = TryParseAddress(address);
            if (!addr.IsSuccess) return OperateResult<byte[]>.Failed(addr.Message, addr.ErrorCode);
            if (addr.Content.IsBitDevice) return OperateResult<byte[]>.Failed($"FX Serial 字/字节写入暂仅支持 D/R 字设备地址: {address}");

            try
            {
                return OperateResult<byte[]>.Success(FxFrameBuilder.BuildWriteCommand(addr.Content.DeviceCode, addr.Content.Address, data));
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed(ex.Message);
            }
        }

        public async Task<OperateResult<short>> ReadInt16Async(string address, CancellationToken ct = default)
        {
            var command = BuildReadFrame(address, 1);
            if (!command.IsSuccess) return OperateResult<short>.Failed(command.Message, command.ErrorCode);
            var result = await SendFxAsync(command.Content, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<short>.Failed(result.Message, result.ErrorCode);
            return result.Content.Length >= 2 ? OperateResult<short>.Success((short)((result.Content[1] << 8) | result.Content[0])) : OperateResult<short>.Failed("FX 读取响应数据不足");
        }
        public override OperateResult<short> ReadInt16(string address) => ReadInt16Async(address, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<OperateResult<int>> ReadInt32Async(string address, CancellationToken ct = default)
        {
            var command = BuildReadFrame(address, 2);
            if (!command.IsSuccess) return OperateResult<int>.Failed(command.Message, command.ErrorCode);
            var result = await SendFxAsync(command.Content, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<int>.Failed(result.Message, result.ErrorCode);
            return result.Content.Length >= 4 ? OperateResult<int>.Success((result.Content[3] << 24) | (result.Content[2] << 16) | (result.Content[1] << 8) | result.Content[0]) : OperateResult<int>.Failed("FX 读取响应数据不足");
        }
        public override OperateResult<int> ReadInt32(string address) => ReadInt32Async(address, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<OperateResult<float>> ReadFloatAsync(string address, CancellationToken ct = default)
        {
            var r = await ReadInt32Async(address, ct).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(r.Content), 0)) : OperateResult<float>.Failed(r.Message, r.ErrorCode);
        }
        public override OperateResult<float> ReadFloat(string address) => ReadFloatAsync(address, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<OperateResult<string>> ReadStringAsync(string address, ushort length, CancellationToken ct = default)
        {
            int words = (length + 1) / 2;
            var command = BuildReadFrame(address, words);
            if (!command.IsSuccess) return OperateResult<string>.Failed(command.Message, command.ErrorCode);
            var result = await SendFxAsync(command.Content, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<string>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length < length) return OperateResult<string>.Failed("FX 读取字符串响应数据不足");
            return OperateResult<string>.Success(Encoding.ASCII.GetString(result.Content, 0, length).TrimEnd('\0'));
        }
        public override OperateResult<string> ReadString(string address, ushort length) => ReadStringAsync(address, length, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length, CancellationToken ct = default)
        {
            int words = (length + 1) / 2;
            var command = BuildReadFrame(address, words);
            if (!command.IsSuccess) return OperateResult<byte[]>.Failed(command.Message, command.ErrorCode);
            var result = await SendFxAsync(command.Content, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length < length) return OperateResult<byte[]>.Failed("FX 读取字节响应数据不足");
            byte[] data = new byte[length];
            Buffer.BlockCopy(result.Content, 0, data, 0, length);
            return OperateResult<byte[]>.Success(data);
        }
        public override OperateResult<byte[]> ReadBytes(string address, ushort length) => ReadBytesAsync(address, length, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<OperateResult> WriteAsync(string address, short value, CancellationToken ct = default)
        {
            var command = BuildWriteFrame(address, new byte[] { (byte)(value & 0xFF), (byte)(value >> 8) });
            if (!command.IsSuccess) return OperateResult.Failed(command.Message, command.ErrorCode);
            var result = await SendFxAsync(command.Content, ct).ConfigureAwait(false);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }
        public override OperateResult Write(string address, short value) => WriteAsync(address, value, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<OperateResult> WriteAsync(string address, int value, CancellationToken ct = default)
        {
            var command = BuildWriteFrame(address, BitConverter.GetBytes(value));
            if (!command.IsSuccess) return OperateResult.Failed(command.Message, command.ErrorCode);
            var result = await SendFxAsync(command.Content, ct).ConfigureAwait(false);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }
        public override OperateResult Write(string address, int value) => WriteAsync(address, value, CancellationToken.None).GetAwaiter().GetResult();

        public override OperateResult Write(string address, float value) => Write(address, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        public async Task<OperateResult> WriteAsync(string address, string value, CancellationToken ct = default)
        {
            if (value == null) return OperateResult.Failed("写入字符串不能为空");
            byte[] data = Encoding.ASCII.GetBytes(value);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            var command = BuildWriteFrame(address, data);
            if (!command.IsSuccess) return OperateResult.Failed(command.Message, command.ErrorCode);
            var result = await SendFxAsync(command.Content, ct).ConfigureAwait(false);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }
        public override OperateResult Write(string address, string value) => WriteAsync(address, value, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<OperateResult> WriteAsync(string address, byte[] data, CancellationToken ct = default)
        {
            if (data == null) return OperateResult.Failed("写入数据不能为空");
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            var command = BuildWriteFrame(address, data);
            if (!command.IsSuccess) return OperateResult.Failed(command.Message, command.ErrorCode);
            var result = await SendFxAsync(command.Content, ct).ConfigureAwait(false);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }
        public override OperateResult Write(string address, byte[] data) => WriteAsync(address, data, CancellationToken.None).GetAwaiter().GetResult();

        // ── 补全类型读取 ──────────────────────────

        public Task<OperateResult<bool>> ReadBoolAsync(string address, CancellationToken ct = default)
        {
            var addr = TryParseAddress(address);
            if (!addr.IsSuccess) return Task.FromResult(OperateResult<bool>.Failed(addr.Message, addr.ErrorCode));

            return Task.FromResult(OperateResult<bool>.Failed("FX Serial Bool 读取需要编程口位设备地址映射验证，当前拒绝执行未验证读取"));
        }
        public override OperateResult<bool> ReadBool(string address) => ReadBoolAsync(address, CancellationToken.None).GetAwaiter().GetResult();

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public async Task<OperateResult<long>> ReadInt64Async(string address, CancellationToken ct = default)
        {
            var command = BuildReadFrame(address, 4);
            if (!command.IsSuccess) return OperateResult<long>.Failed(command.Message, command.ErrorCode);
            var result = await SendFxAsync(command.Content, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<long>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length < 8) return OperateResult<long>.Failed("FX 读取长整型响应数据不足");
            return OperateResult<long>.Success(BitConverter.ToInt64(result.Content, 0));
        }
        public override OperateResult<long> ReadInt64(string address) => ReadInt64Async(address, CancellationToken.None).GetAwaiter().GetResult();

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public async Task<OperateResult<double>> ReadDoubleAsync(string address, CancellationToken ct = default)
        {
            var r = await ReadInt64Async(address, ct).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult<double>.Success(BitConverter.ToDouble(BitConverter.GetBytes(r.Content), 0)) : OperateResult<double>.Failed(r.Message, r.ErrorCode);
        }
        public override OperateResult<double> ReadDouble(string address) => ReadDoubleAsync(address, CancellationToken.None).GetAwaiter().GetResult();

        // ── 补全类型写入 ──────────────────────────

        public Task<OperateResult> WriteAsync(string address, bool value, CancellationToken ct = default)
        {
            var addr = TryParseAddress(address);
            if (!addr.IsSuccess) return Task.FromResult(OperateResult.Failed(addr.Message, addr.ErrorCode));
            if (!addr.Content.IsBitDevice) return Task.FromResult(OperateResult.Failed($"FX Bool 写入只支持位设备地址: {address}"));

            return Task.FromResult(OperateResult.Failed("FX Serial Bool 写入需要编程口强制位命令和地址映射验证，当前拒绝执行未验证写入"));
        }
        public override OperateResult Write(string address, bool value) => WriteAsync(address, value, CancellationToken.None).GetAwaiter().GetResult();

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public async Task<OperateResult> WriteAsync(string address, long value, CancellationToken ct = default)
        {
            byte[] data = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes((int)(value & 0xFFFFFFFF)), 0, data, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((int)(value >> 32)), 0, data, 4, 4);
            var command = BuildWriteFrame(address, data);
            if (!command.IsSuccess) return OperateResult.Failed(command.Message, command.ErrorCode);
            var result = await SendFxAsync(command.Content, ct).ConfigureAwait(false);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }
        public override OperateResult Write(string address, long value) => WriteAsync(address, value, CancellationToken.None).GetAwaiter().GetResult();

        public override OperateResult Write(string address, ulong value) => Write(address, unchecked((long)value));
        public override OperateResult Write(string address, double value) => Write(address, BitConverter.ToInt64(BitConverter.GetBytes(value), 0));

        // ── 异步覆写（补全类型）──────────────────

        public override Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public override Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, ushort value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, uint value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, long value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, ulong value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, double value) => Task.Run(() => Write(address, value));

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
