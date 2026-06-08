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

        // ── 字节序转换（指定 Endianness）─────────────────

        /// <summary>根据字节序重排字节数组（原地修改）。</summary>
        public static void Reorder(byte[] buf, int offset, int length, Endianness byteOrder)
        {
            switch (byteOrder)
            {
                case Endianness.BigEndian:
                    break;

                case Endianness.LittleEndian:
                    Array.Reverse(buf, offset, length);
                    break;

                case Endianness.MidBigEndian:
                    for (int i = offset; i + 1 < offset + length; i += 2)
                    {
                        byte tmp = buf[i];
                        buf[i] = buf[i + 1];
                        buf[i + 1] = tmp;
                    }
                    break;

                case Endianness.MidLittleEndian:
                    for (int i = offset; i + 3 < offset + length; i += 4)
                    {
                        byte t0 = buf[i], t1 = buf[i + 1];
                        buf[i] = buf[i + 2];
                        buf[i + 1] = buf[i + 3];
                        buf[i + 2] = t0;
                        buf[i + 3] = t1;
                    }
                    break;
            }
        }

        // ── 读取（指定字节序）──────────────────────

        public static short ToInt16(byte[] data, int offset, Endianness byteOrder)
        {
            byte a = data[offset], b = data[offset + 1];
            switch (byteOrder)
            {
                case Endianness.LittleEndian:
                case Endianness.MidBigEndian:
                    return (short)((b << 8) | a);
                default:
                    return (short)((a << 8) | b);
            }
        }

        public static ushort ToUInt16(byte[] data, int offset, Endianness byteOrder)
            => (ushort)ToInt16(data, offset, byteOrder);

        public static int ToInt32(byte[] data, int offset, Endianness byteOrder)
        {
            byte a = data[offset], b = data[offset + 1], c = data[offset + 2], d = data[offset + 3];
            switch (byteOrder)
            {
                case Endianness.LittleEndian:
                    return (d << 24) | (c << 16) | (b << 8) | a;
                case Endianness.MidBigEndian:
                    return (b << 24) | (a << 16) | (d << 8) | c;
                case Endianness.MidLittleEndian:
                    return (c << 24) | (d << 16) | (a << 8) | b;
                default:
                    return (a << 24) | (b << 16) | (c << 8) | d;
            }
        }

        public static uint ToUInt32(byte[] data, int offset, Endianness byteOrder)
            => (uint)ToInt32(data, offset, byteOrder);

        public static long ToInt64(byte[] data, int offset, Endianness byteOrder)
        {
            if (byteOrder == Endianness.BigEndian)
                return ToInt64(data, offset);

            byte[] buf = {
                data[offset], data[offset + 1], data[offset + 2], data[offset + 3],
                data[offset + 4], data[offset + 5], data[offset + 6], data[offset + 7]
            };
            Reorder(buf, 0, 8, byteOrder);
            uint hi = (uint)((buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3]);
            uint lo = (uint)((buf[4] << 24) | (buf[5] << 16) | (buf[6] << 8) | buf[7]);
            return ((long)hi << 32) | lo;
        }

        public static ulong ToUInt64(byte[] data, int offset, Endianness byteOrder)
            => (ulong)ToInt64(data, offset, byteOrder);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe float ToFloat(byte[] data, int offset, Endianness byteOrder)
        {
            int v = ToInt32(data, offset, byteOrder);
            return *(float*)&v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe double ToDouble(byte[] data, int offset, Endianness byteOrder)
        {
            long v = ToInt64(data, offset, byteOrder);
            return *(double*)&v;
        }

        // ── 写入（指定字节序）──────────────────────

        public static byte[] GetBytes(short value, Endianness byteOrder)
        {
            var bytes = GetBytes(value);
            Reorder(bytes, 0, bytes.Length, byteOrder);
            return bytes;
        }

        public static byte[] GetBytes(ushort value, Endianness byteOrder)
        {
            var bytes = GetBytes(value);
            Reorder(bytes, 0, bytes.Length, byteOrder);
            return bytes;
        }

        public static byte[] GetBytes(int value, Endianness byteOrder)
        {
            var bytes = GetBytes(value);
            Reorder(bytes, 0, bytes.Length, byteOrder);
            return bytes;
        }

        public static byte[] GetBytes(uint value, Endianness byteOrder)
        {
            var bytes = GetBytes(value);
            Reorder(bytes, 0, bytes.Length, byteOrder);
            return bytes;
        }

        public static byte[] GetBytes(long value, Endianness byteOrder)
        {
            var bytes = GetBytes(value);
            Reorder(bytes, 0, bytes.Length, byteOrder);
            return bytes;
        }

        public static byte[] GetBytes(ulong value, Endianness byteOrder)
        {
            var bytes = GetBytes(value);
            Reorder(bytes, 0, bytes.Length, byteOrder);
            return bytes;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe byte[] GetBytes(float value, Endianness byteOrder)
            => GetBytes(*(int*)&value, byteOrder);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe byte[] GetBytes(double value, Endianness byteOrder)
            => GetBytes(*(long*)&value, byteOrder);

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
