using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Xinje
{
    /// <summary>
    /// 信捷 Xinje TCP 客户端 — 继承 TcpDeviceBase，复用连接管理。
    /// <para>兼容 Modbus TCP 协议模式 (默认端口 502)。</para>
    /// <para>支持 D/HD/SD/SM/M/Y/X/C/T/S 区域读写。</para>
    /// <para>推荐使用此类替代旧版 XinjeClient。</para>
    /// </summary>
    public class XinjeTcpClient : TcpDeviceBase
    {
        /// <summary>站号（默认 1）。</summary>
        public byte Station { get; set; } = 1;

        /// <inheritdoc/>
        protected override int ResponseHeaderLength => 7; // MBAP: TxId(2) + ProtocolId(2) + Length(2) + UnitId(1)

        /// <inheritdoc/>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 7) return 0;
            int respLen = (header[4] << 8) | header[5];
            return respLen - 1; // 去掉 UnitId
        }

        private static int _transactionId;

        /// <summary>构建完整的 MBAP 帧（MBAP 头 + PDU）。</summary>
        private byte[] BuildMbapFrame(byte[] pdu)
        {
            ushort tid = unchecked((ushort)System.Threading.Interlocked.Increment(ref _transactionId));
            int len = pdu.Length + 1;
            byte[] frame = new byte[7 + pdu.Length];
            frame[0] = (byte)(tid >> 8);
            frame[1] = (byte)tid;
            frame[2] = 0x00; // Protocol ID (Modbus)
            frame[3] = 0x00;
            frame[4] = (byte)(len >> 8);
            frame[5] = (byte)len;
            frame[6] = Station;
            Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);
            return frame;
        }

        /// <summary>
        /// 创建信捷 TCP 客户端实例。
        /// </summary>
        /// <param name="ip">PLC IP 地址。</param>
        /// <param name="port">端口号（默认 502）。</param>
        /// <param name="station">站号（默认 1）。</param>
        public XinjeTcpClient(string ip, int port = 502, byte station = 1)
            : base(ip, port)
        {
            Station = station;
        }

        // ═══════════════════════════════════════════
        //  Modbus 兼容帧
        // ═══════════════════════════════════════════

        /// <summary>构建读取 PDU。</summary>
        public static byte[] BuildReadPdu(ushort startAddr, byte function, ushort count)
        {
            return new byte[]
            {
                function,
                (byte)(startAddr >> 8), (byte)startAddr,
                (byte)(count >> 8), (byte)count
            };
        }

        /// <summary>构建写入单寄存器 PDU。</summary>
        public static byte[] BuildWriteSinglePdu(ushort startAddr, byte[] data)
        {
            byte[] pdu = new byte[5 + data.Length];
            pdu[0] = 0x06;
            pdu[1] = (byte)(startAddr >> 8);
            pdu[2] = (byte)startAddr;
            Buffer.BlockCopy(data, 0, pdu, 3, data.Length);
            return pdu;
        }

        /// <summary>构建写入多寄存器 PDU (FC16)。</summary>
        public static byte[] BuildWriteMultiplePdu(ushort startAddr, byte[] data)
        {
            ushort wordCount = (ushort)(data.Length / 2);
            byte byteCount = (byte)data.Length;
            byte[] pdu = new byte[6 + 1 + data.Length];
            pdu[0] = 0x10;
            pdu[1] = (byte)(startAddr >> 8);
            pdu[2] = (byte)startAddr;
            pdu[3] = (byte)(wordCount >> 8);
            pdu[4] = (byte)wordCount;
            pdu[5] = byteCount;
            Buffer.BlockCopy(data, 0, pdu, 6, data.Length);
            return pdu;
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addrResult = XinjeAddress.TryParse(address);
            if (addrResult == null)
                return OperateResult<byte[]>.Failed($"无法解析信捷地址: {address}");

            byte fc = addrResult.ReadFunctionCode;
            byte[] pdu = BuildReadPdu(addrResult.Address, fc, length);
            byte[] frame = BuildMbapFrame(pdu);

            var result = SendAndReceive(frame);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            byte[] resp = result.Content;
            if (resp == null || resp.Length < 10)
                return OperateResult<byte[]>.Failed("响应长度不足");

            // 检查异常响应
            if ((resp[7] & 0x80) != 0)
            {
                byte errCode = resp.Length > 8 ? resp[8] : (byte)0;
                return OperateResult<byte[]>.Failed($"Modbus异常: 0x{errCode:X2}", errCode);
            }

            // 提取数据 (MBAP头7 + FC1 + ByteCount1 + data)
            int byteCount = resp[8];
            if (resp.Length < 9 + byteCount)
                return OperateResult<byte[]>.Failed("响应数据长度不足");

            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(resp, 9, data, 0, byteCount);
            return OperateResult<byte[]>.Success(data);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var addrResult = XinjeAddress.TryParse(address);
            if (addrResult == null)
                return OperateResult.Failed($"无法解析信捷地址: {address}");

            ushort addr = addrResult.Address;
            byte[] pdu = BuildWriteMultiplePdu(addr, data);
            byte[] frame = BuildMbapFrame(pdu);
            var result = SendAndReceive(frame);
            if (!result.IsSuccess) return result;

            byte[] resp = result.Content;
            if (resp == null || resp.Length < 12)
                return OperateResult.Failed("写入响应长度不足");

            if ((resp[7] & 0x80) != 0)
            {
                byte errCode = resp.Length > 8 ? resp[8] : (byte)0;
                return OperateResult.Failed($"Modbus异常: 0x{errCode:X2}", errCode);
            }

            return OperateResult.Success();
        }

        // ── 高层方法 ──

        public override OperateResult<bool> ReadBool(string address)
        {
            var result = ReadBytes(address, 1);
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message);
            return OperateResult<bool>.Success((result.Content[0] & 0x01) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var result = ReadBytes(address, 1);
            if (!result.IsSuccess) return OperateResult<short>.Failed(result.Message);
            return OperateResult<short>.Success((short)((result.Content[0] << 8) | result.Content[1]));
        }

        public override OperateResult Write(string address, bool value) => Write(address, new byte[] { (byte)(value ? 0xFF : 0x00), 0x00 });
        public override OperateResult Write(string address, short value) => Write(address, new byte[] { (byte)(value >> 8), (byte)value });
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) => Write(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, (int)value);
        public override OperateResult Write(string address, ulong value) => Write(address, (int)value);
        public override OperateResult Write(string address, float value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, double value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, string value) => Write(address, Encoding.ASCII.GetBytes(value));

        public override OperateResult<ushort> ReadUInt16(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message); }
        public override OperateResult<int> ReadInt32(string address) => ReadValueSafe<int>(address, 2, d => (d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3]);
        public override OperateResult<uint> ReadUInt32(string address) => ReadValueSafe<uint>(address, 2, d => (uint)((d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3]));
        public override OperateResult<long> ReadInt64(string address) => ReadValueSafe<long>(address, 4, d => BitConverter.ToInt64(d, 0));
        public override OperateResult<ulong> ReadUInt64(string address) => ReadValueSafe<ulong>(address, 4, d => BitConverter.ToUInt64(d, 0));
        public override OperateResult<float> ReadFloat(string address) => ReadValueSafe<float>(address, 2, d => BitConverter.ToSingle(d, 0));
        public override OperateResult<double> ReadDouble(string address) => ReadValueSafe<double>(address, 4, d => BitConverter.ToDouble(d, 0));
        public override OperateResult<string> ReadString(string address, ushort length) => ReadValueSafe<string>(address, length, d => Encoding.ASCII.GetString(d).TrimEnd('\0'));

        private OperateResult<T> ReadValueSafe<T>(string address, ushort length, Func<byte[], T> converter)
        {
            var result = ReadBytes(address, length);
            if (!result.IsSuccess) return OperateResult<T>.Failed(result.Message);
            try { return OperateResult<T>.Success(converter(result.Content)); }
            catch (Exception ex) { return OperateResult<T>.Failed(ex.Message); }
        }
    }
}
