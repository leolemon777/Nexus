using System;
using System.Text;

namespace Nexus.Redis
{
    public enum RespType
    {
        SimpleString,
        Error,
        Integer,
        BulkString,
        Array
    }

    public class RespValue
    {
        public RespType Type { get; }
        public string StringValue { get; }
        public long IntegerValue { get; }
        public byte[] BulkValue { get; }
        public RespValue[] ArrayValue { get; }
        public bool IsNull { get; }

        private RespValue(RespType type)
        {
            Type = type;
            IsNull = true;
        }

        private RespValue(RespType type, string s)
        {
            Type = type;
            StringValue = s;
        }

        private RespValue(RespType type, long i)
        {
            Type = type;
            IntegerValue = i;
        }

        private RespValue(RespType type, byte[] bulk)
        {
            Type = type;
            BulkValue = bulk;
            StringValue = bulk != null ? Encoding.UTF8.GetString(bulk) : null;
        }

        private RespValue(RespType type, RespValue[] array)
        {
            Type = type;
            ArrayValue = array;
        }

        public static RespValue SimpleString(string value) => new RespValue(RespType.SimpleString, value);
        public static RespValue Error(string message) => new RespValue(RespType.Error, message);
        public static RespValue Integer(long value) => new RespValue(RespType.Integer, value);
        public static RespValue BulkString(byte[] value) => new RespValue(RespType.BulkString, value);
        public static RespValue BulkNull() => new RespValue(RespType.BulkString);
        public static RespValue Array(RespValue[] items) => new RespValue(RespType.Array, items);
        public static RespValue ArrayNull() => new RespValue(RespType.Array);

        public string AsString() => StringValue ?? "";
        public long AsInt64() => IntegerValue;
        public RedisValue ToRedisValue()
        {
            switch (Type)
            {
                case RespType.SimpleString: return RedisValue.FromString(StringValue);
                case RespType.Integer: return RedisValue.FromInteger(IntegerValue);
                case RespType.BulkString:
                    if (IsNull || BulkValue == null) return RedisValue.Null;
                    return RedisValue.FromBytes(BulkValue);
                default: return RedisValue.FromString(StringValue ?? "");
            }
        }

        public override string ToString()
        {
            switch (Type)
            {
                case RespType.SimpleString: return $"+{StringValue}";
                case RespType.Error: return $"-{StringValue}";
                case RespType.Integer: return $":{IntegerValue}";
                case RespType.BulkString:
                    if (IsNull) return "$-1";
                    return $"${BulkValue?.Length ?? 0}\r\n{StringValue}";
                case RespType.Array:
                    if (IsNull) return "*-1";
                    var sb = new StringBuilder();
                    sb.Append($"*{ArrayValue?.Length ?? 0}");
                    if (ArrayValue != null)
                        foreach (var item in ArrayValue)
                            sb.Append($"\r\n{item}");
                    return sb.ToString();
                default: return "";
            }
        }
    }

    public static class RespParser
    {
        private static readonly Encoding Utf8 = Encoding.UTF8;

        public static RespValue Parse(byte[] data)
        {
            int offset = 0;
            return ParseValue(data, ref offset);
        }

        public static RespValue Parse(byte[] data, ref int offset)
        {
            return ParseValue(data, ref offset);
        }

        private static RespValue ParseValue(byte[] data, ref int offset)
        {
            if (offset >= data.Length)
                throw new RespException("Unexpected end of data");

            byte typeByte = data[offset++];
            switch ((char)typeByte)
            {
                case '+': return ParseSimpleString(data, ref offset);
                case '-': return ParseError(data, ref offset);
                case ':': return ParseInteger(data, ref offset);
                case '$': return ParseBulkString(data, ref offset);
                case '*': return ParseArray(data, ref offset);
                default:
                    throw new RespException($"Unexpected RESP type byte: 0x{typeByte:X2}");
            }
        }

        private static RespValue ParseSimpleString(byte[] data, ref int offset)
        {
            string line = ReadLine(data, ref offset);
            return RespValue.SimpleString(line);
        }

        private static RespValue ParseError(byte[] data, ref int offset)
        {
            string line = ReadLine(data, ref offset);
            return RespValue.Error(line);
        }

