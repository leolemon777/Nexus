using System;
using System.Globalization;
using Nexus;

namespace Nexus.Omron
{
    /// <summary>
    /// FINS 地址 — 表示欧姆龙 PLC 的内存区域地址。
    /// </summary>
    public sealed class FinsAddress : IDataAddress
    {
        /// <summary>用户输入的原始地址字符串。</summary>
        public string Original { get; }

        /// <summary>内存区域。</summary>
        public FinsMemoryArea Area { get; }

        /// <summary>字地址（word offset）。</summary>
        public ushort WordAddress { get; }

        /// <summary>位偏移（0-15）。-1 表示整字操作。</summary>
        public int BitOffset { get; }

        /// <summary>EM bank 编号（仅 EM 区域使用）。</summary>
        public byte EmBank { get; }

        public FinsAddress(string original, FinsMemoryArea area, ushort wordAddress, int bitOffset = -1, byte emBank = 0)
        {
            Original = original ?? throw new ArgumentNullException(nameof(original));
            Area = area;
            WordAddress = wordAddress;
            BitOffset = bitOffset;
            EmBank = emBank;
        }

        public override string ToString()
        {
            string areaPrefix = Area switch
            {
                FinsMemoryArea.CIO => "CIO",
                FinsMemoryArea.WR => "W",
                FinsMemoryArea.HR => "H",
                FinsMemoryArea.AR => "A",
                FinsMemoryArea.DM => "D",
                FinsMemoryArea.EM => $"E{EmBank}_",
                FinsMemoryArea.TimerPV => "T",
                FinsMemoryArea.TimerFlags => "TF",
                FinsMemoryArea.CounterPV => "C",
                FinsMemoryArea.CounterFlags => "CF",
                _ => Area.ToString()
            };
            return BitOffset >= 0 ? $"{areaPrefix}{WordAddress}.{BitOffset:D2}" : $"{areaPrefix}{WordAddress}";
        }
    }

    /// <summary>
    /// FINS 地址解析器 — 将字符串地址转为 FinsAddress。
    /// <para>支持格式:</para>
    /// <para>  D100     → DM 区, word 100</para>
    /// <para>  CIO100   → CIO 区, word 100</para>
    /// <para>  W100     → WR 区, word 100</para>
    /// <para>  H100     → HR 区, word 100</para>
    /// <para>  A100     → AR 区, word 100</para>
    /// <para>  D100.03  → DM 区, word 100, bit 3</para>
    /// <para>  CIO100.15 → CIO 区, word 100, bit 15</para>
    /// <para>  E0_100   → EM bank 0, word 100</para>
    /// <para>  纯数字 100 → 默认 DM 区</para>
    /// </summary>
    public sealed class FinsAddressParser : IAddressParser<FinsAddress>
    {
        public FinsAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            if (TryParseInternal(address.Trim().ToUpperInvariant(), out var parsed))
                return parsed!;

            throw new AddressParseException(address, "无法解析的 FINS 地址格式");
        }

        public bool TryParse(string address, out FinsAddress? parsed)
        {
            parsed = null;
            if (string.IsNullOrWhiteSpace(address))
                return false;
            return TryParseInternal(address.Trim().ToUpperInvariant(), out parsed);
        }

        private static bool TryParseInternal(string address, out FinsAddress? parsed)
        {
            parsed = null;

            // 处理位偏移: D100.03, CIO100.15
            int bitOffset = -1;
            int dotIdx = address.IndexOf('.');
            if (dotIdx > 0)
            {
                if (!int.TryParse(address.Substring(dotIdx + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int bit))
                    return false;
                if (bit < 0 || bit > 15)
                    return false;
                bitOffset = bit;
                address = address.Substring(0, dotIdx);
            }

            // EM 区域: E0_100, E1_200
            if (address.StartsWith("E") && address.Contains("_"))
            {
                string[] parts = address.Substring(1).Split('_');
                if (parts.Length != 2) return false;
                if (!byte.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out byte bank))
                    return false;
                if (!ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ushort wordAddr))
                    return false;
                parsed = new FinsAddress($"{address}{(bitOffset >= 0 ? $".{bitOffset}" : "")}", FinsMemoryArea.EM, wordAddr, bitOffset, bank);
                return true;
            }

            // 带前缀的区域
            FinsMemoryArea area;
            string numPart;

            if (address.StartsWith("CIO"))
            {
                area = FinsMemoryArea.CIO;
                numPart = address.Substring(3);
            }
            else if (address.StartsWith("WR"))
            {
                area = FinsMemoryArea.WR;
                numPart = address.Substring(2);
            }
            else if (address.StartsWith("W") && !address.StartsWith("WR"))
            {
                area = FinsMemoryArea.WR;
                numPart = address.Substring(1);
            }
            else if (address.StartsWith("HR"))
            {
                area = FinsMemoryArea.HR;
                numPart = address.Substring(2);
            }
            else if (address.StartsWith("H"))
            {
                area = FinsMemoryArea.HR;
                numPart = address.Substring(1);
            }
            else if (address.StartsWith("AR"))
            {
                area = FinsMemoryArea.AR;
                numPart = address.Substring(2);
            }
            else if (address.StartsWith("A"))
            {
                area = FinsMemoryArea.AR;
                numPart = address.Substring(1);
            }
            else if (address.StartsWith("DM"))
            {
                area = FinsMemoryArea.DM;
                numPart = address.Substring(2);
            }
            else if (address.StartsWith("D"))
            {
                area = FinsMemoryArea.DM;
                numPart = address.Substring(1);
            }
            else if (address.StartsWith("T"))
            {
                area = FinsMemoryArea.TimerPV;
                numPart = address.Substring(1);
            }
            else if (address.StartsWith("C"))
            {
                area = FinsMemoryArea.CounterPV;
                numPart = address.Substring(1);
            }
            else if (char.IsDigit(address[0]))
            {
                // 纯数字 → 默认 DM 区
                area = FinsMemoryArea.DM;
                numPart = address;
            }
            else
            {
                return false;
            }

            if (string.IsNullOrEmpty(numPart))
                return false;

            // 验证数字部分
            foreach (char c in numPart)
            {
                if (!char.IsDigit(c))
                    return false;
            }

            if (!ushort.TryParse(numPart, NumberStyles.None, CultureInfo.InvariantCulture, out ushort wordAddr2))
                return false;

            string original = address + (bitOffset >= 0 ? $".{bitOffset}" : "");
            parsed = new FinsAddress(original, area, wordAddr2, bitOffset);
            return true;
        }
    }
}
