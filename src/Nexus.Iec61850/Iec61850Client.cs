using System;
using System.Text;
using System.Threading.Tasks;

namespace Nexus.Iec61850
{
    /// <summary>
    /// IEC 61850 MMS 客户端 — 支持 IEC 61850变电站自动化通信。
    /// <para>通过 MMS (Manufacturing Message Specification) 协议与 IED 通信。</para>
    /// <para>支持数据模型浏览、数据读写、报告订阅、控制操作。</para>
    /// </summary>
    public class Iec61850Client : TcpDeviceBase
    {
        /// <summary>IED 逻辑设备名称。</summary>
        public string LogicalDevice { get; set; } = "LD0";

        /// <summary>默认报告触发选项。</summary>
        public ReportTriggerOptions TriggerOptions { get; set; } = ReportTriggerOptions.DataChanged | ReportTriggerOptions.QualityChanged;

        /// <inheritdoc/>
        protected override int ResponseHeaderLength => 8;

        /// <inheritdoc/>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 8) return 0;
            int len = (header[6] << 8) | header[7];
            return len - 6;
        }

        public Iec61850Client(string ip, int port = 102)
            : base(ip, port)
        {
        }

        // ═══════════════════════════════════════════
        //  MMS 请求构建
        // ═══════════════════════════════════════════

        /// <summary>构建 MMS GetDataValues 请求。</summary>
        public static byte[] BuildGetDataValuesRequest(string ldName, string lnName, string dataName, FunctionalConstraint fc)
        {
            // 简化的 MMS 帧: 服务类型(1) + InvokeId(4) + LD(32-len) + LN(32-len) + Data(32-len) + FC(1)
            byte[] request = new byte[102];
            request[0] = 0x03; // GetDataValues service
            // Invoke ID (4 bytes)
            request[1] = 0x00;
            request[2] = 0x00;
            request[3] = 0x00;
            request[4] = 0x01;
            // LD name (32 bytes, null-padded)
            WritePaddedString(request, 5, 32, ldName);
            // LN name (32 bytes)
            WritePaddedString(request, 37, 32, lnName);
            // Data name (32 bytes)
            WritePaddedString(request, 69, 32, dataName);
            // FC (1 byte)
            request[101] = (byte)fc;
            return request;
        }

        /// <summary>构建 MMS SetDataValues 请求。</summary>
        public static byte[] BuildSetDataValuesRequest(string ldName, string lnName, string dataName, FunctionalConstraint fc, byte[] value)
        {
            byte[] request = new byte[102 + value.Length];
            request[0] = 0x04; // SetDataValues service
            request[1] = 0x00;
            request[2] = 0x00;
            request[3] = 0x00;
            request[4] = 0x02;
            WritePaddedString(request, 5, 32, ldName);
            WritePaddedString(request, 37, 32, lnName);
            WritePaddedString(request, 69, 32, dataName);
            request[101] = (byte)fc;
            // Value length + value
            request[102] = (byte)value.Length;
            if (value.Length > 0)
                Buffer.BlockCopy(value, 0, request, 103, value.Length);
            return request;
        }

        private static void WritePaddedString(byte[] buffer, int offset, int length, string value)
        {
            byte[] strBytes = Encoding.ASCII.GetBytes(value ?? "");
            int copyLen = Math.Min(strBytes.Length, length);
            if (copyLen > 0)
                Buffer.BlockCopy(strBytes, 0, buffer, offset, copyLen);
            for (int i = copyLen; i < length; i++)
                buffer[offset + i] = 0x00;
        }

        // ═══════════════════════════════════════════
        //  数据模型操作
        // ═══════════════════════════════════════════

        /// <summary>构建对象引用路径 (LD/LN.DO.DA)。</summary>
        public static string BuildObjectReference(string ld, string ln, string dataName, string? daName = null)
        {
            string ref_ = $"{ld}/{ln}.{dataName}";
            if (daName != null) ref_ += $".{daName}";
            return ref_;
        }

