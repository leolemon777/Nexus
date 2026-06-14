using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// 三菱 FX Links 计算机链接协议客户端 — FX 系列 PLC (FX0/FX0N/FX1/FX2/FX2N/FX3U) RS-485 多站通信。
    /// <para>帧格式: ENQ(0x05) + Station(2hex) + PC(2hex) + Command(2char) + SubCommand(2char) + Data + SumCheck(2hex)</para>
    /// <para>响应: STX(0x02) + Station(2hex) + PC(2hex) + Command(2char) + SubCommand(2char) + Data + ETX(0x03) + SumCheck(2hex)</para>
    /// <para>命令: BR=读字, BW=写字, WR=写字(备), RR=远程运行, RS=远程停止</para>
    /// <para>地址格式: D100, M100, X0, Y10, T100, C100, S100, R100, V0, Z0</para>
    /// </summary>
    public class MelsecFxLinksClient : SerialDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        private readonly object _fxlLock = new object();

        private const string CmdBatchRead = "BR";
        private const string CmdBatchWrite = "BW";
        private const string SubCmd00 = "00";

        /// <summary>站号（00-1F，默认 00）。</summary>
        public byte Station { get; set; }

        /// <summary>PC 编号（默认 FF）。</summary>
        public byte PCNumber { get; set; } = 0xFF;

        public MelsecFxLinksClient(ISerialPort port, byte station = 0, int timeout = 5000)
            : base(port, timeout)
        {
            Station = station;
        }

        protected override int ResponseHeaderLength => 1;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ═══════════════════════════════════════════
        //  FX Links 帧收发
        // ═══════════════════════════════════════════

        private OperateResult<string> SendReceiveFxLinks(string cmdData)
        {
            try
            {
                lock (_fxlLock)
                {
                    if (!Port.IsOpen) return OperateResult<string>.Failed("串口未打开");

                    string body = Station.ToString("X2") + PCNumber.ToString("X2") + cmdData;
                    byte sum = ComputeSum(Encoding.ASCII.GetBytes(body));
                    string frame = "\x05" + body + sum.ToString("X2");

                    byte[] frameBytes = Encoding.ASCII.GetBytes(frame);
                    Port.Write(frameBytes, 0, frameBytes.Length);

                    int b = ReadByteWithTimeout();
                    if (b < 0) return OperateResult<string>.Failed("读取 FX Links 响应超时");

                    if (b == 0x15)
                    {
                        byte[] errBuf = new byte[2];
                        if (ReadExact(errBuf, 2) < 2)
                            return OperateResult<string>.Failed("NAK 错误码读取超时");
                        return OperateResult<string>.Failed($"FX Links NAK 错误: {Encoding.ASCII.GetString(errBuf)}");
                    }

                    if (b == 0x02)
                    {
                        using var ms = new System.IO.MemoryStream();
                        while (true)
                        {
                            int c = ReadByteWithTimeout();
                            if (c < 0) return OperateResult<string>.Failed("读取 FX Links 数据超时");
                            if (c == 0x03)
                            {
                                byte[] sumBuf = new byte[2];
                                if (ReadExact(sumBuf, 2) < 2)
                                    return OperateResult<string>.Failed("FX Links Sum check 读取超时");

                                byte[] checkData = new byte[ms.Length + 1];
                                ms.Position = 0;
                                ms.Read(checkData, 0, (int)ms.Length);
                                checkData[checkData.Length - 1] = 0x03;
                                byte expected = ComputeSum(checkData);
                                string actual = Encoding.ASCII.GetString(sumBuf);
                                if (!expected.ToString("X2").Equals(actual, StringComparison.OrdinalIgnoreCase))
                                    return OperateResult<string>.Failed($"FX Links Sum check 校验失败: 期望 {expected:X2}, 实际 {actual}");
                                break;
                            }
                            ms.WriteByte((byte)c);
                        }

                        string responseData = Encoding.ASCII.GetString(ms.ToArray());
                        return OperateResult<string>.Success(responseData);
                    }

                    if (b == 0x06)
                        return OperateResult<string>.Success("");

                    return OperateResult<string>.Failed($"未知 FX Links 响应: 0x{b:X2}");
                }
            }
            catch (Exception ex)
            {
                return OperateResult<string>.Failed($"FX Links 通讯异常: {ex.Message}");
            }
        }

        private int ReadByteWithTimeout()
        {
            int start = Environment.TickCount;
            while (unchecked(Environment.TickCount - start) <= Timeout)
            {
                try
                {
                    byte[] buf = new byte[1];
                    int n = Port.Read(buf, 0, 1);
                    if (n > 0) return buf[0];
                }
                catch (TimeoutException) { return -1; }
            }
            return -1;
        }

        private int ReadExact(byte[] buffer, int count)
        {
            int offset = 0;
            int start2 = Environment.TickCount;
            while (offset < count && unchecked(Environment.TickCount - start2) <= Timeout)
            {
                try
                {
                    int n = Port.Read(buffer, offset, count - offset);
                    if (n <= 0) return offset;
                    offset += n;
                }
                catch (TimeoutException) { return offset; }
            }
            return offset;
        }

        private static byte ComputeSum(byte[] data)
        {
            byte sum = 0;
            foreach (byte b in data) sum += b;
            return sum;
        }

        // ═══════════════════════════════════════════
        //  地址解析
        // ═══════════════════════════════════════════

        private struct ParsedAddress
        {
            public char DeviceCode;
            public string AddressHex;
            public bool IsBit;
        }

        private static ParsedAddress ParseAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空");

            address = address.Trim().ToUpperInvariant();
            char prefix = address[0];
            string numPart = address.Substring(1);
            int num = int.Parse(numPart);

            return prefix switch
            {
                'D' => new ParsedAddress { DeviceCode = 'D', AddressHex = num.ToString("X4"), IsBit = false },
                'M' => new ParsedAddress { DeviceCode = 'M', AddressHex = num.ToString("X4"), IsBit = true },
                'X' => new ParsedAddress { DeviceCode = 'X', AddressHex = (num / 8).ToString("X2"), IsBit = true },
                'Y' => new ParsedAddress { DeviceCode = 'Y', AddressHex = (num / 8).ToString("X2"), IsBit = true },
                'T' => new ParsedAddress { DeviceCode = 'T', AddressHex = num.ToString("X4"), IsBit = true },
                'C' => new ParsedAddress { DeviceCode = 'C', AddressHex = num.ToString("X4"), IsBit = true },
                'S' => new ParsedAddress { DeviceCode = 'S', AddressHex = num.ToString("X4"), IsBit = true },
                'R' => new ParsedAddress { DeviceCode = 'R', AddressHex = num.ToString("X4"), IsBit = false },
                'V' => new ParsedAddress { DeviceCode = 'V', AddressHex = num.ToString("X2"), IsBit = false },
                'Z' => new ParsedAddress { DeviceCode = 'Z', AddressHex = num.ToString("X2"), IsBit = false },
                _ => throw new ArgumentException($"不支持的地址类型: {address}")
            };
        }

        // ═══════════════════════════════════════════
        //  标准类型读取
        // ═══════════════════════════════════════════

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = ParseAddress(address);
            string cmd = CmdBatchRead + SubCmd00 + addr.DeviceCode + addr.AddressHex + "0001";
            var r = SendReceiveFxLinks(cmd);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            byte[] raw = HexToBytes(r.Content.Trim());
            return raw.Length >= 2
                ? OperateResult<short>.Success((short)((raw[1] << 8) | raw[0]))
                : OperateResult<short>.Failed("FX Links 读取响应数据不足");
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = ParseAddress(address);
            string cmd = CmdBatchRead + SubCmd00 + addr.DeviceCode + addr.AddressHex + "0002";
            var r = SendReceiveFxLinks(cmd);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            byte[] raw = HexToBytes(r.Content.Trim());
            return raw.Length >= 4
                ? OperateResult<int>.Success((raw[3] << 24) | (raw[2] << 16) | (raw[1] << 8) | raw[0])
                : OperateResult<int>.Failed("FX Links 读取响应数据不足");
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var addr = ParseAddress(address);
            string cmd = CmdBatchRead + SubCmd00 + addr.DeviceCode + addr.AddressHex + "0004";
            var r = SendReceiveFxLinks(cmd);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            byte[] raw = HexToBytes(r.Content.Trim());
            if (raw.Length < 8) return OperateResult<long>.Failed("FX Links 读取长整型响应数据不足");
            return OperateResult<long>.Success(BitConverter.ToInt64(raw, 0));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(r.Content), 0)) : OperateResult<float>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<double>.Success(BitConverter.ToDouble(BitConverter.GetBytes(r.Content), 0)) : OperateResult<double>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = ParseAddress(address);
            string cmd = CmdBatchRead + SubCmd00 + addr.DeviceCode + addr.AddressHex + "0001";
            var r = SendReceiveFxLinks(cmd);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Trim() == "01");
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = ParseAddress(address);
            int words = (length + 1) / 2;
            string cmd = CmdBatchRead + SubCmd00 + addr.DeviceCode + addr.AddressHex + words.ToString("D4");
            var r = SendReceiveFxLinks(cmd);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] raw = HexToBytes(r.Content.Trim());
            if (raw.Length < length)
                return OperateResult<byte[]>.Failed($"FX Links 读取字节响应数据不足: 期望 {length}, 实际 {raw.Length}");
            byte[] result = new byte[length];
            Buffer.BlockCopy(raw, 0, result, 0, length);
            return OperateResult<byte[]>.Success(result);
        }

        // ═══════════════════════════════════════════
        //  标准类型写入
        // ═══════════════════════════════════════════

        public override OperateResult Write(string address, bool value)
        {
            var addr = ParseAddress(address);
            string dataHex = value ? "01" : "00";
            string cmd = CmdBatchWrite + SubCmd00 + addr.DeviceCode + addr.AddressHex + "0001" + dataHex;
            var r = SendReceiveFxLinks(cmd);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, short value)
        {
            var addr = ParseAddress(address);
            string dataHex = unchecked((ushort)value).ToString("X4");
            string cmd = CmdBatchWrite + SubCmd00 + addr.DeviceCode + addr.AddressHex + "0001" + dataHex;
            var r = SendReceiveFxLinks(cmd);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var addr = ParseAddress(address);
            string dataHex = unchecked((uint)value).ToString("X8");
            string cmd = CmdBatchWrite + SubCmd00 + addr.DeviceCode + addr.AddressHex + "0002" + dataHex;
            var r = SendReceiveFxLinks(cmd);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var addr = ParseAddress(address);
            string dataHex = unchecked((ulong)value).ToString("X16");
            string cmd = CmdBatchWrite + SubCmd00 + addr.DeviceCode + addr.AddressHex + "0004" + dataHex;
            var r = SendReceiveFxLinks(cmd);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, ulong value) => Write(address, unchecked((long)value));

        public override OperateResult Write(string address, float value) => Write(address, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        public override OperateResult Write(string address, double value) => Write(address, BitConverter.ToInt64(BitConverter.GetBytes(value), 0));

        public override OperateResult Write(string address, string value)
        {
            if (value == null) return OperateResult.Failed("写入字符串不能为空");
            var addr = ParseAddress(address);
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            if (bytes.Length % 2 != 0) Array.Resize(ref bytes, bytes.Length + 1);
            int words = bytes.Length / 2;
            string dataHex = BytesToHex(bytes);
            string cmd = CmdBatchWrite + SubCmd00 + addr.DeviceCode + addr.AddressHex + words.ToString("D4") + dataHex;
            var r = SendReceiveFxLinks(cmd);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null) return OperateResult.Failed("写入数据不能为空");
            var addr = ParseAddress(address);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            int words = data.Length / 2;
            string dataHex = BytesToHex(data);
            string cmd = CmdBatchWrite + SubCmd00 + addr.DeviceCode + addr.AddressHex + words.ToString("D4") + dataHex;
            var r = SendReceiveFxLinks(cmd);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        // ═══════════════════════════════════════════
        //  IBatchReadWrite 实现
        // ═══════════════════════════════════════════

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = new List<string>(addresses);
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

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = new List<string>(addresses);
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList)
            {
                var r = ReadBytes(addr, 2);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(RandomRead(addresses));

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            foreach (var kv in items)
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

        public event EventHandler<DataChangeEventArgs>? OnDataChanged;

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

        public void Unsubscribe(string address)
        {
            lock (_monitorLock) { _monitors.Remove(address); }
        }

        public void StartSubscriptions(int globalIntervalMs = 500)
        {
            if (_monitoring) return;
            _monitoring = true;
            _monitorTimer = new Timer(PollMonitors, null, globalIntervalMs, globalIntervalMs);
        }

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

        // ═══════════════════════════════════════════
        //  工具方法
        // ═══════════════════════════════════════════

        private static byte[] HexToBytes(string hex)
        {
            hex = hex.Trim();
            if (hex.Length % 2 != 0) hex = "0" + hex;
            byte[] r = new byte[hex.Length / 2];
            for (int i = 0; i < r.Length; i++)
                r[i] = (byte)(HexVal(hex[i * 2]) << 4 | HexVal(hex[i * 2 + 1]));
            return r;
        }

        private static string BytesToHex(byte[] d)
        {
            var sb = new StringBuilder(d.Length * 2);
            foreach (byte b in d) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        private static int HexVal(char c) =>
            c >= '0' && c <= '9' ? c - '0' :
            c >= 'A' && c <= 'F' ? c - 'A' + 10 :
            c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0;

        public override string ToString() => $"MelsecFxLinksClient[Station={Station:D2}]";
    }
}
