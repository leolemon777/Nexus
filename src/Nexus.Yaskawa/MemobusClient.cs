using System;
using System.Text;
using System.Threading;

namespace Nexus.Yaskawa
{
    /// <summary>
    /// YASKAWA Memobus TCP 协议客户端。
    /// Memobus 类似 Modbus 但有扩展：CpuTo/CpuFrom 字段、扩展功能码、随机读写。
    /// 外层帧格式：[0x11, id, 0x00×4, totalLen(2 LE), 0x00×4, innerCommand...]
    /// 内层帧格式：[payloadLen(2 LE), MFC(1), SFC(1), cpuToFrom(1), data...]
    /// </summary>
    public class MemobusClient : TcpDeviceBase, IBatchReadWrite
    {
        #region 常量

        /// <summary>外层帧头固定长度。</summary>
        private const int OuterHeaderLength = 12;

        /// <summary>外层帧头标记字节。</summary>
        private const byte OuterHeaderMarker = 0x11;

        /// <summary>默认主功能码。</summary>
        private const byte DefaultMfc = 0x20;

        /// <summary>命名区域主功能码。</summary>
        private const byte NamedMfc = 0x43;

        #endregion

        #region 属性

        /// <summary>目标 CPU 编号（默认 2）。</summary>
        public byte CpuTo { get; set; } = 2;

        /// <summary>源 CPU 编号（默认 1）。</summary>
        public byte CpuFrom { get; set; } = 1;

        #endregion

        #region TcpDeviceBase 实现

        /// <inheritdoc/>
        protected override int ResponseHeaderLength => 8;

        /// <inheritdoc/>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 8) return 0;
            int totalLen = header[6] | (header[7] << 8);
            return totalLen - 8;
        }

        #endregion

        #region 构造

        /// <summary>
        /// 创建 Memobus TCP 客户端实例。
        /// </summary>
        /// <param name="ip">PLC IP 地址。</param>
        /// <param name="port">端口号（默认 502）。</param>
        public MemobusClient(string ip, int port = 502)
            : base(ip, port)
        {
        }

        #endregion

        #region 外层帧封装

        /// <summary>
        /// 将内层 Memobus 命令封装到外层帧中。
        /// 外层帧: [0x11, id, 0x00×4, totalLen(2 LE), 0x00×4, innerCommand...]
        /// </summary>
        public static byte[] WrapWithOuterHeader(byte[] innerCommand)
        {
            byte[] frame = new byte[OuterHeaderLength + innerCommand.Length];
            frame[0] = OuterHeaderMarker;
            frame[1] = 0; // connection id placeholder
            // bytes[2-5] = 0x00
            int totalLen = frame.Length;
            frame[6] = (byte)(totalLen & 0xFF);
            frame[7] = (byte)((totalLen >> 8) & 0xFF);
            // bytes[8-11] = 0x00
            Buffer.BlockCopy(innerCommand, 0, frame, OuterHeaderLength, innerCommand.Length);
            return frame;
        }

        /// <summary>
        /// 从完整响应帧中提取内层 Memobus 数据（去掉外层 12 字节头）。
        /// </summary>
        public static OperateResult<byte[]> UnwrapOuterHeader(byte[] fullResponse)
        {
            if (fullResponse == null || fullResponse.Length < OuterHeaderLength)
                return OperateResult<byte[]>.Failed($"响应数据过短: {fullResponse?.Length ?? 0} 字节");

            byte[] inner = new byte[fullResponse.Length - OuterHeaderLength];
            Buffer.BlockCopy(fullResponse, OuterHeaderLength, inner, 0, inner.Length);
            return OperateResult<byte[]>.Success(inner);
        }

        #endregion

        #region 内层帧头设置

        /// <summary>
        /// 设置内层 Memobus 帧头的公共字段。
        /// </summary>
        private void SetByteHead(byte[] buffer, byte mfc, byte sfc)
        {
            // [0-1] payload length = buffer.Length - 2
            buffer[0] = (byte)((buffer.Length - 2) & 0xFF);
            buffer[1] = (byte)(((buffer.Length - 2) >> 8) & 0xFF);
            buffer[2] = mfc;
            buffer[3] = sfc;
            buffer[4] = (byte)((CpuTo << 4) | CpuFrom);
        }

