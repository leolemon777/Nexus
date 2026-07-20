// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.
// Rewritten for Nexus: template-method base, lean surface, netstandard2.0-safe.

using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace Nexus
{
    /// <summary>
    /// <see cref="IByteTransform"/> 抽象基类 — 模板方法模式。
    /// 子类只需重写 <see cref="RearrangeRead(byte[], int, int)"/> 和
    /// <see cref="RearrangeWrite(byte[], int, int)"/> 决定字节重排策略,
    /// 所有值类型互转方法自动复用。
    /// </summary>
    /// <remarks>
    /// <b>实现策略</b>:从 buffer 取出的原始字节先复制到临时小端序缓冲(因为
    /// BitConverter 在所有现代 .NET 平台都是小端),然后调用子类的
    /// <see cref="RearrangeRead"/> 进行字节序重排,最后用 BitConverter 转为目标类型。
    /// 这样避免了 netstandard2.0 下 unsafe 指针转换在 WebAssembly/AOT 场景的限制。
    /// </remarks>
    public abstract class ByteTransformBase : IByteTransform
    {
        /// <inheritdoc />
        public abstract Endianness ByteOrder { get; }

        // ── 子类实现的字节重排钩子 ─────────────────

        /// <summary>
        /// 从设备读到 length 字节(原始顺序)后,重排为 CPU 自然顺序(小端序)。
        /// 例如大端 ABCD → 小端 DCBA 需要 reverse。
        /// </summary>
        /// <param name="buffer">原始字节缓冲(就地修改)</param>
        /// <param name="offset">起始偏移</param>
        /// <param name="length">字节数(2/4/8)</param>
        protected abstract void RearrangeRead(byte[] buffer, int offset, int length);

        /// <summary>
        /// 写入设备前,把 CPU 小端序字节重排为目标字节序。与 RearrangeRead 互逆。
        /// </summary>
        protected abstract void RearrangeWrite(byte[] buffer, int offset, int length);

        // ── IByteTransform: byte[] → 值 ────────────────

        /// <inheritdoc />
        public virtual bool ToBool(byte[] buffer, int offset)
            => buffer[offset] != 0;

        /// <inheritdoc />
        public virtual byte ToByte(byte[] buffer, int offset)
            => buffer[offset];

        /// <inheritdoc />
        public virtual short ToInt16(byte[] buffer, int offset)
        {
            // 复制到临时小端缓冲,再重排(就地),最后 BitConverter。
            byte[] tmp = { buffer[offset], buffer[offset + 1] };
            RearrangeRead(tmp, 0, 2);
            return BitConverter.ToInt16(tmp, 0);
        }

        /// <inheritdoc />
        public virtual ushort ToUInt16(byte[] buffer, int offset)
            => (ushort)ToInt16(buffer, offset);

        /// <inheritdoc />
        public virtual int ToInt32(byte[] buffer, int offset)
        {
            byte[] tmp = {
                buffer[offset], buffer[offset + 1],
                buffer[offset + 2], buffer[offset + 3]
            };
            RearrangeRead(tmp, 0, 4);
            return BitConverter.ToInt32(tmp, 0);
        }

        /// <inheritdoc />
        public virtual uint ToUInt32(byte[] buffer, int offset)
            => (uint)ToInt32(buffer, offset);

        /// <inheritdoc />
        public virtual long ToInt64(byte[] buffer, int offset)
        {
            byte[] tmp = new byte[8];
            Array.Copy(buffer, offset, tmp, 0, 8);
            RearrangeRead(tmp, 0, 8);
            return BitConverter.ToInt64(tmp, 0);
        }

        /// <inheritdoc />
        public virtual ulong ToUInt64(byte[] buffer, int offset)
            => (ulong)ToInt64(buffer, offset);

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual unsafe float ToSingle(byte[] buffer, int offset)
        {
            int v = ToInt32(buffer, offset);
            return *(float*)&v;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual unsafe double ToDouble(byte[] buffer, int offset)
        {
            long v = ToInt64(buffer, offset);
            return *(double*)&v;
        }

        // ── IByteTransform: 值 → byte[] ────────────────

        /// <inheritdoc />
        public virtual byte[] GetBytes(bool value)
            => new byte[] { value ? (byte)1 : (byte)0 };

        /// <inheritdoc />
        public virtual byte[] GetBytes(byte value)
            => new byte[] { value };

        /// <inheritdoc />
        public virtual byte[] GetBytes(short value)
        {
            byte[] tmp = BitConverter.GetBytes(value);
            RearrangeWrite(tmp, 0, 2);
            return tmp;
        }

        /// <inheritdoc />
        public virtual byte[] GetBytes(ushort value)
            => GetBytes((short)value);

        /// <inheritdoc />
        public virtual byte[] GetBytes(int value)
        {
            byte[] tmp = BitConverter.GetBytes(value);
            RearrangeWrite(tmp, 0, 4);
            return tmp;
        }

        /// <inheritdoc />
        public virtual byte[] GetBytes(uint value)
            => GetBytes((int)value);

        /// <inheritdoc />
        public virtual byte[] GetBytes(long value)
        {
            byte[] tmp = BitConverter.GetBytes(value);
            RearrangeWrite(tmp, 0, 8);
            return tmp;
        }

        /// <inheritdoc />
        public virtual byte[] GetBytes(ulong value)
            => GetBytes((long)value);

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual unsafe byte[] GetBytes(float value)
            => GetBytes(*(int*)&value);

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual unsafe byte[] GetBytes(double value)
            => GetBytes(*(long*)&value);

        // ── IByteTransform: 字符串 ─────────────────────

        /// <inheritdoc />
        public virtual string GetString(byte[] buffer, int offset, int length, Encoding? encoding = null)
        {
            encoding = encoding ?? Encoding.ASCII;
            string s = encoding.GetString(buffer, offset, length);
            return s.TrimEnd('\0', ' ');
        }

        /// <inheritdoc />
        public virtual byte[] GetBytes(string value, Encoding? encoding = null)
            => (encoding ?? Encoding.ASCII).GetBytes(value);
    }
}
