using System;
using System.Text;

namespace Nexus.Inovance
{
    /// <summary>
    /// 汇川 Easy 系列私有协议客户端。
    /// 协议帧格式：前 2 字节为总帧长度(LE)，固定 22 字节头 + 数据。
    /// 地址支持: W/D/R/X/Y/M/S/B/U/UB/UW。
    /// </summary>
    public class InovanceEasyClient : TcpDeviceBase
    {
        /// <summary>EasyNet 协议固定帧头长度（22 字节）。</summary>
        private const int FrameHeaderLength = 22;

        /// <summary>EasyNet 命令码：读取。</summary>
        private const byte CmdRead = 0x01;

        /// <summary>EasyNet 命令码：写入。</summary>
        private const byte CmdWrite = 0x02;

        /// <summary>EasyNet 错误标志位（response[8] == 0x0F 表示错误）。</summary>
        private const byte ErrorFlag = 0x0F;

        /// <inheritdoc/>
        protected override int ResponseHeaderLength => 2;

        /// <inheritdoc/>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 2) return 0;
            int totalLen = header[0] | (header[1] << 8);
            return totalLen - 2;
        }

        /// <summary>
        /// 创建汇川 Easy 系列客户端实例。
        /// </summary>
        /// <param name="ip">PLC IP 地址。</param>
        /// <param name="port">端口号（默认 502）。</param>
        public InovanceEasyClient(string ip, int port = 502)
            : base(ip, port)
        {
        }

        // ── 地址解析 ──────────────────────────────────

        /// <summary>
        /// 将地址字符串解析为 4 字节二进制编码。
        /// 编码规则: [addr0, addr1, addr2|type_nibble, 0x00]。
        /// </summary>
        /// <param name="address">PLC 地址字符串，如 D100, X0, Y0A, M100, B3 等。</param>
        /// <returns>4 字节地址编码。</returns>
        public static OperateResult<byte[]> ParseAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return OperateResult<byte[]>.Failed("地址不能为空");

