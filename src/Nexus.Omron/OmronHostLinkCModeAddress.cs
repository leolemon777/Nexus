using System;
using System.Globalization;

namespace Nexus.Omron
{
    /// <summary>
    /// HostLink C-Mode 内存区域代码（2 字节）。
    /// </summary>
    public enum CModeArea : byte
    {
        DM = 0,
        AR,
        HR,
        LR,
        TC,
        EM,
    }

    /// <summary>
    /// HostLink C-Mode 地址 — 表示 C-Mode 协议的 PLC 内存区域地址。
    /// <para>支持格式：D100, W0, H0, T0, C0, A0, E0_100, D100.03</para>
    /// </summary>
    public sealed class OmronHostLinkCModeAddress : IDataAddress
    {
        public string Original { get; }
        public CModeArea Area { get; }
        public ushort WordAddress { get; }
        public int BitOffset { get; }
        public byte EmBank { get; }

        public OmronHostLinkCModeAddress(string original, CModeArea area, ushort wordAddress, int bitOffset = -1, byte emBank = 0)
        {
            Original = original ?? throw new ArgumentNullException(nameof(original));
            Area = area;
            WordAddress = wordAddress;
            BitOffset = bitOffset;
            EmBank = emBank;
        }

        /// <summary>
        /// 获取 C-Mode 区域代码（2 字节，大端序）。
        /// </summary>
        public byte[] GetAreaCode()
        {
            return Area switch
            {
                CModeArea.DM => new byte[] { 0x82, 0x00 },
                CModeArea.AR => new byte[] { 0x83, 0x00 },
                CModeArea.HR => new byte[] { 0x82, 0x01 },
                CModeArea.LR => new byte[] { 0x82, 0x02 },
                CModeArea.TC => new byte[] { 0x82, 0x03 },
                CModeArea.EM => new byte[] { 0x82, 0x20 },
                _ => new byte[] { 0x82, 0x00 },
            };
        }

        public override string ToString()
        {
            string prefix = Area switch
            {
                CModeArea.DM => "D",
                CModeArea.AR => "W",
                CModeArea.HR => "H",
                CModeArea.LR => "L",
                CModeArea.TC => "T",
                CModeArea.EM => $"E{EmBank}_",
                _ => "?",
            };
            return BitOffset >= 0 ? $"{prefix}{WordAddress}.{BitOffset:D2}" : $"{prefix}{WordAddress}";
        }
    }

    /// <summary>
    /// HostLink C-Mode 地址解析器。
    /// <para>支持格式：</para>
    /// <para>  D100     → DM 区, word 100</para>
    /// <para>  W0       → AR 区, word 0</para>
    /// <para>  H0       → HR 区, word 0</para>
    /// <para>  T0       → TC 区（定时器）, word 0</para>
    /// <para>  C0       → TC 区（计数器）, word 0</para>
    /// <para>  A0       → AR 区, word 0</para>
    /// <para>  D100.03  → DM 区, word 100, bit 3</para>
    /// <para>  E0_100   → EM bank 0, word 100</para>
    /// <para>  纯数字   → 默认 DM 区</para>
    /// </summary>
    public sealed class OmronHostLinkCModeAddressParser : IAddressParser<OmronHostLinkCModeAddress>
    {
        public OmronHostLinkCModeAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            if (TryParseInternal(address.Trim().ToUpperInvariant(), out var parsed))
                return parsed!;

            throw new AddressParseException(address, "无法解析的 HostLink C-Mode 地址格式");
        }

        public bool TryParse(string address, out OmronHostLinkCModeAddress? parsed)
        {
            parsed = null;
            if (string.IsNullOrWhiteSpace(address))
                return false;
            return TryParseInternal(address.Trim().ToUpperInvariant(), out parsed);
        }

        private static bool TryParseInternal(string address, out OmronHostLinkCModeAddress? parsed)
        {
            parsed = null;

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

            if (address.StartsWith("E") && address.Contains("_"))
            {
                string[] parts = address.Substring(1).Split('_');
                if (parts.Length != 2) return false;
                if (!byte.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out byte bank))
                    return false;
                if (!ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ushort wordAddr))
                    return false;
                parsed = new OmronHostLinkCModeAddress(
                    $"{address}{(bitOffset >= 0 ? $".{bitOffset}" : "")}",
                    CModeArea.EM, wordAddr, bitOffset, bank);
                return true;
            }

            CModeArea area;
            string numPart;

            if (address.StartsWith("DM"))
            {
                area = CModeArea.DM;
                numPart = address.Substring(2);
            }
            else if (address.StartsWith("D"))
            {
                area = CModeArea.DM;
                numPart = address.Substring(1);
            }
            else if (address.StartsWith("WR") || address.StartsWith("AR"))
            {
                area = CModeArea.AR;
                numPart = address.Substring(2);
            }
            else if (address.StartsWith("W") || address.StartsWith("A"))
            {
                area = address.StartsWith("W") ? CModeArea.AR : CModeArea.AR;
                numPart = address.Substring(1);
            }
            else if (address.StartsWith("HR"))
            {
                area = CModeArea.HR;
                numPart = address.Substring(2);
            }
            else if (address.StartsWith("H"))
            {
                area = CModeArea.HR;
                numPart = address.Substring(1);
            }
            else if (address.StartsWith("LR"))
            {
                area = CModeArea.LR;
                numPart = address.Substring(2);
            }
            else if (address.StartsWith("L"))
            {
                area = CModeArea.LR;
                numPart = address.Substring(1);
            }
            else if (address.StartsWith("T"))
            {
                area = CModeArea.TC;
                numPart = address.Substring(1);
            }
            else if (address.StartsWith("C"))
            {
                area = CModeArea.TC;
                numPart = address.Substring(1);
            }
            else if (char.IsDigit(address[0]))
            {
                area = CModeArea.DM;
                numPart = address;
            }
            else
            {
                return false;
            }

            if (string.IsNullOrEmpty(numPart))
                return false;

            foreach (char c in numPart)
            {
                if (!char.IsDigit(c))
                    return false;
            }

            if (!ushort.TryParse(numPart, NumberStyles.None, CultureInfo.InvariantCulture, out ushort wordAddr2))
                return false;

            string original = address + (bitOffset >= 0 ? $".{bitOffset}" : "");
            parsed = new OmronHostLinkCModeAddress(original, area, wordAddr2, bitOffset);
            return true;
        }
    }
}
