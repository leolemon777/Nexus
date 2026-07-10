using System;
using System.Text;

namespace Nexus.Idec
{
    /// <summary>
    /// IDEC Computer Link 响应解析结果（不可变结构体）。
    /// </summary>
    public struct IdecResponse
    {
        /// <summary>是否成功（STX/ACK 为 true，NAK 为 false）。</summary>
        public bool IsSuccess;

        /// <summary>是否携带读数据（仅 STX 成功响应为 true；ACK 无数据）。</summary>
        public bool HasData;

        /// <summary>成功响应中的 ASCII-HEX 数据（STX 场景），无数据时为空串。</summary>
        public string Data;

        /// <summary>NAK 失败响应的错误码（1 char），成功时为 '\0'。</summary>
        public char ErrorCode;

        /// <summary>BCC 校验是否通过。</summary>
        public bool BccValid;

        /// <summary>响应类型描述（STX/ACK/NAK/Unknown）。</summary>
        public string Kind;

        /// <summary>构造一个成功响应。</summary>
        public static IdecResponse Ok(string data, bool bccValid)
            => new IdecResponse
            {
                IsSuccess = true,
                HasData = data.Length > 0,
                Data = data ?? string.Empty,
                ErrorCode = '\0',
                BccValid = bccValid,
                Kind = data.Length > 0 ? "STX" : "ACK"
            };

        /// <summary>构造一个 NAK 失败响应。</summary>
        public static IdecResponse Nak(char errorCode, bool bccValid)
            => new IdecResponse
            {
                IsSuccess = false,
                HasData = false,
                Data = string.Empty,
                ErrorCode = errorCode,
                BccValid = bccValid,
                Kind = "NAK"
            };
    }

    /// <summary>
    /// IDEC Computer Link 纯函数帧构造器（便于离线单元测试）。
    /// <para>所有方法无副作用、无 IO，纯字节/字符串运算。</para>
    /// <para>帧格式（ASCII 文本帧）：</para>
    /// <para>· 读请求:  [ENQ][站号1hex][命令2][类型码1][operand 6位右对齐][count 2位][ETX][BCC 2][CR]</para>
    /// <para>· 写请求:  [ENQ][站号1hex][命令2][类型码1][operand 6位右对齐][count 2位][数据HEX][ETX][BCC 2][CR]</para>
    /// <para>· 读成功:  [STX][站号][数据HEX][ETX][BCC 2][CR]</para>
    /// <para>· 写成功:  [ACK][站号][BCC 2][CR]</para>
    /// <para>· 失败:    [NAK][站号][错误码1][BCC 2][CR]</para>
    /// <para>BCC = 从站号到 BCC 前一字节（含 ETX）全部字节的 XOR，表示为 2 位 ASCII-HEX（大写）。</para>
    /// <para>来源: IDEC MicroSmart Communication Protocol Manual (fc4a_protocol_im.pdf，公开手册)。</para>
    /// </summary>
    public static class IdecFrame
    {
        /// <summary>operand 号码定宽位数（右对齐，前导零填充）。</summary>
        public const int OperandWidth = 6;

        /// <summary>count（点数）定宽位数。</summary>
        public const int CountWidth = 2;

        /// <summary>BCC 字段宽度（2 位 ASCII-HEX）。</summary>
        public const int BccWidth = 2;

        // ═══════════════════════════════════════════
        //  BCC 计算
        // ═══════════════════════════════════════════

        /// <summary>
        /// 计算 BCC：对字符串的全部 ASCII 字节做 XOR。
        /// <para>调用方应传入「从站号到 BCC 前一字节（含 ETX）」的字符串。</para>
        /// </summary>
        /// <param name="fromStationToBeforeBcc">参与校验的 ASCII 文本段。</param>
        /// <returns>1 字节 XOR 结果。</returns>
        public static byte ComputeBcc(string fromStationToBeforeBcc)
        {
            if (string.IsNullOrEmpty(fromStationToBeforeBcc))
                return 0;

            byte[] bytes = Encoding.ASCII.GetBytes(fromStationToBeforeBcc);
            byte bcc = 0;
            for (int i = 0; i < bytes.Length; i++)
                bcc ^= bytes[i];
            return bcc;
        }

        /// <summary>计算一段字节数组的 XOR（用于响应校验）。</summary>
        internal static byte ComputeBcc(byte[] bytes, int offset, int length)
        {
            byte bcc = 0;
            for (int i = 0; i < length; i++)
                bcc ^= bytes[offset + i];
            return bcc;
        }

        /// <summary>站号转 1 位大写 hex char（0-F）。</summary>
        /// <param name="station">站号（0-15）。</param>
        /// <returns>1 位 hex 字符的字符串。</returns>
        public static string FormatStationHex(byte station)
        {
            int s = station & 0x0F;
            return s < 10
                ? ((char)('0' + s)).ToString()
                : ((char)('A' + (s - 10))).ToString();
        }

