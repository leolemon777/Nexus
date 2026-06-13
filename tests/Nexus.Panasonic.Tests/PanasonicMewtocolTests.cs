using Xunit;
using Nexus.Panasonic;
using System;
using System.IO;
using System.Text;

namespace Nexus.Panasonic.Tests
{
    public class PanasonicMewtocolTests
    {
        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new PanasonicMewtocolClient(ms);
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Constructor_SetsDefaults()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new PanasonicMewtocolClient(ms);
            Assert.True(client.IsConnected); // MemoryStream is always readable+writeable
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new PanasonicMewtocolClient(ms);
            client.Dispose();
        }

        [Fact]
        public void BatchOperations_EmptyInput_ReturnsError()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new PanasonicMewtocolClient(ms);

            Assert.False(client.BatchRead(new string[0]).IsSuccess);
            Assert.False(client.RandomRead(new string[0]).IsSuccess);
            Assert.False(client.BatchWrite(System.Array.Empty<System.Collections.Generic.KeyValuePair<string, object>>()).IsSuccess);
        }

        [Fact]
        public void WriteUInt64_WritesFourRegisters()
        {
            var stream = new DuplexStream(BuildResponse("WD", ""));
            var client = new PanasonicMewtocolClient(stream);

            var result = client.Write("DT100", 0x1122334455667788UL);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(BuildFrame("WD", "D001001122334455667788"), stream.WrittenText);
        }

        [Fact]
        public void WriteDouble_WritesIeee754Bits()
        {
            var stream = new DuplexStream(BuildResponse("WD", ""));
            var client = new PanasonicMewtocolClient(stream);

            var result = client.Write("DT100", 1.5d);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(BuildFrame("WD", "D001003FF8000000000000"), stream.WrittenText);
        }

        [Fact]
        public void ReadUInt64_ReadsFourRegisters()
        {
            var stream = new DuplexStream(BuildResponse("RD", "1122334455667788"));
            var client = new PanasonicMewtocolClient(stream);

            var result = client.ReadUInt64("DT100");

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x1122334455667788UL, result.Content);
        }

        [Fact]
        public void WriteBytes_Null_ReturnsFailure()
        {
            var client = new PanasonicMewtocolClient(new MemoryStream());

            var result = client.Write("DT100", (byte[])null!);

            Assert.False(result.IsSuccess);
        }

        private static string BuildFrame(string command, string data)
        {
            string body = "01" + command + data;
            return "%" + body + ComputeBcc(body).ToString("X2") + "\r";
        }

        private static string BuildResponse(string command, string data)
            => BuildFrame(command, data);

        private static byte ComputeBcc(string data)
        {
            byte bcc = 0;
            foreach (char c in data)
                bcc ^= (byte)c;
            return bcc;
        }

        [Fact]
        public void ReadInt16_ReadsSingleRegister()
        {
            var stream = new DuplexStream(BuildResponse("RD", "0064"));
            var client = new PanasonicMewtocolClient(stream);
            var result = client.ReadInt16("DT100");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((short)100, result.Content);
        }

        [Fact]
        public void ReadInt32_ReadsTwoRegisters()
        {
            var stream = new DuplexStream(BuildResponse("RD", "00010000"));
            var client = new PanasonicMewtocolClient(stream);
            var result = client.ReadInt32("DT100");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x00010000, result.Content);
        }

        [Fact]
        public void ReadFloat_ReadsTwoRegisters()
        {
            // 3F80 = 1.0 in IEEE 754 half-word view → as float bytes
            var stream = new DuplexStream(BuildResponse("RD", "3F800000"));
            var client = new PanasonicMewtocolClient(stream);
            var result = client.ReadFloat("DT100");
            Assert.True(result.IsSuccess, result.Message);
        }

        [Fact]
        public void WriteInt16_SendsCorrectFrame()
        {
            var stream = new DuplexStream(BuildResponse("WD", ""));
            var client = new PanasonicMewtocolClient(stream);
            var result = client.Write("DT100", (short)100);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(BuildFrame("WD", "D001000064"), stream.WrittenText);
        }

        [Fact]
        public void WriteBool_SendsCorrectFrame()
        {
            var stream = new DuplexStream(BuildResponse("WD", ""));
            var client = new PanasonicMewtocolClient(stream);
            var result = client.Write("R100", true);
            Assert.True(result.IsSuccess, result.Message);
        }

        [Fact]
        public void ReadString_ReadsData()
        {
            // "48454C4C4F" = "HELLO"
            var stream = new DuplexStream(BuildResponse("RD", "48454C4C4F"));
            var client = new PanasonicMewtocolClient(stream);
            var result = client.ReadString("DT100", 5);
            Assert.True(result.IsSuccess, result.Message);
        }

        [Fact]
        public void BatchRead_NotConnectedStream_ReturnsError()
        {
            var client = new PanasonicMewtocolClient(new MemoryStream());
            var result = client.BatchRead(new[] { "DT100", "DT200" });
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Subscribe_Unsubscribe_DoesNotThrow()
        {
            using var ms = new MemoryStream();
            var client = new PanasonicMewtocolClient(ms);
            client.Subscribe("DT100", 1000, "Int16");
            client.Unsubscribe("DT100");
            client.StartSubscriptions();
            client.StopSubscriptions();
            client.Dispose();
        }

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

    public class PanasonicVirtualServerTests
    {
        [Fact]
        public void Constructor_SetsPort()
        {
            using var server = new PanasonicVirtualServer(19094);
            Assert.Equal(19094, server.Port);
            Assert.False(server.IsRunning);
        }

        [Fact]
        public void StartStop_DoesNotThrow()
        {
            using var server = new PanasonicVirtualServer(19095);
            server.Start();
            Assert.True(server.IsRunning);
            server.Stop();
            Assert.False(server.IsRunning);
        }

        [Fact]
        public void SetRegister_And_SetCoil()
        {
            using var server = new PanasonicVirtualServer(19096);
            server.SetRegister(0, 1234);
            server.SetCoil(0, true);
            server.SetCoil(1, false);
        }

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var server = new PanasonicVirtualServer(19097);
            server.Dispose();
            server.Dispose();
        }
    }
}
