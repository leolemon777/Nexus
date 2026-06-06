using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace Nexus
{
    /// <summary>
    /// 数据类型转换工具 — 字节序处理（大端序）、基础类型编解码。
    /// </summary>
    public static class DataConverter
    {
        // ── 字节数组 → 值（大端序）─────────────────

        public static bool ToBool(byte[] data, int offset = 0)
            => data[offset] != 0;

        public static short ToInt16(byte[] data, int offset = 0)
            => (short)((data[offset] << 8) | data[offset + 1]);

        public static ushort ToUInt16(byte[] data, int offset = 0)
            => (ushort)((data[offset] << 8) | data[offset + 1]);

        public static int ToInt32(byte[] data, int offset = 0)
            => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

        public static uint ToUInt32(byte[] data, int offset = 0)
            => (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

        public static long ToInt64(byte[] data, int offset = 0)
        {
            uint hi = ToUInt32(data, offset);
            uint lo = ToUInt32(data, offset + 4);
            return ((long)hi << 32) | lo;
        }

        public static ulong ToUInt64(byte[] data, int offset = 0)
        {
            uint hi = ToUInt32(data, offset);
            uint lo = ToUInt32(data, offset + 4);
            return ((ulong)hi << 32) | lo;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe float ToFloat(byte[] data, int offset = 0)
        {
            int v = ToInt32(data, offset);
            return *(float*)&v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe double ToDouble(byte[] data, int offset = 0)
        {
            long v = ToInt64(data, offset);
            return *(double*)&v;
        }

        public static string ToString(byte[] data, int offset, int length)
            => Encoding.ASCII.GetString(data, offset, length).TrimEnd('\0', ' ');

        // ── 值 → 字节数组（大端序）─────────────────

        public static byte[] GetBytes(bool value) => new byte[] { value ? (byte)1 : (byte)0 };

        public static byte[] GetBytes(short value)
            => new byte[] { (byte)(value >> 8), (byte)value };

        public static byte[] GetBytes(ushort value)
            => new byte[] { (byte)(value >> 8), (byte)value };

        public static byte[] GetBytes(int value)
            => new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

        public static byte[] GetBytes(uint value)
            => new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe byte[] GetBytes(float value)
        {
            int v = *(int*)&value;
            return GetBytes(v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe byte[] GetBytes(double value)
        {
            long v = *(long*)&value;
            return GetBytes(v);
        }

        public static byte[] GetBytes(long value)
            => new byte[] { (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32),
                           (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

        public static byte[] GetBytes(string value)
            => Encoding.ASCII.GetBytes(value);

        // ── 辅助 ──────────────────────────────────

        public static string ToHexString(byte[] data)
        {
            if (data == null) return string.Empty;
            var sb = new StringBuilder(data.Length * 3);
            foreach (byte b in data) sb.AppendFormat("{0:X2} ", b);
            return sb.ToString().Trim();
        }

        public static string ToHexString(byte[] data, int offset, int length)
        {
            if (data == null) return string.Empty;
            var sb = new StringBuilder(length * 3);
            int end = Math.Min(offset + length, data.Length);
            for (int i = offset; i < end; i++) sb.AppendFormat("{0:X2} ", data[i]);
            return sb.ToString().Trim();
        }
    }
}