        #endregion

        #region 地址解析

        /// <summary>
        /// 判断地址是否为命名区域地址（M/G/I/O/S 开头）。
        /// </summary>
        public static bool IsNamedAddress(string address)
        {
            if (string.IsNullOrEmpty(address)) return false;
            char c = char.ToUpperInvariant(address[0]);
            return c == 'M' || c == 'G' || c == 'I' || c == 'O' || c == 'S';
        }

        /// <summary>
        /// 获取命名区域的数据类型编码。
        /// M=77('M'), G=71('G'), I=73('I'), O=79('O'), S=83('S')
        /// </summary>
        public static byte GetAddressDataType(string address)
        {
            if (string.IsNullOrEmpty(address)) return 0;
            return (byte)char.ToUpperInvariant(address[0]);
        }

        /// <summary>
        /// 解析命名区域的布尔索引。
        /// 支持 MB100, M100.5 格式。如果无 '.' 或 'B'，按最后一个字符作为位号。
        /// </summary>
        public static int CalculateBoolIndex(string address)
        {
            // 去掉首字母
            string body = address.Substring(1);
            if (body.Length > 0 && (body[0] == 'B' || body[0] == 'b'))
                body = body.Substring(1);

            int dotIdx = body.IndexOf('.');
            if (dotIdx > 0)
            {
                int wordNo = Convert.ToInt32(body.Substring(0, dotIdx));
                int bitNo = CalculateBitIndex(body.Substring(dotIdx + 1));
                return wordNo * 16 + bitNo;
            }

            // 无点位分隔：最后一个字符是位号
            if (body.Length > 1 && char.IsLetter(body[body.Length - 1]))
            {
                int wordNo = Convert.ToInt32(body.Substring(0, body.Length - 1));
                int bitNo = CalculateBitIndex(body.Substring(body.Length - 1));
                return wordNo * 16 + bitNo;
            }

            return Convert.ToInt32(body) * 16;
        }

        private static int CalculateBitIndex(string bitStr)
        {
            if (bitStr.Length == 0) return 0;
            char c = char.ToUpperInvariant(bitStr[0]);
            if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
            return int.Parse(bitStr);
        }

        /// <summary>
        /// 判断命名地址是否包含位访问（MB 或 . 格式）。
        /// </summary>
        public static bool IsBitAccess(string address)
        {
            if (address.Length < 2) return false;
            if (address[1] == 'B' || address[1] == 'b') return true;
            return address.IndexOf('.') > 1;
        }

        #endregion

        #region 命令构建 — 读取

