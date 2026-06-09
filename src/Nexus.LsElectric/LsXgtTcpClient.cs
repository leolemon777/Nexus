using System;
using System.Text;

namespace Nexus.LsElectric
{
    /// <summary>
    /// LG/LS Electric XGT 协议 TCP 客户端 — 继承 TcpDeviceBase，复用连接管理。
    /// <para>XGT Binary 帧格式: ENQ(1) + Company(2) + PLCInfo(2) + ... + ExtLen(2) + Data。</para>
    /// <para>推荐使用此类替代旧版 LsXgtClient。</para>
    /// </summary>
    public class LsXgtTcpClient : TcpDeviceBase
    {
        /// <summary>目标 CPU 编号。</summary>
        public byte CpuTo { get; set; } = 0;

        /// <summary>源 CPU 编号。</summary>
        public byte CpuFrom { get; set; } = 0;

        /// <inheritdoc/>
        protected override int ResponseHeaderLength => 20; // XGT 响应头固定 20 字节

        /// <inheritdoc/>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 20) return 0;
            // XGT 帧: 尾部 2 字节为数据长度
            int extLen = (header[18] << 8) | header[19];
            return extLen;
        }

        public LsXgtTcpClient(string ip, int port = 2004)
            : base(ip, port)
        {
        }

        // ═══════════════════════════════════════════
        //  XGT 帧构建
        // ═══════════════════════════════════════════

        /// <summary>构建 XGT Binary 请求帧。</summary>
        public static byte[] BuildXgtFrame(byte mfc, byte sfc, byte dataType, byte[] data)
        {
            // XGT Binary Frame: ENQ(1) + Company(2) + PLCInfo(2) + Filler(4) + CpuTo(1) + CpuFrom(1) + SFC(1) + MFC(1) + DataType(1) + Reserved(2) + ExtLen(2) + Data(N)
            byte[] frame = new byte[20 + data.Length];
            frame[0] = 0x05; // ENQ
            frame[1] = (byte)'L';
            frame[2] = (byte)'S';
            frame[3] = 0x00; // PLC Info
            frame[4] = 0x00;
            frame[5] = 0x00; // Filler
            frame[6] = 0x00;
            frame[7] = 0x00;
            frame[8] = 0x00;
            frame[9] = 0x00; // CpuTo placeholder
            frame[10] = 0x00; // CpuFrom placeholder
            frame[11] = sfc;
            frame[12] = mfc;
            frame[13] = dataType;
            frame[14] = 0x00; // Reserved
            frame[15] = 0x00;
            frame[16] = (byte)((data.Length >> 8) & 0xFF); // ExtLen Hi
            frame[17] = (byte)(data.Length & 0xFF);         // ExtLen Lo
            // Bytes 18-19 are part of header structure
            if (data.Length > 0)
                Buffer.BlockCopy(data, 0, frame, 20, data.Length);
            return frame;
        }

        /// <summary>构建单地址读取请求。</summary>
        public static byte[] BuildReadRequest(byte dataType, string address, ushort count)
        {
            byte[] addrBytes = Encoding.ASCII.GetBytes(address.PadRight(8));
            byte[] data = new byte[8 + 2];
            Buffer.BlockCopy(addrBytes, 0, data, 0, 8);
            data[8] = (byte)(count >> 8);
            data[9] = (byte)(count & 0xFF);
            return BuildXgtFrame(0x54, 0x01, dataType, data);
        }

