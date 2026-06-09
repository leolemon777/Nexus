using System;
using System.Text;

namespace Nexus.Schneider
{
    /// <summary>
    /// 施耐德 Modicon M580/M340 协议客户端。
    /// <para>基于 Modbus TCP，支持标准 FC01-06 及 Modicon 扩展功能码 (OFs/UNA)。</para>
    /// <para>地址格式: %MW100 (内部字), %M50 (内部位), %I0.0 (输入位), %IW10 (输入字), %Q0.1 (输出位), %QW20 (输出字), %S0 (系统位), %SW100 (系统字)。</para>
    /// </summary>
    public class SchneiderModiconClient : TcpDeviceBase
    {
        /// <summary>Modbus 从站地址 (默认 1)。</summary>
        public byte SlaveId { get; set; } = 1;

        /// <inheritdoc/>
        protected override int ResponseHeaderLength => 9;

        /// <inheritdoc/>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 9) return 0;
            // MBAP 头: TxId(2) + ProtocolId(2) + Length(2) + UnitId(1) + FC(1) + ByteCount(1)
            int totalLen = (header[4] << 8) | header[5];
            return totalLen - 3; // 去掉 UnitId + FC + ByteCount (在 payload 部分)
        }

        public SchneiderModiconClient(string ip, int port = 502)
            : base(ip, port)
        {
        }

        /// <summary>Modbus 字节序（默认大端）。</summary>
        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;

        // ═══════════════════════════════════════════
        //  MBAP 帧构建
        // ═══════════════════════════════════════════

        private static int _transactionId;

