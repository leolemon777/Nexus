using System;

namespace Nexus.Idec
{
    /// <summary>
    /// IDEC MicroSmart 设备区域（数据类型）枚举。
    /// <para>对应 Computer Link 协议的数据类型码（1 char）。</para>
    /// </summary>
    public enum IdecArea
    {
        /// <summary>数据寄存器 Data Register（字设备，十进制地址）。</summary>
        DataRegister,

        /// <summary>输入 Input（位设备，八进制地址 X）。</summary>
        InputBit,

        /// <summary>输出 Output（位设备，八进制地址 Y）。</summary>
        OutputBit,

        /// <summary>内部继电器 Internal Relay（位设备，十进制地址 M）。</summary>
        InternalRelay,

        /// <summary>定时器 Timer（字设备，当前值 / 触点，十进制地址 T）。</summary>
        Timer,

        /// <summary>计数器 Counter（字设备，当前值 / 触点，十进制地址 C）。</summary>
        Counter
    }

    /// <summary>
    /// IDEC Computer Link 命令族（2 chars ASCII）。
    /// <para>R = Read（读），W = Write（写），后缀 1/2/3 表示单点/连续/扩展。</para>
    /// </summary>
    public static class IdecCommandType
    {
        /// <summary>读单点。</summary>
        public const string ReadSingle = "R1";

        /// <summary>连续读（本库主推，覆盖最常用读场景）。</summary>
        public const string ReadContinuous = "R2";

        /// <summary>扩展读。</summary>
        public const string ReadExtended = "R3";

        /// <summary>写单点。</summary>
        public const string WriteSingle = "W1";

        /// <summary>连续写（本库主推，覆盖最常用写场景）。</summary>
        public const string WriteContinuous = "W2";

        /// <summary>扩展写。</summary>
        public const string WriteExtended = "W3";

        /// <summary>清除命令族前缀。</summary>
        public const string ClearSingle = "C1";
    }

    /// <summary>
    /// IDEC Computer Link 帧控制字符（ASCII）。
    /// <para>来源: fc4a_protocol_im.pdf（公开手册）。</para>
    /// </summary>
    public static class IdecFrameControl
    {
        /// <summary>ENQ (0x05) — 主站请求帧起始。</summary>
        public const byte ENQ = 0x05;

        /// <summary>STX (0x02) — 从站成功响应数据起始。</summary>
        public const byte STX = 0x02;

        /// <summary>ETX (0x03) — 成功响应数据结束（在 BCC 前）。</summary>
        public const byte ETX = 0x03;

        /// <summary>ACK (0x06) — 确认。</summary>
        public const byte ACK = 0x06;

        /// <summary>NAK (0x15) — 从站失败响应起始（后跟错误码）。</summary>
        public const byte NAK = 0x15;

        /// <summary>CR (0x0D) — 帧结尾。</summary>
        public const byte CR = 0x0D;
    }

    /// <summary>
    /// IDEC Computer Link 数据类型码（1 char ASCII），用于选择 operand 设备类型。
    /// <para>静态方法 <see cref="For(IdecArea)"/> 把 <see cref="IdecArea"/> 映射为协议的 1 char 类型码。</para>
    /// </summary>
    public static class IdecDataTypeCode
    {
        /// <summary>映射 <see cref="IdecArea"/> 到协议 1 char 数据类型码。</summary>
        /// <param name="area">设备区域。</param>
        /// <returns>ASCII 类型码字符。</returns>
        /// <exception cref="ArgumentException">未知的区域。</exception>
        public static char For(IdecArea area)
        {
            switch (area)
            {
                case IdecArea.DataRegister: return 'D';
                case IdecArea.InputBit: return 'X';
                case IdecArea.OutputBit: return 'Y';
                case IdecArea.InternalRelay: return 'M';
                case IdecArea.Timer: return 'T';
                case IdecArea.Counter: return 'C';
                default:
                    throw new ArgumentException($"未知的 IDEC 区域: {area}", nameof(area));
            }
        }

        /// <summary>反向映射: 1 char 类型码 → <see cref="IdecArea"/>。</summary>
        /// <param name="code">ASCII 类型码字符（大小写不敏感）。</param>
        /// <returns>对应区域。</returns>
        /// <exception cref="ArgumentException">未知的类型码。</exception>
        public static IdecArea From(char code)
        {
            switch (char.ToUpperInvariant(code))
            {
                case 'D': return IdecArea.DataRegister;
                case 'X': return IdecArea.InputBit;
                case 'Y': return IdecArea.OutputBit;
                case 'M': return IdecArea.InternalRelay;
                case 'T': return IdecArea.Timer;
                case 'C': return IdecArea.Counter;
                default:
                    throw new ArgumentException($"未知的 IDEC 数据类型码: {code}", nameof(code));
            }
        }
    }
}