        /// <summary>构建单地址写入请求。</summary>
        public static byte[] BuildWriteRequest(byte dataType, string address, byte[] value)
        {
            byte[] addrBytes = Encoding.ASCII.GetBytes(address.PadRight(8));
            byte[] data = new byte[8 + value.Length];
            Buffer.BlockCopy(addrBytes, 0, data, 0, 8);
            Buffer.BlockCopy(value, 0, data, 8, value.Length);
            return BuildXgtFrame(0x54, 0x02, dataType, data);
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            try
            {
                var parsed = LsXgtAddress.Parse(address);
                byte dataType = parsed.AreaCode;
                byte[] req = BuildReadRequest(dataType, address, length);

                var result = SendAndReceive(req);
                if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);

                byte[] resp = result.Content;
                if (resp == null || resp.Length < 20)
                    return OperateResult<byte[]>.Failed("XGT 响应长度不足");

                // 检查错误码 (响应位置在帧头尾)
                byte errCode = resp[14];
                if (errCode != 0x00)
                    return OperateResult<byte[]>.Failed($"XGT 错误: 0x{errCode:X2}");

                // 提取数据
                int dataLen = resp.Length - 20;
                if (dataLen <= 0) return OperateResult<byte[]>.Success(new byte[0]);
                byte[] data = new byte[dataLen];
                Buffer.BlockCopy(resp, 20, data, 0, dataLen);
                return OperateResult<byte[]>.Success(data);
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
                var parsed = LsXgtAddress.Parse(address);
                byte dataType = parsed.AreaCode;
                byte[] req = BuildWriteRequest(dataType, address, data);

                var result = SendAndReceive(req);
                if (!result.IsSuccess) return result;

                byte[] resp = result.Content;
                if (resp == null || resp.Length < 20)
                    return OperateResult.Failed("XGT 写入响应长度不足");

                byte errCode = resp[14];
                if (errCode != 0x00)
                    return OperateResult.Failed($"XGT 错误: 0x{errCode:X2}");

                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed(ex.Message);
            }
        }

        // ── 高层方法 ──

        public override OperateResult<bool> ReadBool(string address) { var r = ReadBytes(address, 1); if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message); return OperateResult<bool>.Success((r.Content[0] & 0x01) != 0); }
        public override OperateResult<short> ReadInt16(string address) => ReadValueSafe<short>(address, 1, d => (short)((d[0] << 8) | d[1]));
        public override OperateResult<ushort> ReadUInt16(string address) => ReadValueSafe<ushort>(address, 1, d => (ushort)((d[0] << 8) | d[1]));
        public override OperateResult<int> ReadInt32(string address) => ReadValueSafe<int>(address, 2, d => (d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3]);
        public override OperateResult<uint> ReadUInt32(string address) => ReadValueSafe<uint>(address, 2, d => (uint)((d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3]));
        public override OperateResult<long> ReadInt64(string address) => ReadValueSafe<long>(address, 4, d => BitConverter.ToInt64(d, 0));
        public override OperateResult<ulong> ReadUInt64(string address) => ReadValueSafe<ulong>(address, 4, d => BitConverter.ToUInt64(d, 0));
        public override OperateResult<float> ReadFloat(string address) => ReadValueSafe<float>(address, 2, d => BitConverter.ToSingle(d, 0));
        public override OperateResult<double> ReadDouble(string address) => ReadValueSafe<double>(address, 4, d => BitConverter.ToDouble(d, 0));
        public override OperateResult<string> ReadString(string address, ushort length) => ReadValueSafe<string>(address, length, d => Encoding.ASCII.GetString(d).TrimEnd('\0'));

        public override OperateResult Write(string address, bool value) => Write(address, new byte[] { (byte)(value ? 0x01 : 0x00) });
        public override OperateResult Write(string address, short value) => Write(address, new byte[] { (byte)(value >> 8), (byte)value });
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) => Write(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, (int)value);
        public override OperateResult Write(string address, ulong value) => Write(address, (int)value);
        public override OperateResult Write(string address, float value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, double value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, string value) => Write(address, Encoding.ASCII.GetBytes(value));

        private OperateResult<T> ReadValueSafe<T>(string address, ushort length, Func<byte[], T> converter)
        {
            var result = ReadBytes(address, length);
            if (!result.IsSuccess) return OperateResult<T>.Failed(result.Message);
            try { return OperateResult<T>.Success(converter(result.Content)); }
            catch (Exception ex) { return OperateResult<T>.Failed(ex.Message); }
        }
    }
}
