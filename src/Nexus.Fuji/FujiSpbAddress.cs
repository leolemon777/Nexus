using System;

namespace Nexus.Fuji
{
    /// <summary>富士 SPB 协议类型码。</summary>
    public enum FujiSpbTypeCode : byte
    {
        /// <summary>输出 (Y) — 0x00。</summary>
        Output = 0x00,
        /// <summary>输入 (X) — 0x01。</summary>
        Input = 0x01,
        /// <summary>内部继电器 (M) — 0x02。</summary>
        InternalRelay = 0x02,
        /// <summary>锁存继电器 (L) — 0x03。</summary>
        LatchRelay = 0x03,
        /// <summary>定时器/计数器线圈 (TC) — 0x04。</summary>
        TimerCounterCoil = 0x04,
        /// <summary>计数器线圈 (CC) — 0x05。</summary>
        CounterCoil = 0x05,
        /// <summary>定时器当前值 (TN) — 0x0A。</summary>
        TimerCurrentValue = 0x0A,
        /// <summary>计数器当前值 (CN) — 0x0B。</summary>
        CounterCurrentValue = 0x0B,
        /// <summary>数据寄存器 (D) — 0x0C。</summary>
        DataRegister = 0x0C,
        /// <summary>文件寄存器 (R) — 0x0D。</summary>
        FileRegister = 0x0D,
        /// <summary>链接寄存器 (W) — 0x0E。</summary>
        LinkRegister = 0x0E,
    }

    /// <summary>解析后的富士 SPB 地址。</summary>
    public sealed class FujiSpbAddress
    {
        /// <summary>类型码。</summary>
        public FujiSpbTypeCode TypeCode { get; }

        /// <summary>字地址（对于位区域为绝对位地址，对于字区域为字地址）。</summary>
        public int WordAddress { get; }

        /// <summary>位索引（-1 表示字访问，0-15 表示位访问）。</summary>
        public int BitIndex { get; }

        /// <summary>是否为位访问。</summary>
        public bool IsBit => BitIndex >= 0;

        /// <summary>是否为位区域（线圈类）。</summary>
        public bool IsBitArea =>
            TypeCode == FujiSpbTypeCode.Input ||
            TypeCode == FujiSpbTypeCode.Output ||
            TypeCode == FujiSpbTypeCode.InternalRelay ||
            TypeCode == FujiSpbTypeCode.LatchRelay ||
            TypeCode == FujiSpbTypeCode.TimerCounterCoil ||
            TypeCode == FujiSpbTypeCode.CounterCoil;

        /// <summary>原始地址字符串。</summary>
        public string RawAddress { get; }

        private FujiSpbAddress(FujiSpbTypeCode typeCode, int wordAddress, int bitIndex, string raw)
        {
            TypeCode = typeCode;
            WordAddress = wordAddress;
            BitIndex = bitIndex;
            RawAddress = raw;
        }

        /// <summary>
        /// 解析富士 SPB 地址。
        /// <para>支持: X10, Y20, M100, L50, TC10, CC10, TN10, CN10, D200, R100, W50</para>
        /// <para>位访问: D100.12, M50.3</para>
        /// </summary>
        public static FujiSpbAddress? TryParse(string address)
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

                FujiSpbTypeCode typeCode;
                string numStr;

                if (mainPart.StartsWith("TC"))
                {
                    typeCode = FujiSpbTypeCode.TimerCounterCoil;
                    numStr = mainPart.Substring(2);
                }
                else if (mainPart.StartsWith("CC"))
                {
                    typeCode = FujiSpbTypeCode.CounterCoil;
                    numStr = mainPart.Substring(2);
                }
                else if (mainPart.StartsWith("TN"))
                {
                    typeCode = FujiSpbTypeCode.TimerCurrentValue;
                    numStr = mainPart.Substring(2);
                }
                else if (mainPart.StartsWith("CN"))
                {
                    typeCode = FujiSpbTypeCode.CounterCurrentValue;
                    numStr = mainPart.Substring(2);
                }
                else if (mainPart.Length >= 2)
                {
                    char prefix = mainPart[0];
                    numStr = mainPart.Substring(1);

                    switch (prefix)
                    {
                        case 'X': typeCode = FujiSpbTypeCode.Input; break;
                        case 'Y': typeCode = FujiSpbTypeCode.Output; break;
                        case 'M': typeCode = FujiSpbTypeCode.InternalRelay; break;
                        case 'L': typeCode = FujiSpbTypeCode.LatchRelay; break;
                        case 'D': typeCode = FujiSpbTypeCode.DataRegister; break;
                        case 'R': typeCode = FujiSpbTypeCode.FileRegister; break;
                        case 'W': typeCode = FujiSpbTypeCode.LinkRegister; break;
                        default: return null;
                    }
                }
                else
                {
                    return null;
                }

                if (!int.TryParse(numStr, out int num) || num < 0)
                    return null;

                return new FujiSpbAddress(typeCode, num, bitIndex, address);
            }
            catch
            {
                return null;
            }
        }
    }
}
