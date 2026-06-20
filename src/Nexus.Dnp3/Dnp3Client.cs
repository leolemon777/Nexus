using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Dnp3
{
    /// <summary>
    /// DNP3 TCP 客户端 — 支持 IEEE 1815 DNP3 协议。
    /// <para>用于电力 SCADA 系统与 RTU/IED 的通信。</para>
    /// <para>支持 Read/Write/DirectOperate/Freeze 等功能码。</para>
    /// </summary>
    public class Dnp3Client : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        /// <summary>主站地址。</summary>
        public ushort MasterAddress { get; set; } = Dnp3Constants.DefaultMasterAddress;
        /// <summary>从站地址。</summary>
        public ushort OutstationAddress { get; set; } = Dnp3Constants.DefaultOutstationAddress;
        /// <summary>确认超时（毫秒）。</summary>
        public int ConfirmTimeout { get; set; } = Dnp3Constants.DefaultConfirmTimeout;

        private byte _appSequence;
        private byte _transportSequence;

        /// <inheritdoc/>
        protected override int ResponseHeaderLength => 10;

        /// <inheritdoc/>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 10) return 0;
            // 数据链路层帧头: Start(2) + Length(1) + Control(1) + Dest(2) + Src(2) + CRC(2)
            int length = header[2];
            return length - 8; // 减去帧头已读的部分
        }

        public Dnp3Client(string ip, int port = 20000, int timeout = 5000)
            : base(ip, port, timeout)
        {
        }

        // ═══════════════════════════════════════════
        //  数据链路层帧构建
        // ═══════════════════════════════════════════

        /// <summary>构建数据链路层帧头。</summary>
        public static byte[] BuildLinkHeader(ushort destination, ushort source, byte functionCode, byte[] userData)
        {
            byte dataLength = (byte)(userData?.Length ?? 0);
            byte totalLength = (byte)(5 + dataLength); // Control + Dest + Src + Data

            byte[] frame = new byte[10 + dataLength];
            frame[0] = Dnp3Constants.StartByte1;
            frame[1] = Dnp3Constants.StartByte2;
            frame[2] = (byte)(totalLength + 5); // Length field
            frame[3] = functionCode;            // Control (master=0xC4 for request)
            frame[4] = (byte)(destination & 0xFF);
            frame[5] = (byte)((destination >> 8) & 0xFF);
            frame[6] = (byte)(source & 0xFF);
            frame[7] = (byte)((source >> 8) & 0xFF);

            // CRC-16/DNP3 for header (bytes 0-7)
            ushort headerCrc = CalculateDnp3Crc(frame, 0, 8);
            frame[8] = (byte)(headerCrc & 0xFF);
            frame[9] = (byte)((headerCrc >> 8) & 0xFF);

            if (userData != null && userData.Length > 0)
            {
                Buffer.BlockCopy(userData, 0, frame, 10, userData.Length);
                // CRC-16/DNP3 for user data block
                ushort dataCrc = CalculateDnp3Crc(frame, 10, userData.Length);
                int crcOffset = 10 + userData.Length;
                // Reallocate if needed to append data CRC
                byte[] fullFrame = new byte[crcOffset + 2];
                Buffer.BlockCopy(frame, 0, fullFrame, 0, crcOffset);
                fullFrame[crcOffset] = (byte)(dataCrc & 0xFF);
                fullFrame[crcOffset + 1] = (byte)((dataCrc >> 8) & 0xFF);
                return fullFrame;
            }

            return frame;
        }

        /// <summary>计算 CRC-16/DNP3（多项式 0xA6BC，初始值 0x0000，输入反转，输出反转）。</summary>
        public static ushort CalculateDnp3Crc(byte[] data, int offset, int length)
        {
            ushort crc = 0x0000;
            for (int i = offset; i < offset + length; i++)
            {
                crc = (ushort)(crc ^ data[i]);
                for (int j = 0; j < 8; j++)
                    crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA6BC : crc >> 1);
            }
            return crc;
        }

        // ═══════════════════════════════════════════
        //  应用层帧构建
        // ═══════════════════════════════════════════

        /// <summary>构建 Read 请求。</summary>
        public static byte[] BuildReadRequest(byte sequence, Dnp3Group group, Dnp3Variation variation, ushort start, ushort stop)
        {
            // Application Control(1) + FC(1) + IIN(2) + ObjectHeader(3) + StartStop(4)
            byte[] pdu = new byte[12];
            pdu[0] = 0x01; // AC: FIR=1, FIN=1, Sequence=0
            pdu[0] = (byte)(0xC0 | (sequence & 0x0F)); // FIR|FIN|SEQ
            pdu[1] = (byte)Dnp3FunctionCode.Read;
            pdu[2] = (byte)group;
            pdu[3] = (byte)variation;
            pdu[4] = 0x00; // Qualifier: 1-byte start/stop
            pdu[5] = (byte)(start & 0xFF);
            pdu[6] = (byte)((start >> 8) & 0xFF);
            pdu[7] = 0x01; // Qualifier: 1-byte count
            pdu[8] = (byte)(stop & 0xFF);
            pdu[9] = (byte)((stop >> 8) & 0xFF);
            return pdu;
        }

        /// <summary>构建 DirectOperate 请求。</summary>
        public static byte[] BuildDirectOperateRequest(byte sequence, Dnp3Group group, Dnp3Variation variation, ushort index, byte[] data)
        {
            byte[] pdu = new byte[8 + data.Length];
            pdu[0] = (byte)(0xC0 | (sequence & 0x0F));
            pdu[1] = (byte)Dnp3FunctionCode.DirectOperate;
            pdu[2] = (byte)group;
            pdu[3] = (byte)variation;
            pdu[4] = 0x17; // Qualifier: 1-byte index
            pdu[5] = (byte)(index & 0xFF);
            pdu[6] = (byte)((index >> 8) & 0xFF);
            pdu[7] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, pdu, 8, data.Length);
            return pdu;
        }

        /// <summary>构建 Select（预操作）请求。</summary>
        public static byte[] BuildSelectRequest(byte sequence, Dnp3Group group, Dnp3Variation variation, ushort index, byte[] data)
        {
            byte[] pdu = new byte[8 + data.Length];
            pdu[0] = (byte)(0xC0 | (sequence & 0x0F));
            pdu[1] = (byte)Dnp3FunctionCode.Select;
            pdu[2] = (byte)group;
            pdu[3] = (byte)variation;
            pdu[4] = 0x17; // Qualifier: 1-byte index
            pdu[5] = (byte)(index & 0xFF);
            pdu[6] = (byte)((index >> 8) & 0xFF);
            pdu[7] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, pdu, 8, data.Length);
            return pdu;
        }

        /// <summary>构建 Operate（操作）请求。</summary>
        public static byte[] BuildOperateRequest(byte sequence, Dnp3Group group, Dnp3Variation variation, ushort index, byte[] data)
        {
            byte[] pdu = new byte[8 + data.Length];
            pdu[0] = (byte)(0xC0 | (sequence & 0x0F));
            pdu[1] = (byte)Dnp3FunctionCode.Operate;
            pdu[2] = (byte)group;
            pdu[3] = (byte)variation;
            pdu[4] = 0x17; // Qualifier: 1-byte index
            pdu[5] = (byte)(index & 0xFF);
            pdu[6] = (byte)((index >> 8) & 0xFF);
            pdu[7] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, pdu, 8, data.Length);
            return pdu;
        }

        /// <summary>构建 Write 请求（带对象头）。</summary>
        public static byte[] BuildWriteRequest(byte sequence, Dnp3Group group, Dnp3Variation variation, ushort index, byte[] data)
        {
            byte[] pdu = new byte[8 + data.Length];
            pdu[0] = (byte)(0xC0 | (sequence & 0x0F));
            pdu[1] = (byte)Dnp3FunctionCode.Write;
            pdu[2] = (byte)group;
            pdu[3] = (byte)variation;
            pdu[4] = 0x17; // Qualifier: 1-byte index
            pdu[5] = (byte)(index & 0xFF);
            pdu[6] = (byte)((index >> 8) & 0xFF);
            pdu[7] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, pdu, 8, data.Length);
            return pdu;
        }

        /// <summary>构建 ColdRestart 请求。</summary>
        public static byte[] BuildColdRestartRequest(byte sequence)
        {
            byte[] pdu = new byte[2];
            pdu[0] = (byte)(0xC0 | (sequence & 0x0F));
            pdu[1] = (byte)Dnp3FunctionCode.ColdRestart;
            return pdu;
        }

        /// <summary>构建 DelayMeasure 请求。</summary>
        public static byte[] BuildDelayMeasureRequest(byte sequence)
        {
            byte[] pdu = new byte[2];
            pdu[0] = (byte)(0xC0 | (sequence & 0x0F));
            pdu[1] = (byte)Dnp3FunctionCode.DelayMeasure;
            return pdu;
        }

        /// <summary>获取下一个传输层序列号（0-63 循环）。</summary>
        private byte NextTransportSequence()
        {
            return unchecked(++_transportSequence);
        }

        /// <summary>添加传输层头（FIN=1, FIR=1, 序列号）。</summary>
        public static byte[] WrapWithTransportHeader(byte transportSeq, byte[] appData)
        {
            byte[] result = new byte[1 + appData.Length];
            result[0] = (byte)(0xC0 | (transportSeq & 0x3F)); // FIR=1, FIN=1
            Buffer.BlockCopy(appData, 0, result, 1, appData.Length);
            return result;
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            try
            {
                byte seq = unchecked(++_appSequence);
                ushort stop = length > 0 ? (ushort)(length - 1) : (ushort)0;
                byte[] appPdu = BuildReadRequest(seq, Dnp3Group.AnalogInput, Dnp3Variation.AnalogInputFloat32, 0, stop);
                byte[] linkFrame = BuildLinkHeader(OutstationAddress, MasterAddress, 0xC4, appPdu);

                var result = SendAndReceive(linkFrame);
                if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);

                // 解析响应数据（简化版本）
                return ParseResponseData(result.Content);
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed(ex.Message);
            }
        }

        public override OperateResult Write(string address, byte[] data)
        {
            try
            {
                byte seq = unchecked(++_appSequence);
                byte[] appPdu = BuildDirectOperateRequest(seq, Dnp3Group.AnalogOutput, Dnp3Variation.AnalogInputFloat32, 0, data);
                byte[] linkFrame = BuildLinkHeader(OutstationAddress, MasterAddress, 0xC4, appPdu);

                var result = SendAndReceive(linkFrame);
                if (!result.IsSuccess) return result;

                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed(ex.Message);
            }
        }

        // ── 高层读取 ──

        /// <summary>读取模拟输入（浮点值）。</summary>
        public OperateResult<float[]> ReadAnalogInputs(ushort start, ushort count)
        {
            var result = ReadBytes($"AI{start}", count);
            if (!result.IsSuccess) return OperateResult<float[]>.Failed(result.Message);

            byte[] raw = result.Content;
            int floatCount = raw.Length / 4;
            var values = new float[floatCount];
            for (int i = 0; i < floatCount; i++)
                values[i] = BitConverter.ToSingle(raw, i * 4);

            return OperateResult<float[]>.Success(values);
        }

        /// <summary>读取二进制输入。</summary>
        public OperateResult<bool[]> ReadBinaryInputs(ushort start, ushort count)
        {
            try
            {
                byte seq = unchecked(++_appSequence);
                byte[] appPdu = BuildReadRequest(seq, Dnp3Group.BinaryInput, Dnp3Variation.BinaryInputPacked, start, (ushort)(start + count - 1));
                byte[] linkFrame = BuildLinkHeader(OutstationAddress, MasterAddress, 0xC4, appPdu);

                var result = SendAndReceive(linkFrame);
                if (!result.IsSuccess) return OperateResult<bool[]>.Failed(result.Message);

                byte[] resp = result.Content;
                const int dataOffset = 12;
                int byteCount = (count + 7) / 8;
                if (resp == null || resp.Length < dataOffset + byteCount)
                    return OperateResult<bool[]>.Failed("响应数据不足");

                ushort iin = (ushort)((resp[4] << 8) | resp[5]);
                if ((iin & (ushort)Dnp3IinFlags.DeviceTrouble) != 0)
                    return OperateResult<bool[]>.Failed("设备故障: " + Dnp3ErrorCodes.GetIinDescription(iin));

                var values = new bool[count];
                for (int i = 0; i < count && dataOffset < resp.Length; i++)
                {
                    int byteIdx = i / 8;
                    int bitIdx = i % 8;
                    if (dataOffset + byteIdx < resp.Length)
                        values[i] = (resp[dataOffset + byteIdx] & (1 << bitIdx)) != 0;
                }

                return OperateResult<bool[]>.Success(values);
            }
            catch (Exception ex)
            {
                return OperateResult<bool[]>.Failed(ex.Message);
            }
        }

        /// <summary>读取计数器（32 位无符号整数）。</summary>
        public OperateResult<uint[]> ReadCounters(ushort start, ushort count)
        {
            try
            {
                byte seq = unchecked(++_appSequence);
                byte[] appPdu = BuildReadRequest(seq, Dnp3Group.Counter, Dnp3Variation.Counter32, start, (ushort)(start + count - 1));
                byte[] linkFrame = BuildLinkHeader(OutstationAddress, MasterAddress, 0xC4, appPdu);

                var result = SendAndReceive(linkFrame);
                if (!result.IsSuccess) return OperateResult<uint[]>.Failed(result.Message);

                byte[] resp = result.Content;
                if (resp == null || resp.Length < 12 + count * 4)
                    return OperateResult<uint[]>.Failed("响应数据不足");

                ushort iin = (ushort)((resp[4] << 8) | resp[5]);
                if ((iin & (ushort)Dnp3IinFlags.DeviceTrouble) != 0)
                    return OperateResult<uint[]>.Failed("设备故障: " + Dnp3ErrorCodes.GetIinDescription(iin));

                var values = new uint[count];
                for (int i = 0; i < count; i++)
                    values[i] = BitConverter.ToUInt32(resp, 12 + i * 4);

                return OperateResult<uint[]>.Success(values);
            }
            catch (Exception ex)
            {
                return OperateResult<uint[]>.Failed(ex.Message);
            }
        }

        /// <summary>读取模拟输出状态。</summary>
        public OperateResult<float[]> ReadAnalogOutputs(ushort start, ushort count)
        {
            try
            {
                byte seq = unchecked(++_appSequence);
                byte[] appPdu = BuildReadRequest(seq, Dnp3Group.AnalogOutput, Dnp3Variation.AnalogOutputFloat32, start, (ushort)(start + count - 1));
                byte[] linkFrame = BuildLinkHeader(OutstationAddress, MasterAddress, 0xC4, appPdu);

                var result = SendAndReceive(linkFrame);
                if (!result.IsSuccess) return OperateResult<float[]>.Failed(result.Message);

                byte[] resp = result.Content;
                if (resp == null || resp.Length < 12 + count * 4)
                    return OperateResult<float[]>.Failed("响应数据不足");

                var values = new float[count];
                for (int i = 0; i < count; i++)
                    values[i] = BitConverter.ToSingle(resp, 12 + i * 4);

                return OperateResult<float[]>.Success(values);
            }
            catch (Exception ex)
            {
                return OperateResult<float[]>.Failed(ex.Message);
            }
        }

        /// <summary>Select-Before-Operate：先 Select 再 Operate（二进制输出）。</summary>
        public OperateResult SelectBeforeOperateBinary(ushort index, bool value)
        {
            try
            {
                byte seq = unchecked(++_appSequence);
                byte[] data = new byte[] { (byte)(value ? 1 : 0) };

                // Select
                byte[] selectPdu = BuildSelectRequest(seq, Dnp3Group.BinaryOutput, Dnp3Variation.BinaryOutputPacked, index, data);
                byte[] selectFrame = BuildLinkHeader(OutstationAddress, MasterAddress, 0xC4, selectPdu);
                var selectResult = SendAndReceive(selectFrame);
                if (!selectResult.IsSuccess) return OperateResult.Failed("Select 失败: " + selectResult.Message);

                // Operate
                byte seq2 = unchecked(++_appSequence);
                byte[] operatePdu = BuildOperateRequest(seq2, Dnp3Group.BinaryOutput, Dnp3Variation.BinaryOutputPacked, index, data);
                byte[] operateFrame = BuildLinkHeader(OutstationAddress, MasterAddress, 0xC4, operatePdu);
                var operateResult = SendAndReceive(operateFrame);
                if (!operateResult.IsSuccess) return OperateResult.Failed("Operate 失败: " + operateResult.Message);

                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed(ex.Message);
            }
        }

        /// <summary>Select-Before-Operate：先 Select 再 Operate（模拟输出）。</summary>
        public OperateResult SelectBeforeOperateAnalog(ushort index, float value)
        {
            try
            {
                byte[] data = BitConverter.GetBytes(value);

                byte seq = unchecked(++_appSequence);
                byte[] selectPdu = BuildSelectRequest(seq, Dnp3Group.AnalogOutput, Dnp3Variation.AnalogOutputFloat32, index, data);
                byte[] selectFrame = BuildLinkHeader(OutstationAddress, MasterAddress, 0xC4, selectPdu);
                var selectResult = SendAndReceive(selectFrame);
                if (!selectResult.IsSuccess) return OperateResult.Failed("Select 失败: " + selectResult.Message);

                byte seq2 = unchecked(++_appSequence);
                byte[] operatePdu = BuildOperateRequest(seq2, Dnp3Group.AnalogOutput, Dnp3Variation.AnalogOutputFloat32, index, data);
                byte[] operateFrame = BuildLinkHeader(OutstationAddress, MasterAddress, 0xC4, operatePdu);
                var operateResult = SendAndReceive(operateFrame);
                if (!operateResult.IsSuccess) return OperateResult.Failed("Operate 失败: " + operateResult.Message);

                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed(ex.Message);
            }
        }

        /// <summary>执行冷重启。</summary>
        public OperateResult ColdRestart()
        {
            try
            {
                byte seq = unchecked(++_appSequence);
                byte[] appPdu = BuildColdRestartRequest(seq);
                byte[] linkFrame = BuildLinkHeader(OutstationAddress, MasterAddress, 0xC4, appPdu);
                var result = SendAndReceive(linkFrame);
                if (!result.IsSuccess) return OperateResult.Failed(result.Message);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed(ex.Message);
            }
        }

        /// <summary>执行延迟测量（返回延迟毫秒数）。</summary>
        public OperateResult<ushort> DelayMeasure()
        {
            try
            {
                byte seq = unchecked(++_appSequence);
                byte[] appPdu = BuildDelayMeasureRequest(seq);
                byte[] linkFrame = BuildLinkHeader(OutstationAddress, MasterAddress, 0xC4, appPdu);
                var result = SendAndReceive(linkFrame);
                if (!result.IsSuccess) return OperateResult<ushort>.Failed(result.Message);

                byte[] resp = result.Content;
                if (resp == null || resp.Length < 14)
                    return OperateResult<ushort>.Failed("延迟测量响应不足");

                ushort delayMs = (ushort)((resp[12] << 8) | resp[13]);
                return OperateResult<ushort>.Success(delayMs);
            }
            catch (Exception ex)
            {
                return OperateResult<ushort>.Failed(ex.Message);
            }
        }

        /// <summary>Direct Operate 二进制输出。</summary>
        public OperateResult DirectOperateBinary(ushort index, bool value)
        {
            try
            {
                byte seq = unchecked(++_appSequence);
                byte[] data = new byte[] { (byte)(value ? 1 : 0) };
                byte[] appPdu = BuildDirectOperateRequest(seq, Dnp3Group.BinaryOutput, Dnp3Variation.BinaryOutputPacked, index, data);
                byte[] linkFrame = BuildLinkHeader(OutstationAddress, MasterAddress, 0xC4, appPdu);
                var result = SendAndReceive(linkFrame);
                if (!result.IsSuccess) return result;
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed(ex.Message);
            }
        }

        /// <summary>Direct Operate 模拟输出。</summary>
        public OperateResult DirectOperateAnalog(ushort index, float value)
        {
            try
            {
                byte seq = unchecked(++_appSequence);
                byte[] data = BitConverter.GetBytes(value);
                byte[] appPdu = BuildDirectOperateRequest(seq, Dnp3Group.AnalogOutput, Dnp3Variation.AnalogOutputFloat32, index, data);
                byte[] linkFrame = BuildLinkHeader(OutstationAddress, MasterAddress, 0xC4, appPdu);
                var result = SendAndReceive(linkFrame);
                if (!result.IsSuccess) return result;
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed(ex.Message);
            }
        }

        // ── 标准数据类型 ──

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadBinaryInputs(0, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            return OperateResult<bool>.Success(r.Content[0]);
        }

        public override OperateResult<short> ReadInt16(string address) => ReadValueSafe<short>(address, 1, d => BitConverter.ToInt16(d, 0));
        public override OperateResult<ushort> ReadUInt16(string address) => ReadValueSafe<ushort>(address, 1, d => BitConverter.ToUInt16(d, 0));
        public override OperateResult<int> ReadInt32(string address) => ReadValueSafe<int>(address, 2, d => BitConverter.ToInt32(d, 0));
        public override OperateResult<uint> ReadUInt32(string address) => ReadValueSafe<uint>(address, 2, d => BitConverter.ToUInt32(d, 0));
        public override OperateResult<float> ReadFloat(string address) => ReadValueSafe<float>(address, 2, d => BitConverter.ToSingle(d, 0));
        public override OperateResult<double> ReadDouble(string address) => ReadValueSafe<double>(address, 4, d => BitConverter.ToDouble(d, 0));
        public override OperateResult<long> ReadInt64(string address) => ReadValueSafe<long>(address, 4, d => BitConverter.ToInt64(d, 0));
        public override OperateResult<ulong> ReadUInt64(string address) => ReadValueSafe<ulong>(address, 4, d => BitConverter.ToUInt64(d, 0));
        public override OperateResult<string> ReadString(string address, ushort length) => OperateResult<string>.Failed("DNP3 不支持字符串读取");

        public override OperateResult Write(string address, bool value) => Write(address, new byte[] { (byte)(value ? 1 : 0) });
        public override OperateResult Write(string address, short value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, ushort value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, int value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, uint value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, long value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, ulong value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, float value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, double value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, string value) => OperateResult.Failed("DNP3 不支持字符串写入");

        private OperateResult<T> ReadValueSafe<T>(string address, ushort length, Func<byte[], T> converter)
        {
            var result = ReadBytes(address, length);
            if (!result.IsSuccess) return OperateResult<T>.Failed(result.Message);
            try { return OperateResult<T>.Success(converter(result.Content)); }
            catch (Exception ex) { return OperateResult<T>.Failed(ex.Message); }
        }

        private static OperateResult<byte[]> ParseResponseData(byte[] response)
        {
            // 简化解析：跳过链路层和应用层头部
            if (response == null || response.Length < 14)
                return OperateResult<byte[]>.Failed("响应数据不足");

            // 检查应用层确认位和IIN
            ushort iin = (ushort)((response[4] << 8) | response[5]);
            if ((iin & (ushort)Dnp3IinFlags.DeviceTrouble) != 0)
                return OperateResult<byte[]>.Failed("设备故障: " + Dnp3ErrorCodes.GetIinDescription(iin));

            // 提取有效数据（简化：返回除头部外的所有数据）
            int dataLen = response.Length - 10;
            if (dataLen <= 0) return OperateResult<byte[]>.Success(new byte[0]);
            byte[] data = new byte[dataLen];
            Buffer.BlockCopy(response, 10, data, 0, dataLen);
            return OperateResult<byte[]>.Success(data);
        }

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

        /// <summary>数据变化事件。</summary>
        public event EventHandler<DataChangeEventArgs>? OnDataChanged;

        /// <summary>订阅指定地址的数据变化。</summary>
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

        /// <summary>取消订阅。</summary>
        public void Unsubscribe(string address)
        {
            lock (_monitorLock) { _monitors.Remove(address); }
        }

        /// <summary>启动所有订阅。</summary>
        public void StartSubscriptions(int globalIntervalMs = 500)
        {
            if (_monitoring) return;
            _monitoring = true;
            _monitorTimer = new Timer(PollMonitors, null, globalIntervalMs, globalIntervalMs);
        }

        /// <summary>停止所有订阅。</summary>
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

        /// <inheritdoc/>
        protected override byte[]? BuildHeartbeat()
        {
            try { return BuildReadRequest(0, Dnp3Group.AnalogInput, Dnp3Variation.AnalogInputInt16, 0, 1); }
            catch { return null; }
        }
    }
}