        // ═══════════════════════════════════════════
        //  请求帧构造
        // ═══════════════════════════════════════════

        /// <summary>
        /// 构造读请求帧。
        /// <para>结果: [ENQ][站号][命令][类型码][operand定宽6][count定宽2][ETX][BCC 2][CR]</para>
        /// </summary>
        /// <param name="station">站号（0-15）。</param>
        /// <param name="command">命令族（如 "R2"）。</param>
        /// <param name="dataTypeCode">数据类型码（如 'D'）。</param>
        /// <param name="startOperand">起始 operand 号码（十进制值，X/Y 已按八进制解析）。</param>
        /// <param name="count">读取点数（字数或位数）。</param>
        /// <returns>ASCII 字节帧。</returns>
        public static byte[] BuildReadRequest(byte station, string command, char dataTypeCode, int startOperand, ushort count)
        {
            string stationHex = FormatStationHex(station);
            string operandStr = startOperand.ToString("D" + OperandWidth).PadLeft(OperandWidth, '0');
            string countStr = ((int)count).ToString("D" + CountWidth).PadLeft(CountWidth, '0');

            // BCC 覆盖: 站号 + 命令 + 类型码 + operand + count + ETX
            string body = stationHex + command + dataTypeCode + operandStr + countStr + (char)IdecFrameControl.ETX;
            byte bcc = ComputeBcc(body);
            string bccStr = bcc.ToString("X2");

            string frame = (char)IdecFrameControl.ENQ + body + bccStr + (char)IdecFrameControl.CR;
            return Encoding.ASCII.GetBytes(frame);
        }

        /// <summary>
        /// 构造写请求帧（word 设备场景：count 由数据段长度按 4 hex/word 推导）。
        /// <para>结果: [ENQ][站号][命令][类型码][operand定宽6][count定宽2][数据HEX][ETX][BCC 2][CR]</para>
        /// </summary>
        /// <param name="station">站号（0-15）。</param>
        /// <param name="command">命令族（如 "W2"）。</param>
        /// <param name="dataTypeCode">数据类型码。</param>
        /// <param name="startOperand">起始 operand 号码。</param>
        /// <param name="dataHex">ASCII-HEX 数据段（word: 4 hex/word；bit: 1 char/bit）。</param>
        /// <returns>ASCII 字节帧。</returns>
        public static byte[] BuildWriteRequest(byte station, string command, char dataTypeCode, int startOperand, string dataHex)
        {
            // 默认按 word 设备推导 count（4 hex/word）
            ushort count = (ushort)(dataHex.Length / 4);
            return BuildWriteRequest(station, command, dataTypeCode, startOperand, count, dataHex);
        }

        /// <summary>
        /// 构造写请求帧（显式指定点数，bit 场景使用）。
        /// <para>结果: [ENQ][站号][命令][类型码][operand定宽6][count定宽2][数据HEX][ETX][BCC 2][CR]</para>
        /// </summary>
        /// <param name="station">站号（0-15）。</param>
        /// <param name="command">命令族（如 "W2"）。</param>
        /// <param name="dataTypeCode">数据类型码。</param>
        /// <param name="startOperand">起始 operand 号码。</param>
        /// <param name="count">写入点数（字数或位数）。</param>
        /// <param name="dataHex">ASCII-HEX 数据段。</param>
        /// <returns>ASCII 字节帧。</returns>
        public static byte[] BuildWriteRequest(byte station, string command, char dataTypeCode, int startOperand, ushort count, string dataHex)
        {
            string stationHex = FormatStationHex(station);
            string operandStr = startOperand.ToString("D" + OperandWidth).PadLeft(OperandWidth, '0');
            string countStr = ((int)count).ToString("D" + CountWidth).PadLeft(CountWidth, '0');

            // BCC 覆盖: 站号 + 命令 + 类型码 + operand + count + 数据 + ETX
            string body = stationHex + command + dataTypeCode + operandStr + countStr + (dataHex ?? string.Empty) + (char)IdecFrameControl.ETX;
            byte bcc = ComputeBcc(body);
            string bccStr = bcc.ToString("X2");

            string frame = (char)IdecFrameControl.ENQ + body + bccStr + (char)IdecFrameControl.CR;
            return Encoding.ASCII.GetBytes(frame);
        }

        // ═══════════════════════════════════════════
        //  响应帧解析
        // ═══════════════════════════════════════════

