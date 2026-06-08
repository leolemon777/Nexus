using System;
using System.Collections.Generic;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// MC-3E 地址解析 — 将三菱地址字符串转换为协议帧所需的子标签号和地址值。
    /// <para>支持格式: D100, M100, X0, Y0, Z0, R100, B100, W100, L100, F100, S100, TS100, TC100, CS100, CC100</para>
    /// <para>注意: X/Y/B 地址为十六进制。</para>
    /// </summary>
    public static class Mc3EAddressParser
    {
        /// <summary>MC-3E Binary 子标签号映射。</summary>
        private static readonly Dictionary<string, byte> SubLabels = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
        {
            { "D",  0xA8 },  // 数据寄存器
            { "M",  0x90 },  // 内部继电器
            { "X",  0x9C },  // 输入
            { "Y",  0x9D },  // 输出
            { "Z",  0xCC },  // 变址寄存器
            { "R",  0xAF },  // 文件寄存器
            { "B",  0xA0 },  // 链接继电器
            { "W",  0xB4 },  // 链接寄存器
            { "L",  0x92 },  // 锁存继电器
            { "F",  0x93 },  // 状态
            { "V",  0x94 },  // 边沿继电器
            { "S",  0x98 },  // 步进继电器
            { "TS", 0xC1 },  // 定时器触点
            { "TC", 0xC0 },  // 定时器线圈
            { "CS", 0xC4 },  // 计数器触点
            { "CC", 0xC3 },  // 计数器线圈
            { "SM", 0x91 },  // 特殊继电器
            { "SD", 0xA9 },  // 特殊寄存器
            { "DX", 0xA2 },  // 直接输入
            { "SW", 0xB5 },  // 直接链接寄存器
            { "ZR", 0xB0 },  // 文件寄存器(扩展)
        };

        /// <summary>X/Y/B/DX 地址使用十六进制解析。</summary>
        private static readonly HashSet<string> HexAddressPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "X", "Y", "B", "DX"
        };

        /// <summary>
        /// 解析地址字符串，返回 (子标签号, 起始地址)。
        /// </summary>
        /// <param name="address">地址字符串，如 "D100", "X0", "TS100"。</param>
        /// <returns>(subLabel, addressValue)</returns>
        public static (byte subLabel, uint address) Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空");

            address = address.Trim().ToUpperInvariant();

            // 尝试匹配两字符前缀再尝试单字符前缀
            foreach (var prefix in new[] { "TS", "TC", "CS", "CC", "SM", "SD", "DX", "SW", "ZR" })
            {
                if (address.StartsWith(prefix) && address.Length > prefix.Length)
                {
                    byte subLabel = SubLabels[prefix];
                    string part = address.Substring(prefix.Length);
                    bool isHexPrefix = HexAddressPrefixes.Contains(prefix);
                    return (subLabel, ParseNumber(part, isHexPrefix));
                }
            }

            // 单字符前缀
            if (address.Length < 2)
                throw new ArgumentException($"无效地址格式: {address}");

            char prefixChar = address[0];
            string prefixStr = prefixChar.ToString();
            if (!SubLabels.ContainsKey(prefixStr))
                throw new ArgumentException($"不支持的区域前缀: {prefixChar}");

            byte sub = SubLabels[prefixStr];
            bool isHex = HexAddressPrefixes.Contains(prefixStr);
            string numPart = address.Substring(1);
            return (sub, ParseNumber(numPart, isHex));
        }

        /// <summary>
        /// 解析是否为位地址（线圈/触点类型）。
        /// </summary>
        public static bool IsBitAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return false;
            address = address.Trim().ToUpperInvariant();
            // Two-char prefixes that are bit addresses
            foreach (var prefix in new[] { "TS", "TC", "CS", "CC", "SM", "DX" })
            {
                if (address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            // Two-char prefixes that are word addresses (SD, SW, ZR) — 短路检查
            foreach (var prefix in new[] { "SD", "SW", "ZR" })
            {
                if (address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            char c = address[0];
            // M, X, Y, L, F, S, B, V are bit addresses
            // D, W, Z, R are word addresses
            switch (c)
            {
                case 'M': case 'X': case 'Y': case 'L':
                case 'F': case 'S': case 'B': case 'V': return true;
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
