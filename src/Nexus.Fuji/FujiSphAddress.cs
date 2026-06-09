using System;

namespace Nexus.Fuji
{
    /// <summary>富士 SPH/SPB 地址区域类型。</summary>
    public enum FujiArea
    {
        /// <summary>数据寄存器 (D) — 区域码 01。</summary>
        DataRegister,
        /// <summary>内部继电器 (M) — 区域码 02。</summary>
        InternalRelay,
        /// <summary>输入 (X) — 区域码 03。</summary>
        Input,
        /// <summary>输出 (Y) — 区域码 04。</summary>
        Output,
        /// <summary>定时器 (T) — 区域码 05。</summary>
        Timer,
        /// <summary>计数器 (C) — 区域码 06。</summary>
        Counter,
        /// <summary>文件寄存器 (R) — 区域码 07。</summary>
        FileRegister,
        /// <summary>链接寄存器 (L) — 区域码 08。</summary>
        LinkRegister,
    }

    /// <summary>富士 PLC 型号。</summary>
    public enum FujiPlcModel
    {
        /// <summary>SPH 系列。</summary>
        SPH,
        /// <summary>SPB 系列。</summary>
        SPB,
        /// <summary>SPB-N 系列。</summary>
        SPBN,
        /// <summary>NX 系列。</summary>
        NX,
        /// <summary>MICREX-SX 系列。</summary>
        MicrexSX,
        /// <summary>MICREX-SX SP10。</summary>
        SP10,
        /// <summary>MICREX-SX SP20。</summary>
        SP20,
    }

    /// <summary>解析后的富士 S-BUS 地址。</summary>
    public sealed class FujiSphAddress
    {
        /// <summary>区域类型。</summary>
        public FujiArea Area { get; }
        /// <summary>S-BUS 区域码（2 位十六进制字符串）。</summary>
        public string AreaCode { get; }
        /// <summary>数值地址。</summary>
        public int Number { get; }
        /// <summary>原始地址字符串。</summary>
        public string RawAddress { get; }

        private FujiSphAddress(FujiArea area, string areaCode, int number, string raw)
        {
            Area = area;
            AreaCode = areaCode;
            Number = number;
            RawAddress = raw;
        }

        /// <summary>
        /// 解析富士 SPH/SPB 地址字符串。
        /// 支持: D100, M0, X0, Y0, T0, C0, R100, L100。
        /// </summary>
        public static FujiSphAddress? TryParse(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            try
            {
                string addr = address.Trim().ToUpperInvariant();
                if (addr.Length < 2) return null;
                char prefix = addr[0];
                if (!int.TryParse(addr.Substring(1), out int num)) return null;

                switch (prefix)
                {
                    case 'D': return new FujiSphAddress(FujiArea.DataRegister, "01", num, address);
                    case 'M': return new FujiSphAddress(FujiArea.InternalRelay, "02", num, address);
                    case 'X': return new FujiSphAddress(FujiArea.Input, "03", num, address);
                    case 'Y': return new FujiSphAddress(FujiArea.Output, "04", num, address);
                    case 'T': return new FujiSphAddress(FujiArea.Timer, "05", num, address);
                    case 'C': return new FujiSphAddress(FujiArea.Counter, "06", num, address);
                    case 'R': return new FujiSphAddress(FujiArea.FileRegister, "07", num, address);
                    case 'L': return new FujiSphAddress(FujiArea.LinkRegister, "08", num, address);
                    default: return null;
                }
            }
            catch { return null; }
        }
    }
}
