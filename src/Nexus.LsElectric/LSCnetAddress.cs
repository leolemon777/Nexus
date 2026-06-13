using System;

namespace Nexus.LsElectric
{
    /// <summary>
    /// LS Electric Cnet 协议地址解析。
    /// <para>支持区域: P(程序存储器)、M(内部继电器)、K(保持继电器)、
    /// T(定时器)、C(计数器)、D(数据寄存器)、L(链接寄存器)、N(文件寄存器)</para>
    /// </summary>
    public sealed class LSCnetAddress
    {
        /// <summary>Cnet 区域代码（ASCII 码）。</summary>
        public byte AreaCode { get; }

        /// <summary>区域偏移地址。</summary>
        public int Offset { get; }

        /// <summary>区域类型。</summary>
        LSCnetArea Area { get; }

        /// <summary>是否为位区域。</summary>
        public bool IsBitArea { get; }

        internal LSCnetAddress(byte areaCode, int offset, LSCnetArea area, bool isBitArea)
        {
            AreaCode = areaCode;
            Offset = offset;
            Area = area;
            IsBitArea = isBitArea;
        }

        /// <summary>
        /// 解析 Cnet 地址字符串。
        /// <para>示例: "D100", "M100", "P0", "K50", "T0", "C0", "N100", "L10"</para>
        /// </summary>
        public static LSCnetAddress Parse(string address)
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
                'P' => new LSCnetAddress(0x50, num, LSCnetArea.Program, true),
                'M' => new LSCnetAddress(0x4D, num, LSCnetArea.InternalRelay, true),
                'K' => new LSCnetAddress(0x4B, num, LSCnetArea.KeepRelay, true),
                'T' => new LSCnetAddress(0x54, num, LSCnetArea.Timer, false),
                'C' => new LSCnetAddress(0x43, num, LSCnetArea.Counter, false),
                'D' => new LSCnetAddress(0x44, num, LSCnetArea.DataRegister, false),
                'L' => new LSCnetAddress(0x4C, num, LSCnetArea.LinkRegister, false),
                'N' => new LSCnetAddress(0x4E, num, LSCnetArea.FileRegister, false),
                _ => new LSCnetAddress(0x44, num, LSCnetArea.DataRegister, false)
            };
        }

        /// <summary>尝试解析地址，失败返回 null。</summary>
        public static LSCnetAddress? TryParse(string address)
        {
            try { return Parse(address); }
            catch { return null; }
        }

        /// <summary>计算偏移后的地址。</summary>
        public LSCnetAddress WithOffset(int offset)
        {
            return new LSCnetAddress(AreaCode, Offset + offset, Area, IsBitArea);
        }

        public override string ToString() => $"{(char)AreaCode}{Offset} → AreaCode=0x{AreaCode:X2}";
    }
}