            try
            {
                byte typeCode = 0;
                int value = 0;
                string upper = address.ToUpperInvariant();

                if (upper.StartsWith("UB") || upper.StartsWith("UW"))
                {
                    // UB/UW — 16 进制地址
                    typeCode = 0xF0;
                    value = Convert.ToInt32(address.Substring(2), 16);
                    return BuildAddressBytes(value, typeCode, isExtended: true);
                }

                if (upper.StartsWith("U"))
                {
                    // U — 16 进制地址
                    typeCode = 0xF0;
                    value = Convert.ToInt32(address.Substring(1), 16);
                    return BuildAddressBytes(value, typeCode, isExtended: true);
                }

                if (upper.StartsWith("W"))
                {
                    typeCode = 0x60;
                    return BuildWordAddress(address.Substring(1), typeCode);
                }

                if (upper.StartsWith("D"))
                {
                    typeCode = 0x40;
                    return BuildWordAddress(address.Substring(1), typeCode);
                }

                if (upper.StartsWith("R"))
                {
                    typeCode = 0x50;
                    return BuildWordAddress(address.Substring(1), typeCode);
                }

                if (upper.StartsWith("X"))
                {
                    typeCode = 0x00;
                    // X 地址为八进制
                    value = Convert.ToInt32(address.Substring(1), 8);
                    return BuildAddressBytes(value, typeCode, isExtended: false);
                }

                if (upper.StartsWith("Y"))
                {
                    typeCode = 0x00;
                    // Y 地址为八进制，偏移 0x80000
                    value = Convert.ToInt32(address.Substring(1), 8) + 0x80000;
                    return BuildAddressBytes(value, typeCode, isExtended: false);
                }

                if (upper.StartsWith("M"))
                {
                    typeCode = 0x10;
                    value = Convert.ToInt32(address.Substring(1));
                    return BuildAddressBytes(value, typeCode, isExtended: false);
                }

                if (upper.StartsWith("S"))
                {
                    typeCode = 0x10;
                    value = Convert.ToInt32(address.Substring(1)) + 0x80000;
                    return BuildAddressBytes(value, typeCode, isExtended: false);
                }

                if (upper.StartsWith("B"))
                {
                    typeCode = 0x20;
                    value = Convert.ToInt32(address.Substring(1));
                    return BuildAddressBytes(value, typeCode, isExtended: false);
                }

                return OperateResult<byte[]>.Failed($"不支持的地址类型: {address}");
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed($"地址解析失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 构建 W/D/R 类型地址编码 — 支持 word 和 bit 寻址（word * 16 + bit）。
        /// </summary>
        private static OperateResult<byte[]> BuildWordAddress(string addrPart, byte typeCode)
        {
            int value;
            int dotIdx = addrPart.IndexOf('.');
            if (dotIdx >= 0)
            {
                int wordNo = int.Parse(addrPart.Substring(0, dotIdx));
                int bitNo = int.Parse(addrPart.Substring(dotIdx + 1));
                value = wordNo * 16 + bitNo;
            }
            else
            {
                value = int.Parse(addrPart) * 16;
            }

            byte[] result = new byte[4];
            result[0] = (byte)(value & 0xFF);
            result[1] = (byte)((value >> 8) & 0xFF);
            result[2] = (byte)((value >> 16) & 0x0F);
            result[2] = (byte)(result[2] | typeCode);
            return OperateResult<byte[]>.Success(result);
        }

        /// <summary>
        /// 构建标准 4 字节地址编码。
        /// </summary>
        private static OperateResult<byte[]> BuildAddressBytes(int value, byte typeCode, bool isExtended)
        {
            byte[] result = new byte[4];

            if (isExtended)
            {
                // 扩展地址（U 系列）: 4 字节直接存储值
                result[0] = (byte)(value & 0xFF);
                result[1] = (byte)((value >> 8) & 0xFF);
                result[2] = (byte)((value >> 16) & 0xFF);
                result[3] = (byte)((value >> 24) & 0xFF);
            }
            else
            {
                result[0] = (byte)(value & 0xFF);
                result[1] = (byte)((value >> 8) & 0xFF);
                result[2] = (byte)((value >> 16) & 0x0F);
                result[2] = (byte)(result[2] | typeCode);
            }

            return OperateResult<byte[]>.Success(result);
        }

        // ── 命令构建 ──────────────────────────────────

        /// <summary>
        /// 构建读取命令帧。
        /// </summary>
        /// <param name="address">PLC 地址。</param>
        /// <param name="length">读取长度（字读取时为字数，位读取时为位数）。</param>
        /// <param name="isBit">是否为位读取。</param>
        /// <returns>完整的请求帧。</returns>
        public OperateResult<byte[]> BuildReadCommand(string address, ushort length, bool isBit)
        {
            var addrResult = ParseAddress(address);
            if (!addrResult.IsSuccess)
                return OperateResult<byte[]>.Failed(addrResult.Message);

            // 固定 22 字节头
            byte[] frame = new byte[FrameHeaderLength];
            // 默认模板
            frame[0] = (byte)FrameHeaderLength;       // 总长度低字节
            frame[1] = 0x00;                           // 总长度高字节
            frame[2] = 0x01;
            frame[3] = 0x03;
            frame[4] = 0x01;
            frame[5] = 0x00;
            frame[6] = 0x00;
            frame[7] = 0x00;
            frame[8] = CmdRead;                        // 读取命令
            frame[9] = 0x00;
            frame[10] = 0x01;
            frame[11] = 0x00;
            frame[12] = 0x00;
            frame[13] = 0x00;

            // 地址编码写入 [14..17]
            byte[] addrBytes = addrResult.Content;
            frame[14] = addrBytes[0];
            frame[15] = addrBytes[1];
            frame[16] = addrBytes[2];
            frame[17] = addrBytes[3];

            // 读取长度（以 bit 为单位）
            int bitCount = isBit ? length : length * 16;
            frame[18] = (byte)(bitCount & 0xFF);
            frame[19] = (byte)((bitCount >> 8) & 0xFF);
            frame[20] = (byte)((bitCount >> 16) & 0xFF);

            return OperateResult<byte[]>.Success(frame);
        }

        /// <summary>
        /// 构建字写入命令帧。
        /// </summary>
        /// <param name="address">PLC 地址。</param>
        /// <param name="data">写入数据。</param>
        /// <returns>完整的请求帧。</returns>
        public OperateResult<byte[]> BuildWriteCommand(string address, byte[] data)
        {
            var addrResult = ParseAddress(address);
            if (!addrResult.IsSuccess)
                return OperateResult<byte[]>.Failed(addrResult.Message);

            byte[] frame = new byte[FrameHeaderLength + data.Length];

            // 写入模板
            frame[2] = 0x01;
            frame[3] = 0x03;
            frame[4] = 0x01;
            frame[5] = 0x00;
            frame[6] = 0x00;
            frame[7] = 0x00;
            frame[8] = CmdWrite;                       // 写入命令
            frame[9] = 0x00;
            frame[10] = 0x01;
            frame[11] = 0x00;
            frame[12] = 0x00;
            frame[13] = 0x00;

            // 地址
            byte[] addrBytes = addrResult.Content;
            frame[14] = addrBytes[0];
            frame[15] = addrBytes[1];
            frame[16] = addrBytes[2];
            frame[17] = addrBytes[3];

            // 写入长度（以 bit 为单位）
            int bitCount = data.Length * 8;
            frame[18] = (byte)(bitCount & 0xFF);
            frame[19] = (byte)((bitCount >> 8) & 0xFF);
            frame[20] = (byte)((bitCount >> 16) & 0xFF);

            // 数据
            Buffer.BlockCopy(data, 0, frame, FrameHeaderLength, data.Length);

            // 更新总长度
            frame[0] = (byte)(frame.Length & 0xFF);
            frame[1] = (byte)((frame.Length >> 8) & 0xFF);

            return OperateResult<byte[]>.Success(frame);
        }

        /// <summary>
        /// 构建位写入命令帧。
        /// </summary>
        /// <param name="address">PLC 地址。</param>
        /// <param name="values">布尔值数组。</param>
        /// <returns>完整的请求帧。</returns>
        public OperateResult<byte[]> BuildWriteBoolCommand(string address, bool[] values)
        {
            // 将 bool[] 转为 byte[]
            byte[] data = new byte[(values.Length + 7) / 8];
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i])
                    data[i / 8] |= (byte)(1 << (i % 8));
            }