        /// <summary>解析对象引用路径。</summary>
        public static (string ld, string ln, string data, string? da) ParseObjectReference(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw new ArgumentException("对象引用不能为空");

            string[] parts = reference.Split('/');
            if (parts.Length < 2)
                throw new FormatException($"无效的对象引用格式: {reference}");

            string ld = parts[0];
            string rest = parts[1];
            string[] dotParts = rest.Split('.');
            if (dotParts.Length < 2)
                throw new FormatException($"缺少数据名称: {reference}");

            string ln = dotParts[0];
            string data = dotParts[1];
            string? da = dotParts.Length > 2 ? dotParts[2] : null;

            return (ld, ln, data, da);
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            try
            {
                var (ld, ln, data, da) = ParseObjectReference(address);
                byte[] request = BuildGetDataValuesRequest(ld, ln, data, FunctionalConstraint.MX);

                var result = SendAndReceive(request);
                if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);

                byte[] resp = result.Content;
                if (resp == null || resp.Length < 10)
                    return OperateResult<byte[]>.Failed("MMS 响应长度不足");

                // 检查服务错误
                if (resp[0] != 0x03)
                    return OperateResult<byte[]>.Failed(Iec61850ErrorCodes.GetServiceErrorDescription(resp[1]));

                // 提取数据值
                int dataLen = resp.Length - 10;
                if (dataLen <= 0) return OperateResult<byte[]>.Success(new byte[0]);
                byte[] value = new byte[dataLen];
                Buffer.BlockCopy(resp, 10, value, 0, dataLen);
                return OperateResult<byte[]>.Success(value);
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
                var (ld, ln, dataName, da) = ParseObjectReference(address);
                byte[] request = BuildSetDataValuesRequest(ld, ln, dataName, FunctionalConstraint.SP, data);

                var result = SendAndReceive(request);
                if (!result.IsSuccess) return result;

                byte[] resp = result.Content;
                if (resp == null || resp.Length < 2)
                    return OperateResult.Failed("MMS 写入响应长度不足");

                if (resp[0] != 0x04)
                    return OperateResult.Failed(Iec61850ErrorCodes.GetServiceErrorDescription(resp[1]));

                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed(ex.Message);
            }
        }

        // ── 高层方法 ──

        public override OperateResult<bool> ReadBool(string address) => ReadValueSafe<bool>(address, 1, d => d[0] != 0);
        public override OperateResult<short> ReadInt16(string address) => ReadValueSafe<short>(address, 1, d => BitConverter.ToInt16(d, 0));
        public override OperateResult<ushort> ReadUInt16(string address) => ReadValueSafe<ushort>(address, 1, d => BitConverter.ToUInt16(d, 0));
        public override OperateResult<int> ReadInt32(string address) => ReadValueSafe<int>(address, 2, d => BitConverter.ToInt32(d, 0));
        public override OperateResult<uint> ReadUInt32(string address) => ReadValueSafe<uint>(address, 2, d => BitConverter.ToUInt32(d, 0));
        public override OperateResult<long> ReadInt64(string address) => ReadValueSafe<long>(address, 4, d => BitConverter.ToInt64(d, 0));
        public override OperateResult<ulong> ReadUInt64(string address) => ReadValueSafe<ulong>(address, 4, d => BitConverter.ToUInt64(d, 0));
        public override OperateResult<float> ReadFloat(string address) => ReadValueSafe<float>(address, 2, d => BitConverter.ToSingle(d, 0));
        public override OperateResult<double> ReadDouble(string address) => ReadValueSafe<double>(address, 4, d => BitConverter.ToDouble(d, 0));
        public override OperateResult<string> ReadString(string address, ushort length) => ReadValueSafe<string>(address, length, d => Encoding.ASCII.GetString(d).TrimEnd('\0'));

        public override OperateResult Write(string address, bool value) => Write(address, new byte[] { (byte)(value ? 1 : 0) });
        public override OperateResult Write(string address, short value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, ushort value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, int value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, uint value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, long value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, ulong value) => Write(address, BitConverter.GetBytes(value));
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
