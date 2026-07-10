using System;

namespace Nexus.Phoenix
{
    /// <summary>
    /// Phoenix Contact AXC PLC 地址解析（IEC 61131-3 → Modbus）。
    /// </summary>
    /// <remarks>
    /// 支持的 IEC 地址语法（标准 IEC 编号，无统一偏移）：
    /// <list type="bullet">
    /// <item><description><c>%MWn</c>  保持寄存器 → Holding Register n（FC03 / FC16）</description></item>
    /// <item><description><c>%IWn</c>  输入寄存器 → Input Register n（FC04，只读）</description></item>
    /// <item><description><c>%QWn</c>  保持寄存器 → Holding Register n（FC03 / FC16）</description></item>
    /// <item><description><c>%IXn</c> 或 <c>%In</c>  离散输入 → Discrete Input n（FC02，只读）</description></item>
    /// <item><description><c>%QXn</c> 或 <c>%Qn</c> 或 <c>%Mn</c>  线圈 → Coil n（FC01 / FC05）</description></item>
    /// </list>
    /// <para><b>不支持</b>：<c>%IB</c> / <c>%QB</c> 字节寻址。Phoenix PLCnext Technology 的字节寻址
    /// 没有官方固定的 Modbus 偏移公式，必须在 PLCnext 程序侧将字节变量显式映射到寄存器/线圈后，
    /// 再以 <c>%MW</c>/<c>%QX</c> 等访问。</para>
    /// <para>地址映射来源：Phoenix Contact PLCnext Engineer Modbus Parameterization 文档。</para>
    /// </remarks>
    public sealed class PhoenixAddress
    {
        /// <summary>Modbus 起始地址。</summary>
        public ushort Address { get; }

        /// <summary>读功能码。</summary>
        public byte ReadFunctionCode { get; }

        /// <summary>写功能码（0 = 只读区域）。</summary>
        public byte WriteFunctionCode { get; }

        /// <summary>区域类型。</summary>
        public PhoenixArea Area { get; }

        /// <summary>原始偏移量。</summary>
        public int RawOffset { get; }

        private PhoenixAddress(ushort address, byte readFc, byte writeFc, PhoenixArea area, int rawOffset)
        {
            Address = address;
            ReadFunctionCode = readFc;
            WriteFunctionCode = writeFc;
            Area = area;
            RawOffset = rawOffset;
        }

        /// <summary>
        /// 解析 Phoenix Contact AXC IEC 地址字符串。
        /// </summary>
        /// <param name="address">IEC 地址，例如 "%MW100"、"%IW0"、"%IX0"、"%QX0"、"%M10"。</param>
        /// <example>
        /// <code>
        /// var a = PhoenixAddress.Parse("%MW100");   // → addr 100, FC03/FC16
        /// var b = PhoenixAddress.Parse("%IW0");     // → addr 0, FC04
        /// var c = PhoenixAddress.Parse("%M5");      // → addr 5, FC01/FC05
        /// </code>
        /// </example>
        public static PhoenixAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空", nameof(address));

            string s = address.Trim().ToUpperInvariant();
            if (s.StartsWith("%")) s = s.Substring(1);

            if (s.Length < 2)
                throw new ArgumentException($"地址格式无效: {address}", nameof(address));

            // %IB / %QB 字节寻址：Phoenix PLCnext 无固定 Modbus 映射，显式拒绝
            if (s.StartsWith("IB") || s.StartsWith("QB"))
                throw new ArgumentException(
                    "Phoenix %IB/%QB 字节寻址无固定映射，请在 PLCnext 程序侧配置（映射到 %MW/%QX 等寄存器/线圈）", nameof(address));

            string prefix;
            string numStr;
            if (s.StartsWith("MW")) { prefix = "MW"; numStr = s.Substring(2); }
            else if (s.StartsWith("IW")) { prefix = "IW"; numStr = s.Substring(2); }
            else if (s.StartsWith("QW")) { prefix = "QW"; numStr = s.Substring(2); }
            else if (s.StartsWith("IX")) { prefix = "IX"; numStr = s.Substring(2); }
            else if (s.StartsWith("QX")) { prefix = "QX"; numStr = s.Substring(2); }
            else if (s.StartsWith("M"))  { prefix = "M";  numStr = s.Substring(1); }
            else if (s.StartsWith("I"))  { prefix = "I";  numStr = s.Substring(1); }
            else if (s.StartsWith("Q"))  { prefix = "Q";  numStr = s.Substring(1); }
            else throw new ArgumentException($"无法识别的 Phoenix 地址前缀: {address}");

            int num = ParseInt(numStr);

            return prefix switch
            {
                "MW" => new PhoenixAddress((ushort)num, 0x03, 0x10, PhoenixArea.MemoryWord, num),
                "IW" => new PhoenixAddress((ushort)num, 0x04, 0x00, PhoenixArea.InputWord, num),
                "QW" => new PhoenixAddress((ushort)num, 0x03, 0x10, PhoenixArea.MemoryWord, num),
                "IX" => new PhoenixAddress((ushort)num, 0x02, 0x00, PhoenixArea.InputBit, num),
                "QX" => new PhoenixAddress((ushort)num, 0x01, 0x05, PhoenixArea.Coil, num),
                "M"  => new PhoenixAddress((ushort)num, 0x01, 0x05, PhoenixArea.Coil, num),
                "I"  => new PhoenixAddress((ushort)num, 0x02, 0x00, PhoenixArea.InputBit, num),
                "Q"  => new PhoenixAddress((ushort)num, 0x01, 0x05, PhoenixArea.Coil, num),
                _    => throw new ArgumentException($"无法识别的 Phoenix 地址前缀: {prefix}")
            };
        }

        /// <summary>尝试解析地址，失败返回 null。</summary>
        public static PhoenixAddress? TryParse(string address)
        {
            try { return Parse(address); }
            catch { return null; }
        }

        /// <summary>计算偏移后的地址。</summary>
        public PhoenixAddress WithOffset(int offset)
            => new PhoenixAddress((ushort)(Address + offset), ReadFunctionCode, WriteFunctionCode, Area, RawOffset + offset);

        /// <summary>是否为只读区域。</summary>
        public bool IsReadOnly => WriteFunctionCode == 0;

        /// <summary>是否为位区域。</summary>
        public bool IsBitArea => ReadFunctionCode == 0x01 || ReadFunctionCode == 0x02;

        /// <summary>是否为寄存器区域。</summary>
        public bool IsRegisterArea => ReadFunctionCode == 0x03 || ReadFunctionCode == 0x04;

        private static int ParseInt(string s)
        {
            s = s.TrimStart('0');
            if (s.Length == 0) return 0;
            if (int.TryParse(s, out int val)) return val;
            throw new ArgumentException($"数字部分无效: {s}");
        }

        public override string ToString()
            => $"{Area}{RawOffset} → Modbus 0x{Address:X4} FC{ReadFunctionCode}";
    }
}