            var addrResult = ParseAddress(address);
            if (!addrResult.IsSuccess)
                return OperateResult<byte[]>.Failed(addrResult.Message);

            byte[] frame = new byte[FrameHeaderLength + data.Length];

            frame[2] = 0x01;
            frame[3] = 0x03;
            frame[4] = 0x01;
            frame[5] = 0x00;
            frame[6] = 0x00;
            frame[7] = 0x00;
            frame[8] = CmdWrite;
            frame[9] = 0x00;
            frame[10] = 0x01;
            frame[11] = 0x00;
            frame[12] = 0x00;
            frame[13] = 0x00;

            byte[] addrBytes = addrResult.Content;
            frame[14] = addrBytes[0];
            frame[15] = addrBytes[1];
            frame[16] = addrBytes[2];
            frame[17] = addrBytes[3];

            // 位写入时长度就是 bool 个数
            frame[18] = (byte)(values.Length & 0xFF);
            frame[19] = (byte)((values.Length >> 8) & 0xFF);
            frame[20] = (byte)((values.Length >> 16) & 0xFF);

            Buffer.BlockCopy(data, 0, frame, FrameHeaderLength, data.Length);

            frame[0] = (byte)(frame.Length & 0xFF);
            frame[1] = (byte)((frame.Length >> 8) & 0xFF);