        /// <summary>
        /// 构建标准读取命令（SFC 01-04, 09, 0A）。
        /// 数字地址默认 SFC=03（读保持寄存器），支持 x=N 前缀指定 SFC。
        /// 命名地址自动选择 MFC=0x43。
        /// </summary>
        public OperateResult<byte[]> BuildReadCommand(string address, ushort length)
        {
            if (string.IsNullOrWhiteSpace(address))
                return OperateResult<byte[]>.Failed("地址不能为空");

            byte mfc = ExtractParameter(ref address, "mfc", DefaultMfc);
            byte sfc = ExtractParameter(ref address, "x", 3);

            // 命名区域地址
            if (IsNamedAddress(address))
            {
                byte dataType = GetAddressDataType(address);
                if (IsBitAccess(address))
                {
                    // 位读取: MFC=0x43, SFC=0x41
                    int boolIndex = CalculateBoolIndex(address);
                    byte[] cmd = new byte[16];
                    SetByteHead(cmd, NamedMfc, 0x41);
                    cmd[6] = dataType;
                    // boolIndex as int32 LE
                    cmd[8] = (byte)(boolIndex & 0xFF);
                    cmd[9] = (byte)((boolIndex >> 8) & 0xFF);
                    cmd[10] = (byte)((boolIndex >> 16) & 0xFF);
                    cmd[11] = (byte)((boolIndex >> 24) & 0xFF);
                    // length as uint16 LE
                    cmd[12] = (byte)(length & 0xFF);
                    cmd[13] = (byte)((length >> 8) & 0xFF);
                    return OperateResult<byte[]>.Success(cmd);
                }
                else
                {
                    // 字读取: MFC=0x43, SFC=0x49
                    uint addrNum = Convert.ToUInt32(address.Substring(1));
                    byte[] cmd = new byte[14];
                    SetByteHead(cmd, NamedMfc, 0x49);
                    cmd[6] = dataType;
                    // address as uint32 LE
                    cmd[8] = (byte)(addrNum & 0xFF);
                    cmd[9] = (byte)((addrNum >> 8) & 0xFF);
                    cmd[10] = (byte)((addrNum >> 16) & 0xFF);
                    cmd[11] = (byte)((addrNum >> 24) & 0xFF);
                    // length as uint16 LE
                    cmd[12] = (byte)(length & 0xFF);
                    cmd[13] = (byte)((length >> 8) & 0xFF);
                    return OperateResult<byte[]>.Success(cmd);
                }
            }

            // 标准数字地址
            if (!ushort.TryParse(address, out ushort addrValue))
                return OperateResult<byte[]>.Failed($"地址格式无效: {address}");

            if (sfc == 1 || sfc == 2 || sfc == 3 || sfc == 4)
            {
                // 标准 Modbus 类似: 地址和数量用大端序
                byte[] cmd = new byte[9];
                SetByteHead(cmd, mfc, sfc);
                cmd[5] = (byte)((addrValue >> 8) & 0xFF); // 地址高字节
                cmd[6] = (byte)(addrValue & 0xFF);          // 地址低字节
                cmd[7] = (byte)((length >> 8) & 0xFF);     // 数量高字节
                cmd[8] = (byte)(length & 0xFF);             // 数量低字节
                return OperateResult<byte[]>.Success(cmd);
            }

            if (sfc == 9 || sfc == 10)
            {
                // 扩展读取: 地址和数量用小端序
                byte[] cmd = new byte[10];
                SetByteHead(cmd, mfc, sfc);
                cmd[5] = 0; // reserved
                cmd[6] = (byte)(addrValue & 0xFF);
                cmd[7] = (byte)((addrValue >> 8) & 0xFF);
                cmd[8] = (byte)(length & 0xFF);
                cmd[9] = (byte)((length >> 8) & 0xFF);
                return OperateResult<byte[]>.Success(cmd);
            }

            return OperateResult<byte[]>.Failed($"不支持的功能码: SFC={sfc}");
        }

        /// <summary>
        /// 构建随机读取命令（标准地址，SFC=0x0D）。
        /// </summary>
        public OperateResult<byte[]> BuildReadRandomCommand(ushort[] addresses)
        {
            if (addresses == null || addresses.Length == 0)
                return OperateResult<byte[]>.Failed("地址列表不能为空");

            byte[] cmd = new byte[8 + addresses.Length * 2];
            SetByteHead(cmd, DefaultMfc, 0x0D);
            cmd[6] = (byte)(addresses.Length & 0xFF);
            cmd[7] = (byte)((addresses.Length >> 8) & 0xFF);
            for (int i = 0; i < addresses.Length; i++)
            {
                cmd[8 + i * 2] = (byte)(addresses[i] & 0xFF);
                cmd[8 + i * 2 + 1] = (byte)((addresses[i] >> 8) & 0xFF);
            }
            return OperateResult<byte[]>.Success(cmd);
        }

        #endregion

        #region 命令构建 — 写入

        /// <summary>
        /// 构建字写入命令（标准 SFC=0x10 或扩展 SFC=0x0B 或命名区域）。
        /// </summary>
        public OperateResult<byte[]> BuildWriteCommand(string address, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(address))
                return OperateResult<byte[]>.Failed("地址不能为空");

            byte mfc = ExtractParameter(ref address, "mfc", DefaultMfc);
            byte sfc = ExtractParameter(ref address, "x", 0x10);
            if (sfc == 3) sfc = 0x10;
            if (sfc == 9) sfc = 0x0B;

