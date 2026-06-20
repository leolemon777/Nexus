using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Siemens
{
    public class SiemensPpiClient : SerialDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        public byte MasterAddress { get; set; } = 1;
        public byte SlaveAddress { get; set; } = 2;
        public SiemensPpiClient(ISerialPort port, int timeout = 5000) : base(port, timeout) { }

        protected override int ResponseHeaderLength => 8;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header.Length < 8 || header[0] != 0x68 || header[3] != 0x68) return 0;
            int len = header[1];
            int remaining = len - 2;
            return remaining > 0 ? remaining : 0;
        }

        private byte[] BuildPpiFrame(byte control, byte functionCode, byte[] data)
        {
            int dataLen = data?.Length ?? 0;
            int lenField = 4 + dataLen;
            byte[] frame = new byte[4 + lenField + 2];
            frame[0] = 0x68; frame[1] = (byte)lenField; frame[2] = (byte)lenField; frame[3] = 0x68;
            frame[4] = control; frame[5] = SlaveAddress; frame[6] = MasterAddress; frame[7] = functionCode;
            if (dataLen > 0) Buffer.BlockCopy(data, 0, frame, 8, dataLen);
            byte bcc = 0;
            for (int i = 4; i < 8 + dataLen; i++) bcc ^= frame[i];
            frame[8 + dataLen] = bcc;
            frame[9 + dataLen] = 0x16;
            return frame;
        }

        private bool VerifyPpiFrame(byte[] response, out byte functionCode, out byte[] data)
        {
            functionCode = 0; data = Array.Empty<byte>();
            if (response.Length < 9 || response[0] != 0x68 || response[3] != 0x68 || response[response.Length - 1] != 0x16) return false;
            int lenField = response[1];
            if (response[2] != response[1]) return false;
            if (response.Length != 4 + lenField + 2) return false;
            byte bcc = 0;
            for (int i = 4; i < response.Length - 2; i++) bcc ^= response[i];
            if (bcc != response[response.Length - 2]) return false;
            functionCode = response[7];
            int dataLen = lenField - 4;
            if (dataLen < 0) return false;
            if (dataLen > 0) { data = new byte[dataLen]; Buffer.BlockCopy(response, 8, data, 0, dataLen); }
            return true;
        }

        protected async Task<OperateResult<byte[]>> SendPpiAsync(byte control, byte functionCode, byte[] data, CancellationToken ct)
        {
            byte[] request = BuildPpiFrame(control, functionCode, data);
            var result = await base.SendAndReceiveAsync(request, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);
            if (!VerifyPpiFrame(result.Content, out byte respFc, out byte[] respData))
                return OperateResult<byte[]>.Failed("PPI 响应帧格式或 BCC 校验失败");
            if (respFc == 0x01 || respFc == 0x03) return OperateResult<byte[]>.Failed($"PPI 设备返回错误码: 0x{respFc:X2}");
            return OperateResult<byte[]>.Success(respData);
        }

        protected OperateResult<byte[]> SendPpi(byte control, byte functionCode, byte[] data)
            => SendPpiAsync(control, functionCode, data, CancellationToken.None).GetAwaiter().GetResult();

        private static readonly Regex _ppiAddrRegex = new Regex(@"^(SM|[VMIQSC])(\d+)(?:\.(\d+))?$", RegexOptions.IgnoreCase);
        private class PpiAddress { public byte AreaCode; public int ByteAddress; public int BitOffset; public bool IsBit; }
        private static PpiAddress ParseAddress(string address)
        {
            var match = _ppiAddrRegex.Match(address.ToUpperInvariant());
            if (!match.Success) throw new ArgumentException($"无效的 PPI 地址格式: {address}");
            string area = match.Groups[1].Value;
            int byteAddr = int.Parse(match.Groups[2].Value);
            int bitOffset = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
            if (byteAddr > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(address), "PPI 字节地址不能超过 65535");
            if (match.Groups[3].Success && bitOffset > 7) throw new ArgumentOutOfRangeException(nameof(address), "PPI 位偏移必须在 0-7 之间");
            byte areaCode = area switch { "V" => 0x85, "I" => 0x81, "Q" => 0x82, "M" => 0x83, "S" => 0x84, "SM" => 0x86, "C" => 0x1C, _ => 0x85 };
            return new PpiAddress { AreaCode = areaCode, ByteAddress = byteAddr, BitOffset = bitOffset, IsBit = match.Groups[3].Success };
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = ParseAddress(address);
            if (!addr.IsBit) throw new ArgumentException("读取 Bool 需要位地址 (如 V100.0)");
            byte[] cmd = new byte[] { 0x01, 0x00, 0x01, addr.AreaCode, (byte)(addr.ByteAddress >> 8), (byte)addr.ByteAddress, (byte)addr.BitOffset };
            var result = SendPpi(0x00, 0x01, cmd);
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message, result.ErrorCode);
            return result.Content.Length > 1 && result.Content[0] == 0xFF
                ? OperateResult<bool>.Success((result.Content[1] & (1 << addr.BitOffset)) != 0)
                : OperateResult<bool>.Failed("PPI 读取位响应异常");
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = ParseAddress(address);
            byte[] cmd = new byte[] { 0x01, 0x00, 0x02, addr.AreaCode, (byte)(addr.ByteAddress >> 8), (byte)addr.ByteAddress, 0x00 };
            var result = SendPpi(0x00, 0x01, cmd);
            if (!result.IsSuccess) return OperateResult<short>.Failed(result.Message, result.ErrorCode);
            return result.Content.Length >= 3 && result.Content[0] == 0xFF
                ? OperateResult<short>.Success((short)((result.Content[1] << 8) | result.Content[2]))
                : OperateResult<short>.Failed("PPI 读取字响应异常");
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = ParseAddress(address);
            byte[] cmd = new byte[] { 0x01, 0x00, 0x04, addr.AreaCode, (byte)(addr.ByteAddress >> 8), (byte)addr.ByteAddress, 0x00 };
            var result = SendPpi(0x00, 0x01, cmd);
            if (!result.IsSuccess) return OperateResult<int>.Failed(result.Message, result.ErrorCode);
            return result.Content.Length >= 5 && result.Content[0] == 0xFF
                ? OperateResult<int>.Success((result.Content[1] << 24) | (result.Content[2] << 16) | (result.Content[3] << 8) | result.Content[4])
                : OperateResult<int>.Failed("PPI 读取双字响应异常");
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(r.Content), 0)) : OperateResult<float>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            if (length > byte.MaxValue) return OperateResult<string>.Failed("PPI 单次读取长度不能超过 255 字节");
            var addr = ParseAddress(address);
            byte[] cmd = new byte[] { 0x01, 0x00, (byte)length, addr.AreaCode, (byte)(addr.ByteAddress >> 8), (byte)addr.ByteAddress, 0x00 };
            var result = SendPpi(0x00, 0x01, cmd);
            if (!result.IsSuccess) return OperateResult<string>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length < 1 + length || result.Content[0] != 0xFF) return OperateResult<string>.Failed("PPI 读取字符串响应异常");
            byte[] data = new byte[length];
            Buffer.BlockCopy(result.Content, 1, data, 0, length);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(data).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            if (length > byte.MaxValue) return OperateResult<byte[]>.Failed("PPI 单次读取长度不能超过 255 字节");
            var addr = ParseAddress(address);
            byte[] cmd = new byte[] { 0x01, 0x00, (byte)length, addr.AreaCode, (byte)(addr.ByteAddress >> 8), (byte)addr.ByteAddress, 0x00 };
            var result = SendPpi(0x00, 0x01, cmd);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length < 1 + length || result.Content[0] != 0xFF) return OperateResult<byte[]>.Failed("PPI 读取字节响应异常");
            byte[] data = new byte[length];
            Buffer.BlockCopy(result.Content, 1, data, 0, length);
            return OperateResult<byte[]>.Success(data);
        }

        public override OperateResult Write(string address, bool value)
        {
            var addr = ParseAddress(address);
            if (!addr.IsBit) throw new ArgumentException("写入 Bool 需要位地址 (如 V100.0)");
            byte val = value ? (byte)(1 << addr.BitOffset) : (byte)0;
            byte[] cmd = new byte[] { 0x01, 0x00, 0x01, addr.AreaCode, (byte)(addr.ByteAddress >> 8), (byte)addr.ByteAddress, (byte)addr.BitOffset, val };
            var result = SendPpi(0x00, 0x02, cmd);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override OperateResult Write(string address, short value)
        {
            var addr = ParseAddress(address);
            byte[] cmd = new byte[] { 0x01, 0x00, 0x02, addr.AreaCode, (byte)(addr.ByteAddress >> 8), (byte)addr.ByteAddress, 0x00, (byte)(value >> 8), (byte)(value & 0xFF) };
            var result = SendPpi(0x00, 0x02, cmd);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override OperateResult Write(string address, int value)
        {
            var addr = ParseAddress(address);
            byte[] cmd = new byte[] { 0x01, 0x00, 0x04, addr.AreaCode, (byte)(addr.ByteAddress >> 8), (byte)addr.ByteAddress, 0x00, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)(value & 0xFF) };
            var result = SendPpi(0x00, 0x02, cmd);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override OperateResult Write(string address, float value) => Write(address, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        public override OperateResult Write(string address, string value)
        {
            if (value == null) return OperateResult.Failed("写入字符串不能为空");
            var addr = ParseAddress(address);
            byte[] data = Encoding.ASCII.GetBytes(value);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            if (data.Length > byte.MaxValue) return OperateResult.Failed("PPI 单次写入长度不能超过 255 字节");
            byte[] cmd = new byte[7 + data.Length];
            cmd[0] = 0x01; cmd[1] = 0x00; cmd[2] = (byte)data.Length; cmd[3] = addr.AreaCode;
            cmd[4] = (byte)(addr.ByteAddress >> 8); cmd[5] = (byte)addr.ByteAddress; cmd[6] = 0x00;
            Buffer.BlockCopy(data, 0, cmd, 7, data.Length);
            var result = SendPpi(0x00, 0x02, cmd);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null) return OperateResult.Failed("写入数据不能为空");
            var addr = ParseAddress(address);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            if (data.Length > byte.MaxValue) return OperateResult.Failed("PPI 单次写入长度不能超过 255 字节");
            byte[] cmd = new byte[7 + data.Length];
            cmd[0] = 0x01; cmd[1] = 0x00; cmd[2] = (byte)data.Length; cmd[3] = addr.AreaCode;
            cmd[4] = (byte)(addr.ByteAddress >> 8); cmd[5] = (byte)addr.ByteAddress; cmd[6] = 0x00;
            Buffer.BlockCopy(data, 0, cmd, 7, data.Length);
            var result = SendPpi(0x00, 0x02, cmd);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public override Task<OperateResult<short>> ReadInt16Async(string address) => Task.Run(() => ReadInt16(address));
        public override Task<OperateResult<int>> ReadInt32Async(string address) => Task.Run(() => ReadInt32(address));
        public override Task<OperateResult<float>> ReadFloatAsync(string address) => Task.Run(() => ReadFloat(address));
        public override Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.Run(() => ReadString(address, length));
        public override Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.Run(() => ReadBytes(address, length));
        public override Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, short value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

        // ── 无符号 / 大类型读取 ──────────────────

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

        public override OperateResult<long> ReadInt64(string address)
        {
            var addr = ParseAddress(address);
            byte[] cmd = new byte[] { 0x01, 0x00, 0x08, addr.AreaCode, (byte)(addr.ByteAddress >> 8), (byte)addr.ByteAddress, 0x00 };
            var result = SendPpi(0x00, 0x01, cmd);
            if (!result.IsSuccess) return OperateResult<long>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length < 9 || result.Content[0] != 0xFF)
                return OperateResult<long>.Failed("PPI 读取长整型响应异常");
            return OperateResult<long>.Success(
                ((long)result.Content[1] << 56) | ((long)result.Content[2] << 48) |
                ((long)result.Content[3] << 40) | ((long)result.Content[4] << 32) |
                ((long)result.Content[5] << 24) | ((long)result.Content[6] << 16) |
                ((long)result.Content[7] << 8)  | (long)result.Content[8]);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess
                ? OperateResult<double>.Success(BitConverter.ToDouble(BitConverter.GetBytes(r.Content), 0))
                : OperateResult<double>.Failed(r.Message, r.ErrorCode);
        }

        // ── 无符号 / 大类型写入 ──────────────────

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var addr = ParseAddress(address);
            byte[] cmd = new byte[]
            {
                0x01, 0x00, 0x08, addr.AreaCode,
                (byte)(addr.ByteAddress >> 8), (byte)addr.ByteAddress, 0x00,
                (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32),
                (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8),  (byte)(value & 0xFF)
            };
            var result = SendPpi(0x00, 0x02, cmd);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override OperateResult Write(string address, ulong value) => Write(address, unchecked((long)value));
        public override OperateResult Write(string address, double value) => Write(address, BitConverter.ToInt64(BitConverter.GetBytes(value), 0));

        // ── 异步覆写（ushort/uint/long/ulong/double）──

        public new Task<OperateResult> WriteAsync(string address, ushort value) => Task.Run(() => Write(address, value));
        public new Task<OperateResult> WriteAsync(string address, uint value) => Task.Run(() => Write(address, value));
        public new Task<OperateResult> WriteAsync(string address, long value) => Task.Run(() => Write(address, value));
        public new Task<OperateResult> WriteAsync(string address, ulong value) => Task.Run(() => Write(address, value));
        public new Task<OperateResult> WriteAsync(string address, double value) => Task.Run(() => Write(address, value));

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
