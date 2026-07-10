using System;

namespace Nexus.Wago
{
    /// <summary>
    /// WAGO 750/PFC PLC 地址解析（IEC 61131-3 → Modbus）。
    /// </summary>
    /// <remarks>
    /// 支持的 IEC 地址语法：
    /// <list type="bullet">
    /// <item><description><c>%MWn</c>  保持寄存器 → Holding Register 0x3000+n（FC03 / FC16）</description></item>
    /// <item><description><c>%IWn</c>  输入寄存器 → Input Register 0x3000+n（FC04，只读）</description></item>
    /// <item><description><c>%IXn</c> 或 <c>%In</c>  离散输入 → Discrete Input 0x3000+n（FC02，只读）</description></item>
    /// <item><description><c>%QXn</c> 或 <c>%Qn</c> 或 <c>%Mn</c>  线圈 → Coil 0x3000+n（FC01 / FC05）</description></item>
    /// </list>
    /// <para>地址映射来源：WAGO 750 Ethernet Coupler Manual §4.5.6 Modbus/TCP。</para>
    /// <para>所有区域统一从 0x3000 (12288) 起始。两种偏移约定见 <see cref="WagoOffsetMode"/>。</para>
    /// </remarks>
    public sealed class WagoAddress
    {
        private const int BaseOffset = 0x3000; // 12288

        /// <summary>Modbus 起始地址（线圈/寄存器编号）。</summary>
        public ushort Address { get; }

        /// <summary>读功能码。</summary>
        public byte ReadFunctionCode { get; }

        /// <summary>写功能码（0 = 只读区域）。</summary>
        public byte WriteFunctionCode { get; }

        /// <summary>区域类型。</summary>
        public WagoArea Area { get; }

        /// <summary>原始 IEC 地址偏移量。</summary>
        public int RawOffset { get; }

        private WagoAddress(ushort address, byte readFc, byte writeFc, WagoArea area, int rawOffset)
        {
            Address = address;
            ReadFunctionCode = readFc;
            WriteFunctionCode = writeFc;
            Area = area;
            RawOffset = rawOffset;
        }

        /// <summary>
        /// 解析 WAGO IEC 地址字符串。
        /// </summary>
        /// <param name="address">IEC 地址，例如 "%MW100"、"%IW0"、"%IX0"、"%QX0"、"%M10"。</param>
        /// <param name="mode">偏移约定（默认手册的 ZeroBased）。</param>
        /// <example>
        /// <code>
        /// var a = WagoAddress.Parse("%MW100");              // → 0x3000+100, FC03/FC16
        /// var b = WagoAddress.Parse("%IW0");                // → 0x3000, FC04
        /// var c = WagoAddress.Parse("%IX5");                // → 0x3000+5, FC02
        /// var d = WagoAddress.Parse("%QX7");                // → 0x3000+7, FC01/FC05
        /// </code>
        /// </example>
        public static WagoAddress Parse(string address, WagoOffsetMode mode = WagoOffsetMode.ZeroBased)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空", nameof(address));

            string s = address.Trim().ToUpperInvariant();
            int baseOff = mode == WagoOffsetMode.OneBased ? BaseOffset + 1 : BaseOffset;

            // 去掉可选的 % 前缀
            if (s.StartsWith("%")) s = s.Substring(1);

            if (s.Length < 2)
                throw new ArgumentException($"地址格式无效: {address}", nameof(address));

            // 识别区域前缀（双字符优先）
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
            else throw new ArgumentException($"无法识别的 WAGO 地址前缀: {address}");

            int num = ParseInt(numStr);

            return prefix switch
            {
                "MW" => new WagoAddress((ushort)(baseOff + num), 0x03, 0x10, WagoArea.MemoryWord, num),
                "IW" => new WagoAddress((ushort)(baseOff + num), 0x04, 0x00, WagoArea.InputWord, num),
                "QW" => new WagoAddress((ushort)(baseOff + num), 0x03, 0x10, WagoArea.MemoryWord, num),
                "IX" => new WagoAddress((ushort)(baseOff + num), 0x02, 0x00, WagoArea.InputBit, num),
                "IB" => new WagoAddress((ushort)(baseOff + num), 0x02, 0x00, WagoArea.InputBit, num),
                "QX" => new WagoAddress((ushort)(baseOff + num), 0x01, 0x05, WagoArea.Coil, num),
                "QB" => new WagoAddress((ushort)(baseOff + num), 0x01, 0x05, WagoArea.Coil, num),
                "M"  => new WagoAddress((ushort)(baseOff + num), 0x01, 0x05, WagoArea.Coil, num),
                "I"  => new WagoAddress((ushort)(baseOff + num), 0x02, 0x00, WagoArea.InputBit, num),
                "Q"  => new WagoAddress((ushort)(baseOff + num), 0x01, 0x05, WagoArea.Coil, num),
                _    => throw new ArgumentException($"无法识别的 WAGO 地址前缀: {prefix}")
            };
        }

        /// <summary>尝试解析地址，失败返回 null。</summary>
        public static WagoAddress? TryParse(string address, WagoOffsetMode mode = WagoOffsetMode.ZeroBased)
        {
            try { return Parse(address, mode); }
            catch { return null; }
        }

        /// <summary>计算偏移后的地址。</summary>
        public WagoAddress WithOffset(int offset)
            => new WagoAddress((ushort)(Address + offset), ReadFunctionCode, WriteFunctionCode, Area, RawOffset + offset);

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
