using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Modbus
{
    /// <summary>
    /// Modbus TCP 客户端 — 完整实现。
    /// 支持功能码 01-06, 15, 16, 23。
    /// 支持地址前缀（0x/1x/3x/4x）、字节序选项、批量读写、报文日志、连接事件。
    /// </summary>
    public class ModbusTcpClient : TcpDeviceBase
    {
        public byte Station { get; set; }
        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;
        private int _transactionId;

        public ModbusTcpClient(string ip, int port = 502, byte station = 1, int timeout = 5000)
            : base(ip, port, timeout)
        {
            Station = station;
        }

        // ── Modbus ADU 帧结构 ─────────────────────
        // MBAP Header (7 bytes): TransactionId(2) + ProtocolId(2) + Length(2) + UnitId(1)
        // PDU: FunctionCode(1) + Data(...)

        protected override int ResponseHeaderLength => 7;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            int length = (header[4] << 8) | header[5];
            return length - 1;
        }

        private ushort NextTid() => (ushort)(Interlocked.Increment(ref _transactionId) & 0xFFFF);

        // ── 报文构建 ──────────────────────────────

        private byte[] BuildMbap(byte[] pdu)
        {
            ushort tid = NextTid();
            int pduLen = pdu.Length;
            int totalLen = pduLen + 1; // +1 for UnitId
            byte[] frame = new byte[7 + pduLen];
            frame[0] = (byte)(tid >> 8); frame[1] = (byte)tid;
            frame[2] = 0; frame[3] = 0; // ProtocolId
            frame[4] = (byte)(totalLen >> 8); frame[5] = (byte)totalLen;
            frame[6] = Station;
            Buffer.BlockCopy(pdu, 0, frame, 7, pduLen);
            return frame;
        }

        // ── 地址解析（支持前缀模式）───────────────

        /// <summary>
        /// 解析地址并返回对应的功能码。
        /// 前缀规则：0xxxx=线圈(FC01), 1xxxx=离散输入(FC02), 3xxxx=输入寄存器(FC04), 4xxxx=保持寄存器(FC03)。
        /// 无前缀时默认为保持寄存器。
        /// </summary>
        private static (ushort address, byte readFc, byte writeFc) ParseAddressEx(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空");

            address = address.Trim();

            // 检查前缀（至少5位数字才算前缀模式：0xxxx, 1xxxx, 3xxxx, 4xxxx）
            char prefix = address[0];
            string numPart = address.Substring(1);

            if (address.Length >= 5 && char.IsDigit(prefix))
            {
                if (prefix == '0')
                    return (ParseUshort(numPart), 0x01, 0x05);       // 0xxxx 线圈
                if (prefix == '1')
                    return (ParseUshort(numPart), 0x02, (byte)0);    // 1xxxx 离散输入
                if (prefix == '3')
                    return (ParseUshort(numPart), 0x04, (byte)0);    // 3xxxx 输入寄存器
                if (prefix == '4')
                    return (ParseUshort(numPart), 0x03, 0x06);       // 4xxxx 保持寄存器
            }

            // 无前缀或短地址 — 默认保持寄存器
            return (ParseUshort(address), 0x03, 0x06);
        }

        private static ushort ParseUshort(string s) => ushort.Parse(s.TrimStart('0').Length == 0 ? "0" : s.TrimStart('0'));
        private static ushort ParseAddress(string address) => ParseAddressEx(address).address;

        // ── 响应检查 ──────────────────────────────

        private static OperateResult CheckResponse(byte[] response)
        {
            if (response == null || response.Length < 9)
                return OperateResult.Failed("响应报文长度不足");

            byte funcCode = response[7];
            if ((funcCode & 0x80) != 0)
            {
                byte exCode = response.Length > 8 ? response[8] : (byte)0;
                string msg = exCode switch
                {
                    1 => "非法功能码",
                    2 => "非法数据地址",
                    3 => "非法数据值",
                    4 => "从站设备故障",
                    _ => $"Modbus异常码: {exCode}"
                };
                return OperateResult.Failed(msg, exCode);
            }
            return OperateResult.Success();
        }

        // ── 字节序数据转换 ────────────────────────

        private short ToInt16Ordered(byte[] data, int offset) => ByteOrder switch
        {
            Endianness.LittleEndian => (short)(data[offset] | (data[offset + 1] << 8)),
            _ => DataConverter.ToInt16(data, offset) // BigEndian (ABCD) default
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
        //  FC01 — 读线圈 (Read Coils)
        //  FC02 — 读离散输入 (Read Discrete Inputs)
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x02 ? (byte)0x02 : (byte)0x01;
            var req = BuildMbap(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 1 });
            var result = SendAndReceive(req);
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message, result.ErrorCode);
            var check = CheckResponse(result.Content);
            if (!check.IsSuccess) return OperateResult<bool>.Failed(check.Message, check.ErrorCode);
            return OperateResult<bool>.Success((result.Content[9] & 0x01) != 0);
        }

        /// <summary>读取多个线圈/离散输入。</summary>
        public OperateResult<bool[]> ReadBools(string address, ushort count)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x02 ? (byte)0x02 : (byte)0x01;
            var req = BuildMbap(new byte[] { fc, (byte)(addr >> 8), (byte)addr, (byte)(count >> 8), (byte)count });
            var result = SendAndReceive(req);
            if (!result.IsSuccess) return OperateResult<bool[]>.Failed(result.Message, result.ErrorCode);
            var check = CheckResponse(result.Content);
            if (!check.IsSuccess) return OperateResult<bool[]>.Failed(check.Message, check.ErrorCode);

            byte byteCount = result.Content[8];
            bool[] values = new bool[count];
            for (int i = 0; i < count; i++)
                values[i] = (result.Content[9 + i / 8] & (1 << (i % 8))) != 0;
            return OperateResult<bool[]>.Success(values);
        }

        // ═══════════════════════════════════════════
        //  FC03 — 读保持寄存器 (Read Holding Registers)
        //  FC04 — 读输入寄存器 (Read Input Registers)
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> ReadRegistersCore(ushort address, ushort count, byte fc)
        {
            var req = BuildMbap(new byte[] { fc, (byte)(address >> 8), (byte)address, (byte)(count >> 8), (byte)count });
            var result = SendAndReceive(req);
            if (!result.IsSuccess) return result;
            var check = CheckResponse(result.Content);
            if (!check.IsSuccess) return OperateResult<byte[]>.Failed(check.Message, check.ErrorCode);
            byte byteCount = result.Content[8];
            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(result.Content, 9, data, 0, byteCount);
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
            // Use big-endian for 64-bit (two 32-bit values)
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0));
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
            return OperateResult<double>.Success(DataConverter.ToDouble(r.Content, 0));
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
            var pdu = new byte[] { 0x05, (byte)(addr >> 8), (byte)addr, (byte)(value ? 0xFF : 0x00), 0x00 };
            var result = SendAndReceive(BuildMbap(pdu));
            if (!result.IsSuccess) return result;
            return CheckResponse(result.Content);
        }

        // ═══════════════════════════════════════════
        //  FC06 — 写单寄存器 (Write Single Register)
        // ═══════════════════════════════════════════

        public override OperateResult Write(string address, short value)
        {
            ushort addr = ParseAddress(address);
            var vb = GetBytesOrdered(value);
            var pdu = new byte[] { 0x06, (byte)(addr >> 8), (byte)addr, vb[0], vb[1] };
            var result = SendAndReceive(BuildMbap(pdu));
            if (!result.IsSuccess) return result;
            return CheckResponse(result.Content);
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

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

            byte[] pdu = new byte[5 + 1 + byteCount];
            pdu[0] = 0x0F;
            pdu[1] = (byte)(startAddress >> 8); pdu[2] = (byte)startAddress;
            pdu[3] = (byte)(values.Length >> 8); pdu[4] = (byte)values.Length;
            pdu[5] = (byte)byteCount;
            Buffer.BlockCopy(coilBytes, 0, pdu, 6, byteCount);

            var result = SendAndReceive(BuildMbap(pdu));
            if (!result.IsSuccess) return result;
            return CheckResponse(result.Content);
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

            var result = SendAndReceive(BuildMbap(pdu));
            if (!result.IsSuccess) return result;
            return CheckResponse(result.Content);
        }

        public override OperateResult Write(string address, int value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegisters(addr, 2, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, (int)value);
        public override OperateResult Write(string address, ulong value) => Write(address, (int)value);

        public override OperateResult Write(string address, float value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegisters(addr, 2, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, double value) => Write(address, (float)value);

        public override OperateResult Write(string address, string value)
        {
            ushort addr = ParseAddress(address);
            var data = DataConverter.GetBytes(value);
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
        //  FC23 — 读写多个寄存器 (Read/Write Multiple, 原子操作)
        // ═══════════════════════════════════════════

        /// <summary>原子操作：同时写入并读取寄存器（FC23）。</summary>
        public OperateResult<byte[]> ReadWriteMultipleRegisters(
            ushort readAddress, ushort readCount,
            ushort writeAddress, byte[] writeData)
        {
            ushort writeRegCount = (ushort)(writeData.Length / 2);
            byte writeByteCount = (byte)writeData.Length;
            byte[] pdu = new byte[9 + 1 + writeByteCount];
            pdu[0] = 0x17; // FC23
            pdu[1] = (byte)(readAddress >> 8); pdu[2] = (byte)readAddress;
            pdu[3] = (byte)(readCount >> 8); pdu[4] = (byte)readCount;
            pdu[5] = (byte)(writeAddress >> 8); pdu[6] = (byte)writeAddress;
            pdu[7] = (byte)(writeRegCount >> 8); pdu[8] = (byte)writeRegCount;
            pdu[9] = writeByteCount;
            Buffer.BlockCopy(writeData, 0, pdu, 10, writeByteCount);

            var result = SendAndReceive(BuildMbap(pdu));
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);
            var check = CheckResponse(result.Content);
            if (!check.IsSuccess) return OperateResult<byte[]>.Failed(check.Message, check.ErrorCode);

            byte byteCount = result.Content[8];
            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(result.Content, 9, data, 0, byteCount);
            return OperateResult<byte[]>.Success(data);
        }

        // ═══════════════════════════════════════════
        //  批量读取 — 一次请求读取多个不连续地址
        // ═══════════════════════════════════════════

        /// <summary>
        /// 批量读取保持寄存器（连续地址范围）。
        /// 返回从 startAddress 开始 count 个寄存器的原始字节数据。
        /// </summary>
        public OperateResult<byte[]> ReadRegistersBatch(ushort startAddress, ushort count)
        {
            return ReadRegistersCore(startAddress, count, 0x03);
        }

        /// <summary>
        /// 批量读取线圈（连续地址范围）。
        /// </summary>
        public OperateResult<bool[]> ReadCoilsBatch(ushort startAddress, ushort count)
        {
            return ReadBools("0" + startAddress, count);
        }
    }
}
