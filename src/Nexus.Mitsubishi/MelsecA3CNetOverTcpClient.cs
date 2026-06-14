using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// 三菱 A3C 计算机链接协议 over TCP 客户端 — A 系列 PLC 以太网通信。
    /// <para>适用于 A 系列 PLC 通过串口服务器/以太网适配器暴露 A3C 协议的场景。</para>
    /// <para>帧格式: ENQ(0x05) + Station(2hex) + PCNo(2hex) + Cmd(2hex) + SubCmd(2hex) + Data + SumCheck(2hex) + CR/LF</para>
    /// <para>地址格式: D100, M100, X0, Y10, S100, B1A, TS100, TC100, TN100, CS100, CC100, CN100, R100, W100</para>
    /// </summary>
    public class MelsecA3CNetOverTcpClient : TcpDeviceBase, IBatchReadWrite
    {
        // ── A3C 设备代码（ASCII 2字符）────────────
        private const string DevX = "X*";
        private const string DevY = "Y*";
        private const string DevM = "M*";
        private const string DevS = "S*";
        private const string DevB = "B*";
        private const string DevTS = "TS";
        private const string DevTC = "TC";
        private const string DevTN = "TN";
        private const string DevCS = "CS";
        private const string DevCC = "CC";
        private const string DevCN = "CN";
        private const string DevD = "D*";
        private const string DevW = "W*";
        private const string DevR = "R*";

        /// <summary>默认 TCP 端口。</summary>
        public const int DefaultPort = 5551;

        /// <summary>站号（00-1F，默认 00）。</summary>
        public byte Station { get; set; }

        /// <summary>PC 编号（默认 FF）。</summary>
        public byte PCNumber { get; set; } = 0xFF;

        // ── 构造 ────────────────────────────────

        public MelsecA3CNetOverTcpClient(string ip, int port = DefaultPort, byte station = 0, int timeout = 5000)
            : base(ip, port, timeout)
        {
            Station = station;
        }

        // ── TcpDeviceBase 抽象实现 ───────────────

        protected override int ResponseHeaderLength => 1;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ═══════════════════════════════════════════
        //  A3C 帧收发（TCP 通道）
        // ═══════════════════════════════════════════

        private readonly object _a3cLock = new object();

        private OperateResult<string> SendReceiveA3C(string cmdData)
        {
            lock (_a3cLock)
            {
                if (!IsConnected)
                {
                    var conn = Connect();
                    if (!conn.IsSuccess) return OperateResult<string>.Failed(conn.Message);
                }

                try
                {
                    // 构建帧: ENQ + Station(2hex) + PCNo(2hex) + CmdData + SumCheck(2hex)
                    string body = Station.ToString("X2") + PCNumber.ToString("X2") + cmdData;
                    byte sum = ComputeSum(Encoding.ASCII.GetBytes(body));
                    string frame = "\x05" + body + sum.ToString("X2");

                    byte[] frameBytes = Encoding.ASCII.GetBytes(frame);
                    _stream!.Write(frameBytes, 0, frameBytes.Length);

                    // 读取响应
                    int b = ReadByteWithTimeout();
                    if (b < 0) return OperateResult<string>.Failed("读取 A3C 响应超时");

                    if (b == 0x15) // NAK
                    {
                        byte[] errBuf = new byte[2];
                        if (ReadExact(errBuf, 2) < 2)
                            return OperateResult<string>.Failed("NAK 错误码读取超时");
                        return OperateResult<string>.Failed($"A3C NAK 错误: {Encoding.ASCII.GetString(errBuf)}");
                    }

                    if (b == 0x02) // STX — 读响应带数据
                    {
                        using var ms = new System.IO.MemoryStream();
                        while (true)
                        {
                            int c = ReadByteWithTimeout();
                            if (c < 0) return OperateResult<string>.Failed("读取 A3C 数据超时");
                            if (c == 0x03) // ETX
                            {
                                byte[] sumBuf = new byte[2];
                                if (ReadExact(sumBuf, 2) < 2)
                                    return OperateResult<string>.Failed("A3C Sum check 读取超时");

                                // 校验 SUM
                                byte[] checkData = new byte[ms.Length + 1];
                                ms.Position = 0;
                                ms.Read(checkData, 0, (int)ms.Length);
                                checkData[checkData.Length - 1] = 0x03;
                                byte expected = ComputeSum(checkData);
                                string actual = Encoding.ASCII.GetString(sumBuf);
                                if (!expected.ToString("X2").Equals(actual, StringComparison.OrdinalIgnoreCase))
                                    return OperateResult<string>.Failed($"A3C Sum check 校验失败: 期望 {expected:X2}, 实际 {actual}");

                                break;
                            }
                            ms.WriteByte((byte)c);
                        }

                        string responseData = Encoding.ASCII.GetString(ms.ToArray());
                        return OperateResult<string>.Success(responseData);
                    }

                    if (b == 0x06) // ACK — 写入成功
                    {
                        return OperateResult<string>.Success("");
                    }

                    return OperateResult<string>.Failed($"未知 A3C 响应: 0x{b:X2}");
                }
                catch (Exception ex)
                {
                    return OperateResult<string>.Failed($"A3C TCP 通讯异常: {ex.Message}");
                }
            }
        }

        private int ReadByteWithTimeout()
        {
            int start = Environment.TickCount;
            while (unchecked(Environment.TickCount - start) <= Timeout)
            {
                try
                {
                    int b = _stream!.ReadByte();
                    if (b >= 0) return b;
                }
                catch (TimeoutException) { return -1; }
            }
            return -1;
        }

        private int ReadExact(byte[] buffer, int count)
        {
            int offset = 0;
            int start = Environment.TickCount;
            while (offset < count && unchecked(Environment.TickCount - start) <= Timeout)
            {
                try
                {
                    int n = _stream!.Read(buffer, offset, count - offset);
                    if (n <= 0) return offset;
                    offset += n;
                }
                catch (TimeoutException) { return offset; }
            }
            return offset;
        }

        /// <summary>A3C Sum Check: 字节累加取低8位。</summary>
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
            public string DeviceCode;
            public string AddressHex;
            public bool IsBit;
        }

        private static ParsedAddress ParseAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空");

            address = address.Trim().ToUpperInvariant();
            char c0 = address[0];

            if (address.Length >= 2)
            {
                char c1 = address[1];
                if (c0 == 'T')
                {
                    if (c1 == 'S') return new ParsedAddress { DeviceCode = DevTS, AddressHex = ParseDec(address.Substring(2)), IsBit = true };
                    if (c1 == 'C') return new ParsedAddress { DeviceCode = DevTC, AddressHex = ParseDec(address.Substring(2)), IsBit = true };
                    if (c1 == 'N') return new ParsedAddress { DeviceCode = DevTN, AddressHex = ParseDec(address.Substring(2)), IsBit = false };
                }
                if (c0 == 'C')
                {
                    if (c1 == 'S') return new ParsedAddress { DeviceCode = DevCS, AddressHex = ParseDec(address.Substring(2)), IsBit = true };
                    if (c1 == 'C') return new ParsedAddress { DeviceCode = DevCC, AddressHex = ParseDec(address.Substring(2)), IsBit = true };
                    if (c1 == 'N') return new ParsedAddress { DeviceCode = DevCN, AddressHex = ParseDec(address.Substring(2)), IsBit = false };
                }
            }

            string rest = address.Substring(1);
            switch (c0)
            {
                case 'X': return new ParsedAddress { DeviceCode = DevX, AddressHex = ParseHex(rest), IsBit = true };
                case 'Y': return new ParsedAddress { DeviceCode = DevY, AddressHex = ParseHex(rest), IsBit = true };
                case 'M': return new ParsedAddress { DeviceCode = DevM, AddressHex = ParseDec(rest), IsBit = true };
                case 'S': return new ParsedAddress { DeviceCode = DevS, AddressHex = ParseDec(rest), IsBit = true };
                case 'B': return new ParsedAddress { DeviceCode = DevB, AddressHex = ParseHex(rest), IsBit = true };
                case 'D': return new ParsedAddress { DeviceCode = DevD, AddressHex = ParseDec(rest), IsBit = false };
                case 'W': return new ParsedAddress { DeviceCode = DevW, AddressHex = ParseHex(rest), IsBit = false };
                case 'R': return new ParsedAddress { DeviceCode = DevR, AddressHex = ParseDec(rest), IsBit = false };
                default: throw new ArgumentException($"不支持的地址类型: {address}");
            }
        }

        private static string ParseDec(string s) => int.Parse(s).ToString("D4");
        private static string ParseHex(string s) => Convert.ToInt32(s, 16).ToString("X4");

        // ═══════════════════════════════════════════
        //  标准类型读取
        // ═══════════════════════════════════════════

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = ParseAddress(address);
            string cmd = "00" + addr.DeviceCode + addr.AddressHex + "0001";
            var r = SendReceiveA3C(cmd);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            byte[] raw = HexToBytes(r.Content.Trim());
            return raw.Length >= 2
                ? OperateResult<short>.Success((short)((raw[1] << 8) | raw[0]))
                : OperateResult<short>.Failed("A3C 读取响应数据不足");
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = ParseAddress(address);
            string cmd = "00" + addr.DeviceCode + addr.AddressHex + "0002";
            var r = SendReceiveA3C(cmd);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            byte[] raw = HexToBytes(r.Content.Trim());
            return raw.Length >= 4
                ? OperateResult<int>.Success((raw[3] << 24) | (raw[2] << 16) | (raw[1] << 8) | raw[0])
                : OperateResult<int>.Failed("A3C 读取响应数据不足");
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var addr = ParseAddress(address);
            string cmd = "00" + addr.DeviceCode + addr.AddressHex + "0004";
            var r = SendReceiveA3C(cmd);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            byte[] raw = HexToBytes(r.Content.Trim());
            if (raw.Length < 8) return OperateResult<long>.Failed("A3C 读取长整型响应数据不足");
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
            string cmd = "00" + addr.DeviceCode + addr.AddressHex + "0001";
            var r = SendReceiveA3C(cmd);
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
            string cmd = "00" + addr.DeviceCode + addr.AddressHex + words.ToString("D4");
            var r = SendReceiveA3C(cmd);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] raw = HexToBytes(r.Content.Trim());
            if (raw.Length < length)
                return OperateResult<byte[]>.Failed($"A3C 读取字节响应数据不足: 期望 {length}, 实际 {raw.Length}");
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
            string cmd = "01" + addr.DeviceCode + addr.AddressHex + "0001" + dataHex;
            var r = SendReceiveA3C(cmd);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, short value)
        {
            var addr = ParseAddress(address);
            string dataHex = unchecked((ushort)value).ToString("X4");
            string cmd = "01" + addr.DeviceCode + addr.AddressHex + "0001" + dataHex;
            var r = SendReceiveA3C(cmd);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var addr = ParseAddress(address);
            string dataHex = unchecked((uint)value).ToString("X8");
            string cmd = "01" + addr.DeviceCode + addr.AddressHex + "0002" + dataHex;
            var r = SendReceiveA3C(cmd);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var addr = ParseAddress(address);
            string dataHex = unchecked((ulong)value).ToString("X16");
            string cmd = "01" + addr.DeviceCode + addr.AddressHex + "0004" + dataHex;
            var r = SendReceiveA3C(cmd);
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
            string cmd = "01" + addr.DeviceCode + addr.AddressHex + words.ToString("D4") + dataHex;
            var r = SendReceiveA3C(cmd);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null) return OperateResult.Failed("写入数据不能为空");
            var addr = ParseAddress(address);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            int words = data.Length / 2;
            string dataHex = BytesToHex(data);
            string cmd = "01" + addr.DeviceCode + addr.AddressHex + words.ToString("D4") + dataHex;
            var r = SendReceiveA3C(cmd);
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

        protected override byte[]? BuildHeartbeat()
        {
            try
            {
                var addr = ParseAddress("D0");
                string cmd = "00" + addr.DeviceCode + addr.AddressHex + "0001";
                string body = Station.ToString("X2") + PCNumber.ToString("X2") + cmd;
                byte sum = ComputeSum(Encoding.ASCII.GetBytes(body));
                string frame = "\x05" + body + sum.ToString("X2");
                return Encoding.ASCII.GetBytes(frame);
            }
            catch { return null; }
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

        public override string ToString() => $"MelsecA3CNetOverTcpClient[{Ip}:{Port}, Station={Station:D2}]";
    }
}
