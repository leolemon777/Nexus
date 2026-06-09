using System;

namespace Nexus.Fatek
{
    /// <summary>Fatek 地址区域类型。</summary>
    public enum FatekArea
    {
        /// <summary>内部继电器 (R)。</summary>
        InternalRelay,
        /// <summary>输入 (X)。</summary>
        Input,
        /// <summary>输出 (Y)。</summary>
        Output,
        /// <summary>辅助继电器 (M)。</summary>
        AuxiliaryRelay,
        /// <summary>数据寄存器 (D) — 16 位。</summary>
        DataRegister,
        /// <summary>定时器当前值 (T)。</summary>
        TimerValue,
        /// <summary>计数器当前值 (C)。</summary>
        CounterValue,
        /// <summary>定时器触点 (TS)。</summary>
        TimerContact,
        /// <summary>计数器触点 (CS)。</summary>
        CounterContact,
        /// <summary>步进继电器 (S)。</summary>
        StepRelay,
    }

    /// <summary>Fatek PLC 型号。</summary>
    public enum FatekModel
    {
        /// <summary>FBs-10MA。</summary>
        FBs10MA,
        /// <summary>FBs-14MA。</summary>
        FBs14MA,
        /// <summary>FBs-20MA。</summary>
        FBs20MA,
        /// <summary>FBs-24MA。</summary>
        FBs24MA,
        /// <summary>FBs-32MA。</summary>
        FBs32MA,
        /// <summary>FBs-40MA。</summary>
        FBs40MA,
        /// <summary>FBs-60MA。</summary>
        FBs60MA,
        /// <summary>FBs-10MC。</summary>
        FBs10MC,
        /// <summary>FBs-20MC。</summary>
        FBs20MC,
        /// <summary>FBs-32MC。</summary>
        FBs32MC,
        /// <summary>FBs-44MC。</summary>
        FBs44MC,
        /// <summary>FBs-10MB。</summary>
        FBs10MB,
        /// <summary>FBs-20MB。</summary>
        FBs20MB,
        /// <summary>FBs-32MB。</summary>
        FBs32MB,
        /// <summary>FBs-44MB。</summary>
        FBs44MB,
        /// <summary>FBs-10BE。</summary>
        FBs10BE,
        /// <summary>FBs-20BE。</summary>
        FBs20BE,
        /// <summary>FBs-32BE。</summary>
        FBs32BE,
        /// <summary>B1 系列。</summary>
        B1,
        /// <summary>B1z 系列。</summary>
        B1z,
    }

    /// <summary>解析后的 Fatek 地址。</summary>
    public sealed class FatekAddress
    {
        /// <summary>区域类型。</summary>
        public FatekArea Area { get; }
        /// <summary>区域代码字符 (R/X/Y/M/D/T/C)。</summary>
        public string AreaCode { get; }
        /// <summary>数值地址。</summary>
        public int Number { get; }
        /// <summary>是否为位操作区域。</summary>
        public bool IsBit { get; }

        private FatekAddress(FatekArea area, string code, int number, bool isBit)
        {
            Area = area;
            AreaCode = code;
            Number = number;
            IsBit = isBit;
        }

        /// <summary>
        /// 解析 Fatek 地址字符串。
        /// 支持: R0, R9999, X0, Y0, M0, D100, D3899, T0, C0, TS0, CS0, S0。
        /// </summary>
        public static FatekAddress? TryParse(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            try
            {
                string addr = address.Trim().ToUpperInvariant();

                // 双字母前缀: TS, CS
                if (addr.Length >= 3 && (addr.StartsWith("TS") || addr.StartsWith("CS")))
                {
                    char prefix2 = addr[0];
                    if (!int.TryParse(addr.Substring(2), out int num2)) return null;
                    if (prefix2 == 'T')
                        return new FatekAddress(FatekArea.TimerContact, "TS", num2, true);
                    else
                        return new FatekAddress(FatekArea.CounterContact, "CS", num2, true);
                }

                if (addr.Length < 2) return null;
                char prefix = addr[0];
                if (!int.TryParse(addr.Substring(1), out int num)) return null;

                switch (prefix)
                {
                    case 'R': return new FatekAddress(FatekArea.InternalRelay, "R", num, true);
                    case 'X': return new FatekAddress(FatekArea.Input, "X", num, true);
                    case 'Y': return new FatekAddress(FatekArea.Output, "Y", num, true);
                    case 'M': return new FatekAddress(FatekArea.AuxiliaryRelay, "M", num, true);
                    case 'D': return new FatekAddress(FatekArea.DataRegister, "D", num, false);
                    case 'T': return new FatekAddress(FatekArea.TimerValue, "T", num, false);
                    case 'C': return new FatekAddress(FatekArea.CounterValue, "C", num, false);
                    case 'S': return new FatekAddress(FatekArea.StepRelay, "S", num, true);
                    default: return null;
                }
            }
            catch { return null; }
        }

        /// <summary>获取用于 Fatek 协议命令的地址格式化字符串。</summary>
        public string ToCommandFormat() => $"{AreaCode}{Number:D4}";
    }
}
