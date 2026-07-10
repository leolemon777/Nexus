using System;

namespace Nexus.Hitachi
{
    /// <summary>
    /// 日立 EH-150 系列 PLC 地址解析（日系 operand → Modbus）。
    /// </summary>
    /// <remarks>
    /// <para>EH-150 CPU 本身不内建 Modbus；Modbus master/slave 能力由 <b>EH-SIO 串口通信模块</b>提供，
    /// 且仅支持 Modbus RTU（无原生 TCP，需通过串口服务器/网关转 RTU-over-TCP）。</para>
    /// <para>本解析器把日系 operand（D/X/Y/M/T/C/W/R）映射到标准 Modbus 区：</para>
    /// <list type="bullet">
    /// <item><description><c>D</c>(数据寄存器) → Holding Register（FC03 / FC06·16）</description></item>
    /// <item><description><c>R</c>/<c>W</c>(扩展寄存器) → Holding Register（FC03 / FC06·16）</description></item>
    /// <item><description><c>T</c>/<c>C</c>(定时/计数器当前值) → Holding Register（FC03）</description></item>
    /// <item><description><c>Y</c>(输出) → Coil（FC01 / FC05）</description></item>
    /// <item><description><c>M</c>/<c>L</c>(内部继电器) → Coil（FC01 / FC05）</description></item>
    /// <item><description><c>X</c>(输入) → Discrete Input（FC02，只读）</description></item>
    /// </list>
    /// <para><b>重要：地址映射表基于日系 PLC 惯例，未实机验证</b>。具体偏移请以 EH-SIO Application Manual (NJI443BX) 为准。</para>
    /// </remarks>
    public static class HitachiAddress
    {
        /// <summary>
        /// 解析日立 operand 为 (Modbus 地址, 读 FC, 写 FC)。写 FC 为 0 表示只读。
        /// </summary>
        /// <param name="address">operand，例如 "D100"、"X0"、"Y10"、"M100"、"T0"、"C0"、"W100"、"R100"。</param>
        /// <returns>(Modbus 地址, 读功能码, 写功能码)。</returns>
        public static (ushort address, byte readFc, byte writeFc) Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空", nameof(address));

            string s = address.Trim().ToUpperInvariant();
            if (s.Length < 2)
                throw new ArgumentException($"地址格式无效: {address}", nameof(address));

            char prefix = s[0];
            string numStr = s.Substring(1);
            int num = ParseInt(numStr);

            // 日系惯例映射（基于 EH-SIO Modbus 约定，未实机验证）
            switch (prefix)
            {
                case 'D': // 数据寄存器 → Holding Register
                    return ((ushort)num, 0x03, 0x06);
                case 'R': // 扩展寄存器
                case 'W':
                    return ((ushort)(0x1000 + num), 0x03, 0x06);
                case 'T': // 定时器当前值 → Holding Register
                    return ((ushort)(0x2000 + num), 0x03, 0x00);
                case 'C': // 计数器当前值 → Holding Register
                    return ((ushort)(0x2800 + num), 0x03, 0x00);
                case 'Y': // 输出 → Coil
                    return ((ushort)(0x0020 + num), 0x01, 0x05);
                case 'M': // 内部继电器 → Coil
                case 'L':
                    return ((ushort)(0x0100 + num), 0x01, 0x05);
                case 'X': // 输入 → Discrete Input (只读)
                    return ((ushort)(0x0000 + num), 0x02, 0x00);
                default:
                    throw new ArgumentException($"无法识别的日立地址前缀: {prefix}");
            }
        }

        /// <summary>尝试解析，失败返回 false。</summary>
        public static bool TryParse(string address, out (ushort addr, byte readFc, byte writeFc) result)
        {
            try { result = Parse(address); return true; }
            catch { result = default; return false; }
        }

        private static int ParseInt(string s)
        {
            s = s.TrimStart('0');
            if (s.Length == 0) return 0;
            if (int.TryParse(s, out int val)) return val;
            throw new ArgumentException($"数字部分无效: {s}");
        }
    }
}
