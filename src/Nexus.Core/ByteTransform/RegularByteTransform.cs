// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

using System;

namespace Nexus
{
    /// <summary>
    /// 大端序 (ABCD) 字节变换器 — Nexus / Modbus / 大多数工业协议默认。
    /// CPU 是小端序,所以读字节时 reverse,写字节时 reverse。
    /// </summary>
    public sealed class RegularByteTransform : ByteTransformBase
    {
        /// <inheritdoc />
        public override Endianness ByteOrder => Endianness.BigEndian;

        /// <inheritdoc />
        protected override void RearrangeRead(byte[] buffer, int offset, int length)
            => Array.Reverse(buffer, offset, length);

        /// <inheritdoc />
        protected override void RearrangeWrite(byte[] buffer, int offset, int length)
            => Array.Reverse(buffer, offset, length);

        /// <summary>单例实例(无状态,可复用)。</summary>
        public static RegularByteTransform Instance { get; } = new RegularByteTransform();
    }
}
