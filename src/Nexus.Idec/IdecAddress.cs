using System;
using Nexus;

namespace Nexus.Idec
{
    /// <summary>
    /// IDEC MicroSmart Computer Link operand 地址。
    /// <para>支持设备: D（数据寄存器，字）、X（输入，八进制位）、Y（输出，八进制位）、</para>
    /// <para>M（内部继电器，十进制位）、T（定时器）、C（计数器）。</para>
    /// <para>X/Y 按八进制解析（参考 Delta DVP），D/M/T/C 按十进制解析。</para>
    /// </summary>
    public sealed class IdecAddress : IDataAddress
    {
        /// <inheritdoc/>
        public string Original { get; }

        /// <summary>设备区域。</summary>
        public IdecArea Area { get; }

        /// <summary>operand 号码（X/Y 为八进制解析后的十进制值）。</summary>
        public int Number { get; }

        /// <summary>是否为位设备（X/Y/M 为 true；D/T/C 为 false）。</summary>
        public bool IsBitArea { get; }

        /// <summary>
        /// 创建 IDEC 地址。
        /// </summary>
        /// <param name="original">原始地址字符串。</param>
        /// <param name="area">设备区域。</param>
        /// <param name="number">operand 号码。</param>
        public IdecAddress(string original, IdecArea area, int number)
        {
            Original = original;
            Area = area;
            Number = number;
            IsBitArea = area == IdecArea.InputBit || area == IdecArea.OutputBit || area == IdecArea.InternalRelay;
        }

        /// <summary>
        /// 解析地址字符串为 <see cref="IdecAddress"/>。
        /// <para>示例: "D100"、"M100"、"X0"（八进制）、"Y10"（八进制）、"T0"、"C0"。</para>
        /// </summary>
        /// <param name="address">地址字符串。</param>
        /// <returns>解析后的 <see cref="IdecAddress"/>。</returns>
        /// <exception cref="AddressParseException">地址格式无效。</exception>
        public static IdecAddress Parse(string address)
            => new IdecAddressParser().Parse(address);

        /// <summary>
        /// 尝试解析地址字符串，不抛异常。
        /// </summary>
        public static bool TryParse(string address, out IdecAddress? parsed)
            => new IdecAddressParser().TryParse(address, out parsed);

        /// <inheritdoc/>
        public override string ToString()
            => $"{IdecDataTypeCode.For(Area)}{Number}";
    }

    /// <summary>
    /// IDEC 地址解析器 — 把 "D100"/"X7"（八进制）/"M10" 等字符串解析为 <see cref="IdecAddress"/>。
    /// </summary>
    public sealed class IdecAddressParser : IAddressParser<IdecAddress>
    {
        /// <inheritdoc/>
        public IdecAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address ?? "", "地址不能为空");

            string original = address;
            string s = address.Trim().ToUpperInvariant();

            if (s.Length < 2)
                throw new AddressParseException(address, "地址至少需 1 位字母前缀 + 数字");

            char prefix = s[0];
            string numStr = s.Substring(1);

            if (numStr.Length == 0)
                throw new AddressParseException(address, "地址缺少数字部分");

            IdecArea area;
            int number;

            switch (prefix)
            {
                case 'D':
                    area = IdecArea.DataRegister;
                    number = ParseDecimal(numStr, address);
                    break;

                case 'X':
                    area = IdecArea.InputBit;
                    number = ParseOctal(numStr, address);
                    break;

                case 'Y':
                    area = IdecArea.OutputBit;
                    number = ParseOctal(numStr, address);
                    break;

                case 'M':
                    area = IdecArea.InternalRelay;
                    number = ParseDecimal(numStr, address);
                    break;

                case 'T':
                    area = IdecArea.Timer;
                    number = ParseDecimal(numStr, address);
                    break;

                case 'C':
                    area = IdecArea.Counter;
                    number = ParseDecimal(numStr, address);
                    break;

                default:
                    throw new AddressParseException(address,
                        $"不支持的前缀 '{prefix}'（支持: D/X/Y/M/T/C）");
            }

            return new IdecAddress(original, area, number);
        }

        /// <inheritdoc/>
        public bool TryParse(string address, out IdecAddress? parsed)
        {
            try
            {
                parsed = Parse(address);
                return true;
            }
            catch
            {
                parsed = null;
                return false;
            }
        }

        /// <summary>解析十进制整数（去除前导零）。</summary>
        private static int ParseDecimal(string s, string fullAddress)
        {
            string trimmed = s.TrimStart('0');
            if (trimmed.Length == 0) return 0;
            if (int.TryParse(trimmed, out int val) && val >= 0)
                return val;
            throw new AddressParseException(fullAddress, $"十进制数字部分无效: {s}");
        }

        /// <summary>解析八进制整数（X/Y 地址专用，参考 Delta DVP）。</summary>
        private static int ParseOctal(string s, string fullAddress)
        {
            string trimmed = s.TrimStart('0');
            if (trimmed.Length == 0) return 0;
            int result = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c < '0' || c > '7')
                    throw new AddressParseException(fullAddress, $"八进制数字无效: {s}");
                result = result * 8 + (c - '0');
            }
            return result;
        }
    }
}
