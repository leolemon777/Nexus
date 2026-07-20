// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

using System;

namespace Nexus
{
    /// <summary>
    /// 按 <see cref="Endianness"/> 获取 <see cref="IByteTransform"/> 实例的工厂。
    /// 协议客户端通常只需在构造时调用 <see cref="ForEndianness"/> 拿到一个实例并保存。
    /// </summary>
    public static class ByteTransformFactory
    {
        /// <summary>返回指定字节序对应的 IByteTransform 单例。</summary>
        /// <exception cref="ArgumentOutOfRangeException">byteOrder 不是已知的 Endianness 值。</exception>
        public static IByteTransform ForEndianness(Endianness byteOrder)
        {
            switch (byteOrder)
            {
                case Endianness.BigEndian:
                    return RegularByteTransform.Instance;
                case Endianness.LittleEndian:
                    return ReverseBytesTransform.Instance;
                case Endianness.MidBigEndian:
                    return ReverseWordTransform.MidBigEndianInstance;
                case Endianness.MidLittleEndian:
                    return ReverseWordTransform.MidLittleEndianInstance;
                default:
                    throw new ArgumentOutOfRangeException(nameof(byteOrder), byteOrder,
                        $"未知的字节序: {byteOrder}");
            }
        }
    }
}
