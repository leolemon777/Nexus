using System;
using System.Text;
using Nexus;

namespace Nexus.Modbus
{
    /// <summary>
    /// Modbus 诊断工具 — 将原始报文解析为人类可读的描述。
    /// 支持 TCP/RTU/ASCII/RtuOverTcp 四种传输协议。
    /// 支持 FC01-06, 08, 15, 16, 22, 23, 43。
    /// </summary>
    public static class ModbusDiagnostics
    {
        /// <summary>解析十六进制字符串为可读描述。</summary>
        public static string ParseMessage(string hexString, ModbusProtocol protocol = ModbusProtocol.Tcp)
        {
            if (string.IsNullOrWhiteSpace(hexString))
                return "[空报文]";

            if (protocol == ModbusProtocol.Ascii)
            {
                // ASCII 帧以 ':' 开头，以 CR/LF 结尾 — 直接转为字节交给 ParseMessage
                byte[] raw = Encoding.ASCII.GetBytes(hexString);
                return ParseMessage(raw, protocol);
            }

            byte[] data = HexToBytes(hexString.Replace(" ", "").Replace("\r", "").Replace("\n", ""));
            if (data.Length == 0)
                return "[十六进制解析失败]";

            return ParseMessage(data, protocol);
        }

        /// <summary>解析原始字节为可读描述。</summary>
        public static string ParseMessage(byte[] data, ModbusProtocol protocol = ModbusProtocol.Tcp)
        {
            if (data == null || data.Length == 0)
                return "[空报文]";

            var sb = new StringBuilder();

            switch (protocol)
            {
                case ModbusProtocol.Tcp:
                    ParseTcpMessage(sb, data);
                    break;
                case ModbusProtocol.Rtu:
                    ParseRtuMessage(sb, data);
                    break;
                case ModbusProtocol.Ascii:
                    ParseAsciiMessage(sb, data);
                    break;
                case ModbusProtocol.RtuOverTcp:
                    ParseRtuOverTcpMessage(sb, data);
                    break;
                default:
                    sb.AppendLine("[不支持的协议]");
                    break;
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>将 Modbus 异常码翻译为中文描述。</summary>
        public static string TranslateException(byte exceptionCode)
        {
            return exceptionCode switch
            {
                0x01 => "非法功能码 (Illegal Function)",
                0x02 => "非法数据地址 (Illegal Data Address)",
                0x03 => "非法数据值 (Illegal Data Value)",
                0x04 => "从站设备故障 (Slave Device Failure)",
                0x05 => "确认 (Acknowledge)",
                0x06 => "从站设备忙 (Slave Device Busy)",
                0x08 => "存储奇偶性差错 (Memory Parity Error)",
                0x0A => "不可用网关路径 (Gateway Path Unavailable)",
                0x0B => "网关目标设备响应失败 (Gateway Target Device Failed to Respond)",
                _ => $"未知异常码 0x{exceptionCode:X2} (Unknown Exception)"
            };
        }

        /// <summary>格式化请求-响应事务日志。</summary>
        public static string FormatTransaction(byte[] request, byte[] response, ModbusProtocol protocol = ModbusProtocol.Tcp)
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══ Modbus 事务 ═══");
            sb.AppendLine();
            sb.AppendLine("[请求]");
            sb.AppendLine(ParseMessage(request, protocol));
            sb.AppendLine();
            sb.AppendLine("[响应]");
            sb.AppendLine(response != null && response.Length > 0
                ? ParseMessage(response, protocol)
                : "[无响应/超时]");
            sb.AppendLine("═══════════════════");
            return sb.ToString().TrimEnd();
        }

        // ── TCP (MBAP) ────────────────────────────

        private static void ParseTcpMessage(StringBuilder sb, byte[] data)
        {
            if (data.Length < 8)
            {
                sb.Append("[TCP 报文过短: ").Append(data.Length).Append(" 字节, 最少 8 字节]");
                return;
            }

            ushort transId = ReadUInt16(data, 0);
            ushort protoId = ReadUInt16(data, 2);
            ushort length = ReadUInt16(data, 4);
            byte unitId = data[6];

            sb.Append("协议: Modbus TCP").AppendLine();
            sb.Append("事务 ID: ").Append(transId).AppendLine();
            sb.Append("协议 ID: 0x").Append(protoId.ToString("X4"));
            if (protoId != 0) sb.Append(" ⚠ 异常: 应为 0x0000");
            sb.AppendLine();
            sb.Append("长度: ").Append(length).Append(" 字节").AppendLine();
            sb.Append("单元 ID: ").Append(unitId).AppendLine();

            if (data.Length >= 8)
            {
                sb.AppendLine();
                ParsePduFields(sb, data, 7, data.Length - 7);
            }
        }

        // ── RTU ───────────────────────────────────

        private static void ParseRtuMessage(StringBuilder sb, byte[] data)
        {
            if (data.Length < 5)
            {
                sb.Append("[RTU 报文过短: ").Append(data.Length).Append(" 字节, 最少 5 字节]");
                return;
            }

            byte station = data[0];
            int crcOffset = data.Length - 2;
            ushort actualCrc = (ushort)(data[crcOffset] | (data[crcOffset + 1] << 8));
            ushort expectedCrc = CrcCalculator.ComputeCrc16(data, 0, crcOffset);
            bool crcValid = actualCrc == expectedCrc;

            sb.Append("协议: Modbus RTU").AppendLine();
            sb.Append("从站地址: ").Append(station).AppendLine();
            sb.Append("CRC: 0x").Append(actualCrc.ToString("X4"));
            if (!crcValid)
                sb.Append(" ⚠ CRC 校验失败! 预期: 0x").Append(expectedCrc.ToString("X4"));
            sb.AppendLine();

            sb.AppendLine();
            ParsePduFields(sb, data, 1, data.Length - 3);
        }

        // ── ASCII ─────────────────────────────────

        private static void ParseAsciiMessage(StringBuilder sb, byte[] data)
        {
            string text = Encoding.ASCII.GetString(data);
            string trimmed = text.Trim();

            if (trimmed.Length < 7 || trimmed[0] != ':')
            {
                sb.Append("[ASCII 报文格式无效]");
                return;
            }

            string hex = trimmed.Substring(1);
            byte[] decoded = HexToBytes(hex);

            if (decoded.Length < 3)
            {
                sb.Append("[ASCII 解码后数据过短]");
                return;
            }

            byte station = decoded[0];
            byte actualLrc = decoded[decoded.Length - 1];
            byte expectedLrc = CrcCalculator.ComputeLrc(decoded, 0, decoded.Length - 1);
            bool lrcValid = actualLrc == expectedLrc;

            sb.Append("协议: Modbus ASCII").AppendLine();
            sb.Append("从站地址: ").Append(station).AppendLine();
            sb.Append("LRC: 0x").Append(actualLrc.ToString("X2"));
            if (!lrcValid)
                sb.Append(" ⚠ LRC 校验失败! 预期: 0x").Append(expectedLrc.ToString("X2"));
            sb.AppendLine();

            sb.AppendLine();
            ParsePduFields(sb, decoded, 1, decoded.Length - 2);
        }

        // ── RTU over TCP ──────────────────────────

        private static void ParseRtuOverTcpMessage(StringBuilder sb, byte[] data)
        {
            if (data.Length < 5)
            {
                sb.Append("[RTU-over-TCP 报文过短: ").Append(data.Length).Append(" 字节, 最少 5 字节]");
                return;
            }

            byte station = data[0];
            int crcOffset = data.Length - 2;
            ushort actualCrc = (ushort)(data[crcOffset] | (data[crcOffset + 1] << 8));
            ushort expectedCrc = CrcCalculator.ComputeCrc16(data, 0, crcOffset);
            bool crcValid = actualCrc == expectedCrc;

            sb.Append("协议: Modbus RTU-over-TCP").AppendLine();
            sb.Append("从站地址: ").Append(station).AppendLine();
            sb.Append("CRC: 0x").Append(actualCrc.ToString("X4"));
            if (!crcValid)
                sb.Append(" ⚠ CRC 校验失败! 预期: 0x").Append(expectedCrc.ToString("X4"));
            sb.AppendLine();

            sb.AppendLine();
            ParsePduFields(sb, data, 1, data.Length - 3);
        }

        // ── PDU 字段解析 ──────────────────────────

        private static void ParsePduFields(StringBuilder sb, byte[] buffer, int offset, int length)
        {
            if (length <= 0)
            {
                sb.Append("[PDU 为空]");
                return;
            }

            byte fc = buffer[offset];
            bool isException = (fc & 0x80) != 0;

            if (isException)
            {
                byte baseFc = (byte)(fc & 0x7F);
                sb.Append("功能码: 0x").Append(baseFc.ToString("X2")).Append(" (异常响应 0x").Append(fc.ToString("X2")).Append(")").AppendLine();
                if (length >= 2)
                {
                    byte exCode = buffer[offset + 1];
                    sb.Append("异常码: ").Append(exCode).AppendLine();
                    sb.Append("描述: ").Append(TranslateException(exCode)).AppendLine();
                }
                return;
            }

            sb.Append("功能码: 0x").Append(fc.ToString("X2")).Append(" (").Append(GetFunctionName(fc)).Append(")").AppendLine();

            switch (fc)
            {
                case 0x01:
                case 0x02:
                    ParseReadBitsRequest(sb, buffer, offset, length, fc);
                    break;
                case 0x03:
                case 0x04:
                    ParseReadRegistersRequest(sb, buffer, offset, length);
                    break;
                case 0x05:
                    ParseWriteSingleCoilRequest(sb, buffer, offset, length);
                    break;
                case 0x06:
                    ParseWriteSingleRegisterRequest(sb, buffer, offset, length);
                    break;
                case 0x08:
                    ParseDiagnosticsPdu(sb, buffer, offset, length);
                    break;
                case 0x0F:
                    ParseWriteMultipleCoilsRequest(sb, buffer, offset, length);
                    break;
                case 0x10:
                    ParseWriteMultipleRegistersRequest(sb, buffer, offset, length);
                    break;
                case 0x16:
                    ParseMaskWriteRegister(sb, buffer, offset, length);
                    break;
                case 0x17:
                    ParseReadWriteMultipleRegisters(sb, buffer, offset, length);
                    break;
                case 0x2B:
                    ParseEncapsulatedInterface(sb, buffer, offset, length);
                    break;
                default:
                    if (length > 1)
                    {
                        sb.Append("数据: ").Append(ToHexString(buffer, offset + 1, length - 1)).AppendLine();
                    }
                    break;
            }
        }

        // ── 功能码名称 ────────────────────────────

        private static string GetFunctionName(byte fc)
        {
            return fc switch
            {
                0x01 => "读线圈 (Read Coils)",
                0x02 => "读离散输入 (Read Discrete Inputs)",
                0x03 => "读保持寄存器 (Read Holding Registers)",
                0x04 => "读输入寄存器 (Read Input Registers)",
                0x05 => "写单线圈 (Write Single Coil)",
                0x06 => "写单寄存器 (Write Single Register)",
                0x08 => "诊断 (Diagnostics)",
                0x0F => "写多线圈 (Write Multiple Coils)",
                0x10 => "写多寄存器 (Write Multiple Registers)",
                0x16 => "掩码写寄存器 (Mask Write Register)",
                0x17 => "读写多寄存器 (Read/Write Multiple)",
                0x2B => "封装接口 (Encapsulated Interface)",
                _ => "未知功能码"
            };
        }

        // ── FC01/02 读位 ──────────────────────────

        private static void ParseReadBitsRequest(StringBuilder sb, byte[] buffer, int offset, int length, byte fc)
        {
            if (length >= 5)
            {
                ushort addr = ReadUInt16(buffer, offset + 1);
                ushort qty = ReadUInt16(buffer, offset + 3);
                sb.Append("起始地址: ").Append(addr).AppendLine();
                sb.Append("数量: ").Append(qty).AppendLine();
            }
            else if (length >= 2)
            {
                int byteCount = buffer[offset + 1];
                sb.Append("字节数: ").Append(byteCount).AppendLine();
                if (length > 2)
                    sb.Append("数据: ").Append(ToHexString(buffer, offset + 2, length - 2)).AppendLine();
            }
        }

        // ── FC03/04 读寄存器 ──────────────────────

        private static void ParseReadRegistersRequest(StringBuilder sb, byte[] buffer, int offset, int length)
        {
            if (length >= 5)
            {
                ushort addr = ReadUInt16(buffer, offset + 1);
                ushort qty = ReadUInt16(buffer, offset + 3);
                sb.Append("起始地址: ").Append(addr).AppendLine();
                sb.Append("数量: ").Append(qty).AppendLine();
            }
            else if (length >= 2)
            {
                int byteCount = buffer[offset + 1];
                sb.Append("字节数: ").Append(byteCount).AppendLine();
                if (length > 2)
                    sb.Append("数据: ").Append(ToHexString(buffer, offset + 2, length - 2)).AppendLine();
            }
        }

        // ── FC05 写单线圈 ────────────────────────

        private static void ParseWriteSingleCoilRequest(StringBuilder sb, byte[] buffer, int offset, int length)
        {
            if (length >= 5)
            {
                ushort addr = ReadUInt16(buffer, offset + 1);
                ushort value = ReadUInt16(buffer, offset + 3);
                sb.Append("线圈地址: ").Append(addr).AppendLine();
                sb.Append("值: ").Append(value == 0xFF00 ? "ON (0xFF00)" : value == 0x0000 ? "OFF (0x0000)" : "0x" + value.ToString("X4")).AppendLine();
            }
        }

        // ── FC06 写单寄存器 ──────────────────────

        private static void ParseWriteSingleRegisterRequest(StringBuilder sb, byte[] buffer, int offset, int length)
        {
            if (length >= 5)
            {
                ushort addr = ReadUInt16(buffer, offset + 1);
                ushort value = ReadUInt16(buffer, offset + 3);
                sb.Append("寄存器地址: ").Append(addr).AppendLine();
                sb.Append("值: ").Append(value).Append(" (0x").Append(value.ToString("X4")).Append(")").AppendLine();
            }
        }

        // ── FC08 诊断 ────────────────────────────

        private static void ParseDiagnosticsPdu(StringBuilder sb, byte[] buffer, int offset, int length)
        {
            if (length >= 4)
            {
                ushort subFunc = ReadUInt16(buffer, offset + 1);
                sb.Append("子功能码: 0x").Append(subFunc.ToString("X4")).Append(" (").Append(GetDiagnosticsSubFunctionName(subFunc)).Append(")").AppendLine();
                if (length > 4)
                    sb.Append("数据: ").Append(ToHexString(buffer, offset + 3, length - 3)).AppendLine();
            }
        }

        private static string GetDiagnosticsSubFunctionName(ushort subFunc)
        {
            return subFunc switch
            {
                0x0000 => "返回查询数据 (Return Query Data)",
                0x0001 => "重启通信 (Restart Communications)",
                0x0002 => "返回诊断寄存器 (Return Diagnostic Register)",
                0x000A => "清除计数器 (Clear Counters)",
                0x000B => "总线消息计数 (Bus Message Count)",
                0x000C => "总线通信错误计数 (Bus Comm Error Count)",
                0x000D => "总线异常错误计数 (Bus Exception Error Count)",
                0x000E => "从站消息计数 (Slave Message Count)",
                0x000F => "从站无响应计数 (Slave No Response Count)",
                0x0010 => "从站 NAK 计数 (Slave NAK Count)",
                0x0011 => "从站忙计数 (Slave Busy Count)",
                0x0012 => "总线字符过载计数 (Bus Char Overrun Count)",
                0x0014 => "清除过载计数器 (Clear Overrun Counters)",
                0x0015 => "IOP 过载计数 (IOP Overrun Count)",
                _ => "未知子功能"
            };
        }

        // ── FC15 写多线圈 ────────────────────────

        private static void ParseWriteMultipleCoilsRequest(StringBuilder sb, byte[] buffer, int offset, int length)
        {
            if (length >= 6)
            {
                ushort addr = ReadUInt16(buffer, offset + 1);
                ushort qty = ReadUInt16(buffer, offset + 3);
                int byteCount = buffer[offset + 5];
                sb.Append("起始地址: ").Append(addr).AppendLine();
                sb.Append("数量: ").Append(qty).AppendLine();
                sb.Append("字节数: ").Append(byteCount).AppendLine();
                if (length > 6)
                {
                    sb.Append("数据: ").Append(ToHexString(buffer, offset + 6, Math.Min(byteCount, length - 6))).AppendLine();
                }
            }
        }

        // ── FC16 写多寄存器 ──────────────────────

        private static void ParseWriteMultipleRegistersRequest(StringBuilder sb, byte[] buffer, int offset, int length)
        {
            if (length >= 6)
            {
                ushort addr = ReadUInt16(buffer, offset + 1);
                ushort qty = ReadUInt16(buffer, offset + 3);
                int byteCount = buffer[offset + 5];
                sb.Append("起始地址: ").Append(addr).AppendLine();
                sb.Append("数量: ").Append(qty).AppendLine();
                sb.Append("字节数: ").Append(byteCount).AppendLine();
                if (length > 6)
                {
                    sb.Append("数据: ").Append(ToHexString(buffer, offset + 6, Math.Min(byteCount, length - 6))).AppendLine();
                    sb.Append("寄存器值:");
                    int regCount = Math.Min(qty, (length - 6) / 2);
                    for (int i = 0; i < regCount; i++)
                    {
                        int dataOffset = offset + 6 + i * 2;
                        if (dataOffset + 1 < buffer.Length)
                        {
                            ushort val = ReadUInt16(buffer, dataOffset);
                            sb.Append(" [").Append(addr + i).Append("]=").Append(val);
                        }
                    }
                    sb.AppendLine();
                }
            }
        }

        // ── FC22 掩码写寄存器 ─────────────────────

        private static void ParseMaskWriteRegister(StringBuilder sb, byte[] buffer, int offset, int length)
        {
            if (length >= 7)
            {
                ushort addr = ReadUInt16(buffer, offset + 1);
                ushort andMask = ReadUInt16(buffer, offset + 3);
                ushort orMask = ReadUInt16(buffer, offset + 5);
                sb.Append("寄存器地址: ").Append(addr).AppendLine();
                sb.Append("AND 掩码: 0x").Append(andMask.ToString("X4")).AppendLine();
                sb.Append("OR 掩码: 0x").Append(orMask.ToString("X4")).AppendLine();
            }
        }

        // ── FC23 读写多寄存器 ─────────────────────

        private static void ParseReadWriteMultipleRegisters(StringBuilder sb, byte[] buffer, int offset, int length)
        {
            if (length >= 10)
            {
                ushort readAddr = ReadUInt16(buffer, offset + 1);
                ushort readQty = ReadUInt16(buffer, offset + 3);
                ushort writeAddr = ReadUInt16(buffer, offset + 5);
                ushort writeQty = ReadUInt16(buffer, offset + 7);
                int writeByteCount = buffer[offset + 9];
                sb.Append("读起始地址: ").Append(readAddr).AppendLine();
                sb.Append("读数量: ").Append(readQty).AppendLine();
                sb.Append("写起始地址: ").Append(writeAddr).AppendLine();
                sb.Append("写数量: ").Append(writeQty).AppendLine();
                sb.Append("写字节数: ").Append(writeByteCount).AppendLine();
                if (length > 10)
                    sb.Append("写数据: ").Append(ToHexString(buffer, offset + 10, Math.Min(writeByteCount, length - 10))).AppendLine();
            }
            else if (length >= 2)
            {
                int byteCount = buffer[offset + 1];
                sb.Append("字节数: ").Append(byteCount).AppendLine();
                if (length > 2)
                    sb.Append("数据: ").Append(ToHexString(buffer, offset + 2, length - 2)).AppendLine();
            }
        }

        // ── FC43 封装接口 ────────────────────────

        private static void ParseEncapsulatedInterface(StringBuilder sb, byte[] buffer, int offset, int length)
        {
            if (length < 2)
                return;

            byte meiType = buffer[offset + 1];
            sb.Append("MEI 类型: 0x").Append(meiType.ToString("X2")).AppendLine();

            if (meiType == 0x0E && length >= 4)
            {
                sb.Append("功能: 读设备标识 (Read Device ID)").AppendLine();
                byte readLevel = buffer[offset + 2];
                sb.Append("读取级别: ").Append(readLevel).Append(" (").Append(GetDeviceIdLevelName(readLevel)).Append(")").AppendLine();
                sb.Append("起始对象 ID: 0x").Append(buffer[offset + 3].ToString("X2")).AppendLine();
            }
            else if (length > 2)
            {
                sb.Append("数据: ").Append(ToHexString(buffer, offset + 2, length - 2)).AppendLine();
            }
        }

        private static string GetDeviceIdLevelName(byte level)
        {
            return level switch
            {
                0x01 => "基本 (Basic)",
                0x02 => "常规 (Regular)",
                0x03 => "扩展 (Extended)",
                _ => "未知"
            };
        }

        // ── 工具方法 ──────────────────────────────

        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        private static string ToHexString(byte[] buffer, int offset, int length)
        {
            if (length <= 0 || offset >= buffer.Length) return string.Empty;
            int safeLength = Math.Min(length, buffer.Length - offset);
            var sb = new StringBuilder(safeLength * 3);
            for (int i = 0; i < safeLength; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(buffer[offset + i].ToString("X2"));
            }
            return sb.ToString();
        }

        private static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0)
                return Array.Empty<byte>();

            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int high = HexValue(hex[i * 2]);
                int low = HexValue(hex[i * 2 + 1]);
                if (high < 0 || low < 0) return Array.Empty<byte>();
                bytes[i] = (byte)((high << 4) | low);
            }
            return bytes;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            return -1;
        }
    }

    /// <summary>Modbus 传输协议类型。</summary>
    public enum ModbusProtocol
    {
        /// <summary>Modbus TCP (MBAP 头)。</summary>
        Tcp,
        /// <summary>Modbus RTU (CRC16)。</summary>
        Rtu,
        /// <summary>Modbus ASCII (LRC, ':' 起始)。</summary>
        Ascii,
        /// <summary>Modbus RTU over TCP。</summary>
        RtuOverTcp
    }
}
