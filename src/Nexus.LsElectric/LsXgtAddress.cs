using System;

namespace Nexus.LsElectric
{
    /// <summary>
    /// LS 产电 XGT 协议地址解析。
    /// <para>支持区域: P(I/O)、M(内部继电器)、L(链接继电器)、K(保持继电器)、
    /// F(特殊继电器)、T(定时器)、C(计数器)、D(数据寄存器)、N(文件寄存器)</para>
    /// </summary>
    public sealed class LsXgtAddress
    {
        /// <summary>XGT 区域代码。</summary>
        public byte AreaCode { get; }

        /// <summary>区域偏移地址。</summary>
        public int Offset { get; }

        /// <summary>区域类型。</summary>
        public LsXgtArea Area { get; }

        /// <summary>是否为位区域。</summary>
        public bool IsBitArea { get; }

        private LsXgtAddress(byte areaCode, int offset, LsXgtArea area, bool isBitArea)
        {
            AreaCode = areaCode;
            Offset = offset;
            Area = area;
            IsBitArea = isBitArea;
        }

        /// <summary>
        /// 解析 XGT 地址字符串。
        /// <para>示例: "D100", "M100", "P0", "L10", "K50", "F3", "T0", "C0", "N100"</para>
        /// </summary>
        public static LsXgtAddress Parse(string address)
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
                'P' => new LsXgtAddress(0x00, num, LsXgtArea.IO, true),
                'M' => new LsXgtAddress(0x01, num, LsXgtArea.InternalRelay, true),
                'L' => new LsXgtAddress(0x02, num, LsXgtArea.LinkRelay, true),
                'K' => new LsXgtAddress(0x03, num, LsXgtArea.KeepRelay, true),
                'F' => new LsXgtAddress(0x04, num, LsXgtArea.SpecialRelay, true),
                'T' => new LsXgtAddress(0x05, num, LsXgtArea.Timer, false),
                'C' => new LsXgtAddress(0x06, num, LsXgtArea.Counter, false),
                'D' => new LsXgtAddress(0x07, num, LsXgtArea.DataRegister, false),
                'N' => new LsXgtAddress(0x08, num, LsXgtArea.FileRegister, false),
                _   => new LsXgtAddress(0x07, num, LsXgtArea.DataRegister, false)
            };
        }

        /// <summary>尝试解析地址，失败返回 null。</summary>
        public static LsXgtAddress? TryParse(string address)
        {
            try { return Parse(address); }
            catch { return null; }
        }

        /// <summary>计算偏移后的地址。</summary>
        public LsXgtAddress WithOffset(int offset)
        {
            return new LsXgtAddress(AreaCode, Offset + offset, Area, IsBitArea);
        }

        public override string ToString() => $"{Area}{Offset} → AreaCode=0x{AreaCode:X2}";
    }
}
