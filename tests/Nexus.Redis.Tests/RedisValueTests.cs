using Nexus.Redis;
using System.Text;
using Xunit;

namespace Nexus.Redis.Tests
{
    public class RedisValueTests
    {
        [Fact]
        public void Null_HasExpectedState()
        {
            var val = RedisValue.Null;
            Assert.True(val.IsNull);
            Assert.False(val.HasValue);
            Assert.Equal(RedisValueType.Null, val.ValueType);
        }

        [Fact]
        public void FromString_RoundTrips()
        {
            var val = RedisValue.FromString("hello");
            Assert.Equal(RedisValueType.String, val.ValueType);
            Assert.True(val.HasValue);
            Assert.Equal("hello", val.AsString());
            Assert.Equal("hello", val.ToString());
        }

        [Fact]
        public void FromInteger_RoundTrips()
        {
            var val = RedisValue.FromInteger(42);
            Assert.Equal(RedisValueType.Integer, val.ValueType);
            Assert.Equal(42, val.AsInt64());
            Assert.Equal("42", val.AsString());
        }

        [Fact]
        public void FromDouble_RoundTrips()
        {
            var val = RedisValue.FromDouble(3.14);
            Assert.Equal(RedisValueType.Double, val.ValueType);
            Assert.Equal(3.14, val.AsDouble(), 2);
        }

        [Fact]
        public void FromBytes_RoundTrips()
        {
            byte[] data = { 1, 2, 3 };
            var val = RedisValue.FromBytes(data);
            Assert.Equal(RedisValueType.Bytes, val.ValueType);
            Assert.Equal(data, val.AsBytes());
        }

        [Fact]
        public void ImplicitConversion_FromString()
        {
            RedisValue val = "test";
            Assert.Equal(RedisValueType.String, val.ValueType);
            Assert.Equal("test", val.AsString());
        }

        [Fact]
        public void ImplicitConversion_FromLong()
        {
            RedisValue val = 100L;
            Assert.Equal(RedisValueType.Integer, val.ValueType);
            Assert.Equal(100, val.AsInt64());
        }

        [Fact]
        public void ImplicitConversion_FromInt()
        {
            RedisValue val = 42;
            Assert.Equal(RedisValueType.Integer, val.ValueType);
            Assert.Equal(42, val.AsInt64());
        }

        [Fact]
        public void Equality_SameValues()
        {
            var a = RedisValue.FromString("hello");
            var b = RedisValue.FromString("hello");
            Assert.Equal(a, b);
            Assert.True(a == b);
            Assert.False(a != b);
        }

        [Fact]
        public void Equality_DifferentValues()
        {
            var a = RedisValue.FromString("hello");
            var b = RedisValue.FromString("world");
            Assert.NotEqual(a, b);
            Assert.True(a != b);
        }

        [Fact]
        public void Equality_NullValues()
        {
            Assert.Equal(RedisValue.Null, RedisValue.Null);
        }

        [Fact]
        public void GetHashCode_ConsistentForEqualValues()
        {
            var a = RedisValue.FromString("test");
            var b = RedisValue.FromString("test");
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        [Fact]
        public void ToString_NullShowsNil()
        {
            Assert.Equal("(nil)", RedisValue.Null.ToString());
        }

        [Fact]
        public void AsInt64_FromString()
        {
            var val = RedisValue.FromString("123");
            Assert.Equal(123, val.AsInt64());
        }

        [Fact]
        public void AsDouble_FromInteger()
        {
            var val = RedisValue.FromInteger(42);
            Assert.Equal(42.0, val.AsDouble());
        }

        [Fact]
        public void AsBytes_FromString()
        {
            var val = RedisValue.FromString("hi");
            byte[] expected = Encoding.UTF8.GetBytes("hi");
            Assert.Equal(expected, val.AsBytes());
        }
    }
}
