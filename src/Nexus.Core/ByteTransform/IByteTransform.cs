// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.
// Rewritten for Nexus: netstandard2.0-friendly, no 2D arrays, leaner surface.

using System.Text;

namespace Nexus
{
    /// <summary>
    /// 字节序感知的值类型 ↔ byte[] 互转接口。每个实现代表一种字节序策略
    /// (大端 ABCD、小端 DCBA、字反转 BADC/CDAB),协议客户端持有其中一个实例即可,
    /// 无需在每个 Read/Write 方法里传 <see cref="Endianness"/> 参数。
    /// </summary>
    /// <remarks>
    /// <b>设计哲学</b>:HSL 的 IByteTransform 有 60+ 方法(含 2D 数组、字符串编码等)。
    /// Nexus 版本精简到只覆盖值类型基础互转 — 字符串/BCD/hex 显示等仍归
    /// <see cref="DataConverter"/> / <c>StringConverter</c>,避免接口爆炸。
    /// </remarks>
    public interface IByteTransform
    {
        /// <summary>当前实例代表的字节序(只读 — 改字节序应换实例)。</summary>
        Endianness ByteOrder { get; }

        // ── byte[] → 值类型 ────────────────────────

        bool ToBool(byte[] buffer, int offset);
        byte ToByte(byte[] buffer, int offset);
        short ToInt16(byte[] buffer, int offset);
        ushort ToUInt16(byte[] buffer, int offset);
        int ToInt32(byte[] buffer, int offset);
        uint ToUInt32(byte[] buffer, int offset);
        long ToInt64(byte[] buffer, int offset);
        ulong ToUInt64(byte[] buffer, int offset);
        float ToSingle(byte[] buffer, int offset);
        double ToDouble(byte[] buffer, int offset);

        // ── 值类型 → byte[] ────────────────────────

        byte[] GetBytes(bool value);
        byte[] GetBytes(byte value);
        byte[] GetBytes(short value);
        byte[] GetBytes(ushort value);
        byte[] GetBytes(int value);
        byte[] GetBytes(uint value);
        byte[] GetBytes(long value);
        byte[] GetBytes(ulong value);
        byte[] GetBytes(float value);
        byte[] GetBytes(double value);

        // ── 字符串(可选编码,默认 ASCII)────────────

        /// <summary>从字节数组解码字符串(自动去尾 0 和空格)。</summary>
        string GetString(byte[] buffer, int offset, int length, Encoding? encoding = null);

        /// <summary>用指定编码将字符串编码为字节数组。</summary>
        byte[] GetBytes(string value, Encoding? encoding = null);
    }
}
