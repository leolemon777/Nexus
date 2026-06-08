using System;
using System.Text;

namespace Nexus
{
    /// <summary>
    /// 字符串编码工具 — S7 / Mitsubishi / Modbus / BCD 等工控常用字符串编解码。
    /// </summary>
    public static class StringConverter
    {
        // ── S7 String ──────────────────────────────

        /// <summary>
        /// 解码 S7 String 格式：[maxLen(1)][actualLen(1)][chars...]。
        /// </summary>
        public static string DecodeS7String(byte[] data, int offset)
        {
            if (data == null || data.Length < offset + 2)
                return string.Empty;

            byte maxLen = data[offset];
            byte actualLen = data[offset + 1];

            if (actualLen == 0 || offset + 2 + actualLen > data.Length)
                return string.Empty;

            return Encoding.ASCII.GetString(data, offset + 2, actualLen);
        }

        /// <summary>
        /// 编码为 S7 String 格式：[maxLen(1)][actualLen(1)][chars...]。
        /// </summary>
        public static byte[] EncodeS7String(string value, ushort maxLength = 254)
        {
            if (value == null) value = string.Empty;
            byte[] chars = Encoding.ASCII.GetBytes(value);
            int actualLen = Math.Min(chars.Length, maxLength);

            byte[] result = new byte[2 + maxLength];
            result[0] = (byte)maxLength;
            result[1] = (byte)actualLen;
            Buffer.BlockCopy(chars, 0, result, 2, actualLen);
            return result;
        }

        // ── S7 WString ─────────────────────────────

        /// <summary>
        /// 解码 S7 WString 格式：[maxLen(2, BE)][actualLen(2, BE)][chars_utf16_be...]。
        /// </summary>
        public static string DecodeWString(byte[] data, int offset)
        {
            if (data == null || data.Length < offset + 4)
                return string.Empty;

            int maxLen = (data[offset] << 8) | data[offset + 1];
            int actualLen = (data[offset + 2] << 8) | data[offset + 3];

            if (actualLen == 0 || offset + 4 + actualLen * 2 > data.Length)
                return string.Empty;

            // WString 存储为 UTF-16BE，转换为 UTF-16LE（Windows/.NET 内部格式）
            byte[] chars = new byte[actualLen * 2];
            for (int i = 0; i < actualLen; i++)
            {
                chars[i * 2] = data[offset + 4 + i * 2 + 1];     // LE: low byte first
                chars[i * 2 + 1] = data[offset + 4 + i * 2];     // LE: high byte second
            }

            return Encoding.Unicode.GetString(chars, 0, chars.Length);
        }

        /// <summary>
        /// 编码为 S7 WString 格式：[maxLen(2, BE)][actualLen(2, BE)][chars_utf16_be...]。
        /// </summary>
        public static byte[] EncodeWString(string value, ushort maxLength = 16382)
        {
            if (value == null) value = string.Empty;
            byte[] chars = Encoding.Unicode.GetBytes(value);
            int charCount = chars.Length / 2;
            int actualLen = Math.Min(charCount, maxLength);

            byte[] result = new byte[4 + maxLength * 2];
            result[0] = (byte)(maxLength >> 8);
            result[1] = (byte)maxLength;
            result[2] = (byte)(actualLen >> 8);
            result[3] = (byte)actualLen;

            // .NET Unicode is UTF-16LE, convert to UTF-16BE for S7
            for (int i = 0; i < actualLen; i++)
            {
                result[4 + i * 2] = chars[i * 2 + 1];     // BE: high byte first
                result[4 + i * 2 + 1] = chars[i * 2];     // BE: low byte second
            }

            return result;
        }

        // ── Mitsubishi String ──────────────────────

        /// <summary>
        /// 解码三菱字符串。[len(1/2)][chars...] 或 null-terminated。
        /// </summary>
        public static string DecodeMitsubishiString(byte[] data, int offset, int maxLen, Encoding encoding)
        {
            if (data == null || offset >= data.Length)
                return string.Empty;

            int available = Math.Min(maxLen, data.Length - offset);

            // 查找 null 终止符
            int end = offset;
            while (end < offset + available && data[end] != 0)
                end++;

            int length = end - offset;
            if (length <= 0)
                return string.Empty;

            return (encoding ?? Encoding.ASCII).GetString(data, offset, length).TrimEnd(' ');
        }

        // ── Modbus String ──────────────────────────

        /// <summary>
        /// 解码 Modbus 字符串。寄存器按指定字节序解释。
        /// </summary>
        public static string DecodeModbusString(byte[] data, int offset, int length, Endianness byteOrder)
        {
            if (data == null || length <= 0)
                return string.Empty;

            byte[] buf = new byte[length];
            Buffer.BlockCopy(data, offset, buf, 0, Math.Min(length, data.Length - offset));

            // MidBigEndian / MidLittleEndian 仅影响字内字节序
            if (byteOrder == Endianness.LittleEndian || byteOrder == Endianness.Dcba)
            {
                // 反转每对字节（交换高低字节位置）
                for (int i = 0; i + 1 < buf.Length; i += 2)
                {
                    byte tmp = buf[i];
                    buf[i] = buf[i + 1];
                    buf[i + 1] = tmp;
                }
            }

            return Encoding.ASCII.GetString(buf).TrimEnd('\0', ' ');
        }

        // ── BCD 编码 ───────────────────────────────

        /// <summary>
        /// 解码 BCD 编码字符串。每字节存两位十进制数。
        /// 例如：0x12 0x34 → "1234"。
        /// </summary>
        public static string DecodeBcdString(byte[] data, int offset, int length)
        {
            if (data == null || length <= 0)
                return string.Empty;

            var sb = new StringBuilder(length * 2);
            for (int i = 0; i < length; i++)
            {
                byte b = data[offset + i];
                sb.Append((char)('0' + (b >> 4)));
                sb.Append((char)('0' + (b & 0x0F)));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 编码 BCD 字符串。每两位十进制字符编码为一字节。
        /// 例如："1234" → 0x12 0x34。
        /// </summary>
        public static byte[] EncodeBcdString(string value, int targetLength)
        {
            if (value == null) value = string.Empty;

            // 左填充零到目标长度
            if (value.Length < targetLength * 2)
                value = value.PadLeft(targetLength * 2, '0');

            byte[] result = new byte[targetLength];
            for (int i = 0; i < targetLength; i++)
            {
                int hi = (i * 2 < value.Length) ? value[i * 2] - '0' : 0;
                int lo = (i * 2 + 1 < value.Length) ? value[i * 2 + 1] - '0' : 0;
                result[i] = (byte)((hi << 4) | lo);
            }

            return result;
        }
    }
}
