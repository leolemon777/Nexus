using System;

namespace Nexus.AbbPlc
{
    /// <summary>
    /// ABB AC500 PLC 地址解析（IEC 61131-3 → Modbus）。
    /// </summary>
    /// <remarks>
    /// 支持的 IEC 地址语法（标准 IEC 编号，无统一偏移）：
    /// <list type="bullet">
    /// <item><description><c>%MWn</c>  保持寄存器 → Holding Register n（FC03 / FC16）</description></item>
    /// <item><description><c>%IWn</c>  输入寄存器 → Input Register n（FC04，只读）</description></item>
    /// <item><description><c>%IXn</c> 或 <c>%In</c>  离散输入 → Discrete Input n（FC02，只读）</description></item>
    /// <item><description><c>%Mn</c> 或 <c>%QXn</c>  线圈 → Coil n（FC01 / FC05）</description></item>
    /// </list>
    /// <para>地址映射来源：ABB AC500 V3 Modbus TCP 手册 3ADR010810。</para>
    /// </remarks>
    public sealed class AbbPlcAddress
    {
        /// <summary>Modbus 起始地址。</summary>
        public ushort Address { get; }

        /// <summary>读功能码。</summary>
        public byte ReadFunctionCode { get; }

        /// <summary>写功能码（0 = 只读区域）。</summary>
        public byte WriteFunctionCode { get; }

        /// <summary>区域类型。</summary>
        public AbbArea Area { get; }

        /// <summary>原始偏移量。</summary>
        public int RawOffset { get; }

        private AbbPlcAddress(ushort address, byte readFc, byte writeFc, AbbArea area, int rawOffset)
        {
            Address = address;
            ReadFunctionCode = readFc;
            WriteFunctionCode = writeFc;
            Area = area;
            RawOffset = rawOffset;
        }

        /// <summary>
        /// 解析 ABB AC500 IEC 地址字符串。
        /// </summary>
        /// <param name="address">IEC 地址，例如 "%MW100"、"%IW0"、"%IX0"、"%M10"。</param>
        /// <example>
        /// <code>
        /// var a = AbbPlcAddress.Parse("%MW100");   // → addr 100, FC03/FC16
        /// var b = AbbPlcAddress.Parse("%IW0");     // → addr 0, FC04
        /// var c = AbbPlcAddress.Parse("%M5");      // → addr 5, FC01/FC05
        /// </code>
        /// </example>
        public static AbbPlcAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空", nameof(address));

            string s = address.Trim().ToUpperInvariant();
            if (s.StartsWith("%")) s = s.Substring(1);

            if (s.Length < 2)
                throw new ArgumentException($"地址格式无效: {address}", nameof(address));

            string prefix;
            string numStr;
            if (s.StartsWith("MW")) { prefix = "MW"; numStr = s.Substring(2); }
            else if (s.StartsWith("IW")) { prefix = "IW"; numStr = s.Substring(2); }
            else if (s.StartsWith("QW")) { prefix = "QW"; numStr = s.Substring(2); }
            else if (s.StartsWith("IX")) { prefix = "IX"; numStr = s.Substring(2); }
            else if (s.StartsWith("QX")) { prefix = "QX"; numStr = s.Substring(2); }
            else if (s.StartsWith("IB")) { prefix = "IB"; numStr = s.Substring(2); }
            else if (s.StartsWith("QB")) { prefix = "QB"; numStr = s.Substring(2); }
            else if (s.StartsWith("M"))  { prefix = "M";  numStr = s.Substring(1); }
            else if (s.StartsWith("I"))  { prefix = "I";  numStr = s.Substring(1); }
            else if (s.StartsWith("Q"))  { prefix = "Q";  numStr = s.Substring(1); }
            else throw new ArgumentException($"无法识别的 ABB 地址前缀: {address}");

            int num = ParseInt(numStr);

            return prefix switch
            {
                "MW" => new AbbPlcAddress((ushort)num, 0x03, 0x10, AbbArea.MemoryWord, num),
                "IW" => new AbbPlcAddress((ushort)num, 0x04, 0x00, AbbArea.InputWord, num),
                "QW" => new AbbPlcAddress((ushort)num, 0x03, 0x10, AbbArea.MemoryWord, num),
                "IX" => new AbbPlcAddress((ushort)num, 0x02, 0x00, AbbArea.InputBit, num),
                "IB" => new AbbPlcAddress((ushort)num, 0x02, 0x00, AbbArea.InputBit, num),
                "QX" => new AbbPlcAddress((ushort)num, 0x01, 0x05, AbbArea.Coil, num),
                "QB" => new AbbPlcAddress((ushort)num, 0x01, 0x05, AbbArea.Coil, num),
                "M"  => new AbbPlcAddress((ushort)num, 0x01, 0x05, AbbArea.Coil, num),
                "I"  => new AbbPlcAddress((ushort)num, 0x02, 0x00, AbbArea.InputBit, num),
                "Q"  => new AbbPlcAddress((ushort)num, 0x01, 0x05, AbbArea.Coil, num),
                _    => throw new ArgumentException($"无法识别的 ABB 地址前缀: {prefix}")
            };
        }

        /// <summary>尝试解析地址，失败返回 null。</summary>
        public static AbbPlcAddress? TryParse(string address)
        {
            try { return Parse(address); }
            catch { return null; }
        }

        /// <summary>计算偏移后的地址。</summary>
        public AbbPlcAddress WithOffset(int offset)
            => new AbbPlcAddress((ushort)(Address + offset), ReadFunctionCode, WriteFunctionCode, Area, RawOffset + offset);

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
