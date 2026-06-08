using System;
using System.Text;

namespace Nexus.Redis
{
    public enum RedisValueType
    {
        Null,
        String,
        Integer,
        Double,
        Bytes
    }

    public struct RedisValue : IEquatable<RedisValue>
    {
        private readonly string _stringValue;
        private readonly long _intValue;
        private readonly double _doubleValue;
        private readonly byte[] _bytesValue;

        public RedisValueType ValueType { get; }

        public bool IsNull => ValueType == RedisValueType.Null;
        public bool HasValue => ValueType != RedisValueType.Null;

        private RedisValue(RedisValueType type, string s, long i, double d, byte[] b)
        {
            ValueType = type;
            _stringValue = s;
            _intValue = i;
            _doubleValue = d;
            _bytesValue = b;
        }

        public static RedisValue Null => new RedisValue(RedisValueType.Null, null, 0, 0, null);

        public static RedisValue FromString(string value)
            => new RedisValue(RedisValueType.String, value ?? "", 0, 0, null);

        public static RedisValue FromInteger(long value)
            => new RedisValue(RedisValueType.Integer, null, value, 0, null);

        public static RedisValue FromDouble(double value)
            => new RedisValue(RedisValueType.Double, null, 0, value, null);

        public static RedisValue FromBytes(byte[] value)
            => new RedisValue(RedisValueType.Bytes, null, 0, 0, value);

        public static implicit operator RedisValue(string value) => FromString(value);
        public static implicit operator RedisValue(long value) => FromInteger(value);
        public static implicit operator RedisValue(int value) => FromInteger(value);

        public string AsString()
        {
            switch (ValueType)
            {
                case RedisValueType.String: return _stringValue ?? "";
                case RedisValueType.Integer: return _intValue.ToString();
                case RedisValueType.Double: return _doubleValue.ToString();
                case RedisValueType.Bytes: return _bytesValue != null ? Encoding.UTF8.GetString(_bytesValue) : "";
                default: return "";
            }
        }

        public long AsInt64()
        {
            switch (ValueType)
            {
                case RedisValueType.Integer: return _intValue;
                case RedisValueType.String: return long.TryParse(_stringValue, out var v) ? v : 0;
                case RedisValueType.Double: return (long)_doubleValue;
                default: return 0;
            }
        }

        public double AsDouble()
        {
            switch (ValueType)
            {
                case RedisValueType.Double: return _doubleValue;
                case RedisValueType.Integer: return _intValue;
                case RedisValueType.String: return double.TryParse(_stringValue, out var v) ? v : 0;
                default: return 0;
            }
        }

        public byte[] AsBytes()
        {
            switch (ValueType)
            {
                case RedisValueType.Bytes: return _bytesValue ?? Array.Empty<byte>();
                case RedisValueType.String: return Encoding.UTF8.GetBytes(_stringValue ?? "");
                case RedisValueType.Integer: return BitConverter.GetBytes(_intValue);
                case RedisValueType.Double: return BitConverter.GetBytes(_doubleValue);
                default: return Array.Empty<byte>();
            }
        }

        public override string ToString() => IsNull ? "(nil)" : AsString();

        public bool Equals(RedisValue other)
        {
            if (ValueType != other.ValueType) return false;
            switch (ValueType)
            {
                case RedisValueType.Null: return true;
                case RedisValueType.String: return string.Equals(_stringValue, other._stringValue);
                case RedisValueType.Integer: return _intValue == other._intValue;
                case RedisValueType.Double: return _doubleValue == other._doubleValue;
                case RedisValueType.Bytes: return ByteArrayEquals(_bytesValue, other._bytesValue);
                default: return false;
            }
        }

        public override bool Equals(object obj) => obj is RedisValue other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)ValueType * 397;
                switch (ValueType)
                {
                    case RedisValueType.String: hash ^= (_stringValue?.GetHashCode() ?? 0); break;
                    case RedisValueType.Integer: hash ^= _intValue.GetHashCode(); break;
                    case RedisValueType.Double: hash ^= _doubleValue.GetHashCode(); break;
                    case RedisValueType.Bytes:
                        if (_bytesValue != null)
                            for (int i = 0; i < _bytesValue.Length; i++)
                                hash ^= _bytesValue[i] * (i + 1);
                        break;
                }
                return hash;
            }
        }

        public static bool operator ==(RedisValue left, RedisValue right) => left.Equals(right);
        public static bool operator !=(RedisValue left, RedisValue right) => !left.Equals(right);

        private static bool ByteArrayEquals(byte[] a, byte[] b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
