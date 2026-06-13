using Xunit;
using Nexus.Keyence;
using System;
using System.IO;
using System.Text;

namespace Nexus.Keyence.Tests
{
    public class KeyenceKvTests
    {
        [Fact]
        public void Constructor_SetsDefaults()
        {
            var client = new KeyenceKvClient("192.168.1.1");
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void Constructor_WithPort_SetsPort()
        {
            var client = new KeyenceKvClient("192.168.1.1", 3000);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            var client = new KeyenceKvClient("192.168.1.1");
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            var client = new KeyenceKvClient("192.168.1.1");
            client.Dispose();
        }

        [Fact]
        public void BatchOperations_EmptyInput_ReturnsError()
        {
            var client = new KeyenceKvClient("192.168.1.1");

            Assert.False(client.BatchRead(new string[0]).IsSuccess);
            Assert.False(client.RandomRead(new string[0]).IsSuccess);
            Assert.False(client.BatchWrite(System.Array.Empty<System.Collections.Generic.KeyValuePair<string, object>>()).IsSuccess);
        }

        [Fact]
        public void WriteUInt64_WritesFourRegisters()
        {
            var stream = new DuplexStream("OK\r");
            var client = new KeyenceKvClient(stream);

            var result = client.Write("DM100", 0x1122334455667788UL);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal("00WRS DM100 4 1122 3344 5566 7788\r", stream.WrittenText);
        }

        [Fact]
        public void WriteDouble_WritesIeee754Bits()
        {
            var stream = new DuplexStream("OK\r");
            var client = new KeyenceKvClient(stream);

            var result = client.Write("DM100", 1.5d);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal("00WRS DM100 4 3FF8 0000 0000 0000\r", stream.WrittenText);
        }

        [Fact]
        public void ReadDouble_ReadsFourRegisters()
        {
            var stream = new DuplexStream("3FF8 0000 0000 0000\r");
            var client = new KeyenceKvClient(stream);

            var result = client.ReadDouble("DM100");

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(1.5d, result.Content);
            Assert.Equal("00RDS DM100 4\r", stream.WrittenText);
        }

        [Fact]
        public void ReadBytes_TruncatedPayload_ReturnsFailure()
        {
            var stream = new DuplexStream("00AA\r");
            var client = new KeyenceKvClient(stream);

            var result = client.ReadBytes("DM100", 4);

            Assert.False(result.IsSuccess);
            Assert.Contains("响应数据不足", result.Message);
        }

        [Fact]
        public void WriteBytes_Null_ReturnsFailure()
        {
            var client = new KeyenceKvClient(new DuplexStream("OK\r"));

            var result = client.Write("DM100", (byte[])null!);

            Assert.False(result.IsSuccess);
        }

        #region 补强测试

        [Fact]
        public void ReadInt16_ReadsSingleRegister()
        {
            var stream = new DuplexStream("0064\r");
            var client = new KeyenceKvClient(stream);
            var result = client.ReadInt16("DM100");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((short)100, result.Content);
            Assert.Equal("00RD DM100\r", stream.WrittenText);
        }

        [Fact]
        public void ReadBool_TrueValue()
        {
            var stream = new DuplexStream("0001\r");
            var client = new KeyenceKvClient(stream);
            var result = client.ReadBool("DM100");
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(result.Content);
        }

        [Fact]
        public void WriteInt16_SendsCorrectCommand()
        {
            var stream = new DuplexStream("OK\r");
            var client = new KeyenceKvClient(stream);
            var result = client.Write("DM100", (short)42);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal("00WR DM100 002A\r", stream.WrittenText);
        }

        [Fact]
        public void Subscribe_Unsubscribe_NotConnected_DoesNotThrow()
        {
            var client = new KeyenceKvClient("192.168.1.1");
            client.Subscribe("DM100", 1000, "Int16");
            client.Unsubscribe("DM100");
            client.StartSubscriptions();
            client.StopSubscriptions();
            client.Dispose();
        }

        [Fact]
        public void Station_DefaultIsZero()
        {
            var client = new KeyenceKvClient("192.168.1.1");
            Assert.Equal((byte)0, client.Station);
            client.Station = 5;
            Assert.Equal((byte)5, client.Station);
        }

        #endregion

        private sealed class DuplexStream : Stream
        {
            private readonly byte[] _response;
            private int _readOffset;
            private readonly MemoryStream _written = new MemoryStream();

            public DuplexStream(string response)
            {
                _response = Encoding.ASCII.GetBytes(response);
            }

            public string WrittenText => Encoding.ASCII.GetString(_written.ToArray());

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_readOffset >= _response.Length) return 0;
                int copy = Math.Min(count, _response.Length - _readOffset);
                Buffer.BlockCopy(_response, _readOffset, buffer, offset, copy);
                _readOffset += copy;
                return copy;
            }

            public override int ReadByte()
            {
                if (_readOffset >= _response.Length) return -1;
                return _response[_readOffset++];
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _written.Write(buffer, offset, count);
            }
        }
    }
}
