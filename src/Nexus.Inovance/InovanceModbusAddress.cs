using System;

namespace Nexus.Inovance
{
    /// <summary>汇川 Modbus 系列类型。</summary>
    public enum InovanceSeries
    {
        /// <summary>AM 系列。</summary>
        AM,
        /// <summary>H3U 系列。</summary>
        H3U,
        /// <summary>H5U 系列。</summary>
        H5U,
    }

    /// <summary>
    /// 汇川 Modbus 协议地址解析器。
    /// 支持 AM、H3U、H5U 系列 PLC 地址到 Modbus 地址的映射。
    /// </summary>
    public static class InovanceModbusAddress
    {
        private const byte Fc01 = 0x01;
        private const byte Fc02 = 0x02;
        private const byte Fc03 = 0x03;
        private const byte Fc05 = 0x05;
        private const byte Fc06 = 0x06;
        private const byte Fc15 = 0x0F;
        private const byte Fc16 = 0x10;
        private const byte NoWrite = 0x00;

        /// <summary>
        /// 解析汇川 Modbus 地址字符串。
        /// </summary>
        /// <param name="address">PLC 地址（已去除运行时参数，如 D100, MX10.3, X7）。</param>
        /// <param name="series">汇川 PLC 系列。</param>
        /// <returns>(Modbus 地址, 读功能码, 写功能码)。</returns>
        public static (ushort modbusAddress, byte readFc, byte writeFc) Parse(string address, InovanceSeries series)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空");

            string upper = address.ToUpperInvariant();

            switch (series)
            {
                case InovanceSeries.AM:
                    return ParseAm(upper);
                case InovanceSeries.H3U:
                    return ParseH3U(upper);
                case InovanceSeries.H5U:
                    return ParseH5U(upper);
                default:
                    throw new ArgumentException($"不支持的系列: {series}");
            }
        }

        // ═══════════════════════════════════════════
        //  AM 系列
        // ═══════════════════════════════════════════

        private static (ushort, byte, byte) ParseAm(string upper)
        {
            // QX — 输出位
            if (upper.StartsWith("QX"))
            {
                int addr = int.Parse(upper.Substring(2));
                return ((ushort)addr, Fc01, Fc15);
            }

            // IX — 输入位
            if (upper.StartsWith("IX"))
            {
                int addr = int.Parse(upper.Substring(2));
                return ((ushort)addr, Fc02, NoWrite);
            }

            // Q — 输出位
            if (upper.StartsWith("Q"))
            {
                int addr = int.Parse(upper.Substring(1));
                return ((ushort)addr, Fc01, Fc15);
            }

            // I — 输入位
            if (upper.StartsWith("I") && !upper.StartsWith("IX"))
            {
                int addr = int.Parse(upper.Substring(1));
                return ((ushort)addr, Fc02, NoWrite);
            }

            // SMX — 特殊 M 位
            if (upper.StartsWith("SMX"))
            {
                int addr = int.Parse(upper.Substring(3));
                return ((ushort)(addr + 10000), Fc01, Fc15);
            }

            // SMW — 特殊 M 字
            if (upper.StartsWith("SMW"))
            {
                int addr = int.Parse(upper.Substring(3));
                return ((ushort)(addr + 10000), Fc03, Fc16);
            }

            // SM — 特殊 M
            if (upper.StartsWith("SM"))
            {
                int addr = int.Parse(upper.Substring(2));
                return ((ushort)(addr + 10000), Fc01, Fc15);
            }

            // SDW — 特殊 D 字
            if (upper.StartsWith("SDW"))
            {
                int addr = int.Parse(upper.Substring(3));
                return ((ushort)(addr + 10000), Fc03, Fc16);
            }

            // SD — 特殊 D
            if (upper.StartsWith("SD"))
            {
                int addr = int.Parse(upper.Substring(2));
                return ((ushort)(addr + 10000), Fc03, Fc06);
            }

            // SR — 特殊 R
            if (upper.StartsWith("SR"))
            {
                int addr = int.Parse(upper.Substring(2));
                return ((ushort)(addr + 10000), Fc03, Fc06);
            }

            // MX — M 位（支持点寻址 MX10.3）
            if (upper.StartsWith("MX"))
            {
                string numPart = upper.Substring(2);
                int dotIdx = numPart.IndexOf('.');
                if (dotIdx >= 0)
                {
                    int wordAddr = int.Parse(numPart.Substring(0, dotIdx));
                    int bitAddr = int.Parse(numPart.Substring(dotIdx + 1));
                    ushort modbusAddr = (ushort)(wordAddr / 2);
                    byte writeFc = (wordAddr % 2 == 0) ? Fc05 : Fc15;
                    return (modbusAddr, Fc01, writeFc);
                }
                else
                {
                    int addr = int.Parse(numPart);
                    return ((ushort)(addr / 2), Fc01, Fc15);
                }
            }

            // MW — M 字
            if (upper.StartsWith("MW"))
            {
                int addr = int.Parse(upper.Substring(2));
                return ((ushort)addr, Fc03, Fc16);
            }

            // MD — M 双字
            if (upper.StartsWith("MD"))
            {
                int addr = int.Parse(upper.Substring(2));
                return ((ushort)(addr * 2), Fc03, Fc16);
            }

            // MB — M 字节
            if (upper.StartsWith("MB"))
            {
                int addr = int.Parse(upper.Substring(2));
                return ((ushort)(addr / 2), Fc03, Fc16);
            }

            // QC — 输出线圈
            if (upper.StartsWith("QC"))
            {
                int addr = int.Parse(upper.Substring(2));
                return ((ushort)addr, Fc01, Fc05);
            }

            // IC — 输入线圈
            if (upper.StartsWith("IC"))
            {
                int addr = int.Parse(upper.Substring(2));
                return ((ushort)addr, Fc02, NoWrite);
            }

            // D — 数据寄存器（支持点寻址）
            if (upper.StartsWith("D"))
            {
                string numPart = upper.Substring(1);
                int addr = ParseDotAddress(numPart);
                return ((ushort)addr, Fc03, Fc06);
            }

            // M — 内部继电器（位操作）
            if (upper.StartsWith("M"))
            {
                int addr = int.Parse(upper.Substring(1));
                return ((ushort)addr, Fc01, Fc05);
            }

            throw new ArgumentException($"AM 系列不支持的地址: {upper}");
        }