            return OperateResult<byte[]>.Success(frame);
        }

        // ── 响应解析 ──────────────────────────────────

        /// <summary>
        /// 解析 EasyNet 响应帧，提取数据区域（偏移 22 之后的数据）。
        /// </summary>
        /// <param name="response">完整的响应帧。</param>
        /// <returns>提取的数据字节。</returns>
        public static OperateResult<byte[]> ParseResponse(byte[] response)
        {
            if (response == null || response.Length < FrameHeaderLength)
                return OperateResult<byte[]>.Failed($"响应数据过短: {response?.Length ?? 0} 字节");

            // 检查错误标志
            if (response[8] == ErrorFlag)
            {
                int errorCode = response.Length >= 14
                    ? response[10] | (response[11] << 8)
                    : 10000;
                return OperateResult<byte[]>.Failed($"PLC 返回错误码: {errorCode}", errorCode);
            }

            // 提取数据区域（写入成功时可能无数据）
            int dataLen = response.Length - FrameHeaderLength;
            if (dataLen <= 0)
                return OperateResult<byte[]>.Success(new byte[0]);

            byte[] data = new byte[dataLen];
            Buffer.BlockCopy(response, FrameHeaderLength, data, 0, dataLen);
            return OperateResult<byte[]>.Success(data);
        }

        // ── IReadWriteDevice 实现 ──────────────────────

        /// <inheritdoc/>
        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var cmdResult = BuildReadCommand(address, length, isBit: false);
            if (!cmdResult.IsSuccess)
                return OperateResult<byte[]>.Failed(cmdResult.Message);

            var sendResult = SendAndReceive(cmdResult.Content);
            if (!sendResult.IsSuccess)
                return OperateResult<byte[]>.Failed(sendResult.Message, sendResult.ErrorCode);

            return ParseResponse(sendResult.Content);
        }

        /// <inheritdoc/>
        public override OperateResult Write(string address, byte[] data)
        {
            var cmdResult = BuildWriteCommand(address, data);
            if (!cmdResult.IsSuccess)
                return OperateResult.Failed(cmdResult.Message);

            var sendResult = SendAndReceive(cmdResult.Content);
            if (!sendResult.IsSuccess)
                return OperateResult.Failed(sendResult.Message, sendResult.ErrorCode);

            // 写入成功：检查响应是否无错误
            var parseResult = ParseResponse(sendResult.Content);
            if (!parseResult.IsSuccess)
                return OperateResult.Failed(parseResult.Message, parseResult.ErrorCode);

            return OperateResult.Success();
        }

        /// <inheritdoc/>
        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadBytes(address, 1);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("读取数据不足 2 字节");
            return OperateResult<short>.Success((short)(r.Content[0] | (r.Content[1] << 8)));
        }

        /// <inheritdoc/>
        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadBytes(address, 1);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<ushort>.Failed("读取数据不足 2 字节");
            return OperateResult<ushort>.Success((ushort)(r.Content[0] | (r.Content[1] << 8)));
        }

        /// <inheritdoc/>
        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadBytes(address, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("读取数据不足 4 字节");
            return OperateResult<int>.Success(
                r.Content[0] | (r.Content[1] << 8) | (r.Content[2] << 16) | (r.Content[3] << 24));
        }

        /// <inheritdoc/>
        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadBytes(address, 2);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<uint>.Failed("读取数据不足 4 字节");
            return OperateResult<uint>.Success(
                (uint)(r.Content[0] | (r.Content[1] << 8) | (r.Content[2] << 16) | (r.Content[3] << 24)));
        }

        /// <inheritdoc/>
        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadBytes(address, 4);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<long>.Failed("读取数据不足 8 字节");
            uint lo = (uint)(r.Content[0] | (r.Content[1] << 8) | (r.Content[2] << 16) | (r.Content[3] << 24));
            uint hi = (uint)(r.Content[4] | (r.Content[5] << 8) | (r.Content[6] << 16) | (r.Content[7] << 24));
            return OperateResult<long>.Success(((long)hi << 32) | lo);
        }

        /// <inheritdoc/>
        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadBytes(address, 4);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<ulong>.Failed("读取数据不足 8 字节");
            uint lo = (uint)(r.Content[0] | (r.Content[1] << 8) | (r.Content[2] << 16) | (r.Content[3] << 24));
            uint hi = (uint)(r.Content[4] | (r.Content[5] << 8) | (r.Content[6] << 16) | (r.Content[7] << 24));
            return OperateResult<ulong>.Success(((ulong)hi << 32) | lo);
        }

        /// <inheritdoc/>
        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadBytes(address, 2);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("读取数据不足 4 字节");
            int bits = r.Content[0] | (r.Content[1] << 8) | (r.Content[2] << 16) | (r.Content[3] << 24);
            return OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(bits), 0));
        }

        /// <inheritdoc/>
        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadInt64(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(BitConverter.Int64BitsToDouble(r.Content));
        }

        /// <inheritdoc/>
        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content));
        }

        /// <inheritdoc/>
        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadBytes(address, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 1) return OperateResult<bool>.Failed("读取数据为空");

            // 字读取后提取最低位
            return OperateResult<bool>.Success((r.Content[0] & 0x01) != 0);
        }

        /// <inheritdoc/>
        public override OperateResult Write(string address, short value)
        {
            return Write(address, BitConverter.GetBytes(value));
        }

        /// <inheritdoc/>
        public override OperateResult Write(string address, ushort value)
        {
            return Write(address, BitConverter.GetBytes(value));
        }

        /// <inheritdoc/>
        public override OperateResult Write(string address, int value)
        {
            return Write(address, BitConverter.GetBytes(value));
        }

        /// <inheritdoc/>
        public override OperateResult Write(string address, uint value)
        {
            return Write(address, BitConverter.GetBytes(value));
        }

        /// <inheritdoc/>
        public override OperateResult Write(string address, long value)
        {
            return Write(address, BitConverter.GetBytes(value));
        }

        /// <inheritdoc/>
        public override OperateResult Write(string address, ulong value)
        {
            return Write(address, BitConverter.GetBytes(value));
        }

        /// <inheritdoc/>
        public override OperateResult Write(string address, float value)
        {
            return Write(address, BitConverter.GetBytes(value));
        }

        /// <inheritdoc/>
        public override OperateResult Write(string address, double value)
        {
            return Write(address, BitConverter.GetBytes(value));
        }

        /// <inheritdoc/>
        public override OperateResult Write(string address, string value)
        {
            return Write(address, Encoding.ASCII.GetBytes(value ?? string.Empty));
        }

        /// <inheritdoc/>
        public override OperateResult Write(string address, bool value)
        {
            // 位写入: 发送 1 字节，最低位为布尔值
            return Write(address, new byte[] { (byte)(value ? 1 : 0) });
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return $"InovanceEasyNet[{Ip}:{Port}]";
        }
    }
}
