// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

namespace Nexus
{
    /// <summary>
    /// 小端序 (DCBA) 字节变换器 — 一些 ARM/嵌入式 PLC 的默认字节序。
    /// CPU 也是小端,所以无需重排 — 直接用 BitConverter 自然顺序。
    /// </summary>
    public sealed class ReverseBytesTransform : ByteTransformBase
    {
        /// <inheritdoc />
        public override Endianness ByteOrder => Endianness.LittleEndian;

        /// <inheritdoc />
        protected override void RearrangeRead(byte[] buffer, int offset, int length)
        {
            // 小端 = CPU 自然序,无需重排。
        }

        /// <inheritdoc />
        protected override void RearrangeWrite(byte[] buffer, int offset, int length)
        {
            // 小端 = CPU 自然序,无需重排。
        }

        /// <summary>单例实例(无状态,可复用)。</summary>
        public static ReverseBytesTransform Instance { get; } = new ReverseBytesTransform();
    }
}