        // ═══════════════════════════════════════════
        //  H3U 系列
        // ═══════════════════════════════════════════

        private static (ushort, byte, byte) ParseH3U(string upper)
        {
            // X — 输入（八进制）
            if (upper.StartsWith("X"))
            {
                int addr = Convert.ToInt32(upper.Substring(1), 8);
                return ((ushort)(addr + 63488), Fc01, Fc15);
            }

            // Y — 输出（八进制）
            if (upper.StartsWith("Y"))
            {
                int addr = Convert.ToInt32(upper.Substring(1), 8);
                return ((ushort)(addr + 64512), Fc01, Fc15);
            }

            // SM — 特殊继电器
            if (upper.StartsWith("SM"))
            {
                int addr = int.Parse(upper.Substring(2));
                return ((ushort)(addr + 9216), Fc01, Fc15);
            }

            // S — 步进继电器
            if (upper.StartsWith("S"))
            {
                int addr = int.Parse(upper.Substring(1));
                return ((ushort)(addr + 57344), Fc01, Fc15);
            }

            // T — 定时器
            if (upper.StartsWith("T"))
            {
                int addr = int.Parse(upper.Substring(1));
                return ((ushort)(addr + 61440), Fc03, Fc06);
            }

            // C — 计数器
            if (upper.StartsWith("C"))
            {
                int addr = int.Parse(upper.Substring(1));
                if (addr < 200)
                    return ((ushort)(addr + 62464), Fc03, Fc06);
                else
                    return ((ushort)((addr - 200) * 2 + 63232), Fc03, Fc16);
            }

            // SD — 特殊寄存器
            if (upper.StartsWith("SD"))
            {
                int addr = int.Parse(upper.Substring(2));
                return ((ushort)(addr + 9216), Fc03, Fc06);
            }

            // R — 文件寄存器
            if (upper.StartsWith("R"))
            {
                int addr = int.Parse(upper.Substring(1));
                return ((ushort)(addr + 12288), Fc03, Fc16);
            }

            // D — 数据寄存器
            if (upper.StartsWith("D"))
            {
                int addr = int.Parse(upper.Substring(1));
                return ((ushort)addr, Fc03, Fc06);
            }

            // M — 内部继电器
            if (upper.StartsWith("M"))
            {
                int addr = int.Parse(upper.Substring(1));
                return ((ushort)addr, Fc01, Fc05);
            }

            throw new ArgumentException($"H3U 系列不支持的地址: {upper}");
        }

        // ═══════════════════════════════════════════
        //  H5U 系列
        // ═══════════════════════════════════════════

        private static (ushort, byte, byte) ParseH5U(string upper)
        {
            // X — 输入（八进制）
            if (upper.StartsWith("X"))
            {
                int addr = Convert.ToInt32(upper.Substring(1), 8);
                return ((ushort)(addr + 63488), Fc01, Fc15);
            }

            // Y — 输出（八进制）
            if (upper.StartsWith("Y"))
            {
                int addr = Convert.ToInt32(upper.Substring(1), 8);
                return ((ushort)(addr + 64512), Fc01, Fc15);
            }

            // S — 步进继电器
            if (upper.StartsWith("S"))
            {
                int addr = int.Parse(upper.Substring(1));
                return ((ushort)(addr + 57344), Fc01, Fc15);
            }

            // B — B 继电器
            if (upper.StartsWith("B"))
            {
                int addr = int.Parse(upper.Substring(1));
                return ((ushort)(addr + 12288), Fc01, Fc15);
            }

            // R — 文件寄存器
            if (upper.StartsWith("R"))
            {
                int addr = int.Parse(upper.Substring(1));
                return ((ushort)(addr + 12288), Fc03, Fc16);
            }

            // D — 数据寄存器
            if (upper.StartsWith("D"))
            {
                int addr = int.Parse(upper.Substring(1));
                return ((ushort)addr, Fc03, Fc06);
            }

            // M — 内部继电器
            if (upper.StartsWith("M"))
            {
                int addr = int.Parse(upper.Substring(1));
                return ((ushort)addr, Fc01, Fc05);
            }

            throw new ArgumentException($"H5U 系列不支持的地址: {upper}");
        }

        // ═══════════════════════════════════════════
        //  辅助方法
        // ═══════════════════════════════════════════

        /// <summary>
        /// 解析支持点寻址的地址（如 100.5 → 100 * 16 + 5 = 1605）。
        /// 无点号时返回 addr * 16。
        /// </summary>
        private static int ParseDotAddress(string numPart)
        {
            int dotIdx = numPart.IndexOf('.');
            if (dotIdx >= 0)
            {
                int wordNo = int.Parse(numPart.Substring(0, dotIdx));
                int bitNo = int.Parse(numPart.Substring(dotIdx + 1));
                return wordNo * 16 + bitNo;
            }
            return int.Parse(numPart) * 16;
        }
    }
}
