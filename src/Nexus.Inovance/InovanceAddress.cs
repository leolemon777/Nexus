using System;

namespace Nexus.Inovance
{
    /// <summary>汇川 Easy 系列地址区域类型。</summary>
    public enum InovanceArea
    {
        /// <summary>扩展区域 (UB/UW/U) — 类型码 0xF0。</summary>
        Extended,
        /// <summary>输入 (X) — 类型码 0x00，八进制。</summary>
        Input,
        /// <summary>输出 (Y) — 类型码 0x00，八进制 + 0x80000 偏移。</summary>
        Output,
        /// <summary>辅助继电器 (M) — 类型码 0x10。</summary>
        AuxiliaryRelay,
        /// <summary>步进继电器 (S) — 类型码 0x10 + 0x80000 偏移。</summary>
        StepRelay,
        /// <summary>链接继电器 (B) — 类型码 0x20。</summary>
        LinkRelay,
        /// <summary>数据寄存器 (D) — 类型码 0x40。</summary>
        DataRegister,
        /// <summary>系统寄存器 (R) — 类型码 0x50。</summary>
        SystemRegister,
        /// <summary>链接寄存器 (W) — 类型码 0x60。</summary>
        LinkRegister,
    }

    /// <summary>汇川 PLC 型号。</summary>
    public enum InovanceModel
    {
        /// <summary>H1U 系列。</summary>
        H1U,
        /// <summary>H1U-S 系列。</summary>
        H1US,
        /// <summary>H2U 系列。</summary>
        H2U,
        /// <summary>H3U 系列。</summary>
        H3U,
        /// <summary>H5U 系列。</summary>
        H5U,
        /// <summary>AM 系列。</summary>
        AM,
        /// <summary>AM-N 系列。</summary>
        AMN,
        /// <summary>AC 系列。</summary>
        AC,
        /// <summary>XG 系列。</summary>
        XG,
    }

    /// <summary>解析后的汇川 Easy 地址。</summary>
    public sealed class InovanceAddress
    {
        /// <summary>区域类型。</summary>
        public InovanceArea Area { get; }
        /// <summary>类型码字节。</summary>
        public byte TypeCode { get; }
        /// <summary>地址数值。</summary>
        public int Value { get; }
        /// <summary>是否为扩展地址（16 进制）。</summary>
        public bool IsExtended { get; }
        /// <summary>原始地址字符串。</summary>
        public string RawAddress { get; }

        private InovanceAddress(InovanceArea area, byte typeCode, int value, bool isExtended, string raw)
        {
            Area = area;
            TypeCode = typeCode;
            Value = value;
            IsExtended = isExtended;
            RawAddress = raw;
        }

        /// <summary>
        /// 解析汇川 Easy 地址字符串。
        /// 支持: D100, W200, R10, X0(八进制), Y0(八进制), M100, S50, B3, UB0xFF, UW0x10, U0xFF。
        /// W/D/R 支持位寻址: D100.5（第 100 字的第 5 位）。
        /// </summary>
        public static InovanceAddress? TryParse(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            try
            {
                string upper = address.ToUpperInvariant();

                // UB/UW — 16 进制扩展地址
                if (upper.StartsWith("UB") || upper.StartsWith("UW"))
                {
                    int val = Convert.ToInt32(address.Substring(2), 16);
                    return new InovanceAddress(InovanceArea.Extended, 0xF0, val, true, address);
                }

                // U — 16 进制扩展地址
                if (upper.StartsWith("U"))
                {
                    int val = Convert.ToInt32(address.Substring(1), 16);
                    return new InovanceAddress(InovanceArea.Extended, 0xF0, val, true, address);
                }

                // W — 链接寄存器（支持位寻址）
                if (upper.StartsWith("W"))
                {
                    int val = ParseWordAddress(address.Substring(1));
                    return new InovanceAddress(InovanceArea.LinkRegister, 0x60, val, false, address);
                }

                // D — 数据寄存器（支持位寻址）
                if (upper.StartsWith("D"))
                {
                    int val = ParseWordAddress(address.Substring(1));
                    return new InovanceAddress(InovanceArea.DataRegister, 0x40, val, false, address);
                }

                // R — 系统寄存器（支持位寻址）
                if (upper.StartsWith("R"))
                {
                    int val = ParseWordAddress(address.Substring(1));
                    return new InovanceAddress(InovanceArea.SystemRegister, 0x50, val, false, address);
                }

                // X — 输入（八进制）
                if (upper.StartsWith("X"))
                {
                    int val = Convert.ToInt32(address.Substring(1), 8);
                    return new InovanceAddress(InovanceArea.Input, 0x00, val, false, address);
                }

                // Y — 输出（八进制 + 0x80000 偏移）
                if (upper.StartsWith("Y"))
                {
                    int val = Convert.ToInt32(address.Substring(1), 8) + 0x80000;
                    return new InovanceAddress(InovanceArea.Output, 0x00, val, false, address);
                }

                // M — 辅助继电器
                if (upper.StartsWith("M"))
                {
                    int val = Convert.ToInt32(address.Substring(1));
                    return new InovanceAddress(InovanceArea.AuxiliaryRelay, 0x10, val, false, address);
                }

                // S — 步进继电器（+ 0x80000 偏移）
                if (upper.StartsWith("S"))
                {
                    int val = Convert.ToInt32(address.Substring(1)) + 0x80000;
                    return new InovanceAddress(InovanceArea.StepRelay, 0x10, val, false, address);
                }

                // B — 链接继电器
                if (upper.StartsWith("B"))
                {
                    int val = Convert.ToInt32(address.Substring(1));
                    return new InovanceAddress(InovanceArea.LinkRelay, 0x20, val, false, address);
                }

                return null;
            }
            catch { return null; }
        }

        /// <summary>解析 W/D/R 类型的位寻址地址。</summary>
        private static int ParseWordAddress(string addrPart)
        {
            int dotIdx = addrPart.IndexOf('.');
            if (dotIdx >= 0)
            {
                int wordNo = int.Parse(addrPart.Substring(0, dotIdx));
                int bitNo = int.Parse(addrPart.Substring(dotIdx + 1));
                return wordNo * 16 + bitNo;
            }
            return int.Parse(addrPart) * 16;
        }

        /// <summary>获取 4 字节地址编码（与 InovanceEasyClient.ParseAddress 格式一致）。</summary>
        public byte[] ToAddressBytes()
        {
            var result = new byte[4];
            result[0] = (byte)(Value & 0xFF);
            result[1] = (byte)((Value >> 8) & 0xFF);
            result[2] = (byte)((Value >> 16) & 0xFF);
            if (IsExtended)
                result[3] = (byte)(TypeCode | 0x80);
            else
                result[3] = (byte)((Value >> 24) & 0xFF);

            // 把类型码编码到第 3 字节高 4 位
            if (!IsExtended)
            {
                result[2] = (byte)((Value & 0xFF) | ((TypeCode & 0x0F) << 4));
            }

            return result;
        }
    }
}
