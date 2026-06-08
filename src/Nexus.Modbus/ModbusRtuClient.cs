using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Modbus
{
    /// <summary>
    /// Modbus RTU 协议客户端 — 通过串口传输 RTU 格式报文。
    /// <para>支持功能码: FC01, FC02, FC03, FC04, FC05, FC06, FC15, FC16, FC23。</para>
    /// <para>RTU 帧结构: [Station(1)][FunctionCode(1)][Data(N)][CRC16(2)]</para>
    /// <para>继承 SerialDeviceBase，完全利用其原生异步能力，消除 Task.Run 阻塞。</para>
    /// </summary>
    public class ModbusRtuClient : SerialDeviceBase
    {
        /// <summary>站号。</summary>
        public byte Station { get; set; }

        /// <summary>字节序。</summary>
        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;

        /// <summary>字符串编码选项。</summary>
        public StringEncoding StringEncodingOption { get; set; } = StringEncoding.Ascii;

        /// <summary>
        /// 创建 Modbus RTU 协议客户端。
        /// </summary>
        /// <param name="port">串口实现。</param>
        /// <param name="station">从站地址。</param>
        /// <param name="timeout">超时（毫秒）。</param>
        public ModbusRtuClient(ISerialPort port, byte station = 1, int timeout = 5000)
            : base(port, timeout)
        {
            Station = station;
        }

        // ═══════════════════════════════════════════
        //  SerialDeviceBase 抽象成员实现 (真正异步的关键)
        // ═══════════════════════════════════════════

        /// <summary>
        /// RTU 响应头固定为 3 字节: Station(1) + FunctionCode(1) + ByteCount/ExceptionCode(1)
        /// </summary>
        protected override int ResponseHeaderLength => 3;

        /// <summary>
        /// 根据响应头计算剩余载荷长度（包含 2 字节 CRC）。
        /// </summary>
        private int? _overridePayloadLength;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (_overridePayloadLength.HasValue) return _overridePayloadLength.Value;
            if (header.Length < 3) return 0;
            byte fc = header[1];

            // 异常响应: Station(1) + FC(1, 0x80+) + ExceptionCode(1) + CRC(2) = 5 bytes total. Header is 3, so payload is 2.
            if ((fc & 0x80) != 0) return 2;

            switch (fc)
            {
                case 0x01: case 0x02: case 0x03: case 0x04: case 0x17:
                    // ByteCount(1) + Data(N) + CRC(2). Header already has ByteCount, so payload is ByteCount + 2.
                    return header[2] + 2;
                case 0x05: case 0x06: case 0x0F: case 0x10:
                    return 5;
                default:
                    return 5; // Fallback
            }
        }

        // ═══════════════════════════════════════════
        //  RTU 帧收发 (原生异步)
        // ═══════════════════════════════════════════

        /// <summary>
        /// 异步发送 RTU 请求并接收响应。完全利用 SerialDeviceBase.SendAndReceiveAsync。
        /// </summary>
        protected async Task<OperateResult<byte[]>> SendRtuPduAsync(byte[] pdu, CancellationToken ct)
        {
            byte[] request = new byte[1 + pdu.Length + 2];
            request[0] = Station;
            Buffer.BlockCopy(pdu, 0, request, 1, pdu.Length);

            ushort crc = CrcCalculator.ComputeCrc16(request, 0, 1 + pdu.Length);
            request[1 + pdu.Length] = (byte)(crc & 0xFF);
            request[1 + pdu.Length + 1] = (byte)((crc >> 8) & 0xFF);

            var result = await base.SendAndReceiveAsync(request, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            byte[] response = result.Content;
            if (!CrcCalculator.VerifyCrc16(response))
                return OperateResult<byte[]>.Failed("RTU 响应 CRC 校验失败");

            if (response[0] != Station)
                return OperateResult<byte[]>.Failed($"响应站号不匹配: 期望={Station}, 实际={response[0]}");

            byte fc = response[1];
            if ((fc & 0x80) != 0)
            {
                byte exCode = response.Length > 2 ? response[2] : (byte)0;
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

            int pduLen = response.Length - 3;
            byte[] respPdu = new byte[pduLen];
            Buffer.BlockCopy(response, 1, respPdu, 0, pduLen);
            return OperateResult<byte[]>.Success(respPdu);
        }

        /// <summary>
        /// 同步发送 RTU 请求并接收响应 (供同步 API 使用)。
        /// </summary>
        protected OperateResult<byte[]> SendRtuPdu(byte[] pdu)
        {
            return SendRtuPduAsync(pdu, CancellationToken.None).GetAwaiter().GetResult();
        }

        // ═══════════════════════════════════════════
        //  地址解析（支持前缀模式）
        // ═══════════════════════════════════════════

        private static (ushort address, byte readFc, byte writeFc) ParseAddressEx(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空");

            address = address.Trim();
            char prefix = address[0];
            string numPart = address.Substring(1);

            if (address.Length >= 5 && char.IsDigit(prefix))
            {
                ushort addr = ParseUshort(numPart);

                if (prefix == '0') return (addr, 0x01, 0x05);
                if (prefix == '1') return (addr, 0x02, (byte)0);
                if (prefix == '3') return (addr, 0x04, (byte)0);
                if (prefix == '4') return (addr, 0x03, 0x06);
            }

            return (ParseUshort(address), 0x03, 0x06);
        }

        private static ushort ParseUshort(string s) => ushort.Parse(s.TrimStart('0').Length == 0 ? "0" : s.TrimStart('0'));
        private static ushort ParseAddress(string address) => ParseAddressEx(address).address;

        // ═══════════════════════════════════════════
        //  字节序数据转换
        // ═══════════════════════════════════════════

        private short ToInt16Ordered(byte[] data, int offset) => ByteOrder switch
        {
            Endianness.LittleEndian => (short)(data[offset] | (data[offset + 1] << 8)),
            _ => DataConverter.ToInt16(data, offset)
        };

        private ushort ToUInt16Ordered(byte[] data, int offset) => ByteOrder switch
        {
            Endianness.LittleEndian => (ushort)(data[offset] | (data[offset + 1] << 8)),
            _ => DataConverter.ToUInt16(data, offset)
        };

        private int ToInt32Ordered(byte[] data, int offset) => ByteOrder switch
        {
            Endianness.BigEndian => DataConverter.ToInt32(data, offset),
            Endianness.LittleEndian => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24),
            Endianness.MidBigEndian => (data[offset + 1] << 24) | (data[offset] << 16) | (data[offset + 3] << 8) | data[offset + 2],
            Endianness.MidLittleEndian => (data[offset + 2] << 24) | (data[offset + 3] << 16) | (data[offset] << 8) | data[offset + 1],
            _ => DataConverter.ToInt32(data, offset)
        };

        private float ToFloatOrdered(byte[] data, int offset)
        {
            int v = ToInt32Ordered(data, offset);
            unsafe { return *(float*)&v; }
        }

        private byte[] GetBytesOrdered(short value) => ByteOrder == Endianness.LittleEndian
            ? new byte[] { (byte)value, (byte)(value >> 8) }
            : DataConverter.GetBytes(value);

        private byte[] GetBytesOrdered(int value)
        {
            if (ByteOrder == Endianness.BigEndian) return DataConverter.GetBytes(value);
            if (ByteOrder == Endianness.LittleEndian) return new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };
            if (ByteOrder == Endianness.MidBigEndian) return new byte[] { (byte)(value >> 16), (byte)(value >> 24), (byte)value, (byte)(value >> 8) };
            return new byte[] { (byte)(value >> 8), (byte)value, (byte)(value >> 24), (byte)(value >> 16) };
        }

        private byte[] GetBytesOrdered(float value)
        {
            unsafe { int v = *(int*)&value; return GetBytesOrdered(v); }
        }

        // ═══════════════════════════════════════════
        //  读取实现 (FC01 - FC04)
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x02 ? (byte)0x02 : (byte)0x01;
            var result = SendRtuPdu(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 1 });
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message, result.ErrorCode);
            return OperateResult<bool>.Success((result.Content[2] & 0x01) != 0);
        }

        public override async Task<OperateResult<bool>> ReadBoolAsync(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x02 ? (byte)0x02 : (byte)0x01;
            var result = await SendRtuPduAsync(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 1 }, CancellationToken.None).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message, result.ErrorCode);
            return OperateResult<bool>.Success((result.Content[2] & 0x01) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = SendRtuPdu(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 1 });
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(ToInt16Ordered(r.Content, 2));
        }

        public override async Task<OperateResult<short>> ReadInt16Async(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var result = await SendRtuPduAsync(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 1 }, CancellationToken.None).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<short>.Failed(result.Message, result.ErrorCode);
            return OperateResult<short>.Success(ToInt16Ordered(result.Content, 2));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = SendRtuPdu(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 1 });
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ushort>.Success(ToUInt16Ordered(r.Content, 2));
        }

        public override async Task<OperateResult<ushort>> ReadUInt16Async(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var result = await SendRtuPduAsync(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 1 }, CancellationToken.None).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<ushort>.Failed(result.Message, result.ErrorCode);
            return OperateResult<ushort>.Success(ToUInt16Ordered(result.Content, 2));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = SendRtuPdu(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 2 });
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success(ToInt32Ordered(r.Content, 2));
        }

        public override async Task<OperateResult<int>> ReadInt32Async(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var result = await SendRtuPduAsync(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 2 }, CancellationToken.None).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<int>.Failed(result.Message, result.ErrorCode);
            return OperateResult<int>.Success(ToInt32Ordered(result.Content, 2));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override async Task<OperateResult<uint>> ReadUInt32Async(string address)
        {
            var r = await ReadInt32Async(address).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = SendRtuPdu(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 4 });
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 2));
        }

        public override async Task<OperateResult<long>> ReadInt64Async(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var result = await SendRtuPduAsync(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 4 }, CancellationToken.None).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<long>.Failed(result.Message, result.ErrorCode);
            return OperateResult<long>.Success(DataConverter.ToInt64(result.Content, 2));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override async Task<OperateResult<ulong>> ReadUInt64Async(string address)
        {
            var r = await ReadInt64Async(address).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = SendRtuPdu(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 2 });
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            return OperateResult<float>.Success(ToFloatOrdered(r.Content, 2));
        }

        public override async Task<OperateResult<float>> ReadFloatAsync(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var result = await SendRtuPduAsync(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 2 }, CancellationToken.None).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<float>.Failed(result.Message, result.ErrorCode);
            return OperateResult<float>.Success(ToFloatOrdered(result.Content, 2));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = SendRtuPdu(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 4 });
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(DataConverter.ToDouble(r.Content, 2));
        }

        public override async Task<OperateResult<double>> ReadDoubleAsync(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var result = await SendRtuPduAsync(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 4 }, CancellationToken.None).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<double>.Failed(result.Message, result.ErrorCode);
            return OperateResult<double>.Success(DataConverter.ToDouble(result.Content, 2));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            ushort regCount = (ushort)((length + 1) / 2);
            var r = SendRtuPdu(new byte[] { fc, (byte)(addr >> 8), (byte)addr, (byte)(regCount >> 8), (byte)regCount });
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 2, length));
        }

        public override async Task<OperateResult<string>> ReadStringAsync(string address, ushort length)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            ushort regCount = (ushort)((length + 1) / 2);
            var result = await SendRtuPduAsync(new byte[] { fc, (byte)(addr >> 8), (byte)addr, (byte)(regCount >> 8), (byte)regCount }, CancellationToken.None).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<string>.Failed(result.Message, result.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(result.Content, 2, length));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            ushort regCount = (ushort)((length + 1) / 2);
            var r = SendRtuPdu(new byte[] { fc, (byte)(addr >> 8), (byte)addr, (byte)(regCount >> 8), (byte)regCount });
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] data = new byte[length];
            Buffer.BlockCopy(r.Content, 2, data, 0, Math.Min(length, r.Content.Length - 2));
            return OperateResult<byte[]>.Success(data);
        }

        public override async Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            ushort regCount = (ushort)((length + 1) / 2);
            var result = await SendRtuPduAsync(new byte[] { fc, (byte)(addr >> 8), (byte)addr, (byte)(regCount >> 8), (byte)regCount }, CancellationToken.None).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);
            byte[] data = new byte[length];
            Buffer.BlockCopy(result.Content, 2, data, 0, Math.Min(length, result.Content.Length - 2));
            return OperateResult<byte[]>.Success(data);
        }

        // ═══════════════════════════════════════════
        //  写入实现 (FC05, FC06, FC15, FC16, FC23)
        // ═══════════════════════════════════════════

        public override OperateResult Write(string address, bool value)
        {
            ushort addr = ParseAddress(address);
            var result = SendRtuPdu(new byte[] { 0x05, (byte)(addr >> 8), (byte)addr, (byte)(value ? 0xFF : 0x00), 0x00 });
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override async Task<OperateResult> WriteAsync(string address, bool value)
        {
            ushort addr = ParseAddress(address);
            var result = await SendRtuPduAsync(new byte[] { 0x05, (byte)(addr >> 8), (byte)addr, (byte)(value ? 0xFF : 0x00), 0x00 }, CancellationToken.None).ConfigureAwait(false);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override OperateResult Write(string address, short value)
        {
            ushort addr = ParseAddress(address);
            var vb = GetBytesOrdered(value);
            var result = SendRtuPdu(new byte[] { 0x06, (byte)(addr >> 8), (byte)addr, vb[0], vb[1] });
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override async Task<OperateResult> WriteAsync(string address, short value)
        {
            ushort addr = ParseAddress(address);
            var vb = GetBytesOrdered(value);
            var result = await SendRtuPduAsync(new byte[] { 0x06, (byte)(addr >> 8), (byte)addr, vb[0], vb[1] }, CancellationToken.None).ConfigureAwait(false);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public Task<OperateResult> WriteAsync(string address, ushort value) => WriteAsync(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegisters(addr, 2, GetBytesOrdered(value));
        }

        public override Task<OperateResult> WriteAsync(string address, int value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegistersAsync(addr, 2, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public Task<OperateResult> WriteAsync(string address, uint value) => WriteAsync(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, (int)value);
        public Task<OperateResult> WriteAsync(string address, long value) => WriteAsync(address, (int)value);
        public override OperateResult Write(string address, ulong value) => Write(address, (int)value);
        public Task<OperateResult> WriteAsync(string address, ulong value) => WriteAsync(address, (int)value);

        public override OperateResult Write(string address, float value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegisters(addr, 2, GetBytesOrdered(value));
        }

        public override Task<OperateResult> WriteAsync(string address, float value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegistersAsync(addr, 2, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, double value) => Write(address, (float)value);
        public Task<OperateResult> WriteAsync(string address, double value) => WriteAsync(address, (float)value);

        public override OperateResult Write(string address, string value)
        {
            ushort addr = ParseAddress(address);
            var data = DataConverter.GetBytes(value);
            ushort regCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteMultipleRegisters(addr, regCount, data);
        }

        public override Task<OperateResult> WriteAsync(string address, string value)
        {
            ushort addr = ParseAddress(address);
            var data = DataConverter.GetBytes(value);
            ushort regCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteMultipleRegistersAsync(addr, regCount, data);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            ushort addr = ParseAddress(address);
            ushort regCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteMultipleRegisters(addr, regCount, data);
        }

        public override Task<OperateResult> WriteAsync(string address, byte[] data)
        {
            ushort addr = ParseAddress(address);
            ushort regCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteMultipleRegistersAsync(addr, regCount, data);
        }

        private OperateResult WriteMultipleRegisters(ushort address, ushort count, byte[] registerData)
        {
            byte byteCount = (byte)(count * 2);
            byte[] pdu = new byte[6 + byteCount];
            pdu[0] = 0x10;
            pdu[1] = (byte)(address >> 8); pdu[2] = (byte)address;
            pdu[3] = (byte)(count >> 8); pdu[4] = (byte)count;
            pdu[5] = byteCount;
            Buffer.BlockCopy(registerData, 0, pdu, 6, byteCount);

            var result = SendRtuPdu(pdu);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        private async Task<OperateResult> WriteMultipleRegistersAsync(ushort address, ushort count, byte[] registerData)
        {
            byte byteCount = (byte)(count * 2);
            byte[] pdu = new byte[6 + byteCount];
            pdu[0] = 0x10;
            pdu[1] = (byte)(address >> 8); pdu[2] = (byte)address;
            pdu[3] = (byte)(count >> 8); pdu[4] = (byte)count;
            pdu[5] = byteCount;
            Buffer.BlockCopy(registerData, 0, pdu, 6, byteCount);

            var result = await SendRtuPduAsync(pdu, CancellationToken.None).ConfigureAwait(false);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

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

            var result = SendRtuPdu(pdu);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public OperateResult<byte[]> ReadWriteMultipleRegisters(ushort readAddress, ushort readCount, ushort writeAddress, byte[] writeData)
        {
            ushort writeRegCount = (ushort)(writeData.Length / 2);
            byte writeByteCount = (byte)writeData.Length;
            byte[] pdu = new byte[10 + writeByteCount];
            pdu[0] = 0x17;
            pdu[1] = (byte)(readAddress >> 8); pdu[2] = (byte)readAddress;
            pdu[3] = (byte)(readCount >> 8); pdu[4] = (byte)readCount;
            pdu[5] = (byte)(writeAddress >> 8); pdu[6] = (byte)writeAddress;
            pdu[7] = (byte)(writeRegCount >> 8); pdu[8] = (byte)writeRegCount;
            pdu[9] = writeByteCount;
            Buffer.BlockCopy(writeData, 0, pdu, 10, writeByteCount);

            var result = SendRtuPdu(pdu);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            byte byteCount = result.Content[1];
            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(result.Content, 2, data, 0, byteCount);
            return OperateResult<byte[]>.Success(data);
        }

        // ═══════════════════════════════════════════
        //  便利方法 (CRC, 自定义命令, 批量读取, 编码字符串)
        // ═══════════════════════════════════════════

        /// <summary>计算 CRC16-Modbus 校验和（委托到 CrcCalculator）。</summary>
        public static ushort Crc16(byte[] data, int offset, int length) => CrcCalculator.ComputeCrc16(data, offset, length);

        /// <summary>计算 CRC16-Modbus 校验和（委托到 CrcCalculator）。</summary>
        public static ushort Crc16(byte[] data) => CrcCalculator.ComputeCrc16(data);

        /// <summary>
        /// 发送自定义 Modbus PDU（自动添加站号和 CRC）。
        /// </summary>
        public OperateResult<byte[]> SendCustomModbus(byte[] customPdu)
        {
            // 自定义 FC 的响应格式未知，假设响应长度 ≈ 请求长度 (RTU 帧去掉 header 3 字节 = PDU 长度)
            _overridePayloadLength = customPdu.Length;
            try { return SendRtuPdu(customPdu); }
            finally { _overridePayloadLength = null; }
        }

        /// <summary>
        /// 批量读取线圈/离散输入 (FC01/FC02)，返回 bool 数组。
        /// </summary>
        public OperateResult<bool[]> ReadBools(string address, int count)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x02 ? (byte)0x02 : (byte)0x01;
            var r = SendRtuPdu(new byte[] { fc, (byte)(addr >> 8), (byte)addr, (byte)(count >> 8), (byte)(count & 0xFF) });
            if (!r.IsSuccess) return OperateResult<bool[]>.Failed(r.Message, r.ErrorCode);
            int byteCount = r.Content[1];
            bool[] result = new bool[count];
            for (int i = 0; i < count && (2 + i / 8) < r.Content.Length; i++)
                result[i] = (r.Content[2 + i / 8] & (1 << (i % 8))) != 0;
            return OperateResult<bool[]>.Success(result);
        }

        /// <summary>
        /// 使用 StringEncodingOption 指定的编码读取字符串。
        /// </summary>
        public OperateResult<string> ReadStringEncoded(string address, ushort length)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            ushort regCount = (ushort)((length + 1) / 2);
            var r = SendRtuPdu(new byte[] { fc, (byte)(addr >> 8), (byte)addr, (byte)(regCount >> 8), (byte)regCount });
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            var encoding = StringEncodingOption == StringEncoding.Utf8 ? Encoding.UTF8 : Encoding.ASCII;
            return OperateResult<string>.Success(encoding.GetString(r.Content, 2, Math.Min(length, r.Content.Length - 2)).TrimEnd('\0'));
        }

        /// <summary>
        /// 使用 StringEncodingOption 指定的编码写入字符串。
        /// </summary>
        public OperateResult WriteStringEncoded(string address, string value)
        {
            ushort addr = ParseAddress(address);
            var encoding = StringEncodingOption == StringEncoding.Utf8 ? Encoding.UTF8 : Encoding.ASCII;
            byte[] data = encoding.GetBytes(value);
            ushort regCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteMultipleRegisters(addr, regCount, data);
        }
    }
}
