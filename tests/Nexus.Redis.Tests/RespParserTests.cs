using Nexus.Redis;
using Xunit;

namespace Nexus.Redis.Tests
{
    public class RespParserTests
    {
        [Fact]
        public void Parse_SimpleString()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("+OK\r\n");
            var result = RespParser.Parse(data);
            Assert.Equal(RespType.SimpleString, result.Type);
            Assert.Equal("OK", result.AsString());
        }

        [Fact]
        public void Parse_Error()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("-ERR unknown command\r\n");
            var result = RespParser.Parse(data);
            Assert.Equal(RespType.Error, result.Type);
            Assert.Equal("ERR unknown command", result.AsString());
        }

        [Fact]
        public void Parse_Integer()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(":1000\r\n");
            var result = RespParser.Parse(data);
            Assert.Equal(RespType.Integer, result.Type);
            Assert.Equal(1000, result.AsInt64());
        }

        [Fact]
        public void Parse_NegativeInteger()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(":-42\r\n");
            var result = RespParser.Parse(data);
            Assert.Equal(RespType.Integer, result.Type);
            Assert.Equal(-42, result.AsInt64());
        }

        [Fact]
        public void Parse_BulkString()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("$5\r\nhello\r\n");
            var result = RespParser.Parse(data);
            Assert.Equal(RespType.BulkString, result.Type);
            Assert.Equal("hello", result.AsString());
        }

        [Fact]
        public void Parse_NullBulkString()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("$-1\r\n");
            var result = RespParser.Parse(data);
            Assert.Equal(RespType.BulkString, result.Type);
            Assert.True(result.IsNull);
        }

        [Fact]
        public void Parse_Array()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("*2\r\n$5\r\nhello\r\n$5\r\nworld\r\n");
            var result = RespParser.Parse(data);
            Assert.Equal(RespType.Array, result.Type);
            Assert.Equal(2, result.ArrayValue.Length);
            Assert.Equal("hello", result.ArrayValue[0].AsString());
            Assert.Equal("world", result.ArrayValue[1].AsString());
        }

        [Fact]
        public void Parse_NullArray()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("*-1\r\n");
            var result = RespParser.Parse(data);
            Assert.Equal(RespType.Array, result.Type);
            Assert.True(result.IsNull);
        }

        [Fact]
        public void Parse_EmptyArray()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("*0\r\n");
            var result = RespParser.Parse(data);
            Assert.Equal(RespType.Array, result.Type);
            Assert.NotNull(result.ArrayValue);
            Assert.Empty(result.ArrayValue);
        }

        [Fact]
        public void Parse_EmptyBulkString()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("$0\r\n\r\n");
            var result = RespParser.Parse(data);
            Assert.Equal(RespType.BulkString, result.Type);
            Assert.Equal("", result.AsString());
        }

        [Fact]
        public void Parse_NestedArray()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("*2\r\n*2\r\n$3\r\nfoo\r\n$3\r\nbar\r\n$3\r\nbaz\r\n");
            var result = RespParser.Parse(data);
            Assert.Equal(RespType.Array, result.Type);
            Assert.Equal(2, result.ArrayValue.Length);
            Assert.Equal(RespType.Array, result.ArrayValue[0].Type);
            Assert.Equal(2, result.ArrayValue[0].ArrayValue.Length);
            Assert.Equal("foo", result.ArrayValue[0].ArrayValue[0].AsString());
            Assert.Equal("bar", result.ArrayValue[0].ArrayValue[1].AsString());
            Assert.Equal("baz", result.ArrayValue[1].AsString());
        }

        [Fact]
        public void Encode_SingleCommand()
        {
            byte[] encoded = RespParser.Encode("PING");
            string expected = "*1\r\n$4\r\nPING\r\n";
            Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(encoded));
        }

        [Fact]
        public void Encode_CommandWithArgs()
        {
            byte[] encoded = RespParser.Encode("GET", "mykey");
            string expected = "*2\r\n$3\r\nGET\r\n$5\r\nmykey\r\n";
            Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(encoded));
        }

        [Fact]
        public void Encode_SetCommand()
        {
            byte[] encoded = RespParser.Encode("SET", "mykey", "myvalue");
            string expected = "*3\r\n$3\r\nSET\r\n$5\r\nmykey\r\n$7\r\nmyvalue\r\n";
            Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(encoded));
        }

        [Fact]
        public void Encode_EncodeCommandArray()
        {
            byte[] encoded = RespParser.EncodeCommand("SET", "key", "value");
            string expected = "*3\r\n$3\r\nSET\r\n$3\r\nkey\r\n$5\r\nvalue\r\n";
            Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(encoded));
        }

        [Fact]
        public void Parse_MultipleValues()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("+OK\r\n:42\r\n$5\r\nhello\r\n");
            int offset = 0;
            var r1 = RespParser.Parse(data, ref offset);
            var r2 = RespParser.Parse(data, ref offset);
            var r3 = RespParser.Parse(data, ref offset);

            Assert.Equal(RespType.SimpleString, r1.Type);
            Assert.Equal("OK", r1.AsString());
            Assert.Equal(RespType.Integer, r2.Type);
            Assert.Equal(42, r2.AsInt64());
            Assert.Equal(RespType.BulkString, r3.Type);
            Assert.Equal("hello", r3.AsString());
        }

        [Fact]
        public void Parse_RespValue_ToRedisValue()
        {
            var simple = RespValue.SimpleString("OK");
            Assert.Equal("OK", simple.ToRedisValue().AsString());

            var integer = RespValue.Integer(42);
            Assert.Equal(42, integer.ToRedisValue().AsInt64());

            var bulk = RespValue.BulkString(System.Text.Encoding.UTF8.GetBytes("hello"));
            Assert.Equal("hello", bulk.ToRedisValue().AsString());

            var nil = RespValue.BulkNull();
            Assert.True(nil.ToRedisValue().IsNull);
        }
    }
}
