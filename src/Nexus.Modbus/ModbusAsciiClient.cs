using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Nexus.Modbus
{
    /// <summary>
    /// Modbus ASCII 协议客户端 — 通过串口传输 ASCII 格式报文。
    /// <para>帧格式: ':' + Station(2hex) + FC(2hex) + Data(Nhex) + LRC(2hex) + CR LF</para>
    /// <para>支持功能码: FC01, FC02, FC03, FC04, FC05, FC06, FC15, FC16, FC22, FC23。</para>
    /// <para>继承 SerialDeviceBase，使用 ISerialPort 抽象串口操作。</para>
    /// </summary>
    public class ModbusAsciiClient : SerialDeviceBase, IBatchReadWrite
    {
        /// <summary>站号。</summary>
        public byte Station { get; set; }

        /// <summary>字节序。</summary>
        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;
        private byte? _addressStationOverride;
        private Endianness? _addressByteOrderOverride;

        /// <summary>字符串编码选项。</summary>
        public StringEncoding StringEncodingOption { get; set; } = StringEncoding.Ascii;

        /// <summary>
        /// 创建 Modbus ASCII 客户端。
        /// </summary>
        /// <param name="serialPort">串口抽象接口。</param>
        /// <param name="station">从站地址。</param>
        /// <param name="timeout">超时（毫秒）。</param>
        public ModbusAsciiClient(ISerialPort serialPort, byte station = 1, int timeout = 5000)
            : base(serialPort, timeout)
        {
            Station = station;
        }

        // ═══════════════════════════════════════════
        //  SerialDeviceBase 抽象成员实现
        //  ASCII 响应通过 AsciiSendAndReceive 自行处理，
        //  此处提供基类要求的实现。
        // ═══════════════════════════════════════════

        protected override int ResponseHeaderLength => 4;

        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ═══════════════════════════════════════════
        //  ASCII 帧编解码
        // ═══════════════════════════════════════════

        private static readonly char[] HexChars = "0123456789ABCDEF".ToCharArray();

        private static string BytesToHex(byte[] data, int offset, int length)
        {
            char[] chars = new char[length * 2];
            for (int i = 0; i < length; i++)
            {
                byte b = data[offset + i];
                chars[i * 2] = HexChars[(b >> 4) & 0x0F];
                chars[i * 2 + 1] = HexChars[b & 0x0F];
            }
            return new string(chars);
        }

        private static byte[] HexToBytes(string hex)
        {
            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = (byte)((HexVal(hex[i * 2]) << 4) | HexVal(hex[i * 2 + 1]));
            return result;
        }

        private static int HexVal(char c) => c >= '0' && c <= '9' ? c - '0' :
            c >= 'A' && c <= 'F' ? c - 'A' + 10 :
            c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0;

        /// <summary>构建 ASCII 帧: ':' + Hex(Station + PDU + LRC) + CR LF</summary>
        private byte[] BuildAsciiFrame(byte[] pdu)
        {
            byte station = TakeStation();
            byte[] raw = new byte[1 + pdu.Length];
            raw[0] = station;
            Buffer.BlockCopy(pdu, 0, raw, 1, pdu.Length);

            byte lrc = CrcCalculator.ComputeLrc(raw);

            string frame = ":" + BytesToHex(raw, 0, raw.Length) + BytesToHex(new[] { lrc }, 0, 1) + "\r\n";
            return Encoding.ASCII.GetBytes(frame);
        }

        private byte TakeStation()
        {
            byte station = _addressStationOverride ?? Station;
            _addressStationOverride = null;
            return station;
        }

        // ═══════════════════════════════════════════
        //  ASCII 帧收发
        // ═══════════════════════════════════════════

        private readonly object _asciiLock = new object();

        private OperateResult<byte[]> AsciiSendAndReceive(byte[] pdu)
        {
            try
            {
                lock (_asciiLock)
                {
                    if (!Port.IsOpen)
                        return OperateResult<byte[]>.Failed("串口未打开");

                    byte[] frame = BuildAsciiFrame(pdu);
                    string frameStr = Encoding.ASCII.GetString(frame);
                    Log.Debug($"TX → {frameStr.TrimEnd()}");
                    RaiseMessageSent(frameStr.TrimEnd());

                    Port.Write(frame, 0, frame.Length);

                    if (InterFrameDelay > 0)
                        System.Threading.Thread.Sleep(InterFrameDelay);

                    byte[]? response = ReadAsciiFrame();
                    if (response == null)
                        return OperateResult<byte[]>.Failed("读取 ASCII 响应超时");

                    string respStr = Encoding.ASCII.GetString(response);
                    Log.Debug($"RX ← {respStr.TrimEnd()}");
                    RaiseMessageReceived(respStr.TrimEnd());

                    string hex = respStr.TrimStart(':').TrimEnd('\r', '\n');
                    if (hex.Length < 4)
                        return OperateResult<byte[]>.Failed("ASCII 响应长度不足");

                    byte[] raw = HexToBytes(hex);

                    int dataLen = raw.Length - 1;
                    byte expectedLrc = CrcCalculator.ComputeLrc(raw, 0, dataLen);
                    if (raw[dataLen] != expectedLrc)
                        return OperateResult<byte[]>.Failed($"ASCII 响应 LRC 校验失败: 期望=0x{expectedLrc:X2}, 实际=0x{raw[dataLen]:X2}");

                    byte expectedStation = frame.Length > 3
                        ? HexToBytes(frameStr.TrimStart(':').TrimEnd('\r', '\n').Substring(0, 2))[0]
                        : Station;
                    if (raw[0] != expectedStation)
                        return OperateResult<byte[]>.Failed($"响应站号不匹配: 期望={expectedStation}, 实际={raw[0]}");

                    if ((raw[1] & 0x80) != 0)
                    {
                        byte exCode = raw.Length > 2 ? raw[2] : (byte)0;
                        string msg = exCode switch
                        {
                            1 => "非法功能码",
                            2 => "非法数据地址",
                            3 => "非法数据值",
                            4 => "从站设备故障",
                            _ => $"Modbus异常码: {exCode}"
                        };
                        return OperateResult<byte[]>.Failed(msg, exCode);
                    }

                    byte[] respPdu = new byte[dataLen - 1];
                    Buffer.BlockCopy(raw, 1, respPdu, 0, respPdu.Length);
                    return OperateResult<byte[]>.Success(respPdu);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"ASCII 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                return OperateResult<byte[]>.Failed($"ASCII 通讯异常: {ex.Message}");
            }
        }

        /// <summary>从串口读取完整 ASCII 帧（从 ':' 到 LF）。</summary>
        private byte[]? ReadAsciiFrame()
        {
            int deadline = Environment.TickCount + Timeout;

            bool foundStart = false;
            while (Environment.TickCount <= deadline)
            {
                int b = ReadByteWithTimeout(deadline);
                if (b < 0) return null;
                if (b == ':') { foundStart = true; break; }
            }
            if (!foundStart) return null;

            using (var ms = new MemoryStream())
            {
                ms.WriteByte((byte)':');

                bool sawCr = false;
                while (Environment.TickCount <= deadline)
                {
                    int b = ReadByteWithTimeout(deadline);
                    if (b < 0) return null;
                    ms.WriteByte((byte)b);

                    if (b == '\r') sawCr = true;
                    else if (sawCr && b == '\n')
                        return ms.ToArray();
                    else
                        sawCr = false;
                }

                return null;
            }
        }

        private int ReadByteWithTimeout(int deadline)
        {
            byte[] buf = new byte[1];
            while (Environment.TickCount <= deadline)
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

        // ═══════════════════════════════════════════
        //  地址解析（支持前缀模式）
        //  0xxxx=线圈, 1xxxx=离散输入, 3xxxx=输入寄存器, 4xxxx=保持寄存器
        // ═══════════════════════════════════════════

        protected void CaptureAddressContext(string address)
        {
            var context = AddressContext.Parse(address);
            string? stationValue = GetAddressParameter(context, "unit", "station", "slave", "s");
            if (stationValue != null)
            {
                if (!int.TryParse(stationValue, out int station) || station < byte.MinValue || station > byte.MaxValue)
                    throw new AddressParseException(address, $"站号超出范围: {stationValue}");
                _addressStationOverride = (byte)station;
            }
            else
            {
                _addressStationOverride = null;
            }

            string? byteOrderValue = GetAddressParameter(context, "bo", "byteOrder", "byteorder", "endian", "endianness");
            if (byteOrderValue != null)
            {
                if (!TryParseByteOrder(byteOrderValue, out var byteOrder))
                    throw new AddressParseException(address, $"字节序无效: {byteOrderValue}");
                _addressByteOrderOverride = byteOrder;
            }
            else
            {
                _addressByteOrderOverride = null;
            }
        }

        private static string? GetAddressParameter(AddressContext context, params string[] keys)
        {
            foreach (var key in keys)
                foreach (var kvp in context.Parameters)
                    if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                        return kvp.Value;
            return null;
        }

        private static bool TryParseByteOrder(string value, out Endianness byteOrder)
        {
            string normalized = value.Trim().Replace("-", "").Replace("_", "").ToLowerInvariant();
            switch (normalized)
            {
                case "be":
                case "big":
                case "bigendian":
                case "abcd":
                    byteOrder = Endianness.BigEndian;
                    return true;
                case "le":
                case "little":
                case "littleendian":
                case "dcba":
                    byteOrder = Endianness.LittleEndian;
                    return true;
                case "midbig":
                case "midbigendian":
                case "badc":
                    byteOrder = Endianness.MidBigEndian;
                    return true;
                case "midlittle":
                case "midlittleendian":
                case "cdab":
                    byteOrder = Endianness.MidLittleEndian;
                    return true;
                default:
                    return Enum.TryParse(value, true, out byteOrder);
            }
        }

        protected virtual (ushort address, byte readFc, byte writeFc) ParseAddressEx(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空");
            CaptureAddressContext(address);
            address = AddressContext.ExtractCoreAddress(address).Trim();

            char prefix = address[0];
            string numPart = address.Substring(1);

            if (address.Length >= 5 && char.IsDigit(prefix))
            {
                if (prefix == '0') return (ParseUshort(numPart), 0x01, 0x05);
                if (prefix == '1') return (ParseUshort(numPart), 0x02, (byte)0);
                if (prefix == '3') return (ParseUshort(numPart), 0x04, (byte)0);
                if (prefix == '4') return (ParseUshort(numPart), 0x03, 0x06);
            }

            return (ParseUshort(address), 0x03, 0x06);
        }

        private static ushort ParseUshort(string s) => ushort.Parse(s.TrimStart('0').Length == 0 ? "0" : s.TrimStart('0'));
        private ushort ParseAddress(string address) => ParseAddressEx(address).address;

        // ═══════════════════════════════════════════
        //  字节序数据转换
        //  支持 ABCD / DCBA / BADC / CDAB
        // ═══════════════════════════════════════════

        private Endianness CurrentByteOrder => _addressByteOrderOverride ?? ByteOrder;

        private short ToInt16Ordered(byte[] data, int offset) => CurrentByteOrder switch
        {
            Endianness.LittleEndian => (short)(data[offset] | (data[offset + 1] << 8)),
            _ => DataConverter.ToInt16(data, offset)
        };

        private ushort ToUInt16Ordered(byte[] data, int offset) => CurrentByteOrder switch
        {
            Endianness.LittleEndian => (ushort)(data[offset] | (data[offset + 1] << 8)),
            _ => DataConverter.ToUInt16(data, offset)
        };

        private int ToInt32Ordered(byte[] data, int offset) => CurrentByteOrder switch
        {
            Endianness.BigEndian => DataConverter.ToInt32(data, offset),
            Endianness.LittleEndian => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24),
            Endianness.MidBigEndian => (data[offset + 1] << 24) | (data[offset] << 16) | (data[offset + 3] << 8) | data[offset + 2],
            Endianness.MidLittleEndian => (data[offset + 2] << 24) | (data[offset + 3] << 16) | (data[offset] << 8) | data[offset + 1],
            _ => DataConverter.ToInt32(data, offset)
        };

        private unsafe float ToFloatOrdered(byte[] data, int offset)
        {
            int v = ToInt32Ordered(data, offset);
            return *(float*)&v;
        }

        private byte[] GetBytesOrdered(short value) => CurrentByteOrder == Endianness.LittleEndian
            ? new byte[] { (byte)value, (byte)(value >> 8) }
            : DataConverter.GetBytes(value);

        private byte[] GetBytesOrdered(int value)
        {
            var byteOrder = CurrentByteOrder;
            if (byteOrder == Endianness.BigEndian) return DataConverter.GetBytes(value);
            if (byteOrder == Endianness.LittleEndian) return new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };
            if (byteOrder == Endianness.MidBigEndian) return new byte[] { (byte)(value >> 16), (byte)(value >> 24), (byte)value, (byte)(value >> 8) };
            return new byte[] { (byte)(value >> 8), (byte)value, (byte)(value >> 24), (byte)(value >> 16) };
        }

        private unsafe byte[] GetBytesOrdered(float value)
        {
            int v = *(int*)&value;
            return GetBytesOrdered(v);
        }

        private byte[] GetBytesOrdered(long value) => DataConverter.GetBytes(value, CurrentByteOrder);
        private byte[] GetBytesOrdered(ulong value) => DataConverter.GetBytes(value, CurrentByteOrder);
        private byte[] GetBytesOrdered(double value) => DataConverter.GetBytes(value, CurrentByteOrder);

        // ═══════════════════════════════════════════
        //  FC01 — 读线圈 (Read Coils)
        //  FC02 — 读离散输入 (Read Discrete Inputs)
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x02 ? (byte)0x02 : (byte)0x01;
            byte[] pdu = { fc, (byte)(addr >> 8), (byte)addr, 0, 1 };
            var result = AsciiSendAndReceive(pdu);
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message, result.ErrorCode);
            return OperateResult<bool>.Success((result.Content[2] & 0x01) != 0);
        }

        /// <summary>读取多个线圈/离散输入。</summary>
        public OperateResult<bool[]> ReadBools(string address, ushort count)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x02 ? (byte)0x02 : (byte)0x01;
            byte[] pdu = { fc, (byte)(addr >> 8), (byte)addr, (byte)(count >> 8), (byte)count };
            var result = AsciiSendAndReceive(pdu);
            if (!result.IsSuccess) return OperateResult<bool[]>.Failed(result.Message, result.ErrorCode);

            byte byteCount = result.Content[1];
            bool[] values = new bool[count];
            for (int i = 0; i < count; i++)
                values[i] = (result.Content[2 + i / 8] & (1 << (i % 8))) != 0;
            return OperateResult<bool[]>.Success(values);
        }

        // ═══════════════════════════════════════════
        //  FC03 — 读保持寄存器 (Read Holding Registers)
        //  FC04 — 读输入寄存器 (Read Input Registers)
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> ReadRegistersCore(ushort address, ushort count, byte fc)
        {
            byte[] pdu = { fc, (byte)(address >> 8), (byte)address, (byte)(count >> 8), (byte)count };
            var result = AsciiSendAndReceive(pdu);
            if (!result.IsSuccess) return result;
            byte byteCount = result.Content[1];
            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(result.Content, 2, data, 0, byteCount);
            return OperateResult<byte[]>.Success(data);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = ReadRegistersCore(addr, 1, fc);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(ToInt16Ordered(r.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = ReadRegistersCore(addr, 1, fc);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ushort>.Success(ToUInt16Ordered(r.Content, 0));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = ReadRegistersCore(addr, 2, fc);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success(ToInt32Ordered(r.Content, 0));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = ReadRegistersCore(addr, 4, fc);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0, CurrentByteOrder));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = ReadRegistersCore(addr, 2, fc);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            return OperateResult<float>.Success(ToFloatOrdered(r.Content, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = ReadRegistersCore(addr, 4, fc);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(DataConverter.ToDouble(r.Content, 0, CurrentByteOrder));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            ushort regCount = (ushort)((length + 1) / 2);
            var r = ReadRegistersCore(addr, regCount, fc);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, length));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            ushort regCount = (ushort)((length + 1) / 2);
            var r = ReadRegistersCore(addr, regCount, fc);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] data = new byte[length];
            Buffer.BlockCopy(r.Content, 0, data, 0, Math.Min(length, r.Content.Length));
            return OperateResult<byte[]>.Success(data);
        }

        // ═══════════════════════════════════════════
        //  FC05 — 写单线圈 (Write Single Coil)
        // ═══════════════════════════════════════════

        public override OperateResult Write(string address, bool value)
        {
            ushort addr = ParseAddress(address);
            byte[] pdu = { 0x05, (byte)(addr >> 8), (byte)addr, (byte)(value ? 0xFF : 0x00), 0x00 };
            var result = AsciiSendAndReceive(pdu);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message, result.ErrorCode);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  FC06 — 写单寄存器 (Write Single Register)
        // ═══════════════════════════════════════════

        public override OperateResult Write(string address, short value)
        {
            ushort addr = ParseAddress(address);
            var vb = GetBytesOrdered(value);
            byte[] pdu = { 0x06, (byte)(addr >> 8), (byte)addr, vb[0], vb[1] };
            var result = AsciiSendAndReceive(pdu);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message, result.ErrorCode);
            return OperateResult.Success();
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        // ═══════════════════════════════════════════
        //  FC22 — 掩码写寄存器 (Mask Write Register)
        // ═══════════════════════════════════════════

        /// <summary>
        /// 原子掩码写保持寄存器 (FC22)。
        /// 新值按 Modbus 规范计算为: (当前值 AND andMask) OR (orMask AND NOT andMask)。
        /// </summary>
        public OperateResult MaskWriteRegister(string address, ushort andMask, ushort orMask)
        {
            ushort addr = ParseAddress(address);
            byte[] pdu =
            {
                0x16,
                (byte)(addr >> 8), (byte)addr,
                (byte)(andMask >> 8), (byte)andMask,
                (byte)(orMask >> 8), (byte)orMask
            };

            var result = AsciiSendAndReceive(pdu);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        // ═══════════════════════════════════════════
        //  FC15 — 写多个线圈 (Write Multiple Coils)
        // ═══════════════════════════════════════════

        /// <summary>写入多个线圈值。</summary>
        public OperateResult WriteMultipleCoils(ushort startAddress, bool[] values)
        {
            int byteCount = (values.Length + 7) / 8;
            byte[] coilBytes = new byte[byteCount];
            for (int i = 0; i < values.Length; i++)
                if (values[i]) coilBytes[i / 8] |= (byte)(1 << (i % 8));

            byte[] pdu = new byte[6 + byteCount];
            pdu[0] = 0x0F;
            pdu[1] = (byte)(startAddress >> 8); pdu[2] = (byte)startAddress;
            pdu[3] = (byte)(values.Length >> 8); pdu[4] = (byte)values.Length;
            pdu[5] = (byte)byteCount;
            Buffer.BlockCopy(coilBytes, 0, pdu, 6, byteCount);

            var result = AsciiSendAndReceive(pdu);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message, result.ErrorCode);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  FC16 — 写多个寄存器 (Write Multiple Registers)
        // ═══════════════════════════════════════════

        private OperateResult WriteMultipleRegisters(ushort address, ushort count, byte[] registerData)
        {
            byte byteCount = (byte)(count * 2);
            byte[] pdu = new byte[6 + byteCount];
            pdu[0] = 0x10;
            pdu[1] = (byte)(address >> 8); pdu[2] = (byte)address;
            pdu[3] = (byte)(count >> 8); pdu[4] = (byte)count;
            pdu[5] = byteCount;
            Buffer.BlockCopy(registerData, 0, pdu, 6, byteCount);

            var result = AsciiSendAndReceive(pdu);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message, result.ErrorCode);
            return OperateResult.Success();
        }

        public override OperateResult Write(string address, int value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegisters(addr, 2, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegisters(addr, 4, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, ulong value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegisters(addr, 4, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, float value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegisters(addr, 2, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, double value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegisters(addr, 4, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, string value)
        {
            ushort addr = ParseAddress(address);
            byte[] data = DataConverter.GetBytes(value);
            ushort regCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteMultipleRegisters(addr, regCount, data);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            ushort addr = ParseAddress(address);
            ushort regCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) { Array.Resize(ref data, data.Length + 1); }
            return WriteMultipleRegisters(addr, regCount, data);
        }

        // ═══════════════════════════════════════════
        //  FC23 — 读写多个寄存器 (Read/Write Multiple Registers, 原子操作)
        // ═══════════════════════════════════════════

        /// <summary>
        /// 原子操作：同时写入并读取寄存器（FC23）。
        /// </summary>
        /// <param name="readAddress">读取起始地址。</param>
        /// <param name="readCount">读取寄存器数量。</param>
        /// <param name="writeAddress">写入起始地址。</param>
        /// <param name="writeData">写入数据（字节数组，长度必须为偶数）。</param>
        public OperateResult<byte[]> ReadWriteMultipleRegisters(
            ushort readAddress, ushort readCount,
            ushort writeAddress, byte[] writeData)
        {
            ushort writeRegCount = (ushort)(writeData.Length / 2);
            byte writeByteCount = (byte)writeData.Length;
            byte[] pdu = new byte[10 + writeByteCount];
            pdu[0] = 0x17; // FC23
            pdu[1] = (byte)(readAddress >> 8); pdu[2] = (byte)readAddress;
            pdu[3] = (byte)(readCount >> 8); pdu[4] = (byte)readCount;
            pdu[5] = (byte)(writeAddress >> 8); pdu[6] = (byte)writeAddress;
            pdu[7] = (byte)(writeRegCount >> 8); pdu[8] = (byte)writeRegCount;
            pdu[9] = writeByteCount;
            Buffer.BlockCopy(writeData, 0, pdu, 10, writeByteCount);

            var result = AsciiSendAndReceive(pdu);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            byte byteCount = result.Content[1];
            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(result.Content, 2, data, 0, byteCount);
            return OperateResult<byte[]>.Success(data);
        }

        // ═══════════════════════════════════════════
        //  自定义功能码发送
        // ═══════════════════════════════════════════

        /// <summary>
        /// 发送自定义 Modbus PDU（自动添加站号和 LRC）。
        /// 返回响应 PDU（去掉站号和 LRC）。
        /// </summary>
        public OperateResult<byte[]> SendCustomModbus(byte[] pdu)
        {
            return AsciiSendAndReceive(pdu);
        }

        // ═══════════════════════════════════════════
        //  字符串编码选项
        // ═══════════════════════════════════════════

        /// <summary>使用配置的编码读取字符串。</summary>
        public OperateResult<string> ReadStringEncoded(string address, ushort length)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            ushort regCount = (ushort)((length + 1) / 2);
            var r = ReadRegistersCore(addr, regCount, fc);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);

            string text = StringEncodingOption switch
            {
                StringEncoding.Utf8 => Encoding.UTF8.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0', ' '),
                StringEncoding.Unicode => Encoding.Unicode.GetString(r.Content, 0, Math.Min(length * 2, r.Content.Length)).TrimEnd('\0', ' '),
                _ => DataConverter.ToString(r.Content, 0, length)
            };
            return OperateResult<string>.Success(text);
        }

        /// <summary>使用配置的编码写入字符串。</summary>
        public OperateResult WriteStringEncoded(string address, string value)
        {
            ushort addr = ParseAddress(address);
            byte[] data = StringEncodingOption switch
            {
                StringEncoding.Utf8 => Encoding.UTF8.GetBytes(value),
                StringEncoding.Unicode => Encoding.Unicode.GetBytes(value),
                _ => DataConverter.GetBytes(value)
            };
            ushort regCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteMultipleRegisters(addr, regCount, data);
        }

        // ═══════════════════════════════════════════
        //  Async 方法
        // ═══════════════════════════════════════════

        public override Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public override Task<OperateResult<short>> ReadInt16Async(string address) => Task.Run(() => ReadInt16(address));
        public override Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.Run(() => ReadUInt16(address));
        public override Task<OperateResult<int>> ReadInt32Async(string address) => Task.Run(() => ReadInt32(address));
        public override Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.Run(() => ReadUInt32(address));
        public override Task<OperateResult<long>> ReadInt64Async(string address) => Task.Run(() => ReadInt64(address));
        public override Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.Run(() => ReadUInt64(address));
        public override Task<OperateResult<float>> ReadFloatAsync(string address) => Task.Run(() => ReadFloat(address));
        public override Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.Run(() => ReadDouble(address));
        public override Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.Run(() => ReadString(address, length));
        public override Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.Run(() => ReadBytes(address, length));

        public override Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, short value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

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
    }
}
