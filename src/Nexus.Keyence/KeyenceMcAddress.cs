using System;
using System.Collections.Generic;

namespace Nexus.Keyence
{
    /// <summary>
    /// 基恩士 MC 协议地址解析 — 将 Keyence KV-5000/7000 地址字符串转换为 MC 3E 帧所需的子标签号和地址值。
    /// <para>支持格式: D100, M100, X0, Y0, R100, E100, W100, L100, F100, S100, B100, V100, SM0, TC100, CC100</para>
    /// <para>注意: X/Y/B 地址为十六进制。</para>
    /// </summary>
    public static class KeyenceMcAddress
    {
        private static readonly Dictionary<string, byte> SubLabels = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
        {
            { "D",  0xA8 },
            { "R",  0x94 },
            { "E",  0x95 },
            { "M",  0x90 },
            { "SM", 0x91 },
            { "L",  0x92 },
            { "F",  0x93 },
            { "B",  0xA0 },
            { "V",  0xA1 },
            { "W",  0xB4 },
            { "X",  0x9C },
            { "Y",  0x9D },
            { "S",  0x98 },
            { "TC", 0xC2 },
            { "CC", 0xC5 },
            { "T",  0xC1 },
            { "C",  0xC4 },
        };

        private static readonly HashSet<string> HexAddressPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "X", "Y", "B"
        };

        /// <summary>
        /// 解析地址字符串，返回 (子标签号, 起始地址)。
        /// </summary>
        public static (byte subLabel, uint address) Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空");

            address = address.Trim().ToUpperInvariant();

            foreach (var prefix in new[] { "SM", "TC", "CC" })
            {
                if (address.StartsWith(prefix, StringComparison.Ordinal) && address.Length > prefix.Length)
                {
                    byte subLabel = SubLabels[prefix];
                    string part = address.Substring(prefix.Length);
                    bool isHex = HexAddressPrefixes.Contains(prefix);
                    return (subLabel, ParseNumber(part, isHex));
                }
            }

            if (address.Length < 2)
                throw new ArgumentException($"无效地址格式: {address}");

            char prefixChar = address[0];
            string prefixStr = prefixChar.ToString();
            if (!SubLabels.ContainsKey(prefixStr))
                throw new ArgumentException($"不支持的区域前缀: {prefixChar}");

            byte sub = SubLabels[prefixStr];
            bool isHex2 = HexAddressPrefixes.Contains(prefixStr);
            string numPart = address.Substring(1);
            return (sub, ParseNumber(numPart, isHex2));
        }

        /// <summary>
        /// 解析是否为位地址（需要使用 SubCommand=0x0001 读取）。
        /// </summary>
        public static bool IsBitAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return false;
            address = address.Trim().ToUpperInvariant();
            foreach (var prefix in new[] { "SM", "TC", "CC" })
            {
                if (address.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            char c = address[0];
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