        private byte[] BuildMbap(byte[] pdu)
        {
            ushort tid = (ushort)System.Threading.Interlocked.Increment(ref _transactionId);
            byte[] frame = new byte[7 + pdu.Length];
            // MBAP Header
            frame[0] = (byte)(tid >> 8);
            frame[1] = (byte)tid;
            frame[2] = 0x00; // Protocol ID (Modbus)
            frame[3] = 0x00;
            frame[4] = (byte)((pdu.Length + 1) >> 8);
            frame[5] = (byte)(pdu.Length + 1);
            frame[6] = 0x01; // Unit ID placeholder (使用 SlaveId)
            frame[6] = SlaveId;
            Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);
            return frame;
        }

        // ═══════════════════════════════════════════
        //  命令构建
        // ═══════════════════════════════════════════

        /// <summary>构建读取命令 PDU。</summary>
        public static byte[] BuildReadPdu(byte fc, ushort address, ushort count)
        {
            return new byte[]
            {
                fc,
                (byte)(address >> 8), (byte)address,
                (byte)(count >> 8), (byte)count
            };
        }

        /// <summary>构建写入单个寄存器命令 PDU。</summary>
        public static byte[] BuildWriteSingleRegisterPdu(ushort address, short value)
        {
            return new byte[]
            {
                0x06,
                (byte)(address >> 8), (byte)address,
                (byte)(value >> 8), (byte)value
            };
        }

        /// <summary>构建写入多个寄存器命令 PDU。</summary>
        public static byte[] BuildWriteMultipleRegistersPdu(ushort address, byte[] data)
        {
            ushort wordCount = (ushort)(data.Length / 2);
            byte byteCount = (byte)data.Length;
            byte[] pdu = new byte[6 + 1 + data.Length];
            pdu[0] = 0x10; // FC16
            pdu[1] = (byte)(address >> 8);
            pdu[2] = (byte)address;
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
            var addrResult = SchneiderAddress.TryParse(address);
            if (addrResult == null)
                return OperateResult<byte[]>.Failed($"无法解析施耐德地址: {address}");

            byte[] pdu = BuildReadPdu(addrResult.FunctionCode, addrResult.AddressValue, length);
            byte[] frame = BuildMbap(pdu);

            var result = SendAndReceive(frame);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            byte[] resp = result.Content;
            if (resp == null || resp.Length < 10)
                return OperateResult<byte[]>.Failed("响应长度不足");

            // 检查异常响应
            if ((resp[7] & 0x80) != 0)
            {
                byte errCode = resp.Length > 8 ? resp[8] : (byte)0;
                return OperateResult<byte[]>.Failed(SchneiderErrorCodes.GetDescription(errCode), errCode);
            }

            // 提取数据 (跳过 MBAP头7字节 + FC1字节 + ByteCount1字节)
            int byteCount = resp[8];
            if (resp.Length < 9 + byteCount)
                return OperateResult<byte[]>.Failed("响应数据长度不足");

            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(resp, 9, data, 0, byteCount);
            return OperateResult<byte[]>.Success(data);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var addrResult = SchneiderAddress.TryParse(address);
            if (addrResult == null)
                return OperateResult.Failed($"无法解析施耐德地址: {address}");

            byte[] pdu = BuildWriteMultipleRegistersPdu(addrResult.AddressValue, data);
            byte[] frame = BuildMbap(pdu);

            var result = SendAndReceive(frame);
            if (!result.IsSuccess) return result;

            byte[] resp = result.Content;
            if (resp == null || resp.Length < 12)
                return OperateResult.Failed("写入响应长度不足");

            if ((resp[7] & 0x80) != 0)
            {
                byte errCode = resp.Length > 8 ? resp[8] : (byte)0;
                return OperateResult.Failed(SchneiderErrorCodes.GetDescription(errCode), errCode);
            }

            return OperateResult.Success();
        }

        // ── 高层数据类型读写 ──

        public override OperateResult<short> ReadInt16(string address)
        {
            var result = ReadBytes(address, 1);
            if (!result.IsSuccess) return OperateResult<short>.Failed(result.Message);
            return OperateResult<short>.Success((short)((result.Content[0] << 8) | result.Content[1]));
        }

        public override OperateResult Write(string address, short value)
        {
            return Write(address, new byte[] { (byte)(value >> 8), (byte)value });
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var addrResult = SchneiderAddress.TryParse(address);
            if (addrResult == null)
                return OperateResult<bool>.Failed($"无法解析地址: {address}");

            byte[] pdu = BuildReadPdu(addrResult.FunctionCode, addrResult.AddressValue, 1);
            var result = SendAndReceive(BuildMbap(pdu));
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message);

            byte[] resp = result.Content;
            if (resp == null || resp.Length < 10)
                return OperateResult<bool>.Failed("响应长度不足");

            if ((resp[7] & 0x80) != 0)
                return OperateResult<bool>.Failed(SchneiderErrorCodes.GetDescription(resp[8]));

            // 位读取返回 1 字节
            return OperateResult<bool>.Success((resp[9] & 0x01) != 0);
        }

        public override OperateResult Write(string address, bool value)
        {
            var addrResult = SchneiderAddress.TryParse(address);
            if (addrResult == null)
                return OperateResult.Failed($"无法解析地址: {address}");

            // FC05: Write Single Coil
            ushort coilValue = value ? (ushort)0xFF00 : (ushort)0x0000;
            byte[] pdu =
            {
                0x05,
                (byte)(addrResult.AddressValue >> 8), (byte)addrResult.AddressValue,
                (byte)(coilValue >> 8), (byte)coilValue
            };

            var result = SendAndReceive(BuildMbap(pdu));
            if (!result.IsSuccess) return result;

            byte[] resp = result.Content;
            if (resp == null || resp.Length < 12) return OperateResult.Failed("写入响应长度不足");
            if ((resp[7] & 0x80) != 0)
                return OperateResult.Failed(SchneiderErrorCodes.GetDescription(resp[8]));

            return OperateResult.Success();
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            return OperateResult<ushort>.Success((ushort)r.Content);
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) => Write(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, (int)value);
        public override OperateResult Write(string address, ulong value) => Write(address, (int)value);
        public override OperateResult Write(string address, float value) { unsafe { int bits = *(int*)&value; return Write(address, bits); } }
        public override OperateResult Write(string address, double value) => Write(address, (float)value);
        public override OperateResult Write(string address, string value) => Write(address, Encoding.ASCII.GetBytes(value));
    }
}
