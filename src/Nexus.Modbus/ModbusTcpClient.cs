using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Modbus
{
    /// <summary>
    /// Modbus TCP 客户端 — 完整实现。
    /// 支持功能码 01-06, 15, 16, 23。
    /// 支持地址前缀（0x/1x/3x/4x）、字节序选项、批量读写、报文日志、连接事件。
    /// </summary>
    public class ModbusTcpClient : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
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
                // 标准 Modbus 约定：5位前缀地址 (如 40001) 是 1-based，内部使用 0-based，所以需要减 1
                ushort addr = ParseUshort(numPart);
                if (addr > 0) addr -= 1;

                if (prefix == '0')
                    return (addr, 0x01, 0x05);       // 0xxxx 线圈
                if (prefix == '1')
                    return (addr, 0x02, (byte)0);    // 1xxxx 离散输入
                if (prefix == '3')
                    return (addr, 0x04, (byte)0);    // 3xxxx 输入寄存器
                if (prefix == '4')
                    return (addr, 0x03, 0x06);       // 4xxxx 保持寄存器
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

        /// <summary>写入多个线圈值（高层 API）。</summary>
        public OperateResult Write(string address, bool[] values)
        {
            var (addr, _, _) = ParseAddressEx(address);
            return WriteMultipleCoils(addr, values);
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

        // ═══════════════════════════════════════════
        //  连接自动重试
        // ═══════════════════════════════════════════

        /// <summary>连接重试次数（默认 3 次）。</summary>
        public new int RetryCount { get; set; } = 3;

        /// <summary>重试间隔（毫秒，默认 1000ms）。</summary>
        public new int RetryInterval { get; set; } = 1000;

        /// <summary>带重试的连接。</summary>
        public new OperateResult Connect()
        {
            OperateResult? lastResult = null;
            for (int i = 0; i <= RetryCount; i++)
            {
                if (i > 0)
                {
                    Log.Info($"连接重试 ({i}/{RetryCount})，等待 {RetryInterval}ms...");
                    System.Threading.Thread.Sleep(RetryInterval);
                }
                lastResult = base.Connect();
                if (lastResult.IsSuccess) return lastResult;
            }
            return lastResult ?? OperateResult.Failed("连接失败");
        }

        /// <summary>带重试的异步连接。</summary>
        public new async Task<OperateResult> ConnectAsync()
        {
            OperateResult? lastResult = null;
            for (int i = 0; i <= RetryCount; i++)
            {
                if (i > 0)
                {
                    Log.Info($"连接重试 ({i}/{RetryCount})，等待 {RetryInterval}ms...");
                    await Task.Delay(RetryInterval).ConfigureAwait(false);
                }
                lastResult = await base.ConnectAsync().ConfigureAwait(false);
                if (lastResult.IsSuccess) return lastResult;
            }
            return lastResult ?? OperateResult.Failed("连接失败");
        }

        // ═══════════════════════════════════════════
        //  自定义功能码发送
        // ═══════════════════════════════════════════

        /// <summary>
        /// 发送自定义 Modbus PDU（自动添加 MBAP 头和站号）。
        /// 返回响应 PDU（去掉 MBAP 头）。
        /// </summary>
        public OperateResult<byte[]> SendCustomModbus(byte[] pdu)
        {
            var result = SendAndReceive(BuildMbap(pdu));
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);
            var check = CheckResponse(result.Content);
            if (!check.IsSuccess) return OperateResult<byte[]>.Failed(check.Message, check.ErrorCode);
            // 返回去掉 MBAP 头(7 bytes) 的 PDU
            byte[] respPdu = new byte[result.Content.Length - 7];
            Buffer.BlockCopy(result.Content, 7, respPdu, 0, respPdu.Length);
            return OperateResult<byte[]>.Success(respPdu);
        }

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
        // ═══════════════════════════════════════════

        /// <summary>批量读取多个地址的值。</summary>
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, object?>();
            var addrList = addresses.ToList();

            // 按区域分组读取
            var groups = addrList.GroupBy(a =>
            {
                var parsed = new ModbusAddressParser().Parse(a);
                return (parsed.Area, parsed.ReadFunctionCode);
            });

            foreach (var group in groups)
            {
                var sorted = group.Select(a => new ModbusAddressParser().Parse(a))
                                  .OrderBy(a => a.StartAddress).ToList();

                // 连续地址合并为一次读取
                ushort minAddr = sorted.Min(a => a.StartAddress);
                ushort maxAddr = sorted.Max(a => a.StartAddress);
                ushort range = (ushort)(maxAddr - minAddr + 2); // +2 for 32-bit max

                if (group.Key.Area == ModbusArea.Coil || group.Key.Area == ModbusArea.DiscreteInput)
                {
                    // 线圈/离散输入 — 按 bool 批量读取
                    var bools = ReadBools("0" + minAddr, (ushort)(maxAddr - minAddr + 1));
                    if (!bools.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(bools.Message, bools.ErrorCode);
                    foreach (var addr in addrList.Where(a =>
                    {
                        var p = new ModbusAddressParser().Parse(a);
                        return p.Area == group.Key.Area;
                    }))
                    {
                        var p = new ModbusAddressParser().Parse(addr);
                        int idx = p.StartAddress - minAddr;
                        if (idx >= 0 && idx < bools.Content.Length)
                            result[addr] = bools.Content[idx];
                    }
                }
                else
                {
                    // 寄存器 — 按原始字节批量读取
                    ushort regCount = (ushort)(range);
                    var raw = ReadRegistersCore(minAddr, regCount, group.Key.ReadFunctionCode);
                    if (!raw.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(raw.Message, raw.ErrorCode);

                    foreach (var addr in addrList.Where(a =>
                    {
                        var p = new ModbusAddressParser().Parse(a);
                        return p.Area == group.Key.Area;
                    }))
                    {
                        var p = new ModbusAddressParser().Parse(addr);
                        int byteOffset = (p.StartAddress - minAddr) * 2;
                        if (byteOffset >= 0 && byteOffset + 2 <= raw.Content.Length)
                            result[addr] = ToInt16Ordered(raw.Content, byteOffset);
                    }
                }
            }

            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        /// <summary>批量读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchRead(addresses), cancellationToken);

        /// <summary>随机读取多个不连续地址（返回原始字节）。</summary>
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, byte[]>();
            var parser = new ModbusAddressParser();

            foreach (var addrStr in addresses)
            {
                var parsed = parser.Parse(addrStr);
                ushort regCount = 1; // 每个地址读1个寄存器(2字节)
                var raw = ReadRegistersCore(parsed.StartAddress, regCount, parsed.ReadFunctionCode);
                if (!raw.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(raw.Message, raw.ErrorCode);
                result[addrStr] = raw.Content;
            }

            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        /// <summary>随机读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => RandomRead(addresses), cancellationToken);

        /// <summary>批量写入多个地址的值。</summary>
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

        /// <summary>批量写入（异步）。</summary>
        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchWrite(items), cancellationToken);

        // ═══════════════════════════════════════════
        //  真正的异步路径
        // ═══════════════════════════════════════════

        /// <summary>异步读保持寄存器 (FC03) — 真正的 async，支持取消。</summary>
        public async Task<OperateResult<short>> ReadInt16TrueAsync(string address, CancellationToken ct = default)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var req = BuildMbap(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 1 });
            var result = await SendAndReceiveAsync(req, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<short>.Failed(result.Message, result.ErrorCode);
            var check = CheckResponse(result.Content);
            if (!check.IsSuccess) return OperateResult<short>.Failed(check.Message, check.ErrorCode);
            return OperateResult<short>.Success(ToInt16Ordered(result.Content, 9));
        }

        /// <summary>异步读线圈 (FC01) — 真正的 async。</summary>
        public async Task<OperateResult<bool>> ReadBoolTrueAsync(string address, CancellationToken ct = default)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x02 ? (byte)0x02 : (byte)0x01;
            var req = BuildMbap(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 1 });
            var result = await SendAndReceiveAsync(req, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message, result.ErrorCode);
            var check = CheckResponse(result.Content);
            if (!check.IsSuccess) return OperateResult<bool>.Failed(check.Message, check.ErrorCode);
            return OperateResult<bool>.Success((result.Content[9] & 0x01) != 0);
        }

        /// <summary>异步写单寄存器 (FC06) — 真正的 async。</summary>
        public async Task<OperateResult> WriteTrueAsync(string address, short value, CancellationToken ct = default)
        {
            ushort addr = ParseAddress(address);
            var vb = GetBytesOrdered(value);
            var pdu = new byte[] { 0x06, (byte)(addr >> 8), (byte)addr, vb[0], vb[1] };
            var result = await SendAndReceiveAsync(BuildMbap(pdu), ct).ConfigureAwait(false);
            if (!result.IsSuccess) return result;
            return CheckResponse(result.Content);
        }

        // ═══════════════════════════════════════════
        //  字符串编码选项
        // ═══════════════════════════════════════════

        /// <summary>字符串编码选项。</summary>
        public StringEncoding StringEncodingOption { get; set; } = StringEncoding.Ascii;

        /// <summary>使用配置的编码读取字符串。</summary>
        public OperateResult<string> ReadStringEncoded(string address, ushort length)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            ushort byteCount = length;
            ushort regCount = (ushort)((byteCount + 1) / 2);
            var r = ReadRegistersCore(addr, regCount, fc);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);

            string text = StringEncodingOption switch
            {
                StringEncoding.Utf8 => System.Text.Encoding.UTF8.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0', ' '),
                StringEncoding.Unicode => System.Text.Encoding.Unicode.GetString(r.Content, 0, Math.Min(length * 2, r.Content.Length)).TrimEnd('\0', ' '),
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
                StringEncoding.Utf8 => System.Text.Encoding.UTF8.GetBytes(value),
                StringEncoding.Unicode => System.Text.Encoding.Unicode.GetBytes(value),
                _ => DataConverter.GetBytes(value)
            };
            ushort regCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteMultipleRegisters(addr, regCount, data);
        }

        // ═══════════════════════════════════════════
        //  数据变化订阅/监控引擎
        // ═══════════════════════════════════════════

        /// <summary>数据变化事件。</summary>
        public event EventHandler<Nexus.DataChangeEventArgs>? OnDataChanged;

        private Timer? _monitorTimer;
        private readonly object _monitorLock = new object();
        private readonly Dictionary<string, MonitorEntry> _monitors = new Dictionary<string, MonitorEntry>();
        private volatile bool _monitoring;

        /// <summary>订阅指定地址的数据变化 (ISubscribeDevice)。</summary>
        public void Subscribe(string address, int intervalMs = 1000, string dataType = "Int16")
            => AddMonitor(address, intervalMs, dataType);

        /// <summary>取消订阅 (ISubscribeDevice)。</summary>
        public void Unsubscribe(string address)
            => RemoveMonitor(address);

        /// <summary>启动所有订阅 (ISubscribeDevice)。</summary>
        public void StartSubscriptions(int globalIntervalMs = 500)
            => StartMonitoring(globalIntervalMs);

        /// <summary>停止所有订阅 (ISubscribeDevice)。</summary>
        public void StopSubscriptions()
            => StopMonitoring();

        /// <summary>
        /// 添加监控地址（指定轮询间隔）。
        /// </summary>
        public void AddMonitor(string address, int intervalMs = 1000, string dataType = "Int16")
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

        /// <summary>移除监控地址。</summary>
        public void RemoveMonitor(string address)
        {
            lock (_monitorLock)
            {
                _monitors.Remove(address);
            }
        }

        /// <summary>启动监控。</summary>
        public void StartMonitoring(int pollIntervalMs = 500)
        {
            if (_monitoring) return;
            _monitoring = true;
            _monitorTimer = new Timer(PollMonitors, null, pollIntervalMs, pollIntervalMs);
        }

        /// <summary>停止监控。</summary>
        public void StopMonitoring()
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
                    if (!IsConnected) continue;
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
                            // 首次轮询只初始化基线，不触发变化事件
                            if (entry.LastValue == null)
                            {
                                entry.LastValue = current;
                                continue;
                            }
                            var args = new Nexus.DataChangeEventArgs
                            {
                                Address = entry.Address,
                                OldValue = entry.LastValue,
                                NewValue = current,
                                Timestamp = DateTime.Now
                            };
                            entry.LastValue = current;
                            OnDataChanged?.Invoke(this, args);
                        }
                    }
                    catch { /* 忽略单次轮询异常 */ }
                }
            }
            catch { /* 忽略轮询整体异常 */ }
        }

        private class MonitorEntry
        {
            public string Address { get; set; } = "";
            public string DataType { get; set; } = "Int16";
            public int IntervalMs { get; set; }
            public object? LastValue { get; set; }
        }
    }

    // ═══════════════════════════════════════════
    //  公共类型
    // ═══════════════════════════════════════════

    /// <summary>字符串编码选项。</summary>
    public enum StringEncoding
    {
        /// <summary>ASCII 编码（默认）</summary>
        Ascii = 0,
        /// <summary>UTF-8 编码</summary>
        Utf8 = 1,
        /// <summary>Unicode (UTF-16) 编码</summary>
        Unicode = 2,
    }
}