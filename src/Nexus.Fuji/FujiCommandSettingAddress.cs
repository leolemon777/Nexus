using System;

namespace Nexus.Fuji
{
    /// <summary>富士 Command Setting 协议命令码。</summary>
    public enum FujiCommandCode : byte
    {
        /// <summary>读取命令。</summary>
        Read = 0x01,
        /// <summary>写入命令。</summary>
        Write = 0x02,
        /// <summary>读取位。</summary>
        ReadBit = 0x03,
        /// <summary>写入位。</summary>
        WriteBit = 0x04,
    }

    /// <summary>富士 Command Setting 协议数据类型。</summary>
    public enum FujiCommandDataType : byte
    {
        /// <summary>字（16 位）。</summary>
        Word = 0x00,
        /// <summary>位。</summary>
        Bit = 0x01,
        /// <summary>双字（32 位）。</summary>
        DWord = 0x02,
    }

    /// <summary>解析后的富士 Command Setting 地址。</summary>
    public sealed class FujiCommandSettingAddress
    {
        /// <summary>区域码（如 D=0x0C, M=0x02, R=0x0D, W=0x0E）。</summary>
        public FujiSpbTypeCode TypeCode { get; }

        /// <summary>字地址。</summary>
        public int WordAddress { get; }

        /// <summary>位索引（-1 表示字访问，0-15 表示位访问）。</summary>
        public int BitIndex { get; }

        /// <summary>是否为位访问。</summary>
        public bool IsBit => BitIndex >= 0;

        /// <summary>原始地址字符串。</summary>
        public string RawAddress { get; }

        private FujiCommandSettingAddress(FujiSpbTypeCode typeCode, int wordAddress, int bitIndex, string raw)
        {
            TypeCode = typeCode;
            WordAddress = wordAddress;
            BitIndex = bitIndex;
            RawAddress = raw;
        }

        /// <summary>
        /// 解析富士 Command Setting 地址。
        /// <para>支持: D100, M50, R100, W50, D100.5</para>
        /// </summary>
        public static FujiCommandSettingAddress? TryParse(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            try
            {
                string addr = address.Trim().ToUpperInvariant();
                if (addr.Length < 2) return null;

                int dotIdx = addr.IndexOf('.');
                string mainPart;
                int bitIndex = -1;

                if (dotIdx > 0)
                {
                    mainPart = addr.Substring(0, dotIdx);
                    if (!int.TryParse(addr.Substring(dotIdx + 1), out int bi) || bi < 0 || bi > 15)
                        return null;
                    bitIndex = bi;
                }
                else
                {
                    mainPart = addr;
                }

                if (mainPart.Length < 2) return null;
                char prefix = mainPart[0];
                string numStr = mainPart.Substring(1);

                FujiSpbTypeCode typeCode;
                switch (prefix)
                {
                    case 'D': typeCode = FujiSpbTypeCode.DataRegister; break;
                    case 'M': typeCode = FujiSpbTypeCode.InternalRelay; break;
                    case 'R': typeCode = FujiSpbTypeCode.FileRegister; break;
                    case 'W': typeCode = FujiSpbTypeCode.LinkRegister; break;
                    case 'X': typeCode = FujiSpbTypeCode.Input; break;
                    case 'Y': typeCode = FujiSpbTypeCode.Output; break;
                    case 'L': typeCode = FujiSpbTypeCode.LatchRelay; break;
                    case 'T': typeCode = FujiSpbTypeCode.TimerCurrentValue; break;
                    case 'C': typeCode = FujiSpbTypeCode.CounterCurrentValue; break;
                    default: return null;
                }

                if (!int.TryParse(numStr, out int num) || num < 0)
                    return null;

                return new FujiCommandSettingAddress(typeCode, num, bitIndex, address);
            }
            catch
            {
                return null;
            }
        }
    }
}
