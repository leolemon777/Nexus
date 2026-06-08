using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Omron
{
    /// <summary>
    /// 欧姆龙 HostLink 协议客户端（串口模式）。
    /// <para>帧格式与 TCP 版本相同：ASCII 文本，FCS 校验，CR 结尾。</para>
    /// <para>通过 ISerialPort 串口发送，读取直到 CR (0x0D)。</para>
    /// </summary>
    public class OmronHostLinkSerialClient : SerialDeviceBase
    {
        // ── SerialDeviceBase 抽象实现（串口协议自定义收发，不使用基类 SendAndReceive）──
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;
        // ── HostLink 帧常量 ──────────────────────
        private const byte STX = (byte)'@';
        private const byte ETX = (byte)'*';
        private const byte CR  = 0x0D;

        // ── FINS 命令码 ──────────────────────────
        private const ushort CmdMemoryAreaRead  = 0x0101;
        private const ushort CmdMemoryAreaWrite = 0x0102;

        // ── 属性 ─────────────────────────────────

        /// <summary>站号（0-31，默认 0）。</summary>
        public byte UnitNumber { get; set; } = 0;

        /// <summary>ICF：网络中继标志。</summary>
        public byte ICF { get; set; } = 0x00;

        /// <summary>DA2：目标节点号。</summary>
        public byte DA2 { get; set; } = 0x00;

        /// <summary>SA2：源节点号。</summary>
        public byte SA2 { get; set; } = 0x00;

        /// <summary>SID：服务 ID。</summary>
        public byte SID { get; set; } = 0x00;

        /// <summary>响应等待时间（十六进制字符，0-F，单位 10ms）。</summary>
        public byte ResponseWaitTime { get; set; } = (byte)'0';

        /// <summary>字读取分包大小（默认 260）。</summary>
        public int ReadSplits { get; set; } = 260;

        private int _sidCounter;
        private static readonly FinsAddressParser _addressParser = new FinsAddressParser();
        private readonly object _serialLock = new object();

        // ── 构造 ────────────────────────────────

        public OmronHostLinkSerialClient(ISerialPort serialPort, int timeout = 5000)
            : base(serialPort, timeout) { }

        // ── 串口通讯 ─────────────────────────────

        /// <summary>
        /// 通过串口发送 HostLink 帧并读取响应（直到 CR）。
        /// </summary>
        private OperateResult<byte[]> SendAndReceiveSerial(byte[] frame)
        {
            lock (_serialLock)
            {
                try
                {
                    RaiseMessageSent(DataConverter.ToHexString(frame));

                    Port.Write(frame, 0, frame.Length);

                    // 等待并读取直到 CR (0x0D)
                    var response = new List<byte>();
                    byte[] buf = new byte[256];
                    int globalDeadline = Environment.TickCount + Timeout;

                    while (Environment.TickCount < globalDeadline)
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

                    return OperateResult<byte[]>.Failed($"HostLink 串口响应超时 ({Timeout}ms)");
                }
                catch (Exception ex)
                {
                    RaiseError($"HostLink 串口通讯异常: {ex.Message}");
                    return OperateResult<byte[]>.Failed($"HostLink 串口通讯异常: {ex.Message}");
                }
            }
        }

        // ═══════════════════════════════════════════
        //  原始字节读写
        // ═══════════════════════════════════════════

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
                var cmdData = BuildFinsReadCommand(addr, currentWord, chunk, isBit: false);
                var frame = PackCommand(cmdData);
                var recv = SendAndReceiveSerial(frame);
                if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

                var parsed = OmronHostLinkClient.ParseResponse(recv.Content);
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
            var cmdData = BuildFinsWriteCommand(addr, wordCount, data, isBit: false);
            var frame = PackCommand(cmdData);
            var recv = SendAndReceiveSerial(frame);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            var parsed = OmronHostLinkClient.ParseResponse(recv.Content);
            if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  位操作
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = _addressParser.Parse(address);
            if (addr.BitOffset < 0)
                return OperateResult<bool>.Failed("位读取地址必须包含位偏移，例如 D100.03");

            var cmdData = BuildFinsReadCommand(addr, addr.WordAddress, 1, isBit: true);
            var frame = PackCommand(cmdData);
            var recv = SendAndReceiveSerial(frame);
            if (!recv.IsSuccess) return OperateResult<bool>.Failed(recv.Message);

            var parsed = OmronHostLinkClient.ParseResponse(recv.Content);
            if (!parsed.IsSuccess) return OperateResult<bool>.Failed(parsed.Message);
            return OperateResult<bool>.Success(parsed.Content.Length > 0 && parsed.Content[0] != 0);
        }

        public override OperateResult Write(string address, bool value)
        {
            var addr = _addressParser.Parse(address);
            if (addr.BitOffset < 0)
                return OperateResult.Failed("位写入地址必须包含位偏移，例如 D100.03");

            var data = new byte[] { (byte)(value ? 1 : 0) };
            var cmdData = BuildFinsWriteCommand(addr, 1, data, isBit: true);
            var frame = PackCommand(cmdData);
            var recv = SendAndReceiveSerial(frame);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            var parsed = OmronHostLinkClient.ParseResponse(recv.Content);
            if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  类型化读取（大端序）
        // ═══════════════════════════════════════════

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

        // ═══════════════════════════════════════════
        //  类型化写入
        // ═══════════════════════════════════════════

        public override OperateResult Write(string address, short value) => Write(address, new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) => Write(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)(value & 0xFF) });
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, new byte[] { (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)(value & 0xFF) });
        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);
        public override OperateResult Write(string address, float value) { int bits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0); return Write(address, new byte[] { (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)(bits & 0xFF) }); }
        public override OperateResult Write(string address, double value) { long bits = BitConverter.DoubleToInt64Bits(value); return Write(address, new byte[] { (byte)(bits >> 56), (byte)(bits >> 48), (byte)(bits >> 40), (byte)(bits >> 32), (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)(bits & 0xFF) }); }
        public override OperateResult Write(string address, string value) => Write(address, Encoding.ASCII.GetBytes(value ?? string.Empty));

        // ═══════════════════════════════════════════
        //  HostLink 帧构建（复用 TCP 版本逻辑）
        // ═══════════════════════════════════════════

        /// <summary>将 FINS 二进制命令打包为 HostLink ASCII 帧。</summary>
        public byte[] PackCommand(byte[] finsCmd)
        {
            byte[] cmdAscii = OmronHostLinkClient.BytesToAsciiHex(finsCmd);
            byte sid = (byte)(Interlocked.Increment(ref _sidCounter) & 0xFF);

            int totalLen = 14 + cmdAscii.Length + 4;
            var frame = new byte[totalLen];

            frame[0] = STX;
            frame[1] = OmronHostLinkClient.ToAsciiHexHigh(UnitNumber);
            frame[2] = OmronHostLinkClient.ToAsciiHexLow(UnitNumber);
            frame[3] = (byte)'F';
            frame[4] = (byte)'A';
            frame[5] = ResponseWaitTime;
            frame[6] = OmronHostLinkClient.ToAsciiHexHigh(ICF);
            frame[7] = OmronHostLinkClient.ToAsciiHexLow(ICF);
            frame[8] = OmronHostLinkClient.ToAsciiHexHigh(DA2);
            frame[9] = OmronHostLinkClient.ToAsciiHexLow(DA2);
            frame[10] = OmronHostLinkClient.ToAsciiHexHigh(SA2);
            frame[11] = OmronHostLinkClient.ToAsciiHexLow(SA2);
            frame[12] = OmronHostLinkClient.ToAsciiHexHigh(sid);
            frame[13] = OmronHostLinkClient.ToAsciiHexLow(sid);

            Array.Copy(cmdAscii, 0, frame, 14, cmdAscii.Length);

            frame[totalLen - 2] = ETX;
            frame[totalLen - 1] = CR;

            byte fcs = 0;
            for (int i = 0; i < totalLen - 4; i++)
                fcs ^= frame[i];
            frame[totalLen - 4] = OmronHostLinkClient.ToAsciiHexHigh(fcs);
            frame[totalLen - 3] = OmronHostLinkClient.ToAsciiHexLow(fcs);

            return frame;
        }

        // ═══════════════════════════════════════════
        //  FINS 命令构建
        // ═══════════════════════════════════════════

        private byte[] BuildFinsReadCommand(FinsAddress addr, ushort wordAddress, int length, bool isBit)
        {
            var cmd = new byte[9];
            cmd[0] = (byte)(CmdMemoryAreaRead >> 8);
            cmd[1] = (byte)(CmdMemoryAreaRead & 0xFF);
            cmd[2] = (byte)addr.Area;
            cmd[3] = (byte)(isBit || addr.BitOffset >= 0 ? 0x01 : 0x00);
            cmd[4] = (byte)(wordAddress >> 8);
            cmd[5] = (byte)(wordAddress & 0xFF);
            cmd[6] = (byte)(addr.BitOffset >= 0 ? addr.BitOffset : 0x00);
            cmd[7] = (byte)(length >> 8);
            cmd[8] = (byte)(length & 0xFF);
            return cmd;
        }

        private byte[] BuildFinsWriteCommand(FinsAddress addr, ushort length, byte[] data, bool isBit)
        {
            var cmd = new byte[9 + data.Length];
            cmd[0] = (byte)(CmdMemoryAreaWrite >> 8);
            cmd[1] = (byte)(CmdMemoryAreaWrite & 0xFF);
            cmd[2] = (byte)addr.Area;
            cmd[3] = (byte)(isBit || addr.BitOffset >= 0 ? 0x01 : 0x00);
            cmd[4] = (byte)(addr.WordAddress >> 8);
            cmd[5] = (byte)(addr.WordAddress & 0xFF);
            cmd[6] = (byte)(addr.BitOffset >= 0 ? addr.BitOffset : 0x00);
            cmd[7] = (byte)(length >> 8);
            cmd[8] = (byte)(length & 0xFF);
            if (data.Length > 0)
                Array.Copy(data, 0, cmd, 9, data.Length);
            return cmd;
        }

        public override string ToString() => $"OmronHostLinkSerial[{Port}]";
    }
}
