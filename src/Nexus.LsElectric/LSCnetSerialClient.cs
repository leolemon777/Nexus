using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.LsElectric
{
    /// <summary>
    /// LS Electric Cnet 协议串口客户端 — 支持 XGB/XGI/XGR 系列。
    /// <para>帧格式 (ASCII 模式):</para>
    /// <para>请求: ENQ(1) + Station(2) + PC(2) + Command(2) + AreaCode(2) + Address(4) + Count(4) + ETX(1) + BCC(2)</para>
    /// <para>响应: STX(1) + Station(2) + PC(2) + Command(2) + Data(N) + ETX(1) + BCC(2)</para>
    /// <para>BCC = XOR(Station..ETX)</para>
    /// </summary>
    public class LSCnetSerialClient : SerialDeviceBase, IBatchReadWrite
    {
        /// <summary>站号 (0-255)。</summary>
        public byte Station { get; set; }

        /// <summary>PC 号 (默认 0xFF)。</summary>
        public byte PcNumber { get; set; } = 0xFF;

        private readonly object _cnetLock = new object();

        public LSCnetSerialClient(ISerialPort port, byte station = 0, int timeout = 5000)
            : base(port, timeout)
        {
            Station = station;
        }

        // ═══════════════════════════════════════════
        //  SerialDeviceBase 抽象成员实现
        // ═══════════════════════════════════════════

        /// <summary>
        /// Cnet 响应头固定为 1 字节: STX(1)。
        /// 后续通过动态解析确定完整帧长度。
        /// </summary>
        protected override int ResponseHeaderLength => 1;

        /// <summary>
        /// Cnet 响应需要动态扫描 ETX+BCC 来确定长度。
        /// 由于 SerialDeviceBase 要求固定长度，这里返回一个较大的值，
        /// 实际帧解析在 ReadResponse 中完成。
        /// </summary>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            // Cnet 响应长度不确定，返回 0 让 SendAndReceive 只读取 header
            // 实际收发通过自定义方法完成
            return 0;
        }

        // ═══════════════════════════════════════════
        //  Cnet 帧构建
        // ═══════════════════════════════════════════

        /// <summary>计算 BCC（XOR 校验）。</summary>
        private static byte ComputeBcc(byte[] data, int offset, int length)
        {
            byte bcc = 0;
            for (int i = offset; i < offset + length; i++)
                bcc ^= data[i];
            return bcc;
        }

        /// <summary>将字节转换为 2 字符 ASCII 十六进制。</summary>
        private static void AppendHexByte(byte value, byte[] dest, int offset)
        {
            dest[offset] = (byte)ToHexChar((value >> 4) & 0x0F);
            dest[offset + 1] = (byte)ToHexChar(value & 0x0F);
        }

        /// <summary>将 2 字符 ASCII 十六进制转换为字节。</summary>
        private static byte ParseHexByte(byte[] src, int offset)
        {
            return (byte)((FromHexChar(src[offset]) << 4) | FromHexChar(src[offset + 1]));
        }

        private static char ToHexChar(int value)
        {
            return (char)(value < 10 ? '0' + value : 'A' + value - 10);
        }

        private static int FromHexChar(byte c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            return 0;
        }

        /// <summary>
        /// 构建 Cnet 读取请求帧。
        /// <para>ENQ + Station(2) + PC(2) + "RD" + Area(2) + Address(4) + Count(4) + ETX + BCC(2)</para>
        /// </summary>
        private byte[] BuildReadRequest(LSCnetAddress address, ushort count)
        {
            // ENQ(1) + Station(2) + PC(2) + Cmd(2) + Area(2) + Addr(4) + Count(4) + ETX(1) + BCC(2) = 20
            byte[] frame = new byte[20];
            int i = 0;

            frame[i++] = LSCnetConstants.ENQ;
            AppendHexByte(Station, frame, i); i += 2;
            AppendHexByte(PcNumber, frame, i); i += 2;
            frame[i++] = (byte)'R'; frame[i++] = (byte)'D';
            AppendHexByte(address.AreaCode, frame, i); i += 2;
            // 地址 4 字符 ASCII 十六进制
            frame[i++] = (byte)ToHexChar((address.Offset >> 12) & 0x0F);
            frame[i++] = (byte)ToHexChar((address.Offset >> 8) & 0x0F);
            frame[i++] = (byte)ToHexChar((address.Offset >> 4) & 0x0F);
            frame[i++] = (byte)ToHexChar(address.Offset & 0x0F);
            // 数量 4 字符 ASCII 十六进制
            frame[i++] = (byte)ToHexChar((count >> 12) & 0x0F);
            frame[i++] = (byte)ToHexChar((count >> 8) & 0x0F);
            frame[i++] = (byte)ToHexChar((count >> 4) & 0x0F);
            frame[i++] = (byte)ToHexChar(count & 0x0F);
            frame[i++] = LSCnetConstants.ETX;

            // BCC = XOR(Station..ETX)
            byte bcc = ComputeBcc(frame, 1, 18); // from index 1 (Station) to 18 (ETX)
            AppendHexByte(bcc, frame, i);

            return frame;
        }

        /// <summary>
        /// 构建 Cnet 写入请求帧。
        /// <para>ENQ + Station(2) + PC(2) + "WR" + Area(2) + Address(4) + Count(4) + Data(N) + ETX + BCC(2)</para>
        /// </summary>
        private byte[] BuildWriteRequest(LSCnetAddress address, ushort count, byte[] data)
        {
            // ENQ(1) + Station(2) + PC(2) + Cmd(2) + Area(2) + Addr(4) + Count(4) + Data(N*4) + ETX(1) + BCC(2)
            int dataHexLen = data.Length * 4; // 每字节 4 字符 (word 格式)
            int frameLen = 17 + dataHexLen + 3; // 17 = ENQ+Station+PC+Cmd+Area+Addr+Count, 3 = ETX+BCC(2)
            byte[] frame = new byte[frameLen];
            int i = 0;

            frame[i++] = LSCnetConstants.ENQ;
            AppendHexByte(Station, frame, i); i += 2;
            AppendHexByte(PcNumber, frame, i); i += 2;
            frame[i++] = (byte)'W'; frame[i++] = (byte)'R';
            AppendHexByte(address.AreaCode, frame, i); i += 2;
            frame[i++] = (byte)ToHexChar((address.Offset >> 12) & 0x0F);
            frame[i++] = (byte)ToHexChar((address.Offset >> 8) & 0x0F);
            frame[i++] = (byte)ToHexChar((address.Offset >> 4) & 0x0F);
            frame[i++] = (byte)ToHexChar(address.Offset & 0x0F);
            frame[i++] = (byte)ToHexChar((count >> 12) & 0x0F);
            frame[i++] = (byte)ToHexChar((count >> 8) & 0x0F);
            frame[i++] = (byte)ToHexChar((count >> 4) & 0x0F);
            frame[i++] = (byte)ToHexChar(count & 0x0F);

            // 数据: 每字节 4 字符 (高字节在前)
            for (int d = 0; d < data.Length; d++)
            {
                AppendHexByte(data[d], frame, i);
                i += 2;
                frame[i++] = (byte)'0'; frame[i++] = (byte)'0';
            }

            frame[i++] = LSCnetConstants.ETX;

            // BCC = XOR(Station..ETX)
            byte bcc = ComputeBcc(frame, 1, frameLen - 3);
            AppendHexByte(bcc, frame, i);

            return frame;
        }

        // ═══════════════════════════════════════════
        //  Cnet 帧收发
        // ═══════════════════════════════════════════

        /// <summary>
        /// 发送 Cnet 请求并接收响应。由于帧长度不确定，使用自定义读取逻辑。
        /// </summary>
        private OperateResult<byte[]> SendCnetRequest(byte[] request)
        {
            try
            {
                lock (_cnetLock)
                {
                    if (!Port.IsOpen) return OperateResult<byte[]>.Failed("串口未打开");

                    Log.Debug($"CNET TX → {DataConverter.ToHexString(request)}");
                    RaiseMessageSent(DataConverter.ToHexString(request));

                    Port.Write(request, 0, request.Length);

                    if (InterFrameDelay > 0)
                        Thread.Sleep(InterFrameDelay);

                    // 读取响应: STX(1) + Station(2) + PC(2) + Cmd(2) + Data(N) + ETX(1) + BCC(2)
                    byte[] stx = new byte[1];
                    int read = ReadExactSerial(stx, 0, 1);
                    if (read < 1) return OperateResult<byte[]>.Failed("读取 STX 超时");

                    if (stx[0] == LSCnetConstants.NAK)
                        return OperateResult<byte[]>.Failed("Cnet NAK 响应");

                    if (stx[0] != LSCnetConstants.STX)
                        return OperateResult<byte[]>.Failed($"Cnet 响应异常: 0x{stx[0]:X2}");

                    // 读取 Station(2) + PC(2) + Cmd(2) = 6 字节
                    byte[] header = new byte[6];
                    read = ReadExactSerial(header, 0, 6);
                    if (read < 6) return OperateResult<byte[]>.Failed("读取响应头失败");

                    // 读取数据直到 ETX
                    var dataBytes = new List<byte>();
                    int deadline = Environment.TickCount + Timeout;
                    while (Environment.TickCount <= deadline)
                    {
                        byte[] b = new byte[1];
                        int n = Port.Read(b, 0, 1);
                        if (n <= 0) break;
                        if (b[0] == LSCnetConstants.ETX)
                        {
                            // 读取 BCC(2)
                            byte[] bccBytes = new byte[2];
                            read = ReadExactSerial(bccBytes, 0, 2);
                            if (read < 2) return OperateResult<byte[]>.Failed("读取 BCC 失败");

                            // 验证 BCC
                            int checkLen = 6 + dataBytes.Count + 1;
                            byte[] checkData = new byte[checkLen];
                            Buffer.BlockCopy(header, 0, checkData, 0, 6);
                            for (int j = 0; j < dataBytes.Count; j++)
                                checkData[6 + j] = dataBytes[j];
                            checkData[checkLen - 1] = LSCnetConstants.ETX;

                            byte expectedBcc = ComputeBcc(checkData, 0, checkLen);
                            byte actualBcc = ParseHexByte(bccBytes, 0);

                            if (expectedBcc != actualBcc)
                                return OperateResult<byte[]>.Failed($"BCC 校验失败: 期望 0x{expectedBcc:X2} 实际 0x{actualBcc:X2}");

                            // 检查错误响应
                            byte respCmd = header[4];
                            if (respCmd == (byte)'E' || respCmd == (byte)'e')
                            {
                                string errCode = dataBytes.Count >= 2
                                    ? Encoding.ASCII.GetString(new[] { dataBytes[0], dataBytes[1] })
                                    : "??";
                                return OperateResult<byte[]>.Failed($"Cnet 错误响应: {errCode}");
                            }

                            byte[] result = dataBytes.ToArray();
                            Log.Debug($"CNET RX ← {DataConverter.ToHexString(result)}");
                            RaiseMessageReceived(DataConverter.ToHexString(result));
                            return OperateResult<byte[]>.Success(result);
                        }
                        dataBytes.Add(b[0]);
                    }

                    return OperateResult<byte[]>.Failed("读取响应超时");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Cnet 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        private int ReadExactSerial(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            int deadline = Environment.TickCount + Timeout;
            while (totalRead < count)
            {
                if (Environment.TickCount > deadline) return totalRead;
                try
                {
                    int read = Port.Read(buffer, offset + totalRead, count - totalRead);
                    if (read == 0) return totalRead;
                    totalRead += read;
                }
                catch (TimeoutException) { return totalRead; }
            }
            return totalRead;
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> ReadRegisters(LSCnetAddress address, ushort count)
        {
            byte[] request = BuildReadRequest(address, count);
            var result = SendCnetRequest(request);
            if (!result.IsSuccess) return result;

            // 响应数据已经是原始字节
            return OperateResult<byte[]>.Success(result.Content);
        }

        private OperateResult WriteRegisters(LSCnetAddress address, byte[] data)
        {
            int count = data.Length / 2;
            byte[] request = BuildWriteRequest(address, (ushort)count, data);
            var result = SendCnetRequest(request);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message, result.ErrorCode);
            return OperateResult.Success();
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadRegisters(parsed, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 1) return OperateResult<bool>.Failed("响应数据不足");
            return OperateResult<bool>.Success((r.Content[0] & 0x01) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadRegisters(parsed, 1);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("响应数据不足");
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadRegisters(parsed, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("响应数据不足");
            return OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 0));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadRegisters(parsed, 4);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<long>.Failed("响应数据不足");
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadRegisters(parsed, 2);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("响应数据不足");
            return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadRegisters(parsed, 4);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<double>.Failed("响应数据不足");
            return OperateResult<double>.Success(DataConverter.ToDouble(r.Content, 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var parsed = LSCnetAddress.Parse(address);
            int regCount = (length + 1) / 2;
            var r = ReadRegisters(parsed, (ushort)regCount);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var parsed = LSCnetAddress.Parse(address);
            int regCount = (length + 1) / 2;
            var r = ReadRegisters(parsed, (ushort)regCount);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] data = new byte[length];
            Array.Copy(r.Content, data, Math.Min(length, r.Content.Length));
            return OperateResult<byte[]>.Success(data);
        }

        // ── 写入 ──

        public override OperateResult Write(string address, bool value)
        {
            var parsed = LSCnetAddress.Parse(address);
            byte[] data = new byte[] { (byte)(value ? 1 : 0), 0 };
            return WriteRegisters(parsed, data);
        }

        public override OperateResult Write(string address, short value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteRegisters(parsed, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteRegisters(parsed, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteRegisters(parsed, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, ulong value) => Write(address, (long)(long)value);

        public override OperateResult Write(string address, float value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteRegisters(parsed, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, double value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteRegisters(parsed, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, string value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteRegisters(parsed, Encoding.ASCII.GetBytes(value ?? ""));
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null) return OperateResult.Failed("写入数据不能为空");
            var parsed = LSCnetAddress.Parse(address);
            return WriteRegisters(parsed, data);
        }

        // ═══════════════════════════════════════════
        //  IBatchReadWrite
        // ═══════════════════════════════════════════

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
                    long l => Write(kv.Key, l),
                    ulong ul => Write(kv.Key, ul),
                    float f => Write(kv.Key, f),
                    double d => Write(kv.Key, d),
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
