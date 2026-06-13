using Xunit;
using Nexus.Fuji;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Fuji.Tests
{
    public class FujiSphTests
    {
        private sealed class DuplexStream : Stream
        {
            private readonly MemoryStream _readStream;
            private readonly MemoryStream _writeStream = new MemoryStream();
            private bool _open = true;

            public DuplexStream(byte[] response)
            {
                _readStream = new MemoryStream(response);
            }

            public byte[] WrittenBytes => _writeStream.ToArray();
            public override bool CanRead => _open;
            public override bool CanSeek => false;
            public override bool CanWrite => _open;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _readStream.Read(buffer, offset, count);
            public override int ReadByte() => _readStream.ReadByte();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => _writeStream.Write(buffer, offset, count);

            protected override void Dispose(bool disposing)
            {
                _open = false;
                if (disposing)
                {
                    _readStream.Dispose();
                    _writeStream.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        private static byte[] BuildFrame(byte station, string command, string data)
        {
            string body = station.ToString("D2") + command + data;
            string frameWithoutBcc = "\x02" + body + "\x03";
            byte bcc = ComputeBcc(body + "\x03");
            return Encoding.ASCII.GetBytes(frameWithoutBcc + bcc.ToString("X2"));
        }

        private static byte[] BuildResponse(byte station, string command, string data)
            => BuildFrame(station, command, data);

        private static byte ComputeBcc(string data)
        {
            byte bcc = 0;
            foreach (char c in data)
                bcc ^= (byte)c;
            return bcc;
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            using var ms = new MemoryStream();
            var client = new FujiSphClient(ms);
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Constructor_SetsDefaults()
        {
            using var ms = new MemoryStream();
            var client = new FujiSphClient(ms);
            Assert.True(client.IsConnected); // MemoryStream is always readable+writeable
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            using var ms = new MemoryStream();
            var client = new FujiSphClient(ms);
            client.Dispose();
        }

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            using var ms = new MemoryStream();
            var client = new FujiSphClient(ms);
            client.Dispose();
            client.Dispose();
        }

        [Fact]
        public void Constructor_NullStream_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new FujiSphClient(null!));
        }

        [Fact]
        public void Constructor_WithStation_SetsStation()
        {
            using var ms = new MemoryStream();
            var client = new FujiSphClient(ms, station: 3);
            Assert.Equal((byte)3, client.Station);
        }

        [Fact]
        public void Station_CanBeSet()
        {
            using var ms = new MemoryStream();
            var client = new FujiSphClient(ms);
            client.Station = 7;
            Assert.Equal((byte)7, client.Station);
        }

        [Fact]
        public void Timeout_CanBeSet()
        {
            using var ms = new MemoryStream();
            var client = new FujiSphClient(ms);
            client.Timeout = 10000;
            Assert.Equal(10000, client.Timeout);
        }

        [Fact]
        public void IsConnected_WithOpenStream_ReturnsTrue()
        {
            using var ms = new MemoryStream();
            var client = new FujiSphClient(ms);
            Assert.True(client.IsConnected);
        }

        [Fact]
        public void IsConnected_AfterDispose_ReturnsFalse()
        {
            var ms = new MemoryStream();
            var client = new FujiSphClient(ms);
            client.Dispose();
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void ReadInt16_BuildsReadFrameAndParsesResponse()
        {
            using var stream = new DuplexStream(BuildResponse(1, "RR", "1234"));
            using var client = new FujiSphClient(stream);

            var result = client.ReadInt16("D100");

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((short)0x1234, result.Content);
            Assert.Equal(BuildFrame(1, "RR", "0101000001"), stream.WrittenBytes);
        }

        [Fact]
        public void ReadUInt64_ReadsFourWords()
        {
            using var stream = new DuplexStream(BuildResponse(1, "RR", "1122334455667788"));
            using var client = new FujiSphClient(stream);

            var result = client.ReadUInt64("D100");

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x1122334455667788UL, result.Content);
            Assert.Equal(BuildFrame(1, "RR", "0101000004"), stream.WrittenBytes);
        }

        [Fact]
        public void ReadDouble_ReadsIeee754Bits()
        {
            using var stream = new DuplexStream(BuildResponse(1, "RR", "3FF8000000000000"));
            using var client = new FujiSphClient(stream);

            var result = client.ReadDouble("D100");

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(1.5, result.Content, 6);
        }

        [Fact]
        public void WriteUInt64_WritesFullSixteenHexDigits()
        {
            using var stream = new DuplexStream(BuildResponse(1, "WR", ""));
            using var client = new FujiSphClient(stream);

            var result = client.Write("D100", 0x1122334455667788UL);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(BuildFrame(1, "WR", "0101001122334455667788"), stream.WrittenBytes);
        }

        [Fact]
        public void WriteDouble_WritesIeee754Bits()
        {
            using var stream = new DuplexStream(BuildResponse(1, "WR", ""));
            using var client = new FujiSphClient(stream);

            var result = client.Write("D100", 1.5);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(BuildFrame(1, "WR", "0101003FF8000000000000"), stream.WrittenBytes);
        }

        [Fact]
        public void ReadBytes_TruncatedResponse_ReturnsFailure()
        {
            using var stream = new DuplexStream(BuildResponse(1, "RR", "AA"));
            using var client = new FujiSphClient(stream);

            var result = client.ReadBytes("D100", 2);

            Assert.False(result.IsSuccess);
            Assert.Contains("数据不足", result.Message);
        }

        [Fact]
        public void ReadInt16_InvalidBcc_ReturnsFailure()
        {
            byte[] response = BuildResponse(1, "RR", "1234");
            response[response.Length - 1] = response[response.Length - 1] == (byte)'0' ? (byte)'1' : (byte)'0';
            using var stream = new DuplexStream(response);
            using var client = new FujiSphClient(stream);

            var result = client.ReadInt16("D100");

            Assert.False(result.IsSuccess);
            Assert.Contains("BCC", result.Message);
        }

        [Fact]
        public void ReadInt16_PlcError_ReturnsFailure()
        {
            using var stream = new DuplexStream(BuildResponse(1, "FF", "01"));
            using var client = new FujiSphClient(stream);

            var result = client.ReadInt16("D100");

            Assert.False(result.IsSuccess);
            Assert.Contains("PLC 错误", result.Message);
        }

        [Fact]
        public void ConnectionPool_ReadWrite_ReusesPersistentConnection()
        {
            int port = GetFreeTcpPort();
            using var server = new FujiVirtualServer(port);
            server.Start();

            using var pool = new FujiSphConnectionPool("127.0.0.1", port, maxPoolSize: 1);

            var writeResult = pool.Write("D100", (short)1234);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = pool.ReadInt16("D100");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal((short)1234, readResult.Content);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsPacketEvents()
        {
            int port = GetFreeTcpPort();
            using var server = new FujiVirtualServer(port);
            server.SetDWord(10, 0x1234);
            server.Start();

            using var pool = new FujiSphConnectionPool("127.0.0.1", port, maxPoolSize: 1);
            int sentCount = 0;
            int receivedCount = 0;
            pool.OnMessageSent += (_, _) => Interlocked.Increment(ref sentCount);
            pool.OnMessageReceived += (_, _) => Interlocked.Increment(ref receivedCount);

            var result = pool.ReadUInt16("D10");

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((ushort)0x1234, result.Content);
            Assert.True(sentCount > 0);
            Assert.True(receivedCount > 0);
        }

        [Fact]
        public void ConnectionPool_BatchReadWrite()
        {
            int port = GetFreeTcpPort();
            using var server = new FujiVirtualServer(port);
            server.Start();

            using var pool = new FujiSphConnectionPool("127.0.0.1", port, maxPoolSize: 1);
            var items = new[]
            {
                new KeyValuePair<string, object>("D20", (short)111),
                new KeyValuePair<string, object>("D21", (ushort)222)
            };

            var writeResult = pool.BatchWrite(items);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = pool.BatchRead(new[] { "D20", "D21" });

            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal((short)111, readResult.Content["D20"]);
            Assert.Equal((short)222, readResult.Content["D21"]);
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
