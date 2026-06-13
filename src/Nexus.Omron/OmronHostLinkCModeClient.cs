using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Omron
{
    /// <summary>
    /// 欧姆龙 HostLink C-Mode 协议客户端（串口模式）。
    /// <para>C-Mode 使用直接 ASCII 命令（RD/WD/RR/WR 等），不封装 FINS 二进制。</para>
    /// <para>帧格式：@ + Station(2) + HeaderCode(2) + Text(N) + FCS(2) + * + CR</para>
    /// <para>支持区域：DM(D), AR(W/A), HR(H), LR(L), TC(T/C), EM(E)</para>
    /// </summary>
    public class OmronHostLinkCModeClient : SerialDeviceBase, IBatchReadWrite
    {
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        private const byte STX = (byte)'@';
        private const byte ETX = (byte)'*';
        private const byte CR  = 0x0D;

        public byte UnitNumber { get; set; } = 0;
        public int ReadSplits { get; set; } = 260;

        private static readonly OmronHostLinkCModeAddressParser _addressParser = new OmronHostLinkCModeAddressParser();
        private readonly object _serialLock = new object();

        public OmronHostLinkCModeClient(ISerialPort serialPort, int timeout = 5000)
            : base(serialPort, timeout) { }

        private OperateResult<byte[]> SendAndReceiveSerial(byte[] frame)
        {
            lock (_serialLock)
            {
                try
                {
                    RaiseMessageSent(DataConverter.ToHexString(frame));
                    Port.Write(frame, 0, frame.Length);

                    var response = new List<byte>();
                    byte[] buf = new byte[256];
                    int deadline = Environment.TickCount + Timeout;

                    while (Environment.TickCount < deadline)
                    {
                        int read = Port.Read(buf, 0, buf.Length);
                        if (read > 0)
                        {
                            for (int i = 0; i < read; i++)
                            {
                                response.Add(buf[i]);
                                if (buf[i] == CR)
                                {
                                    byte[] result = response.ToArray();
                                    RaiseMessageReceived(DataConverter.ToHexString(result));
                                    return OperateResult<byte[]>.Success(result);
                                }
                            }
                        }
                    }
                    return OperateResult<byte[]>.Failed($"HostLink C-Mode 串口响应超时 ({Timeout}ms)");
                }
                catch (Exception ex)
                {
                    RaiseError($"HostLink C-Mode 串口通讯异常: {ex.Message}");
                    return OperateResult<byte[]>.Failed($"HostLink C-Mode 串口通讯异常: {ex.Message}");
                }
            }
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = _addressParser.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);

            var result = new List<byte>();
            int remaining = wordCount;
            ushort currentWord = addr.WordAddress;

            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, ReadSplits);
                var frame = BuildReadCommand(addr, currentWord, (ushort)chunk);
                var recv = SendAndReceiveSerial(frame);
                if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

                var parsed = ParseResponse(recv.Content);
                if (!parsed.IsSuccess) return OperateResult<byte[]>.Failed(parsed.Message);

                result.AddRange(parsed.Content);
                currentWord += (ushort)chunk;
                remaining -= chunk;
            }

            byte[] final = result.ToArray();
            if (final.Length > length)
            {
                var trimmed = new byte[length];
                Array.Copy(final, 0, trimmed, 0, length);
                final = trimmed;
            }
            return OperateResult<byte[]>.Success(final);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var addr = _addressParser.Parse(address);
            ushort wordCount = (ushort)(data.Length / 2);
            var frame = BuildWriteCommand(addr, data);
            var recv = SendAndReceiveSerial(frame);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            var parsed = ParseResponse(recv.Content);
            if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);
            return OperateResult.Success();
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadBytes(address, 2);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            return OperateResult<bool>.Success(r.Content.Length > 0 && r.Content[0] != 0);
        }

        public override OperateResult Write(string address, bool value)
        {
            return Write(address, new byte[] { 0, (byte)(value ? 1 : 0) });
        }

        public override OperateResult<short> ReadInt16(string address)
        { var r = ReadBytes(address, 2); return r.IsSuccess ? OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0)) : OperateResult<short>.Failed(r.Message); }

        public override OperateResult<ushort> ReadUInt16(string address)
        { var r = ReadBytes(address, 2); return r.IsSuccess ? OperateResult<ushort>.Success(DataConverter.ToUInt16(r.Content, 0)) : OperateResult<ushort>.Failed(r.Message); }

        public override OperateResult<int> ReadInt32(string address)
        { var r = ReadBytes(address, 4); return r.IsSuccess ? OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 0)) : OperateResult<int>.Failed(r.Message); }

        public override OperateResult<uint> ReadUInt32(string address)
        { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message); }

        public override OperateResult<long> ReadInt64(string address)
        { var r = ReadBytes(address, 8); return r.IsSuccess ? OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0)) : OperateResult<long>.Failed(r.Message); }

        public override OperateResult<ulong> ReadUInt64(string address)
        { var r = ReadInt64(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message); }

        public override OperateResult<float> ReadFloat(string address)
        { var r = ReadBytes(address, 4); return r.IsSuccess ? OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(DataConverter.ToInt32(r.Content, 0)), 0)) : OperateResult<float>.Failed(r.Message); }

        public override OperateResult<double> ReadDouble(string address)
        { var r = ReadBytes(address, 8); return r.IsSuccess ? OperateResult<double>.Success(BitConverter.ToDouble(BitConverter.GetBytes(DataConverter.ToInt64(r.Content, 0)), 0)) : OperateResult<double>.Failed(r.Message); }

        public override OperateResult<string> ReadString(string address, ushort length)
        { var r = ReadBytes(address, length); return r.IsSuccess ? OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content).TrimEnd('\0')) : OperateResult<string>.Failed(r.Message); }

        public override OperateResult Write(string address, short value) => Write(address, new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) => Write(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)(value & 0xFF) });
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, new byte[] { (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)(value & 0xFF) });
        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);
        public override OperateResult Write(string address, float value) { int bits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0); return Write(address, new byte[] { (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)(bits & 0xFF) }); }
        public override OperateResult Write(string address, double value) { long bits = BitConverter.DoubleToInt64Bits(value); return Write(address, new byte[] { (byte)(bits >> 56), (byte)(bits >> 48), (byte)(bits >> 40), (byte)(bits >> 32), (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)(bits & 0xFF) }); }
        public override OperateResult Write(string address, string value) => Write(address, Encoding.ASCII.GetBytes(value ?? string.Empty));

        public override string ToString() => $"OmronHostLinkCModeSerial[{Port}]";

        // ═══════════════════════════════════════════
        //  C-Mode 帧构建
        // ═══════════════════════════════════════════

        /// <summary>构建 RD（读取）命令帧。</summary>
        public byte[] BuildReadCommand(OmronHostLinkCModeAddress addr, ushort startWord, ushort wordCount)
        {
            byte[] areaCode = addr.GetAreaCode();
            byte bitSpec = (byte)(addr.BitOffset >= 0 ? addr.BitOffset : 0x00);

            byte[] headerCode = new byte[] { (byte)'R', (byte)'D' };
            byte[] text = new byte[7];
            text[0] = areaCode[0];
            text[1] = areaCode[1];
            text[2] = (byte)(startWord >> 8);
            text[3] = (byte)(startWord & 0xFF);
            text[4] = bitSpec;
            text[5] = (byte)((wordCount >> 8) & 0x7F);
            text[6] = (byte)(wordCount & 0xFF);

            return PackFrame(headerCode, text);
        }

        /// <summary>构建 WD（写入）命令帧。</summary>
        public byte[] BuildWriteCommand(OmronHostLinkCModeAddress addr, byte[] data)
        {
            byte[] areaCode = addr.GetAreaCode();
            byte bitSpec = (byte)(addr.BitOffset >= 0 ? addr.BitOffset : 0x00);
            ushort wordCount = (ushort)(data.Length / 2);

            byte[] headerCode = new byte[] { (byte)'W', (byte)'D' };
            byte[] dataHex = OmronHostLinkClient.BytesToAsciiHex(data);
            byte[] text = new byte[7 + dataHex.Length];
            text[0] = areaCode[0];
            text[1] = areaCode[1];
            text[2] = (byte)(addr.WordAddress >> 8);
            text[3] = (byte)(addr.WordAddress & 0xFF);
            text[4] = bitSpec;
            text[5] = (byte)((wordCount >> 8) & 0x7F);
            text[6] = (byte)(wordCount & 0xFF);
            Array.Copy(dataHex, 0, text, 7, dataHex.Length);

            return PackFrame(headerCode, text);
        }

        /// <summary>将 C-Mode 命令打包为完整帧。</summary>
        public byte[] PackFrame(byte[] headerCode, byte[] text)
        {
            int bodyLen = 1 + 4 + headerCode.Length + text.Length; // @ + station(4) + header(2) + text
            int totalLen = bodyLen + 2 + 1 + 1; // + FCS(2) + *(1) + CR(1)
            var frame = new byte[totalLen];

            int pos = 0;
            frame[pos++] = STX;
            frame[pos++] = OmronHostLinkClient.ToAsciiHexHigh(UnitNumber);
            frame[pos++] = OmronHostLinkClient.ToAsciiHexLow(UnitNumber);
            frame[pos++] = headerCode[0];
            frame[pos++] = headerCode[1];
            Array.Copy(text, 0, frame, pos, text.Length);
            pos += text.Length;

            // FCS: XOR from [0] to [pos-1]
            byte fcs = 0;
            for (int i = 0; i < pos; i++)
                fcs ^= frame[i];
            frame[pos++] = OmronHostLinkClient.ToAsciiHexHigh(fcs);
            frame[pos++] = OmronHostLinkClient.ToAsciiHexLow(fcs);
            frame[pos++] = ETX;
            frame[pos++] = CR;

            return frame;
        }

        /// <summary>解析 C-Mode 响应帧，提取数据。</summary>
        public static OperateResult<byte[]> ParseResponse(byte[] response)
        {
            if (response == null || response.Length < 11)
                return OperateResult<byte[]>.Failed($"C-Mode 响应过短 ({response?.Length ?? 0} 字节)");

            try
            {
                // @ + station(4) + headerCode(2) + responseCode(2) + text + FCS(2) + * + CR
                // 响应码在 [5..8]（4 个 ASCII hex 字符）
                if (response.Length < 11)
                    return OperateResult<byte[]>.Failed("C-Mode 响应不完整");

                string respCodeStr = Encoding.ASCII.GetString(response, 5, 4);
                int respCode = Convert.ToInt32(respCodeStr, 16);

                // 数据区域 [9..length-4]（ASCII hex）
                byte[] data = new byte[0];
                int dataStart = 9;
                int dataEnd = response.Length - 4; // FCS(2) + *(1) + CR(1) = 4
                if (dataEnd > dataStart)
                {
                    string dataHex = Encoding.ASCII.GetString(response, dataStart, dataEnd - dataStart);
                    data = OmronHostLinkClient.AsciiHexToBytes(dataHex);
                }

                if (respCode != 0x0000 && respCode != 0x00)
                    return OperateResult<byte[]>.Failed($"C-Mode 错误码: 0x{respCode:X4}");

                return OperateResult<byte[]>.Success(data);
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed($"C-Mode 响应解析失败: {ex.Message}");
            }
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
