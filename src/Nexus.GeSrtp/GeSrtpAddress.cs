using System;

namespace Nexus.GeSrtp
{
    /// <summary>GE SRTP 内存区域类型。</summary>
    public enum GeSrtpArea : byte
    {
        /// <summary>寄存器 (%R) — 内存类型 0x08。</summary>
        Register = 0x08,
        /// <summary>模拟输入 (%AI) — 内存类型 0x0A。</summary>
        AnalogInput = 0x0A,
        /// <summary>模拟输出 (%AQ) — 内存类型 0x0C。</summary>
        AnalogOutput = 0x0C,
        /// <summary>离散输入 (%I) — 内存类型 0x10。</summary>
        DiscreteInput = 0x10,
        /// <summary>离散输出 (%Q) — 内存类型 0x12。</summary>
        DiscreteOutput = 0x12,
        /// <summary>系统内存 (%M) — 内存类型 0x14。</summary>
        SystemMemory = 0x14,
        /// <summary>定时器 (%T) — 内存类型 0x16。</summary>
        Timer = 0x16,
    }

    /// <summary>GE PLC 型号。</summary>
    public enum GePlcModel
    {
        /// <summary>Series 90-30。</summary>
        Series90_30,
        /// <summary>Series 90-70。</summary>
        Series90_70,
        /// <summary>PACSystems RX3i。</summary>
        PACSystemsRX3i,
        /// <summary>PACSystems RX7i。</summary>
        PACSystemsRX7i,
        /// <summary>VersaMax。</summary>
        VersaMax,
        /// <summary>VersaMax Nano/Micro。</summary>
        VersaMaxNano,
    }

    /// <summary>解析后的 GE SRTP 地址。</summary>
    public sealed class GeSrtpAddress
    {
        /// <summary>内存类型码。</summary>
        public byte MemoryType { get; }
        /// <summary>区域类型。</summary>
        public GeSrtpArea Area { get; }
        /// <summary>数值偏移地址。</summary>
        public int Offset { get; }
        /// <summary>原始地址字符串。</summary>
        public string RawAddress { get; }

        private GeSrtpAddress(byte memType, GeSrtpArea area, int offset, string raw)
        {
            MemoryType = memType;
            Area = area;
            Offset = offset;
            RawAddress = raw;
        }

        /// <summary>
        /// 解析 GE SRTP 地址字符串。
        /// 支持: R0, %R100, AI0, %AI10, AQ0, %AQ10, I0, %I100, Q0, %Q100, M0, %M100, T0, %T50。
        /// </summary>
        public static GeSrtpAddress? TryParse(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            try
            {
                string addr = address.Trim().ToUpperInvariant().Replace("%", "");
                if (addr.Length < 2) return null;

                char prefix = addr[0];
                byte memType;
                GeSrtpArea area;
                string numStr;

                // AI/AQ 双字符前缀
                if (prefix == 'A' && addr.Length > 1)
                {
                    char second = addr[1];
                    if (second == 'I')
                    {
                        memType = 0x0A;
                        area = GeSrtpArea.AnalogInput;
                        numStr = addr.Substring(2);
                    }
                    else if (second == 'Q')
                    {
                        memType = 0x0C;
                        area = GeSrtpArea.AnalogOutput;
                        numStr = addr.Substring(2);
                    }
                    else return null;
                }
                else
                {
                    numStr = addr.Substring(1);
                    switch (prefix)
                    {
                        case 'R':
                            memType = 0x08;
                            area = GeSrtpArea.Register;
                            break;
                        case 'I':
                            memType = 0x10;
                            area = GeSrtpArea.DiscreteInput;
                            break;
                        case 'Q':
                            memType = 0x12;
                            area = GeSrtpArea.DiscreteOutput;
                            break;
                        case 'M':
                            memType = 0x14;
                            area = GeSrtpArea.SystemMemory;
                            break;
                        case 'T':
                            memType = 0x16;
                            area = GeSrtpArea.Timer;
                            break;
                        default:
                            return null;
                    }
                }

                if (string.IsNullOrEmpty(numStr)) return null;
                if (!int.TryParse(numStr, out int offset)) return null;

                return new GeSrtpAddress(memType, area, offset, address);
            }
            catch { return null; }
        }
    }
}
