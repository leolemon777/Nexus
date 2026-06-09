using System;

namespace Nexus.Delta
{
    /// <summary>
    /// 台达 DVP/AS 系列 PLC 地址解析。
    /// <para>支持区域: D(数据寄存器)、Y(输出)、X(输入)、M(内部继电器)、T(定时器)、C(计数器)、S(状态)</para>
    /// <para>地址映射为标准 Modbus 地址格式。</para>
    /// </summary>
    public sealed class DeltaDvpAddress
    {
        /// <summary>Modbus 起始地址。</summary>
        public ushort Address { get; }

        /// <summary>读功能码。</summary>
        public byte ReadFunctionCode { get; }

        /// <summary>写功能码（0 = 只读区域）。</summary>
        public byte WriteFunctionCode { get; }

        /// <summary>区域类型。</summary>
        public DeltaArea Area { get; }

        /// <summary>原始区域偏移量（地址前缀后的数字）。</summary>
        public int RawOffset { get; }

        private DeltaDvpAddress(ushort address, byte readFc, byte writeFc, DeltaArea area, int rawOffset)
        {
            Address = address;
            ReadFunctionCode = readFc;
            WriteFunctionCode = writeFc;
            Area = area;
            RawOffset = rawOffset;
        }

        /// <summary>
        /// 解析台达地址字符串。
        /// <para>示例: "D100", "Y0", "X10", "T0", "C0", "M100", "S20"</para>
        /// <para>小写自动转换，前后空格自动去除。</para>
        /// </summary>
        /// <exception cref="ArgumentException">地址为空或格式错误。</exception>
        public static DeltaDvpAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空", nameof(address));

            address = address.Trim().ToUpperInvariant();

            if (address.Length < 2)
                throw new ArgumentException($"地址格式无效: {address}", nameof(address));

            char prefix = address[0];
            if (!char.IsLetter(prefix))
                throw new ArgumentException($"地址必须以字母开头: {address}", nameof(address));

            string numStr = address.Substring(1);
            if (!int.TryParse(numStr, out int num))
                throw new ArgumentException($"地址数字部分无效: {numStr}", nameof(address));

            return prefix switch
            {
                'Y' => new DeltaDvpAddress((ushort)(0x0000 + num), 0x01, 0x05, DeltaArea.OutputCoil, num),
                'X' => new DeltaDvpAddress((ushort)(0x0000 + num), 0x02, 0x00, DeltaArea.InputDiscrete, num),
                'M' => new DeltaDvpAddress((ushort)(0x0800 + num), 0x01, 0x05, DeltaArea.InternalRelay, num),
                'T' => new DeltaDvpAddress((ushort)(0x0C00 + num), 0x01, 0x05, DeltaArea.TimerCoil, num),
                'C' => new DeltaDvpAddress((ushort)(0x1000 + num), 0x01, 0x05, DeltaArea.CounterCoil, num),
                'S' => new DeltaDvpAddress((ushort)(0x0800 + 2048 + num), 0x01, 0x05, DeltaArea.StepRelay, num),
                'D' => new DeltaDvpAddress((ushort)(0x1000 + num), 0x03, 0x06, DeltaArea.DataRegister, num),
                _   => new DeltaDvpAddress((ushort)num, 0x03, 0x06, DeltaArea.DataRegister, num)
            };
        }

        /// <summary>尝试解析地址，失败返回 null。</summary>
        public static DeltaDvpAddress? TryParse(string address)
        {
            try { return Parse(address); }
            catch { return null; }
        }

        /// <summary>计算偏移后的地址（用于批量操作）。</summary>
        public DeltaDvpAddress WithOffset(int offset)
        {
            return new DeltaDvpAddress(
                (ushort)(Address + offset),
                ReadFunctionCode,
                WriteFunctionCode,
                Area,
                RawOffset + offset);
        }

        /// <summary>是否为只读区域（如输入 X）。</summary>
        public bool IsReadOnly => WriteFunctionCode == 0;

        /// <summary>是否为位区域（Y/X/M/T/C/S）。</summary>
        public bool IsBitArea => ReadFunctionCode == 0x01 || ReadFunctionCode == 0x02;

        /// <summary>是否为寄存器区域（D）。</summary>
        public bool IsRegisterArea => ReadFunctionCode == 0x03 || ReadFunctionCode == 0x04;

        public override string ToString() => $"{Area}{RawOffset} → Modbus 0x{Address:X4} FC{ReadFunctionCode}";
    }
}
