using System;
using System.Collections.Generic;

namespace Nexus.Panasonic
{
    /// <summary>
    /// Panasonic MC 协议地址解析 — 将松下 PLC 地址字符串转换为 MC 3E Binary 帧所需的子标签号和地址值。
    /// <para>支持区域: X(输入), Y(输出), M(内部继电器), L(锁存继电器), D(数据寄存器), T(定时器), C(计数器), S(步进继电器)</para>
    /// <para>注意: X/Y 地址为十六进制。</para>
    /// </summary>
    public static class PanasonicMcAddress
    {
        private static readonly Dictionary<string, byte> SubLabels = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
        {
            { "X", 0x9C },
            { "Y", 0x9D },
            { "M", 0x90 },
            { "L", 0x92 },
            { "D", 0xA8 },
            { "T", 0xC2 },
            { "C", 0xC5 },
            { "S", 0x98 },
        };

        private static readonly HashSet<string> HexAddressPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "X", "Y"
        };

        /// <summary>
        /// 解析地址字符串，返回 (子标签号, 起始地址)。
        /// </summary>
        /// <param name="address">地址字符串，如 "D100", "X0", "M100"。</param>
        /// <returns>(subLabel, addressValue)</returns>
        public static (byte subLabel, uint address) Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空");

            address = address.Trim().ToUpperInvariant();

            if (address.Length < 2)
                throw new ArgumentException($"无效地址格式: {address}");

            char prefixChar = address[0];
            string prefixStr = prefixChar.ToString();
            if (!SubLabels.ContainsKey(prefixStr))
                throw new ArgumentException($"不支持的区域前缀: {prefixChar}");

            byte subLabel = SubLabels[prefixStr];
            bool isHex = HexAddressPrefixes.Contains(prefixStr);
            string numPart = address.Substring(1);
            return (subLabel, ParseNumber(numPart, isHex));
        }

        /// <summary>
        /// 解析是否为位地址。
        /// </summary>
        public static bool IsBitAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return false;
            address = address.Trim().ToUpperInvariant();
            char c = address[0];
            switch (c)
            {
                case 'X': case 'Y': case 'M': case 'L': case 'S': return true;
                default: return false;
            }
        }

        private static uint ParseNumber(string s, bool isHex)
        {
            if (string.IsNullOrWhiteSpace(s))
                throw new ArgumentException("地址数字部分不能为空");

            if (isHex)
                return Convert.ToUInt32(s, 16);
            return uint.Parse(s);
        }
    }
}
