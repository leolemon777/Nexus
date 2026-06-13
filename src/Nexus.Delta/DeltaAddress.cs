using System;

namespace Nexus.Delta
{
    /// <summary>
    /// 台达 DVP/AS 系列 PLC Modbus 地址解析。
    /// <para>DVP 系列地址映射: S/X/Y/T/C/M/D 寄存器到标准 Modbus 地址。</para>
    /// <para>AS 系列地址映射: SM/HC/S/X/Y/T/C/M/SR/D/E 寄存器到标准 Modbus 地址。</para>
    /// <para>支持八进制解析（DVP 系列 X/Y）、点号位寻址（D100.5）。</para>
    /// </summary>
    public static class DeltaAddress
    {
        /// <summary>
        /// 解析台达 PLC 地址为 Modbus 地址和功能码。
        /// <para>示例: "D100", "M100", "X17" (八进制), "Y10", "T10", "C10", "S10", "SM10", "SR10", "HC10", "E10"</para>
        /// <para>支持点号位寻址: "D100.5" 表示 D100 的第 5 位。</para>
        /// </summary>
        /// <param name="address">PLC 地址字符串。</param>
        /// <param name="series">PLC 系列（DVP 或 AS）。</param>
        /// <returns>(Modbus 地址, 读功能码, 写功能码)。写功能码为 0 表示只读。</returns>
        /// <exception cref="ArgumentException">地址为空或格式错误。</exception>
        public static (ushort modbusAddress, byte readFc, byte writeFc) Parse(string address, DeltaSeries series)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空", nameof(address));

            address = address.Trim().ToUpperInvariant();

            int dotIndex = address.IndexOf('.');
            int bitOffset = -1;
            if (dotIndex > 0 && dotIndex < address.Length - 1)
            {
                string bitPart = address.Substring(dotIndex + 1);
                if (int.TryParse(bitPart, out int bo))
                    bitOffset = bo;
                address = address.Substring(0, dotIndex);
            }

            if (address.Length < 2)
                throw new ArgumentException($"地址格式无效: {address}", nameof(address));

            string prefix = ReadAlphaPrefix(address);
            if (string.IsNullOrEmpty(prefix))
                throw new ArgumentException($"地址必须以字母开头: {address}", nameof(address));

            string numStr = address.Substring(prefix.Length);
            if (string.IsNullOrEmpty(numStr))
                throw new ArgumentException($"地址缺少数字部分: {address}", nameof(address));

            bool isBitOp = bitOffset >= 0;

            return series == DeltaSeries.DVP
                ? ParseDvp(prefix, numStr, isBitOp)
                : ParseAs(prefix, numStr, isBitOp);
        }

        private static (ushort, byte, byte) ParseDvp(string prefix, string numStr, bool isBitOp)
        {
            switch (prefix)
            {
                case "S":
                    int sAddr = ParseInt(numStr);
                    return (0, 0x01, 0x05);

                case "X":
                    int xAddr = ParseOctal(numStr);
                    return ((ushort)(xAddr + 1024), 0x02, 0x00);

                case "Y":
                    int yAddr = ParseOctal(numStr);
                    return ((ushort)(yAddr + 1280), 0x01, 0x05);

                case "T":
                    int tAddr = ParseInt(numStr);
                    return ((ushort)(tAddr + 1536), 0x01, 0x05);

                case "C":
                    int cAddr = ParseInt(numStr);
                    return ((ushort)(cAddr + 3584), 0x01, 0x05);

                case "M":
                    int mAddr = ParseInt(numStr);
                    if (mAddr < 1536)
                        return ((ushort)(mAddr + 2048), 0x01, 0x05);
                    return ((ushort)(mAddr - 1536 + 45056), 0x01, 0x05);

                case "D":
                    int dAddr = ParseInt(numStr);
                    if (isBitOp)
                        return ((ushort)(dAddr + 4096), 0x01, 0x05);
                    if (dAddr < 4096)
                        return ((ushort)(dAddr + 4096), 0x03, 0x06);
                    return ((ushort)(dAddr - 4096 + 36864), 0x03, 0x06);

                default:
                    throw new ArgumentException($"DVP 系列不支持的地址前缀: {prefix}");
            }
        }

        private static (ushort, byte, byte) ParseAs(string prefix, string numStr, bool isBitOp)
        {
            switch (prefix)
            {
                case "SM":
                    int smAddr = ParseInt(numStr);
                    return ((ushort)(smAddr + 16384), 0x01, 0x05);

                case "HC":
                    int hcAddr = ParseInt(numStr);
                    if (isBitOp)
                        return ((ushort)(hcAddr + 64512), 0x01, 0x05);
                    return ((ushort)(hcAddr + 64512), 0x03, 0x06);

                case "S":
                    int sAddr = ParseInt(numStr);
                    return ((ushort)(sAddr + 20480), 0x01, 0x05);

                case "X":
                    int xAddr = ParseInt(numStr);
                    return ((ushort)(xAddr + 24576), 0x02, 0x00);

                case "Y":
                    int yAddr = ParseInt(numStr);
                    return ((ushort)(yAddr + 40960), 0x01, 0x05);

                case "T":
                    int tAddr = ParseInt(numStr);
                    return ((ushort)(tAddr + 57344), 0x01, 0x05);

                case "C":
                    int cAddr = ParseInt(numStr);
                    return ((ushort)(cAddr + 61440), 0x01, 0x05);

                case "M":
                    int mAddr = ParseInt(numStr);
                    return ((ushort)mAddr, 0x01, 0x05);

                case "SR":
                    int srAddr = ParseInt(numStr);
                    return ((ushort)(srAddr + 49152), 0x03, 0x06);

                case "D":
                    int dAddr = ParseInt(numStr);
                    if (isBitOp)
                        return ((ushort)dAddr, 0x01, 0x05);
                    return ((ushort)dAddr, 0x03, 0x06);

                case "E":
                    int eAddr = ParseInt(numStr);
                    return ((ushort)(eAddr + 65024), 0x03, 0x06);

                default:
                    throw new ArgumentException($"AS 系列不支持的地址前缀: {prefix}");
            }
        }

        /// <summary>读取地址字符串开头的字母前缀。</summary>
        private static string ReadAlphaPrefix(string address)
        {
            int len = 0;
            while (len < address.Length && char.IsLetter(address[len]))
                len++;
            return address.Substring(0, len);
        }

        /// <summary>解析十进制整数（前导零去除）。</summary>
        private static int ParseInt(string s)
        {
            s = s.TrimStart('0');
            if (s.Length == 0) return 0;
            if (int.TryParse(s, out int val))
                return val;
            throw new ArgumentException($"数字部分无效: {s}");
        }

        /// <summary>解析八进制整数（DVP 系列 X/Y 地址专用）。</summary>
        private static int ParseOctal(string s)
        {
            s = s.TrimStart('0');
            if (s.Length == 0) return 0;
            int result = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < '0' || c > '7')
                    throw new ArgumentException($"八进制数字无效: {s}");
                result = result * 8 + (c - '0');
            }
            return result;
        }
    }
}