            // 命名区域
            if (IsNamedAddress(address))
            {
                byte dataType = GetAddressDataType(address);
                uint addrNum = Convert.ToUInt32(address.Substring(1));
                int wordCount = data.Length / 2;

                byte[] cmd = new byte[14 + data.Length];
                SetByteHead(cmd, NamedMfc, 0x4B);
                cmd[6] = dataType;
                cmd[8] = (byte)(addrNum & 0xFF);
                cmd[9] = (byte)((addrNum >> 8) & 0xFF);
                cmd[10] = (byte)((addrNum >> 16) & 0xFF);
                cmd[11] = (byte)((addrNum >> 24) & 0xFF);
                cmd[12] = (byte)(wordCount & 0xFF);
                cmd[13] = (byte)((wordCount >> 8) & 0xFF);
                // 命名区域写入需要 word reverse
                byte[] reversed = ReverseWords(data);
                Buffer.BlockCopy(reversed, 0, cmd, 14, reversed.Length);
                return OperateResult<byte[]>.Success(cmd);
            }

            // 标准数字地址
            if (!ushort.TryParse(address, out ushort addrValue))
                return OperateResult<byte[]>.Failed($"地址格式无效: {address}");

            if (sfc == 0x0B)
            {
                // 扩展写入
                int wordCount = data.Length / 2;
                byte[] cmd = new byte[10 + data.Length];
                SetByteHead(cmd, mfc, sfc);
                cmd[5] = 0;
                cmd[6] = (byte)(addrValue & 0xFF);
                cmd[7] = (byte)((addrValue >> 8) & 0xFF);
                cmd[8] = (byte)(wordCount & 0xFF);
                cmd[9] = (byte)((wordCount >> 8) & 0xFF);
                byte[] reversed = ReverseWords(data);
                Buffer.BlockCopy(reversed, 0, cmd, 10, reversed.Length);
                return OperateResult<byte[]>.Success(cmd);
            }

