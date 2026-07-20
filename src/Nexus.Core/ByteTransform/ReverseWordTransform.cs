// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.
// Rewritten for Nexus: byte-rearrangement matches DataConverter for 16/32-bit values.

using System;

namespace Nexus
{
    /// <summary>
    /// 字反转字节变换器 — 支持 <see cref="Endianness.MidBigEndian"/> (BADC) 和
    /// <see cref="Endianness.MidLittleEndian"/> (CDAB)。
    /// </summary>
    /// <remarks>
    /// <b>字节序语义(与 <see cref="DataConverter"/> 对 16/32 位值的实现完全一致)</b>:
    /// <para>
    /// 对 4 字节序列 [a,b,c,d]:
    /// <list type="bullet">
    ///   <item><b>BADC (MidBigEndian)</b>: 值 = (b&lt;&lt;24)|(a&lt;&lt;16)|(d&lt;&lt;8)|c — 字内字节序反转</item>
    ///   <item><b>CDAB (MidLittleEndian)</b>: 值 = (c&lt;&lt;24)|(d&lt;&lt;16)|(a&lt;&lt;8)|b — 字对(4 字节粒度)反转</item>
    /// </list>
    /// 对 16 位值,BADC 退化为 LittleEndian,CDAB 退化为 BigEndian(与 DataConverter 一致)。
    /// 对 64 位值,ByteTransform 走自洽语义(BADC = 全程字内交换,CDAB = 全程字对交换),
    /// 与 DataConverter 64 位的边界行为可能不同 — 协议子类若需要严格匹配 DataConverter 64 位,
    /// 应继续使用 DataConverter。
    /// </para>
    /// </remarks>
    public sealed class ReverseWordTransform : ByteTransformBase
    {
        /// <inheritdoc />
        public override Endianness ByteOrder { get; }

        /// <summary>用指定字节序构造。必须是 MidBigEndian 或 MidLittleEndian。</summary>
        public ReverseWordTransform(Endianness byteOrder)
        {
            if (byteOrder != Endianness.MidBigEndian && byteOrder != Endianness.MidLittleEndian)
                throw new ArgumentException(
                    $"ReverseWordTransform 仅支持 {nameof(Endianness.MidBigEndian)}(BADC)或 {nameof(Endianness.MidLittleEndian)}(CDAB),实际传入: {byteOrder}",
                    nameof(byteOrder));
            ByteOrder = byteOrder;
        }

        /// <inheritdoc />
        protected override void RearrangeRead(byte[] buffer, int offset, int length)
        {
            // 目标:把 DataConverter 风格的字节流重排为 CPU 小端序(BitConverter LE 直接读)。
            //
            // BADC 32 位 [a,b,c,d] → 解读 (b<<24)|(a<<16)|(d<<8)|c
            //   = CPU LE 读 [c,d,a,b](因为 LE 读 = c + d*256 + a*65536 + b*16M)
            //   所以重排:[a,b,c,d] → [c,d,a,b] = 字对交换(4 字节粒度)
            //   但 16 位 [a,b] → 解读 (b<<8)|a = LE 直接读,no-op
            //   即 BADC 对任意长度:每 4 字节字对交换(对 16 位值无效,因为没有 4 字节)。
            //
            // CDAB 32 位 [a,b,c,d] → 解读 (c<<24)|(d<<16)|(a<<8)|b
            //   = CPU LE 读 [b,a,d,c]
            //   所以重排:[a,b,c,d] → [b,a,d,c] = 字内字节交换(2 字节粒度)
            //   16 位 [a,b] → 解读 (a<<8)|b = BE,需反转成 LE:[b,a]。也是字内交换。
            //   即 CDAB 对任意长度:字内字节交换(2 字节粒度)。

            if (ByteOrder == Endianness.MidBigEndian)
            {
                // BADC: 4 字节粒度字对交换。对 16 位 length<4 时 no-op。
                for (int i = offset; i + 3 < offset + length; i += 4)
                {
                    byte b0 = buffer[i], b1 = buffer[i + 1];
                    buffer[i] = buffer[i + 2];
                    buffer[i + 1] = buffer[i + 3];
                    buffer[i + 2] = b0;
                    buffer[i + 3] = b1;
                }
                // 16 位 BADC = LE,no-op。
            }
            else // MidLittleEndian (CDAB)
            {
                // CDAB: 2 字节粒度字内交换。对任意长度都生效。
                int end = offset + length;
                for (int i = offset; i + 1 < end; i += 2)
                {
                    byte tmp = buffer[i];
                    buffer[i] = buffer[i + 1];
                    buffer[i + 1] = tmp;
                }
            }
        }

        /// <inheritdoc />
        protected override void RearrangeWrite(byte[] buffer, int offset, int length)
        {
            // 字对交换与字内交换都是自逆操作(对称变换),Write = Read。
            RearrangeRead(buffer, offset, length);
        }

        // ── 单例便利 ─────────────────────────────

        /// <summary>BADC (MidBigEndian) 单例。</summary>
        public static ReverseWordTransform MidBigEndianInstance { get; } =
            new ReverseWordTransform(Endianness.MidBigEndian);

        /// <summary>CDAB (MidLittleEndian) 单例。</summary>
        public static ReverseWordTransform MidLittleEndianInstance { get; } =
            new ReverseWordTransform(Endianness.MidLittleEndian);
    }
}
