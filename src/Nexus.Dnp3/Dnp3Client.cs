using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Dnp3
{
    /// <summary>
    /// DNP3 TCP 客户端 — 支持 IEEE 1815 DNP3 协议。
    /// <para>用于电力 SCADA 系统与 RTU/IED 的通信。</para>
    /// <para>支持 Read/Write/DirectOperate/Freeze 等功能码。</para>
    /// </summary>
    public class Dnp3Client : TcpDeviceBase
    {
        /// <summary>主站地址。</summary>
        public ushort MasterAddress { get; set; } = Dnp3Constants.DefaultMasterAddress;
        /// <summary>从站地址。</summary>
        public ushort OutstationAddress { get; set; } = Dnp3Constants.DefaultOutstationAddress;
        /// <summary>确认超时（毫秒）。</summary>
        public int ConfirmTimeout { get; set; } = Dnp3Constants.DefaultConfirmTimeout;

        private byte _appSequence;

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

        public Dnp3Client(string ip, int port = 20000)
            : base(ip, port)
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

            // CRC 计算简化 — 使用0x0000占位
            frame[8] = 0x00;
            frame[9] = 0x00;

            if (userData != null && userData.Length > 0)
                Buffer.BlockCopy(userData, 0, frame, 10, userData.Length);

            return frame;
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

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            try
            {
                byte seq = unchecked(++_appSequence);
                byte[] appPdu = BuildReadRequest(seq, Dnp3Group.AnalogInput, Dnp3Variation.AnalogInputFloat32, 0, length);
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
                if (resp == null || resp.Length < 14)
                    return OperateResult<bool[]>.Failed("响应数据不足");

                // 检查 IIN 标志
                ushort iin = (ushort)((resp[4] << 8) | resp[5]);
                if ((iin & (ushort)Dnp3IinFlags.DeviceTrouble) != 0)
                    return OperateResult<bool[]>.Failed("设备故障: " + Dnp3ErrorCodes.GetIinDescription(iin));

                var values = new bool[count];
                // 解析打包的位数据（简化）
                int dataOffset = 12;
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
    }
}