        private static RespValue ParseInteger(byte[] data, ref int offset)
        {
            string line = ReadLine(data, ref offset);
            if (!long.TryParse(line, out long value))
                throw new RespException($"Invalid integer: {line}");
            return RespValue.Integer(value);
        }

        private static RespValue ParseBulkString(byte[] data, ref int offset)
        {
            string lenLine = ReadLine(data, ref offset);
            if (!int.TryParse(lenLine, out int length))
                throw new RespException($"Invalid bulk string length: {lenLine}");

            if (length == -1)
                return RespValue.BulkNull();

            if (length < 0)
                throw new RespException($"Invalid bulk string length: {length}");

            if (offset + length + 2 > data.Length)
                throw new RespException("Bulk string data exceeds available bytes");

            byte[] bulk = new byte[length];
            Buffer.BlockCopy(data, offset, bulk, 0, length);
            offset += length;

            if (data[offset] != '\r' || data[offset + 1] != '\n')
                throw new RespException("Expected CRLF after bulk string data");
            offset += 2;

            return RespValue.BulkString(bulk);
        }

        private static RespValue ParseArray(byte[] data, ref int offset)
        {
            string lenLine = ReadLine(data, ref offset);
            if (!int.TryParse(lenLine, out int count))
                throw new RespException($"Invalid array length: {lenLine}");

            if (count == -1)
                return RespValue.ArrayNull();

            if (count < 0)
                throw new RespException($"Invalid array length: {count}");

            var items = new RespValue[count];
            for (int i = 0; i < count; i++)
                items[i] = ParseValue(data, ref offset);

            return RespValue.Array(items);
        }

        private static string ReadLine(byte[] data, ref int offset)
        {
            int start = offset;
            while (offset < data.Length - 1)
            {
                if (data[offset] == '\r' && data[offset + 1] == '\n')
                {
                    string line = Utf8.GetString(data, start, offset - start);
                    offset += 2;
                    return line;
                }
                offset++;
            }
            throw new RespException("CRLF not found");
        }

        public static byte[] Encode(string command, params string[] args)
        {
            var parts = new string[1 + args.Length];
            parts[0] = command;
            Array.Copy(args, 0, parts, 1, args.Length);
            return EncodeCommand(parts);
        }

        public static byte[] EncodeCommand(params string[] parts)
        {
            var sb = new StringBuilder();
            sb.Append('*').Append(parts.Length).Append("\r\n");
            foreach (var part in parts)
            {
                byte[] bytes = Utf8.GetBytes(part ?? "");
                sb.Append('$').Append(bytes.Length).Append("\r\n");
                sb.Append(part ?? "").Append("\r\n");
            }
            return Utf8.GetBytes(sb.ToString());
        }

        public static byte[] EncodeCommandBytes(params byte[][] parts)
        {
            int totalLen = 0;
            string header = "*" + parts.Length + "\r\n";
            byte[] headerBytes = Utf8.GetBytes(header);
            totalLen += headerBytes.Length;

            foreach (var part in parts)
            {
                string partHeader = "$" + part.Length + "\r\n";
                byte[] partHeaderBytes = Utf8.GetBytes(partHeader);
                totalLen += partHeaderBytes.Length + part.Length + 2; // +2 for CRLF
            }

            byte[] result = new byte[totalLen];
            int pos = 0;
            Buffer.BlockCopy(headerBytes, 0, result, pos, headerBytes.Length);
            pos += headerBytes.Length;

            foreach (var part in parts)
            {
                string partHeader = "$" + part.Length + "\r\n";
                byte[] partHeaderBytes = Utf8.GetBytes(partHeader);
                Buffer.BlockCopy(partHeaderBytes, 0, result, pos, partHeaderBytes.Length);
                pos += partHeaderBytes.Length;
                Buffer.BlockCopy(part, 0, result, pos, part.Length);
                pos += part.Length;
                result[pos++] = (byte)'\r';
                result[pos++] = (byte)'\n';
            }

            return result;
        }
    }

    public class RespException : Exception
    {
        public RespException(string message) : base(message) { }
        public RespException(string message, Exception inner) : base(message, inner) { }
    }
}