            // 标准写入 SFC=0x10
            {
                int wordCount = data.Length / 2;
                byte[] cmd = new byte[9 + data.Length];
                SetByteHead(cmd, mfc, sfc);
                cmd[5] = (byte)((addrValue >> 8) & 0xFF);
                cmd[6] = (byte)(addrValue & 0xFF);
                cmd[7] = (byte)((wordCount >> 8) & 0xFF);
                cmd[8] = (byte)(wordCount & 0xFF);
                Buffer.BlockCopy(data, 0, cmd, 9, data.Length);
                return OperateResult<byte[]>.Success(cmd);
            }
        }

        /// <summary>
        /// 构建单线圈写入命令（SFC=0x05）。
        /// </summary>
        public OperateResult<byte[]> BuildWriteSingleCoilCommand(ushort address, bool value)
        {
            byte[] cmd = new byte[9];
            SetByteHead(cmd, DefaultMfc, 0x05);
            cmd[5] = (byte)((address >> 8) & 0xFF);
            cmd[6] = (byte)(address & 0xFF);
            cmd[7] = (byte)(value ? 0xFF : 0x00);
            cmd[8] = 0x00;
            return OperateResult<byte[]>.Success(cmd);
        }

        /// <summary>
        /// 构建多线圈写入命令（SFC=0x0F）。
        /// </summary>
        public OperateResult<byte[]> BuildWriteMultiCoilCommand(string address, bool[] values)
        {
            byte mfc = ExtractParameter(ref address, "mfc", DefaultMfc);
            byte sfc = ExtractParameter(ref address, "x", 0x0F);

            if (IsNamedAddress(address))
            {
                byte dataType = GetAddressDataType(address);
                int boolIndex = CalculateBoolIndex(address);
                byte[] coilData = BoolArrayToBytes(values);

                byte[] cmd = new byte[16 + coilData.Length];
                SetByteHead(cmd, NamedMfc, 0x4F);
                cmd[6] = dataType;
                cmd[8] = (byte)(boolIndex & 0xFF);
                cmd[9] = (byte)((boolIndex >> 8) & 0xFF);
                cmd[10] = (byte)((boolIndex >> 16) & 0xFF);
                cmd[11] = (byte)((boolIndex >> 24) & 0xFF);
                cmd[12] = (byte)(values.Length & 0xFF);
                cmd[13] = (byte)((values.Length >> 8) & 0xFF);
                Buffer.BlockCopy(coilData, 0, cmd, 16, coilData.Length);
                return OperateResult<byte[]>.Success(cmd);
            }

            if (!ushort.TryParse(address, out ushort addrValue))
                return OperateResult<byte[]>.Failed($"地址格式无效: {address}");

            {
                byte[] coilData = BoolArrayToBytes(values);
                byte[] cmd = new byte[9 + coilData.Length];
                SetByteHead(cmd, mfc, sfc);
                cmd[5] = (byte)((addrValue >> 8) & 0xFF);
                cmd[6] = (byte)(addrValue & 0xFF);
                cmd[7] = (byte)((values.Length >> 8) & 0xFF);
                cmd[8] = (byte)(values.Length & 0xFF);
                Buffer.BlockCopy(coilData, 0, cmd, 9, coilData.Length);
                return OperateResult<byte[]>.Success(cmd);
            }
        }

        /// <summary>
        /// 构建随机写入命令（SFC=0x0E）。
        /// value.Length 必须等于 addresses.Length * 2。
        /// </summary>
        public OperateResult<byte[]> BuildWriteRandomCommand(ushort[] addresses, byte[] value)
        {
            if (value.Length != addresses.Length * 2)
                return OperateResult<byte[]>.Failed("数据长度必须为地址数量的两倍");

            byte[] cmd = new byte[8 + addresses.Length * 4];
            SetByteHead(cmd, DefaultMfc, 0x0E);
            cmd[6] = (byte)(addresses.Length & 0xFF);
            cmd[7] = (byte)((addresses.Length >> 8) & 0xFF);
            for (int i = 0; i < addresses.Length; i++)
            {
                cmd[8 + i * 4] = (byte)(addresses[i] & 0xFF);
                cmd[8 + i * 4 + 1] = (byte)((addresses[i] >> 8) & 0xFF);
                // value is word-swapped (CDAB)
                cmd[8 + i * 4 + 2] = value[i * 2 + 1];
                cmd[8 + i * 4 + 3] = value[i * 2];
            }
            return OperateResult<byte[]>.Success(cmd);
        }

        #endregion

        #region 响应解析

        /// <summary>
        /// 解析 Memobus 内层响应，检查错误，提取数据。
        /// </summary>
        /// <param name="sendInner">发送的内层命令（用于 SFC 比对）。</param>
        /// <param name="recvInner">接收的内层响应。</param>
        /// <returns>提取的数据。</returns>
        public static OperateResult<byte[]> ParseResponse(byte[] sendInner, byte[] recvInner)
        {
            if (sendInner == null || recvInner == null || sendInner.Length < 4 || recvInner.Length < 4)
                return OperateResult<byte[]>.Failed("响应数据不完整");

            // 内层帧中 SFC 在 byte[3]（外层偏移 12+3=15）
            byte sendSfc = sendInner[3];
            byte recvMfc = recvInner.Length > 2 ? recvInner[2] : (byte)0;
            byte recvSfc = recvInner[3];

            // 检查错误响应: SFC + 0x80
            if ((byte)(sendSfc + 0x80) == recvSfc)
            {
                byte errCode = recvInner.Length > 5 ? recvInner[5] : (byte)0xFF;
                string errMsg = GetErrorText(errCode);
                return OperateResult<byte[]>.Failed($"Memobus 错误: {errMsg} (码={errCode})", errCode);
            }

            // SFC 不匹配
            if (sendSfc != recvSfc)
                return OperateResult<byte[]>.Failed($"SFC 不匹配: 发送={sendSfc:X2}, 接收={recvSfc:X2}");

            // 根据响应类型提取数据
            return ExtractPayload(recvMfc, recvSfc, recvInner);
        }

        private static OperateResult<byte[]> ExtractPayload(byte mfc, byte sfc, byte[] inner)
        {
            // MFC = 0x20 (标准)
            if (mfc == DefaultMfc)
            {
                if (sfc == 3 || sfc == 4)
                {
                    // 标准读取响应: 5 字节头后为数据
                    if (inner.Length <= 5) return OperateResult<byte[]>.Success(new byte[0]);
                    byte[] data = new byte[inner.Length - 5];
                    Buffer.BlockCopy(inner, 5, data, 0, data.Length);
                    return OperateResult<byte[]>.Success(data);
                }
                if (sfc == 1 || sfc == 2)
                {
                    // 线圈/输入读取: 5 字节头后为位数据
                    if (inner.Length <= 5) return OperateResult<byte[]>.Success(new byte[0]);
                    byte[] data = new byte[inner.Length - 5];
                    Buffer.BlockCopy(inner, 5, data, 0, data.Length);
                    return OperateResult<byte[]>.Success(data);
                }
                if (sfc == 9 || sfc == 10)
                {
                    // 扩展读取: 8 字节头后为数据，需 word reverse
                    if (inner.Length <= 8) return OperateResult<byte[]>.Success(new byte[0]);
                    byte[] data = new byte[inner.Length - 8];
                    Buffer.BlockCopy(inner, 8, data, 0, data.Length);
                    return OperateResult<byte[]>.Success(ReverseWords(data));
                }
                if (sfc == 0x0D)
                {
                    // 随机读取响应: 8 字节头，word reverse
                    if (inner.Length <= 8) return OperateResult<byte[]>.Success(new byte[0]);
                    byte[] data = new byte[inner.Length - 8];
                    Buffer.BlockCopy(inner, 8, data, 0, data.Length);
                    return OperateResult<byte[]>.Success(ReverseWords(data));
                }
                // 写入成功响应 — 无数据
                return OperateResult<byte[]>.Success(new byte[0]);
            }

            // MFC = 0x43 (命名区域)
            if (mfc == NamedMfc)
            {
                if (sfc == 0x49 || sfc == 0x4D)
                {
                    // 命名字读取/随机读取: 10 字节头，word reverse
                    if (inner.Length <= 10) return OperateResult<byte[]>.Success(new byte[0]);
                    byte[] data = new byte[inner.Length - 10];
                    Buffer.BlockCopy(inner, 10, data, 0, data.Length);
                    return OperateResult<byte[]>.Success(ReverseWords(data));
                }
                if (sfc == 0x41)
                {
                    // 命名位读取: 8 字节头
                    if (inner.Length <= 8) return OperateResult<byte[]>.Success(new byte[0]);
                    byte[] data = new byte[inner.Length - 8];
                    Buffer.BlockCopy(inner, 8, data, 0, data.Length);
                    return OperateResult<byte[]>.Success(data);
                }
                // 写入成功
                return OperateResult<byte[]>.Success(new byte[0]);
            }

            return OperateResult<byte[]>.Failed($"不支持的 MFC: {mfc:X2}");
        }

        /// <summary>
        /// 获取 Memobus 错误码描述。
        /// </summary>
        public static string GetErrorText(byte errorCode)
        {
            switch (errorCode)
            {
                case 0x01: return "非法功能码";
                case 0x02: return "非法数据地址";
                case 0x03: return "非法数据值";
                case 0x40: return "从站设备故障";
                case 0x41: return "CPU 异常";
                case 0x42: return "无法执行";
                default: return $"未知错误 ({errorCode:X2})";
            }
        }

        #endregion

        #region IReadWriteDevice 实现

        /// <inheritdoc/>
        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var cmdResult = BuildReadCommand(address, length);
            if (!cmdResult.IsSuccess)
                return OperateResult<byte[]>.Failed(cmdResult.Message);

            byte[] innerCmd = cmdResult.Content;
            byte[] fullCmd = WrapWithOuterHeader(innerCmd);

            var sendResult = SendAndReceive(fullCmd);
            if (!sendResult.IsSuccess)
                return OperateResult<byte[]>.Failed(sendResult.Message, sendResult.ErrorCode);

            var unwrapResult = UnwrapOuterHeader(sendResult.Content);
            if (!unwrapResult.IsSuccess)
                return OperateResult<byte[]>.Failed(unwrapResult.Message);

            return ParseResponse(innerCmd, unwrapResult.Content);
        }

        /// <inheritdoc/>
        public override OperateResult Write(string address, byte[] data)
        {
            var cmdResult = BuildWriteCommand(address, data);
            if (!cmdResult.IsSuccess)
                return OperateResult.Failed(cmdResult.Message);

            byte[] innerCmd = cmdResult.Content;
            byte[] fullCmd = WrapWithOuterHeader(innerCmd);

            var sendResult = SendAndReceive(fullCmd);
            if (!sendResult.IsSuccess)
                return OperateResult.Failed(sendResult.Message, sendResult.ErrorCode);

            var unwrapResult = UnwrapOuterHeader(sendResult.Content);
            if (!unwrapResult.IsSuccess)
                return OperateResult.Failed(unwrapResult.Message);

            var parseResult = ParseResponse(innerCmd, unwrapResult.Content);
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
            // 命名位地址
            if (IsNamedAddress(address) && IsBitAccess(address))
            {
                var cmdResult = BuildReadCommand(address, 1);
                if (!cmdResult.IsSuccess)
                    return OperateResult<bool>.Failed(cmdResult.Message);

                byte[] innerCmd = cmdResult.Content;
                byte[] fullCmd = WrapWithOuterHeader(innerCmd);
                var sendResult = SendAndReceive(fullCmd);
                if (!sendResult.IsSuccess)
                    return OperateResult<bool>.Failed(sendResult.Message, sendResult.ErrorCode);

                var unwrapResult = UnwrapOuterHeader(sendResult.Content);
                if (!unwrapResult.IsSuccess)
                    return OperateResult<bool>.Failed(unwrapResult.Message);

                var parseResult = ParseResponse(innerCmd, unwrapResult.Content);
                if (!parseResult.IsSuccess)
                    return OperateResult<bool>.Failed(parseResult.Message, parseResult.ErrorCode);

                if (parseResult.Content.Length < 1)
                    return OperateResult<bool>.Failed("读取数据为空");
                return OperateResult<bool>.Success((parseResult.Content[0] & 0x01) != 0);
            }

            // 标准地址: 读 1 个字，取最低位
            var r = ReadBytes(address, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 1) return OperateResult<bool>.Failed("读取数据为空");
            return OperateResult<bool>.Success((r.Content[0] & 0x01) != 0);
        }

        /// <inheritdoc/>
        public override OperateResult Write(string address, bool value)
        {
            // 命名区域位写入
            if (IsNamedAddress(address))
            {
                var cmdResult = BuildWriteMultiCoilCommand(address, new[] { value });
                if (!cmdResult.IsSuccess) return OperateResult.Failed(cmdResult.Message);

                byte[] innerCmd = cmdResult.Content;
                byte[] fullCmd = WrapWithOuterHeader(innerCmd);
                var sendResult = SendAndReceive(fullCmd);
                if (!sendResult.IsSuccess) return OperateResult.Failed(sendResult.Message, sendResult.ErrorCode);

                var unwrapResult = UnwrapOuterHeader(sendResult.Content);
                if (!unwrapResult.IsSuccess) return OperateResult.Failed(unwrapResult.Message);

                var parseResult = ParseResponse(innerCmd, unwrapResult.Content);
                if (!parseResult.IsSuccess) return OperateResult.Failed(parseResult.Message, parseResult.ErrorCode);
                return OperateResult.Success();
            }

            // 标准单线圈写入
            if (!ushort.TryParse(address, out ushort addrValue))
                return OperateResult.Failed($"地址格式无效: {address}");

            var singleCmd = BuildWriteSingleCoilCommand(addrValue, value);
            if (!singleCmd.IsSuccess) return OperateResult.Failed(singleCmd.Message);

            byte[] inner = singleCmd.Content;
            byte[] full = WrapWithOuterHeader(inner);
            var send = SendAndReceive(full);
            if (!send.IsSuccess) return OperateResult.Failed(send.Message, send.ErrorCode);

            var unwrap = UnwrapOuterHeader(send.Content);
            if (!unwrap.IsSuccess) return OperateResult.Failed(unwrap.Message);

            var parse = ParseResponse(inner, unwrap.Content);
            if (!parse.IsSuccess) return OperateResult.Failed(parse.Message, parse.ErrorCode);
            return OperateResult.Success();
        }

        /// <inheritdoc/>
        public override OperateResult Write(string address, short value)
            => Write(address, BitConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, ushort value)
            => Write(address, BitConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, int value)
            => Write(address, BitConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, uint value)
            => Write(address, BitConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, long value)
            => Write(address, BitConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, ulong value)
            => Write(address, BitConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, float value)
            => Write(address, BitConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, double value)
            => Write(address, BitConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, string value)
            => Write(address, Encoding.ASCII.GetBytes(value ?? string.Empty));

        /// <inheritdoc/>
        public override string ToString() => $"Memobus[{Ip}:{Port}]";

        #endregion

        #region 工具方法

        /// <summary>
        /// 从地址字符串中提取参数。格式: key=value;address。
        /// 提取后从地址中移除参数前缀。
        /// </summary>
        private static byte ExtractParameter(ref string address, string key, byte defaultValue)
        {
            string prefix = $"{key}=";
            int idx = address.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return defaultValue;

            int valueStart = idx + prefix.Length;
            int semicolon = address.IndexOf(';', valueStart);
            if (semicolon < 0) return defaultValue;

            string valueStr = address.Substring(valueStart, semicolon - valueStart);
            address = address.Substring(semicolon + 1);
            return Convert.ToByte(valueStr, 10);
        }

        /// <summary>
        /// 按 16 位字反转字节序（CDAB ↔ ABCD）。
        /// </summary>
        public static byte[] ReverseWords(byte[] data)
        {
            if (data == null) return new byte[0];
            byte[] result = new byte[data.Length];
            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                result[i] = data[i + 1];
                result[i + 1] = data[i];
            }
            if (data.Length % 2 != 0)
                result[data.Length - 1] = data[data.Length - 1];
            return result;
        }

        /// <summary>
        /// 将 bool 数组转换为位字节序列。
        /// </summary>
        public static byte[] BoolArrayToBytes(bool[] values)
        {
            byte[] result = new byte[(values.Length + 7) / 8];
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i])
                    result[i / 8] |= (byte)(1 << (i % 8));
            }
            return result;
        }

        /// <summary>
        /// 从字节数组中提取 bool 数组。
        /// </summary>
        public static bool[] BytesToBoolArray(byte[] data, int count)
        {
            bool[] result = new bool[count];
            for (int i = 0; i < count && i < data.Length * 8; i++)
            {
                result[i] = (data[i / 8] & (1 << (i % 8))) != 0;
            }
            return result;
        }

        #endregion

        // ═══════════════════════════════════════════
        //  IBatchReadWrite 实现
        // ═══════════════════════════════════════════

        /// <inheritdoc/>
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, object?>();
            foreach (string addr in addresses)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = (object?)r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        /// <inheritdoc/>
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchRead(addresses), cancellationToken);

        /// <inheritdoc/>
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, byte[]>();
            foreach (string addr in addresses)
            {
                var r = ReadBytes(addr, 2);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        /// <inheritdoc/>
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => RandomRead(addresses), cancellationToken);

        /// <inheritdoc/>
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            foreach (var kv in items)
            {
                OperateResult r = kv.Value switch
                {
                    bool v => Write(kv.Key, v),
                    short v => Write(kv.Key, v),
                    ushort v => Write(kv.Key, v),
                    int v => Write(kv.Key, v),
                    uint v => Write(kv.Key, v),
                    long v => Write(kv.Key, v),
                    ulong v => Write(kv.Key, v),
                    float v => Write(kv.Key, v),
                    double v => Write(kv.Key, v),
                    string v => Write(kv.Key, v),
                    byte[] v => Write(kv.Key, v),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        /// <inheritdoc/>
        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchWrite(items), cancellationToken);

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
    }
}