        /// <summary>
        /// 解析响应帧（不含末尾 CR）。
        /// <para>首字节判断: STX=读成功(带数据), ACK=写成功(无数据), NAK=失败(错误码)。</para>
        /// </summary>
        /// <param name="response">响应字节（从 STX/ACK/NAK 起，到 BCC 结束，不含 CR）。</param>
        /// <returns>解析结果 <see cref="IdecResponse"/>。</returns>
        public static IdecResponse ParseResponse(byte[] response)
        {
            if (response == null || response.Length == 0)
                return new IdecResponse { IsSuccess = false, ErrorCode = '?', BccValid = false, Kind = "Empty", Data = string.Empty };

            byte head = response[0];

            if (head == IdecFrameControl.STX)
                return ParseStx(response);

            if (head == IdecFrameControl.ACK)
                return ParseAck(response);

            if (head == IdecFrameControl.NAK)
                return ParseNak(response);

            // 未知帧头
            return new IdecResponse
            {
                IsSuccess = false,
                ErrorCode = '?',
                BccValid = false,
                Kind = "Unknown",
                Data = Encoding.ASCII.GetString(response)
            };
        }

        /// <summary>解析 STX 读成功响应: [STX][站号][数据...][ETX][BCC 2]。</summary>
        private static IdecResponse ParseStx(byte[] response)
        {
            // 定位 ETX（数据为 ASCII-HEX，>=0x30，不会与 ETX=0x03 冲突）
            int etxIdx = -1;
            for (int i = 1; i < response.Length; i++)
            {
                if (response[i] == IdecFrameControl.ETX) { etxIdx = i; break; }
            }

            if (etxIdx < 0 || response.Length < etxIdx + 1 + BccWidth)
            {
                return new IdecResponse
                {
                    IsSuccess = false,
                    ErrorCode = '?',
                    BccValid = false,
                    Kind = "STX(truncated)",
                    Data = string.Empty
                };
            }

            // 数据 = response[2 .. etxIdx)（站号占 response[1]）
            string data = etxIdx > 2
                ? Encoding.ASCII.GetString(response, 2, etxIdx - 2)
                : string.Empty;

            // BCC 校验: XOR response[1 .. etxIdx]（站号..ETX）
            byte calc = ComputeBcc(response, 1, etxIdx);
            string receivedBcc = Encoding.ASCII.GetString(response, etxIdx + 1, BccWidth);
            bool bccValid = calc.ToString("X2") == receivedBcc;

            return IdecResponse.Ok(data, bccValid);
        }

        /// <summary>解析 ACK 写成功响应: [ACK][站号][BCC 2]。</summary>
        private static IdecResponse ParseAck(byte[] response)
        {
            // 至少: ACK + 站号 + BCC(2)
            if (response.Length < 1 + 1 + BccWidth)
            {
                return new IdecResponse
                {
                    IsSuccess = false,
                    ErrorCode = '?',
                    BccValid = false,
                    Kind = "ACK(truncated)",
                    Data = string.Empty
                };
            }

            // BCC 校验: XOR response[1]（站号）
            byte calc = ComputeBcc(response, 1, 1);
            string receivedBcc = Encoding.ASCII.GetString(response, 2, BccWidth);
            bool bccValid = calc.ToString("X2") == receivedBcc;

            return IdecResponse.Ok(string.Empty, bccValid);
        }

        /// <summary>解析 NAK 失败响应: [NAK][站号][错误码 1][BCC 2]。</summary>
        private static IdecResponse ParseNak(byte[] response)
        {
            // 至少: NAK + 站号 + 错误码 + BCC(2)
            if (response.Length < 1 + 1 + 1 + BccWidth)
            {
                return new IdecResponse
                {
                    IsSuccess = false,
                    ErrorCode = '?',
                    BccValid = false,
                    Kind = "NAK(truncated)",
                    Data = string.Empty
                };
            }

            char errorCode = (char)response[2];

            // BCC 校验: XOR response[1..2]（站号 + 错误码）
            byte calc = ComputeBcc(response, 1, 2);
            string receivedBcc = Encoding.ASCII.GetString(response, 3, BccWidth);
            bool bccValid = calc.ToString("X2") == receivedBcc;

            return IdecResponse.Nak(errorCode, bccValid);
        }

        // ═══════════════════════════════════════════
        //  数据格式辅助
        // ═══════════════════════════════════════════

        /// <summary>ASCII-HEX 字符串 → 字节数组（每 2 个 hex char = 1 byte）。奇数长度则末尾忽略。</summary>
        /// <param name="hex">hex 字符串。</param>
        /// <returns>原始字节数组。</returns>
        public static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();

            int byteCount = hex.Length / 2;
            byte[] result = new byte[byteCount];
            for (int i = 0; i < byteCount; i++)
            {
                string pair = hex.Substring(i * 2, 2);
                result[i] = Convert.ToByte(pair, 16);
            }
            return result;
        }

        /// <summary>字节数组 → 大写 ASCII-HEX 字符串（每 byte = 2 hex char）。</summary>
        /// <param name="bytes">原始字节数组。</param>
        /// <returns>hex 字符串。</returns>
        public static string BytesToHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;

            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("X2"));
            return sb.ToString();
        }
    }
}
