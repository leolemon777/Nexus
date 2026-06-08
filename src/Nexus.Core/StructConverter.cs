using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Nexus
{
    /// <summary>
    /// 结构体映射工具 — 将 PLC 内存字节数组映射到 C# struct，或反向序列化。
    /// 支持 HSL 风格的字节序控制，每个字段按 Endianness 独立解码。
    /// </summary>
    public static class StructConverter
    {
        // ── 基础版本（原生内存布局，blittable struct）──────────

        /// <summary>
        /// 将字节数组反序列化为结构体（原生内存布局）。
        /// 仅适用于 blittable struct（无引用类型字段）。
        /// </summary>
        public static T FromBytes<T>(byte[] data, int offset = 0) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            if (data == null || data.Length < offset + size)
                throw new ArgumentException($"数据长度不足：需要 {size} 字节，实际 {data?.Length - offset ?? 0} 字节");

            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(data, offset, ptr, size);
                return Marshal.PtrToStructure<T>(ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// 将结构体序列化为字节数组（原生内存布局）。
        /// </summary>
        public static byte[] ToBytes<T>(ref T value) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            byte[] data = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, ptr, false);
                Marshal.Copy(ptr, data, 0, size);
                return data;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        // ── Endianness 版本（反射逐字段解码）─────────────────

        /// <summary>
        /// 将字节数组反序列化为结构体，按指定字节序逐字段解码。
        /// 支持所有数值类型（bool, byte, short, int, long, float, double 等）。
        /// 字段顺序由 <see cref="StructLayoutAttribute"/> 或声明顺序决定。
        /// </summary>
        public static T FromBytes<T>(byte[] data, int offset, Endianness byteOrder) where T : struct
        {
            if (byteOrder == Endianness.BigEndian)
                return FromBytes<T>(data, offset);

            var result = (object)(default(T));
            int pos = offset;

            foreach (var field in GetLayoutFields(typeof(T)))
            {
                int fieldSize = Marshal.SizeOf(field.FieldType);
                object fieldValue = ReadField(data, pos, field.FieldType, byteOrder);
                field.SetValue(result, fieldValue);
                pos += fieldSize;
            }

            return (T)result;
        }

        /// <summary>
        /// 将结构体序列化为字节数组，按指定字节序逐字段编码。
        /// </summary>
        public static byte[] ToBytes<T>(ref T value, Endianness byteOrder) where T : struct
        {
            if (byteOrder == Endianness.BigEndian)
                return ToBytes(ref value);

            var fields = GetLayoutFields(typeof(T));
            int totalSize = 0;
            foreach (var f in fields)
                totalSize += Marshal.SizeOf(f.FieldType);

            byte[] result = new byte[totalSize];
            int pos = 0;
            var boxed = (object)value;

            foreach (var field in fields)
            {
                int fieldSize = Marshal.SizeOf(field.FieldType);
                object fieldValue = field.GetValue(boxed);
                WriteField(result, pos, fieldValue, byteOrder);
                pos += fieldSize;
            }

            return result;
        }

        // ── 内部辅助 ──────────────────────────────

        private static FieldInfo[] GetLayoutFields(Type type)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            // 如果有 StructLayout(LayoutKind.Sequential)，按声明顺序
            // 否则也按声明顺序（.NET 默认）
            return fields;
        }

        private static object ReadField(byte[] data, int offset, Type type, Endianness bo)
        {
            if (type == typeof(bool))   return data[offset] != 0;
            if (type == typeof(byte))   return data[offset];
            if (type == typeof(sbyte))  return (sbyte)data[offset];
            if (type == typeof(short))  return DataConverter.ToInt16(data, offset, bo);
            if (type == typeof(ushort)) return DataConverter.ToUInt16(data, offset, bo);
            if (type == typeof(int))    return DataConverter.ToInt32(data, offset, bo);
            if (type == typeof(uint))   return DataConverter.ToUInt32(data, offset, bo);
            if (type == typeof(long))   return DataConverter.ToInt64(data, offset, bo);
            if (type == typeof(ulong))  return DataConverter.ToUInt64(data, offset, bo);
            if (type == typeof(float))  return DataConverter.ToFloat(data, offset, bo);
            if (type == typeof(double)) return DataConverter.ToDouble(data, offset, bo);

            // 嵌套 struct：递归处理
            if (type.IsValueType && !type.IsEnum && !type.IsPrimitive)
                return typeof(StructConverter)
                    .GetMethod(nameof(FromBytes), new[] { typeof(byte[]), typeof(int), typeof(Endianness) })
                    .MakeGenericMethod(type)
                    .Invoke(null, new object[] { data, offset, bo });

            // byte[] 字段
            if (type == typeof(byte[]))
            {
                int size = Marshal.SizeOf(type);
                var buf = new byte[size];
                Buffer.BlockCopy(data, offset, buf, 0, size);
                return buf;
            }

            throw new NotSupportedException($"StructConverter 不支持字段类型: {type.Name}");
        }

        private static void WriteField(byte[] data, int offset, object value, Endianness bo)
        {
            if (value is bool b)     { data[offset] = b ? (byte)1 : (byte)0; return; }
            if (value is byte ub)    { data[offset] = ub; return; }
            if (value is sbyte sb)   { data[offset] = (byte)sb; return; }
            if (value is short s)    { var bytes = DataConverter.GetBytes(s, bo); Buffer.BlockCopy(bytes, 0, data, offset, 2); return; }
            if (value is ushort us)  { var bytes = DataConverter.GetBytes(us, bo); Buffer.BlockCopy(bytes, 0, data, offset, 2); return; }
            if (value is int i)      { var bytes = DataConverter.GetBytes(i, bo); Buffer.BlockCopy(bytes, 0, data, offset, 4); return; }
            if (value is uint ui)    { var bytes = DataConverter.GetBytes(ui, bo); Buffer.BlockCopy(bytes, 0, data, offset, 4); return; }
            if (value is long l)     { var bytes = DataConverter.GetBytes(l, bo); Buffer.BlockCopy(bytes, 0, data, offset, 8); return; }
            if (value is ulong ul)   { var bytes = DataConverter.GetBytes(ul, bo); Buffer.BlockCopy(bytes, 0, data, offset, 8); return; }
            if (value is float f)    { var bytes = DataConverter.GetBytes(f, bo); Buffer.BlockCopy(bytes, 0, data, offset, 4); return; }
            if (value is double d)   { var bytes = DataConverter.GetBytes(d, bo); Buffer.BlockCopy(bytes, 0, data, offset, 8); return; }
        }
    }
}
