using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nexus.AllenBradley
{
    /// <summary>
    /// PCCC (Programmable Controller Communication Commands) 客户端 — 支持 SLC / PLC-5 等传统设备。
    /// <para>协议层次: TCP → ENIP (Encapsulation) → CIP (ExecutePCCC 0x4B) → PCCC (0x0F)</para>
    /// <para>PCCC 功能码: Protected Typed Logical Read (0xA2), Write (0xAA), Mask Write (0xAB)</para>
    /// <para>支持数据文件: N (Integer), B (Bit), T (Timer), C (Counter), F (Float), ST (String), R (Control), S (Status), L (Long), I (Input), O (Output), A (ASCII)</para>
    /// </summary>
    public class PcccClient : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        private uint _sessionHandle;
        private int _transactionCounter;

        /// <summary>目标槽号（背板槽号，默认 0）。</summary>
        public byte Slot { get; set; }

        /// <summary>PCCC 数据字节序（默认 LittleEndian，PCCC/SLC 标准）。</summary>
        public Endianness ByteOrder { get; set; } = Endianness.LittleEndian;

        public PcccClient(string ipAddress, int port = 44818, byte slot = 0, int timeout = 5000)
            : base(ipAddress, port, timeout)
        {
            Slot = slot;
        }

        // ═══════════════════════════════════════════
        //  PCCC 数据文件类型码
        // ═══════════════════════════════════════════

        /// <summary>PCCC 数据文件类型码。</summary>
        private enum PcccFileType : byte
        {
            Output  = 0x82,  // O
            Input   = 0x83,  // I
            Status  = 0x84,  // S
            Bit     = 0x85,  // B
            Timer   = 0x86,  // T
            Counter = 0x87,  // C
            Control = 0x88,  // R
            Integer = 0x89,  // N
            Float   = 0x8A,  // F
            String  = 0x8D,  // ST
            Ascii   = 0x8E,  // A
            Long    = 0x91,  // L
        }

        // ═══════════════════════════════════════════
        //  PCCC 地址解析
        // ═══════════════════════════════════════════

        /// <summary>
        /// PCCC 地址解析结果。
        /// </summary>
        public struct PcccAddress
        {
            /// <summary>数据文件类型码。</summary>
            public byte DataCode;
            /// <summary>文件号（如 N7 中的 7）。</summary>
            public ushort FileNumber;
            /// <summary>元素号（如 N7:0 中的 0）。</summary>
            public ushort Element;
            /// <summary>子元素/位偏移（通常为 0）。</summary>
            public ushort SubElement;

            /// <inheritdoc/>
            public override string ToString()
                => $"File={FileNumber} DataCode=0x{DataCode:X2} Elem={Element} Sub={SubElement}";
        }

        /// <summary>
        /// 解析 PCCC 数据文件地址（公开方法，可供测试使用）。
        /// <para>支持: N7:0, B3:0, T4:0, C5:0, F8:0, ST9:0, R6:0, S2:0, L10:0</para>
        /// <para>支持 I/O: I1:0, O0:0</para>
        /// <para>支持位寻址: B3:0/5, N7:0.1 (通过 / 或 . 后缀)</para>
        /// <para>默认文件号: S→2, I→1, O→0, ST→1</para>
        /// </summary>
        public static PcccAddress ParseAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空", nameof(address));

            string addr = address.Trim().ToUpperInvariant();
            var result = new PcccAddress { SubElement = 0 };

            // 解析文件类型字母
            char typeChar = addr[0];
            int numStart = 1;

            // 处理双字母类型 ST
            if (typeChar == 'S' && addr.Length > 1 && addr[1] == 'T')
            {
                result.DataCode = (byte)PcccFileType.String;
                numStart = 2;
            }
            else
            {
                result.DataCode = typeChar switch
                {
                    'N' => (byte)PcccFileType.Integer,
                    'B' => (byte)PcccFileType.Bit,
                    'T' => (byte)PcccFileType.Timer,
                    'C' => (byte)PcccFileType.Counter,
                    'F' => (byte)PcccFileType.Float,
                    'R' => (byte)PcccFileType.Control,
                    'S' => (byte)PcccFileType.Status,
                    'I' => (byte)PcccFileType.Input,
                    'O' => (byte)PcccFileType.Output,
                    'A' => (byte)PcccFileType.Ascii,
                    'L' => (byte)PcccFileType.Long,
                    _ => throw new ArgumentException($"不支持的 PCCC 文件类型: {typeChar}", nameof(address))
                };
            }

            // 解析文件号:元素号
            string remainder = addr.Substring(numStart);
            string[] parts = remainder.Split(':');
            if (parts.Length < 2)
                throw new ArgumentException($"地址格式错误，需要 '文件号:元素号' (如 N7:0): {address}", nameof(address));

            // 文件号 — 部分类型有默认值
            string filePart = parts[0];
            result.FileNumber = result.DataCode switch
            {
                (byte)PcccFileType.Status => (ushort)(filePart.Length == 0 ? 2 : ushort.Parse(filePart)),
                (byte)PcccFileType.Input  => (ushort)(filePart.Length == 0 ? 1 : ushort.Parse(filePart)),
                (byte)PcccFileType.Output => (ushort)(filePart.Length == 0 ? 0 : ushort.Parse(filePart)),
                (byte)PcccFileType.String => (ushort)(filePart.Length == 0 ? 1 : ushort.Parse(filePart)),
                _ => ushort.Parse(filePart)
            };

            // 元素号 + 位偏移（B3:0/5 或 N7:0.1 语法）
            string elemPart = parts[1];
            int slashIdx = elemPart.IndexOf('/');
            int dotIdx = elemPart.IndexOf('.');

            if (slashIdx >= 0)
            {
                result.Element = ushort.Parse(elemPart.Substring(0, slashIdx));
                result.SubElement = ushort.Parse(elemPart.Substring(slashIdx + 1));
            }
            else if (dotIdx >= 0)
            {
                result.Element = ushort.Parse(elemPart.Substring(0, dotIdx));
                result.SubElement = ushort.Parse(elemPart.Substring(dotIdx + 1));
            }
            else
            {
                result.Element = ushort.Parse(elemPart);
            }

            return result;
        }

        // ═══════════════════════════════════════════
        //  PCCC 命令构建 — Protected Typed Logical
        // ═══════════════════════════════════════════

        /// <summary>
        /// 构建 PCCC Protected Typed Logical Read (0x0F/0xA2) 命令。
        /// </summary>
        private byte[] BuildPcccReadCommand(PcccAddress addr, int byteCount)
        {
            int tns = GetNextTns();
            var ms = new MemoryStream();
            ms.WriteByte(0x0F);                          // Command: PCCC
            ms.WriteByte(0x00);                          // Status
            ms.WriteByte((byte)(tns & 0xFF));            // TNS low
            ms.WriteByte((byte)((tns >> 8) & 0xFF));     // TNS high
            ms.WriteByte(0xA2);                          // Function: Protected Typed Logical Read
            ms.WriteByte((byte)(byteCount & 0xFF));      // Byte count
            WritePcccLength(ms, addr.FileNumber);        // File number
            ms.WriteByte(addr.DataCode);                 // Data type code
            WritePcccLength(ms, addr.Element);           // Element number
            WritePcccLength(ms, addr.SubElement);        // Sub-element
            return ms.ToArray();
        }

        /// <summary>
        /// 构建 PCCC Protected Typed Logical Write (0x0F/0xAA) 命令。
        /// </summary>
        private byte[] BuildPcccWriteCommand(PcccAddress addr, byte[] data)
        {
            int tns = GetNextTns();
            var ms = new MemoryStream();
            ms.WriteByte(0x0F);                          // Command: PCCC
            ms.WriteByte(0x00);                          // Status
            ms.WriteByte((byte)(tns & 0xFF));            // TNS low
            ms.WriteByte((byte)((tns >> 8) & 0xFF));     // TNS high
            ms.WriteByte(0xAA);                          // Function: Protected Typed Logical Write
            ms.WriteByte((byte)(data.Length & 0xFF));    // Data length
            WritePcccLength(ms, addr.FileNumber);        // File number
            ms.WriteByte(addr.DataCode);                 // Data type code
            WritePcccLength(ms, addr.Element);           // Element number
            WritePcccLength(ms, addr.SubElement);        // Sub-element
            ms.Write(data, 0, data.Length);              // Data
            return ms.ToArray();
        }

        /// <summary>
        /// 构建 PCCC Protected Typed Logical Mask Write (0x0F/0xAB) 命令 — 用于位操作。
        /// <para>结果 = (old_value &amp; andMask) | orMask</para>
        /// </summary>
        private byte[] BuildPcccMaskWriteCommand(PcccAddress addr, ushort andMask, ushort orMask)
        {
            int tns = GetNextTns();
            var ms = new MemoryStream();
            ms.WriteByte(0x0F);                          // Command: PCCC
            ms.WriteByte(0x00);                          // Status
            ms.WriteByte((byte)(tns & 0xFF));            // TNS low
            ms.WriteByte((byte)((tns >> 8) & 0xFF));     // TNS high
            ms.WriteByte(0xAB);                          // Function: Protected Typed Logical Mask
            ms.WriteByte(0x02);                          // Byte count (always 2 for mask)
            WritePcccLength(ms, addr.FileNumber);        // File number
            ms.WriteByte(addr.DataCode);                 // Data type code
            WritePcccLength(ms, addr.Element);           // Element number
            WritePcccLength(ms, addr.SubElement);        // Sub-element
            // AND mask (LE)
            ms.WriteByte((byte)(andMask & 0xFF));
            ms.WriteByte((byte)((andMask >> 8) & 0xFF));
            // OR mask (LE)
            ms.WriteByte((byte)(orMask & 0xFF));
            ms.WriteByte((byte)((orMask >> 8) & 0xFF));
            return ms.ToArray();
        }

        /// <summary>
        /// 写入 PCCC 长度字段（短编码: &lt;255 为 1 字节，否则 0xFF + 2 字节 LE）。
        /// </summary>
        private static void WritePcccLength(MemoryStream ms, ushort value)
        {
            if (value < 255)
            {
                ms.WriteByte((byte)value);
            }
            else
            {
                ms.WriteByte(0xFF);
                ms.WriteByte((byte)(value & 0xFF));
                ms.WriteByte((byte)((value >> 8) & 0xFF));
            }
        }

        /// <summary>
        /// 从 PCCC 命令中读取长度字段（短编码: &lt;255 为 1 字节，否则 0xFF + 2 字节 LE）。
        /// </summary>
        public static int ReadPcccLength(byte[] data, int offset, out int bytesRead)
        {
            if (data[offset] == 0xFF)
            {
                bytesRead = 3;
                return data[offset + 1] | (data[offset + 2] << 8);
            }
            bytesRead = 1;
            return data[offset];
        }

        private int GetNextTns() => Interlocked.Increment(ref _transactionCounter) & 0xFFFF;

        // ═══════════════════════════════════════════
        //  CIP Execute PCCC 封装 (Service 0x4B)
        // ═══════════════════════════════════════════

        /// <summary>
        /// 将 PCCC 命令封装到 CIP Execute PCCC 服务 (0x4B) 中。
        /// </summary>
        public static byte[] WrapInCipExecutePccc(byte[] pcccData)
        {
            // CIP Execute PCCC: Service(1) + PathSize(1) + Path(4) + Params(6) + PCCC data
            // Path: 0x20, 0x67 (class) + 0x24, 0x01 (instance)
            // Params: ConnectionID(2,LE) + ConnectionSerial(4,LE)
            var ms = new MemoryStream();
            ms.WriteByte(0x4B);   // Execute PCCC service code
            ms.WriteByte(0x02);   // Request path size (2 words)
            ms.WriteByte(0x20);   // Logical segment (class)
            ms.WriteByte(0x67);   // Class ID: PCCC Execute (0x67)
            ms.WriteByte(0x24);   // Logical segment (instance)
            ms.WriteByte(0x01);   // Instance 1
            // Connection parameters
            ms.WriteByte(0x09);   // Connection ID low
            ms.WriteByte(0x10);   // Connection ID high (0x1009)
            ms.WriteByte(0x0B);   // Connection serial byte 0
            ms.WriteByte(0x46);   // Connection serial byte 1
            ms.WriteByte(0xA5);   // Connection serial byte 2
            ms.WriteByte(0xC1);   // Connection serial byte 3
            // PCCC data
            ms.Write(pcccData, 0, pcccData.Length);
            return ms.ToArray();
        }

        // ═══════════════════════════════════════════
        //  ENIP SendRRData 封装
        // ═══════════════════════════════════════════

        /// <summary>构建 ENIP SendRRData 帧并发送，返回 CIP 响应数据。</summary>
        private OperateResult<byte[]> SendPcccViaEnip(byte[] pcccData)
        {
            // 封装到 CIP Execute PCCC
            byte[] cipData = WrapInCipExecutePccc(pcccData);

            // 封装到 ENIP SendRRData
            byte[] sendRRData = BuildSendRRData(cipData);
            byte[] enipFrame = BuildEnipFrame(0x006F, sendRRData);

            var sendResult = SendAndReceive(enipFrame);
            if (!sendResult.IsSuccess)
                return OperateResult<byte[]>.Failed(sendResult.Message, sendResult.ErrorCode);

            byte[] response = sendResult.Content;
            if (response.Length < 24)
                return OperateResult<byte[]>.Failed("ENIP 响应太短");

            // 解析 ENIP 响应头
            uint respStatus = (uint)(response[8] | (response[9] << 8) | (response[10] << 16) | (response[11] << 24));
            if (respStatus != 0)
                return OperateResult<byte[]>.Failed($"ENIP 错误: Status=0x{respStatus:X8}");

            ushort respLen = (ushort)(response[2] | (response[3] << 8));
            if (respLen < 6)
                return OperateResult<byte[]>.Failed("SendRRData 响应太短");

            // 解析 SendRRData: InterfaceHandle(4) + Timeout(2) + ItemCount(2) +
            //   Item1: Type(2) + Length(2) + Item2: Type(2) + Length(2) + Data
            int offset = 24 + 4 + 2 + 2; // skip header + InterfaceHandle + Timeout + ItemCount
            offset += 4; // skip Item1 Type + Length
            if (offset + 4 > response.Length)
                return OperateResult<byte[]>.Failed("SendRRData Item2 缺失");

            ushort item2Len = (ushort)(response[offset + 2] | (response[offset + 3] << 8));
            offset += 4;

            if (offset + item2Len > response.Length)
                return OperateResult<byte[]>.Failed("SendRRData 数据不完整");

            byte[] cipResponse = new byte[item2Len];
            Buffer.BlockCopy(response, offset, cipResponse, 0, item2Len);

            // CIP 响应: ReplyService(1) + Reserved(1) + Status(1) + ExtStatusSize(1)
            if (cipResponse.Length < 4)
                return OperateResult<byte[]>.Failed("CIP Reply 太短");

            byte cipStatus = cipResponse[2];
            if (cipStatus != 0)
                return OperateResult<byte[]>.Failed($"CIP 错误: 0x{cipStatus:X2}");

            int extSize = cipResponse[3];
            int dataOffset = 4 + extSize * 2;
            if (dataOffset >= cipResponse.Length)
                return OperateResult<byte[]>.Success(new byte[0]);

            byte[] data = new byte[cipResponse.Length - dataOffset];
            Buffer.BlockCopy(cipResponse, dataOffset, data, 0, data.Length);

            // PCCC 响应解析
            return ParsePcccResponse(data);
        }

        /// <summary>解析 PCCC 响应，返回数据部分。</summary>
        public static OperateResult<byte[]> ParsePcccResponse(byte[] response)
        {
            if (response == null || response.Length < 2)
                return OperateResult<byte[]>.Failed("PCCC 响应太短");

            // PCCC 响应: Command(1) + Status(1) + TNS(2) + ExtStatus(1) + Data...
            byte status = response[1];
            if (status != 0)
            {
                string desc = GetStatusDescription(response);
                return OperateResult<byte[]>.Failed($"PCCC 错误: 0x{status:X2} - {desc}", status);
            }

            // 跳过头部: Command(1) + Status(1) + TNS(2) + ExtStatus(1) = 5 bytes
            int headerLen = response.Length > 4 ? 5 : 2;
            if (response.Length <= headerLen)
                return OperateResult<byte[]>.Success(new byte[0]);

            byte[] data = new byte[response.Length - headerLen];
            Buffer.BlockCopy(response, headerLen, data, 0, data.Length);
            return OperateResult<byte[]>.Success(data);
        }

        /// <summary>获取 PCCC 错误状态描述。</summary>
        public static string GetStatusDescription(byte[] response)
        {
            if (response == null || response.Length < 2) return "未知错误";
            byte status = response[1];

            // 如果 status 的高 nibble 为 0xF0，检查 ExtStatus
            if ((status & 0xF0) == 0xF0 && response.Length > 4)
            {
                byte extStatus = response[4];
                return extStatus switch
                {
                    0x01 => "A field has an illegal value",
                    0x02 => "Less levels specified in address than minimum",
                    0x03 => "More levels specified in address than system supports",
                    0x04 => "Symbol not found",
                    0x05 => "Symbol is of improper format",
                    0x06 => "Address doesn't point to something usable",
                    0x07 => "File is wrong size",
                    0x09 => "Data or file is too large",
                    0x0A => "Transaction size plus word address is too large",
                    0x0B => "Access denied, improper privilege",
                    0x0E => "Command cannot be executed",
                    0x11 => "Illegal data type",
                    0x12 => "Invalid parameter or invalid data",
                    _ => $"PCCC Ext Error: 0x{extStatus:X2}"
                };
            }

            return status switch
            {
                0x10 => "Illegal command or format",
                0x20 => "Host has a problem and will not communicate",
                0x30 => "Remote node host is missing, disconnected, or shut down",
                0x40 => "Host could not complete function due to hardware fault",
                0x50 => "Addressing problem or memory protect rungs",
                0x60 => "Function not allowed due to command protection selection",
                0x70 => "Processor is in Program mode",
                0x80 => "Compatibility mode file missing or communication zone problem",
                0x90 => "Remote node cannot buffer command",
                _ => $"PCCC Error: 0x{status:X2}"
            };
        }

        private byte[] BuildSendRRData(byte[] cipData)
        {
            int dataLen = cipData.Length;
            int totalLen = 4 + 2 + 2 + 2 + 2 + 2 + 2 + dataLen;

            byte[] result = new byte[totalLen];
            int i = 0;
            // Interface Handle = 0
            result[i++] = 0; result[i++] = 0; result[i++] = 0; result[i++] = 0;
            // Timeout = 0
            result[i++] = 0; result[i++] = 0;
            // Item Count = 2
            result[i++] = 2; result[i++] = 0;
            // Item 1: Null Address (0x0000)
            result[i++] = 0x00; result[i++] = 0x00;
            result[i++] = 0x00; result[i++] = 0x00;
            // Item 2: Unconnected Data (0x00B2)
            result[i++] = 0xB2; result[i++] = 0x00;
            result[i++] = (byte)(dataLen & 0xFF); result[i++] = (byte)((dataLen >> 8) & 0xFF);
            Buffer.BlockCopy(cipData, 0, result, i, dataLen);

            return result;
        }

        private byte[] BuildEnipFrame(ushort command, byte[] payload)
        {
            byte[] frame = new byte[24 + payload.Length];
            frame[0] = (byte)(command & 0xFF);
            frame[1] = (byte)((command >> 8) & 0xFF);
            // Length
            frame[2] = (byte)(payload.Length & 0xFF);
            frame[3] = (byte)((payload.Length >> 8) & 0xFF);
            // Session handle
            frame[4] = (byte)(_sessionHandle & 0xFF);
            frame[5] = (byte)((_sessionHandle >> 8) & 0xFF);
            frame[6] = (byte)((_sessionHandle >> 16) & 0xFF);
            frame[7] = (byte)((_sessionHandle >> 24) & 0xFF);
            // Status, SenderContext, Options = 0 (already zero)
            Buffer.BlockCopy(payload, 0, frame, 24, payload.Length);
            return frame;
        }

        // ═══════════════════════════════════════════
        //  连接管理
        // ═══════════════════════════════════════════

        public override OperateResult Connect()
        {
            var baseResult = base.Connect();
            if (!baseResult.IsSuccess) return baseResult;

            // Register ENIP Session
            byte[] regPayload = new byte[4];
            regPayload[0] = 0x01; regPayload[1] = 0x00; // Protocol Version = 1
            regPayload[2] = 0x00; regPayload[3] = 0x00; // Options = 0
            byte[] regFrame = BuildEnipFrame(0x0065, regPayload);

            var regResult = SendAndReceive(regFrame);
            if (!regResult.IsSuccess)
            {
                DisconnectCore();
                return OperateResult.Failed($"RegisterSession 失败: {regResult.Message}");
            }

            byte[] resp = regResult.Content;
            if (resp.Length >= 8)
            {
                uint respStatus = (uint)(resp[8] | (resp[9] << 8) | (resp[10] << 16) | (resp[11] << 24));
                if (respStatus != 0)
                {
                    DisconnectCore();
                    return OperateResult.Failed($"RegisterSession 返回错误: 0x{respStatus:X8}");
                }
                _sessionHandle = (uint)(resp[4] | (resp[5] << 8) | (resp[6] << 16) | (resp[7] << 24));
            }

            _transactionCounter = 0;
            Log.Debug($"PCCC 已建立 ENIP Session: 0x{_sessionHandle:X8}");
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  底层读写
        // ═══════════════════════════════════════════

        /// <summary>
        /// PCCC 底层读取 — 发送 Protected Typed Logical Read (0xA2) 命令。
        /// </summary>
        /// <param name="address">PCCC 地址（如 N7:0, T4:0, F8:0）</param>
        /// <param name="byteCount">要读取的字节数</param>
        private OperateResult<byte[]> PcccRead(string address, int byteCount)
        {
            try
            {
                var addr = ParseAddress(address);
                byte[] pcccCmd = BuildPcccReadCommand(addr, byteCount);
                return SendPcccViaEnip(pcccCmd);
            }
            catch (Exception ex)
            {
                Log.Error($"PCCC Read 异常 ({address}) — {ex.Message}");
                return OperateResult<byte[]>.Failed($"PCCC Read 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// PCCC 底层写入 — 发送 Protected Typed Logical Write (0xAA) 命令。
        /// </summary>
        private OperateResult PcccWrite(string address, byte[] data)
        {
            try
            {
                var addr = ParseAddress(address);
                byte[] pcccCmd = BuildPcccWriteCommand(addr, data);
                var result = SendPcccViaEnip(pcccCmd);
                if (!result.IsSuccess)
                    return OperateResult.Failed(result.Message, result.ErrorCode);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"PCCC Write 异常 ({address}) — {ex.Message}");
                return OperateResult.Failed($"PCCC Write 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// PCCC 底层掩码写入 — 发送 Protected Typed Logical Mask (0xAB) 命令。
        /// </summary>
        private OperateResult PcccMaskWrite(string address, ushort andMask, ushort orMask)
        {
            try
            {
                var addr = ParseAddress(address);
                byte[] pcccCmd = BuildPcccMaskWriteCommand(addr, andMask, orMask);
                var result = SendPcccViaEnip(pcccCmd);
                if (!result.IsSuccess)
                    return OperateResult.Failed(result.Message, result.ErrorCode);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"PCCC MaskWrite 异常 ({address}) — {ex.Message}");
                return OperateResult.Failed($"PCCC MaskWrite 异常: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  字节序辅助 (LittleEndian — PCCC/SLC 标准)
        // ═══════════════════════════════════════════

        private static short ToInt16LE(byte[] data, int offset = 0)
            => (short)(data[offset] | (data[offset + 1] << 8));

        private static int ToInt32LE(byte[] data, int offset = 0)
            => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

        private static float ToFloatLE(byte[] data, int offset = 0)
        {
            int v = ToInt32LE(data, offset);
            return BitConverter.ToSingle(BitConverter.GetBytes(v), 0);
        }

        private static byte[] GetBytesLE(short value)
            => new byte[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };

        private static byte[] GetBytesLE(int value)
            => new byte[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)((value >> 24) & 0xFF) };

        private static byte[] GetBytesLE(float value)
            => GetBytesLE(BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 读取
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            // 处理位寻址 (B3:0/5, N7:0.1 等)
            var addr = ParseAddress(address);
            if (addr.SubElement > 0)
            {
                // 读取包含目标位的字
                var r = PcccRead(address, 2);
                if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
                if (r.Content.Length < 2) return OperateResult<bool>.Failed("响应数据不足");
                short val = ToInt16LE(r.Content, 0);
                return OperateResult<bool>.Success((val & (1 << addr.SubElement)) != 0);
            }

            // 非位地址 — 读取 1 字节
            var rr = PcccRead(address, 1);
            if (!rr.IsSuccess) return OperateResult<bool>.Failed(rr.Message, rr.ErrorCode);
            if (rr.Content.Length < 1) return OperateResult<bool>.Failed("响应数据不足");
            return OperateResult<bool>.Success(rr.Content[0] != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = PcccRead(address, 2);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("响应数据不足");
            return OperateResult<short>.Success(ToInt16LE(r.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = PcccRead(address, 2);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<ushort>.Failed("响应数据不足");
            return OperateResult<ushort>.Success((ushort)ToInt16LE(r.Content, 0));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = PcccRead(address, 4);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("响应数据不足");
            return OperateResult<int>.Success(ToInt32LE(r.Content, 0));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = PcccRead(address, 8);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<long>.Failed("响应数据不足");
            uint lo = (uint)ToInt32LE(r.Content, 0);
            uint hi = (uint)ToInt32LE(r.Content, 4);
            return OperateResult<long>.Success(((long)hi << 32) | lo);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = PcccRead(address, 4);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("响应数据不足");
            return OperateResult<float>.Success(ToFloatLE(r.Content, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadFloat(address);
            return r.IsSuccess
                ? OperateResult<double>.Success((double)r.Content)
                : OperateResult<double>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            // ST 字符串: 2字节长度 + 数据 (字对齐)
            if (!string.IsNullOrEmpty(address) && address.TrimStart().ToUpperInvariant().StartsWith("ST"))
            {
                if (length == 0)
                {
                    // 先读 2 字节获取实际长度
                    var lenRead = PcccRead(address, 2);
                    if (!lenRead.IsSuccess) return OperateResult<string>.Failed(lenRead.Message, lenRead.ErrorCode);
                    if (lenRead.Content.Length < 2) return OperateResult<string>.Failed("ST 长度响应不足");
                    int strLen = ToInt16LE(lenRead.Content, 0);
                    int readLen = strLen + 2;
                    if (readLen % 2 != 0) readLen++; // 字对齐

                    var dataRead = PcccRead(address, readLen);
                    if (!dataRead.IsSuccess) return OperateResult<string>.Failed(dataRead.Message, dataRead.ErrorCode);
                    if (dataRead.Content.Length < 2) return OperateResult<string>.Success(string.Empty);
                    int actualLen = Math.Min(strLen, dataRead.Content.Length - 2);
                    return OperateResult<string>.Success(Encoding.ASCII.GetString(dataRead.Content, 2, actualLen));
                }
                else
                {
                    int readLen = length + 2;
                    if (readLen % 2 != 0) readLen++;
                    var r = PcccRead(address, readLen);
                    if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
                    if (r.Content.Length < 2) return OperateResult<string>.Success(string.Empty);
                    int strLen = Math.Min(ToInt16LE(r.Content, 0), r.Content.Length - 2);
                    if (strLen <= 0) return OperateResult<string>.Success(string.Empty);
                    return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content, 2, strLen));
                }
            }

            // 非 ST — 直接读取为 ASCII
            int byteLen = length > 0 ? length : 82;
            var rr = PcccRead(address, Math.Min(byteLen, 240));
            if (!rr.IsSuccess) return OperateResult<string>.Failed(rr.Message, rr.ErrorCode);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(rr.Content));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = PcccRead(address, (int)Math.Min((int)length, 240));
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return OperateResult<byte[]>.Success(r.Content);
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 写入
        // ═══════════════════════════════════════════

        public override OperateResult Write(string address, bool value)
        {
            var addr = ParseAddress(address);
            if (addr.SubElement > 0)
            {
                // 位操作 — 使用掩码写入: result = (old & andMask) | orMask
                ushort andMask = (ushort)~(1 << addr.SubElement);
                ushort orMask = value ? (ushort)(1 << addr.SubElement) : (ushort)0;
                return PcccMaskWrite(address, andMask, orMask);
            }
            return PcccWrite(address, new byte[] { value ? (byte)1 : (byte)0 });
        }

        public override OperateResult Write(string address, short value)
            => PcccWrite(address, GetBytesLE(value));

        public override OperateResult Write(string address, ushort value)
            => PcccWrite(address, GetBytesLE((short)value));

        public override OperateResult Write(string address, int value)
            => PcccWrite(address, GetBytesLE(value));

        public override OperateResult Write(string address, uint value)
            => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
            => Write(address, (int)value);

        public override OperateResult Write(string address, ulong value)
            => Write(address, (int)value);

        public override OperateResult Write(string address, float value)
            => PcccWrite(address, GetBytesLE(value));

        public override OperateResult Write(string address, double value)
            => Write(address, (float)value);

        public override OperateResult Write(string address, string value)
        {
            // ST 字符串: 2字节长度 + 数据 (字对齐)
            if (!string.IsNullOrEmpty(address) && address.TrimStart().ToUpperInvariant().StartsWith("ST"))
            {
                byte[] strBytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
                int strLen = strBytes.Length;
                int dataLen = strLen;
                if (dataLen % 2 != 0) dataLen++; // 字对齐

                byte[] data = new byte[2 + dataLen];
                data[0] = (byte)(strLen & 0xFF);
                data[1] = (byte)((strLen >> 8) & 0xFF);
                Buffer.BlockCopy(strBytes, 0, data, 2, strLen);
                return PcccWrite(address, data);
            }

            // 非 ST — 直接写入 ASCII 字节
            byte[] bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
            return PcccWrite(address, bytes);
        }

        public override OperateResult Write(string address, byte[] data)
            => PcccWrite(address, data);

        // ═══════════════════════════════════════════
        //  连接管理辅助
        // ═══════════════════════════════════════════

        protected override int ResponseHeaderLength => 24;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            // ENIP 响应头中的 Length 字段
            return header[2] | (header[3] << 8);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _sessionHandle != 0)
            {
                try
                {
                    // UnregisterSession
                    byte[] unregFrame = BuildEnipFrame(0x0066, new byte[0]);
                    SendAndReceive(unregFrame);
                }
                catch { }
                _sessionHandle = 0;
            }
            base.Dispose(disposing);
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
    }
}
