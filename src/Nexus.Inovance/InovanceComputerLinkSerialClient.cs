using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Inovance
{
    /// <summary>
    /// 汇川 ComputerLink 协议串口客户端 — 基于三菱 FX 计算机链接协议。
    /// <para>帧格式: ENQ(0x05) + Station(2位十进制) + CmdAndData + SumCheck(2位十六进制)</para>
    /// <para>命令: 0 = 读, 1 = 写</para>
    /// <para>注意: 本客户端继承 SerialDeviceBase，使用 ISerialPort 抽象串口操作。</para>
    /// </summary>
    public class InovanceComputerLinkSerialClient : SerialDeviceBase, IBatchReadWrite
    {
        /// <summary>站号。</summary>
        public byte Station { get; set; }

        /// <summary>是否启用 Sum Check 校验。</summary>
        public bool SumCheckEnabled { get; set; } = true;

        /// <summary>
        /// 创建汇川 ComputerLink 串口客户端。
        /// </summary>
        /// <param name="serialPort">串口抽象接口。</param>
        /// <param name="station">站号（默认 0）。</param>
        /// <param name="timeout">超时（毫秒，默认 5000）。</param>
        public InovanceComputerLinkSerialClient(ISerialPort serialPort, byte station = 0, int timeout = 5000)
            : base(serialPort, timeout)
        {
            Station = station;
        }

        // ── SerialDeviceBase 抽象成员实现 ──────────

        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ── ComputerLink 协议帧收发 ───────────────

        private OperateResult<string> SendReceive(string cmdAndData)
        {
            try
            {
                lock (_lock)
                {
                    if (!Port.IsOpen)
                        return OperateResult<string>.Failed("串口未打开");

                    string body = Station.ToString("D2") + cmdAndData;
                    byte sum = ComputeSum(Encoding.ASCII.GetBytes(body));
                    string frame = "\x05" + body + sum.ToString("X2");

                    Log.Debug($"ComputerLink TX → {frame.Replace("\x05", "[ENQ]")}");
                    RaiseMessageSent(frame);

                    byte[] frameBytes = Encoding.ASCII.GetBytes(frame);
                    Port.Write(frameBytes, 0, frameBytes.Length);

                    if (InterFrameDelay > 0)
                        Thread.Sleep(InterFrameDelay);

                    int b = ReadByteWithTimeout();
                    if (b < 0) return OperateResult<string>.Failed("读取响应超时");

                    if (b == 0x06)
                    {
                        RaiseMessageReceived("ACK");
                        return OperateResult<string>.Success("");
                    }

                    if (b == 0x15)
                    {
                        byte[] errBytes = new byte[2];
                        if (!ReadExact2(errBytes, Timeout))
                            return OperateResult<string>.Failed("NAK 错误码读取超时");
                        string errCode = Encoding.ASCII.GetString(errBytes);
                        RaiseError($"NAK: {errCode}");
                        return OperateResult<string>.Failed($"ComputerLink NAK 错误: {errCode}");
                    }

                    if (b == 0x02)
                    {
                        using var ms = new MemoryStream();
                        byte[]? sumBuf = null;
                        while (true)
                        {
                            int c = ReadByteWithTimeout();
                            if (c < 0) return OperateResult<string>.Failed("读取数据超时");
                            if (c == 0x03)
                            {
                                sumBuf = new byte[2];
                                if (!ReadExact2(sumBuf, Timeout))
                                    return OperateResult<string>.Failed("Sum check 读取超时");
                                break;
                            }
                            ms.WriteByte((byte)c);
                        }
                        byte[] responseBytes = ms.ToArray();
                        if (SumCheckEnabled)
                        {
                            byte[] checkBytes = new byte[responseBytes.Length + 1];
                            Buffer.BlockCopy(responseBytes, 0, checkBytes, 0, responseBytes.Length);
                            checkBytes[checkBytes.Length - 1] = 0x03;
                            string expected = ComputeSum(checkBytes).ToString("X2");
                            string actual = Encoding.ASCII.GetString(sumBuf!);
                            if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
                                return OperateResult<string>.Failed($"Sum check 校验失败: 期望 {expected}, 实际 {actual}");
                        }

                        string responseData = Encoding.ASCII.GetString(responseBytes);
                        RaiseMessageReceived($"Data [{responseData.Length}]");
                        return OperateResult<string>.Success(responseData);
                    }

                    return OperateResult<string>.Failed($"未知响应: 0x{b:X2}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"ComputerLink 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                return OperateResult<string>.Failed($"通讯异常: {ex.Message}");
            }
        }

        private int ReadByteWithTimeout()
        {
            int start = Environment.TickCount;
            byte[] buf = new byte[1];
            while (unchecked(Environment.TickCount - start) <= Timeout)
            {
                try
                {
                    int read = Port.Read(buf, 0, 1);
                    if (read > 0) return buf[0];
                }
                catch (TimeoutException) { return -1; }
            }
            return -1;
        }

        private bool ReadExact2(byte[] buf, int remainingMs)
        {
            int start = Environment.TickCount;
            int offset = 0;
            while (offset < buf.Length && unchecked(Environment.TickCount - start) <= remainingMs)
            {
                try
                {
                    int n = Port.Read(buf, offset, buf.Length - offset);
                    if (n <= 0) return false;
                    offset += n;
                }
                catch (TimeoutException) { return false; }
            }
            return offset >= buf.Length;
        }

        private static byte ComputeSum(byte[] data)
        {
            byte sum = 0;
            foreach (byte b in data) sum += b;
            return sum;
        }

        // ── 地址编码 ──────────────────────────────

        private static (char deviceCode, string addressHex, bool isBit) ParseAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("地址不能为空");
            address = address.Trim().ToUpperInvariant();

            char prefix = address[0];
            string numPart = address.Substring(1);
            int num = int.Parse(numPart);

            return prefix switch
            {
                'D' => ('D', num.ToString("X4"), false),
                'M' => ('M', num.ToString("X4"), true),
                'Y' => ('Y', (num / 8).ToString("X2"), true),
                'X' => ('X', (num / 8).ToString("X2"), true),
                'T' => ('T', num.ToString("X4"), true),
                'C' => ('C', num.ToString("X4"), true),
                'S' => ('S', num.ToString("X4"), true),
                'R' => ('R', num.ToString("X4"), false),
                'Z' => ('Z', num.ToString("X2"), false),
                'V' => ('V', num.ToString("X2"), false),
                _ => ('D', num.ToString("X4"), false),
            };
        }

        // ── 读写命令构建 ──────────────────────────

        private OperateResult<string> ReadWords(char device, string addrHex, int count)
        {
            string cmd = "0" + device + addrHex + count.ToString("X2");
            return SendReceive(cmd);
        }

        private OperateResult<string> ReadBits(char device, string addrHex, int count)
        {
            string cmd = "0" + device + addrHex + count.ToString("X2");
            return SendReceive(cmd);
        }

        private OperateResult WriteWords(char device, string addrHex, string hexData)
        {
            int count = hexData.Length / 4;
            string cmd = "1" + device + addrHex + count.ToString("X2") + hexData;
            var r = SendReceive(cmd);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        private OperateResult WriteBits(char device, string addrHex, string bitData)
        {
            int count = bitData.Length;
            string cmd = "1" + device + addrHex + count.ToString("X2") + bitData;
            var r = SendReceive(cmd);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        // ── IReadWriteDevice 实现 ──────────────────

        public override OperateResult<bool> ReadBool(string address)
        {
            var (device, addrHex, isBit) = ParseAddress(address);
            var r = ReadBits(device, addrHex, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Trim() == "1");
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var (device, addrHex, _) = ParseAddress(address);
            var r = ReadWords(device, addrHex, 1);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(unchecked((short)Convert.ToUInt16(r.Content.Trim(), 16)));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success(unchecked((ushort)r.Content)) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var (device, addrHex, _) = ParseAddress(address);
            var r = ReadWords(device, addrHex, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success(unchecked((int)Convert.ToUInt32(r.Content.Trim(), 16)));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success(unchecked((uint)r.Content)) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadUInt64(address);
            return r.IsSuccess ? OperateResult<long>.Success(unchecked((long)r.Content)) : OperateResult<long>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var (device, addrHex, _) = ParseAddress(address);
            var r = ReadWords(device, addrHex, 4);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ulong>.Success(Convert.ToUInt64(r.Content.Trim(), 16));
        }

        public override unsafe OperateResult<float> ReadFloat(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            int v = r.Content;
            return OperateResult<float>.Success(*(float*)&v);
        }

        public override unsafe OperateResult<double> ReadDouble(string address)
        {
            var r = ReadUInt64(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            ulong v = r.Content;
            return OperateResult<double>.Success(*(double*)&v);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var (d, a, _) = ParseAddress(address);
            int cnt = (length + 1) / 2;
            var r = ReadWords(d, a, cnt);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] raw = HexToBytes(r.Content);
            if (raw.Length < length)
                return OperateResult<byte[]>.Failed($"响应数据不足: 期望 {length} 字节, 实际 {raw.Length} 字节");
            byte[] result = new byte[length];
            Array.Copy(raw, result, length);
            return OperateResult<byte[]>.Success(result);
        }

        public override OperateResult Write(string address, bool value)
        {
            var (d, a, _) = ParseAddress(address);
            return WriteBits(d, a, value ? "1" : "0");
        }

        public override OperateResult Write(string address, short value)
        {
            var (d, a, _) = ParseAddress(address);
            return WriteWords(d, a, unchecked((ushort)value).ToString("X4"));
        }

        public override OperateResult Write(string address, ushort value) => Write(address, unchecked((short)value));

        public override OperateResult Write(string address, int value)
        {
            var (d, a, _) = ParseAddress(address);
            return WriteWords(d, a, unchecked((uint)value).ToString("X8"));
        }

        public override OperateResult Write(string address, uint value) => Write(address, unchecked((int)value));
        public override OperateResult Write(string address, long value) => Write(address, unchecked((ulong)value));

        public override OperateResult Write(string address, ulong value)
        {
            var (d, a, _) = ParseAddress(address);
            return WriteWords(d, a, value.ToString("X16"));
        }

        public override unsafe OperateResult Write(string address, float value) => Write(address, *(int*)&value);

        public override unsafe OperateResult Write(string address, double value)
        {
            ulong bits = *(ulong*)&value;
            return Write(address, bits);
        }

        public override OperateResult Write(string address, string value)
        {
            var (d, a, _) = ParseAddress(address);
            byte[] bytes = Encoding.ASCII.GetBytes(value ?? "");
            if (bytes.Length % 2 != 0) Array.Resize(ref bytes, bytes.Length + 1);
            return WriteWords(d, a, BytesToHex(bytes));
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null) return OperateResult.Failed("写入数据不能为空");
            var (d, a, _) = ParseAddress(address);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteWords(d, a, BytesToHex(data));
        }

        // ── 工具方法 ──────────────────────────────

        private static byte[] HexToBytes(string hex)
        {
            hex = hex.Trim();
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

        // ── IBatchReadWrite 实现 ──────────────────

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

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

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

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(RandomRead(addresses));

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

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));
    }
}
