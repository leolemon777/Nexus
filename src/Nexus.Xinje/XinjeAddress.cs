using System;

namespace Nexus.Xinje
{
    /// <summary>
    /// 信捷 XC/XG/XL 系列 PLC 地址解析。
    /// <para>支持区域: D(数据寄存器)、HD(保持寄存器)、SD(特殊寄存器)、SM(特殊线圈)、
    /// M(内部继电器)、Y(输出)、X(输入)、C(计数器)、T(定时器)、S(状态)</para>
    /// <para>信捷 PLC 使用 Modbus TCP 兼容协议，地址映射为标准 Modbus 格式。</para>
    /// </summary>
    public sealed class XinjeAddress
    {
        /// <summary>Modbus 起始地址。</summary>
        public ushort Address { get; }

        /// <summary>读功能码。</summary>
        public byte ReadFunctionCode { get; }

        /// <summary>写功能码（0 = 只读区域）。</summary>
        public byte WriteFunctionCode { get; }

        /// <summary>区域类型。</summary>
        public XinjeArea Area { get; }

        /// <summary>原始区域偏移量。</summary>
        public int RawOffset { get; }

        private XinjeAddress(ushort address, byte readFc, byte writeFc, XinjeArea area, int rawOffset)
        {
            Address = address;
            ReadFunctionCode = readFc;
            WriteFunctionCode = writeFc;
            Area = area;
            RawOffset = rawOffset;
        }

        /// <summary>
        /// 解析信捷地址字符串。
        /// <para>示例: "D100", "HD100", "SD0", "SM100", "Y0", "X10", "M100", "C0", "T0", "S20"</para>
        /// <para>注意: HD 和 SD 是双字符前缀。</para>
        /// </summary>
        public static XinjeAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空", nameof(address));

            address = address.Trim().ToUpperInvariant();

            if (address.Length < 2)
                throw new ArgumentException($"地址格式无效: {address}", nameof(address));

            // 双字符前缀优先匹配
            if (address.Length >= 3 && address[0] == 'H' && address[1] == 'D')
                return ParseHd(address.Substring(2));

            if (address.Length >= 3 && address[0] == 'S' && address[1] == 'D')
                return ParseSd(address.Substring(2));

            if (address.Length >= 3 && address[0] == 'S' && address[1] == 'M')
                return ParseSm(address.Substring(2));

            // 单字符前缀
            char prefix = address[0];
            string numStr = address.Substring(1);

            if (!int.TryParse(numStr, out int num))
                throw new ArgumentException($"地址数字部分无效: {numStr}", nameof(address));

            return prefix switch
            {
                'Y' => new XinjeAddress((ushort)num, 0x01, 0x05, XinjeArea.OutputCoil, num),
                'X' => new XinjeAddress((ushort)num, 0x02, 0x00, XinjeArea.InputDiscrete, num),
                'M' => new XinjeAddress((ushort)(0x0800 + num), 0x01, 0x05, XinjeArea.InternalRelay, num),
                'C' => new XinjeAddress((ushort)(0x1000 + num), 0x03, 0x06, XinjeArea.Counter, num),
                'T' => new XinjeAddress((ushort)(0x0600 + num), 0x03, 0x06, XinjeArea.Timer, num),
                'S' => new XinjeAddress((ushort)(0x0000 + num), 0x01, 0x05, XinjeArea.StepRelay, num),
                'D' => new XinjeAddress((ushort)num, 0x03, 0x06, XinjeArea.DataRegister, num),
                _   => new XinjeAddress((ushort)num, 0x03, 0x06, XinjeArea.DataRegister, num)
            };
        }

        private static XinjeAddress ParseHd(string numStr)
        {
            int num = int.Parse(numStr);
            return new XinjeAddress((ushort)(0x8000 + num), 0x03, 0x06, XinjeArea.HoldingRegister, num);
        }

        private static XinjeAddress ParseSd(string numStr)
        {
            int num = int.Parse(numStr);
            return new XinjeAddress((ushort)(0xC000 + num), 0x03, 0x06, XinjeArea.SpecialRegister, num);
        }

        private static XinjeAddress ParseSm(string numStr)
        {
            int num = int.Parse(numStr);
            return new XinjeAddress((ushort)(0x0800 + 2048 + num), 0x01, 0x05, XinjeArea.SpecialCoil, num);
        }

        /// <summary>尝试解析地址，失败返回 null。</summary>
        public static XinjeAddress? TryParse(string address)
        {
            try { return Parse(address); }
            catch { return null; }
        }

        /// <summary>计算偏移后的地址。</summary>
        public XinjeAddress WithOffset(int offset)
        {
            return new XinjeAddress(
                (ushort)(Address + offset),
                ReadFunctionCode,
                WriteFunctionCode,
                Area,
                RawOffset + offset);
        }

        /// <summary>是否为只读区域。</summary>
        public bool IsReadOnly => WriteFunctionCode == 0;

        /// <summary>是否为位区域。</summary>
        public bool IsBitArea => ReadFunctionCode == 0x01 || ReadFunctionCode == 0x02;

        /// <summary>是否为寄存器区域。</summary>
        public bool IsRegisterArea => ReadFunctionCode == 0x03 || ReadFunctionCode == 0x04;

        public override string ToString() => $"{Area}{RawOffset} → Modbus 0x{Address:X4} FC{ReadFunctionCode}";
    }
}
