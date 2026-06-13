using Xunit;
using Nexus.Omron;
using System;
using System.IO;

namespace Nexus.Omron.Tests
{
    public class FinsSerialClientTests
    {
        // ═══════════════════════════════════════════
        //  BuildReadCommand — 报文构建
        // ═══════════════════════════════════════════

        [Fact]
        public void BuildReadCommand_DM100()
        {
            using (var fake = new System.IO.MemoryStream())
            {
                var client = new FinsSerialClient(fake);
                byte[] cmd = client.BuildReadCommand(FinsMemoryArea.DM, 100, 0, 10);
                // 应包含 12 字节帧头 + 6 字节数据
                Assert.Equal(18, cmd.Length);
                // 最后 6 字节: area + addressHi + addressLo + bitOff + countHi + countLo
                Assert.Equal((byte)FinsMemoryArea.DM, cmd[12]);
                Assert.Equal(0, cmd[13]);   // addrHi
                Assert.Equal(100, cmd[14]); // addrLo
                Assert.Equal(0, cmd[15]);   // bitOffset
                Assert.Equal(0, cmd[16]);   // countHi
                Assert.Equal(10, cmd[17]);  // countLo
            }
        }

        [Fact]
        public void BuildReadCommand_CIO50_Bit3()
        {
            using (var fake = new System.IO.MemoryStream())
            {
                var client = new FinsSerialClient(fake);
                byte[] cmd = client.BuildReadCommand(FinsMemoryArea.CIO, 50, 3, 1);
                Assert.Equal(18, cmd.Length);
                Assert.Equal((byte)FinsMemoryArea.CIO, cmd[12]);
                Assert.Equal(3, cmd[15]); // bitOffset
            }
        }

        [Fact]
        public void BuildWriteCommand_DM200()
        {
            using (var fake = new System.IO.MemoryStream())
            {
                var client = new FinsSerialClient(fake);
                byte[] data = new byte[] { 0x00, 0x64 }; // 100
                byte[] cmd = client.BuildWriteCommand(FinsMemoryArea.DM, 200, 0, data);
                // 12 字节帧头 + 6 字节地址 + 2 字节数据
                Assert.Equal(20, cmd.Length);
                Assert.Equal((byte)FinsMemoryArea.DM, cmd[12]);
                Assert.Equal(200, cmd[14]); // addrLo
                Assert.Equal(0, cmd[16]);   // wordCount Hi
                Assert.Equal(1, cmd[17]);   // wordCount Lo (2 bytes = 1 word)
            }
        }

        [Fact]
        public void FinsSerialClient_IsConnected()
        {
            using (var stream = new System.IO.MemoryStream())
            {
                var client = new FinsSerialClient(stream);
                Assert.True(client.IsConnected);
            }
        }

        [Fact]
        public void FinsSerialClient_DefaultProperties()
        {
            using (var stream = new System.IO.MemoryStream())
            {
                var client = new FinsSerialClient(stream, destNode: 5);
                Assert.Equal(5, client.DestNode);
                Assert.Equal(0, client.DestNetwork);
                Assert.Equal(0, client.DestUnit);
            }
        }

        [Fact]
        public void FinsSerialClient_ConnectReturnsSuccess()
        {
            using (var stream = new System.IO.MemoryStream())
            {
                var client = new FinsSerialClient(stream);
                var result = client.Connect();
                Assert.True(result.IsSuccess);
            }
        }

        [Fact]
        public void FinsSerialClient_DisposeDoesNotThrow()
        {
            var stream = new System.IO.MemoryStream();
            var client = new FinsSerialClient(stream);
            client.Dispose();
        }

        [Fact]
        public void WriteUInt64_WritesEightPayloadBytes()
        {
            var stream = new DuplexStream(BuildSuccessResponse());
            var client = new FinsSerialClient(stream);

            var result = client.Write("D100", 0x1122334455667788UL);

            Assert.True(result.IsSuccess, result.Message);
            byte[] written = stream.WrittenBytes;
            Assert.Equal(26, written.Length);
            Assert.Equal((byte)FinsMemoryArea.DM, written[12]);
            Assert.Equal(0, written[16]);
            Assert.Equal(4, written[17]);
            Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 }, written[18..26]);
        }

        [Fact]
        public void WriteDouble_WritesIeee754Bits()
        {
            var stream = new DuplexStream(BuildSuccessResponse());
            var client = new FinsSerialClient(stream);

            var result = client.Write("D100", 1.5d);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(new byte[] { 0x3F, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, stream.WrittenBytes[18..26]);
        }

        [Fact]
        public void WriteBytes_Null_ReturnsFailure()
        {
            var client = new FinsSerialClient(new MemoryStream());

            var result = client.Write("D100", (byte[])null!);

            Assert.False(result.IsSuccess);
        }

        private static byte[] BuildSuccessResponse()
        {
            var response = new byte[14];
            response[0] = 0xC0;
            response[10] = 0x01;
            response[11] = 0x02;
            return response;
        }

        private sealed class DuplexStream : Stream
        {
            private readonly byte[] _response;
            private int _readOffset;
            private readonly MemoryStream _written = new MemoryStream();

            public DuplexStream(byte[] response)
            {
                _response = response;
            }

            public byte[] WrittenBytes => _written.ToArray();

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

    public class OmronModelTests
    {
        [Fact]
        public void OmronModel_AllDefined()
        {
            Assert.True(Enum.IsDefined(typeof(OmronModel), OmronModel.CJ2M));
            Assert.True(Enum.IsDefined(typeof(OmronModel), OmronModel.CP1H));
            Assert.True(Enum.IsDefined(typeof(OmronModel), OmronModel.NJ501));
            Assert.True(Enum.IsDefined(typeof(OmronModel), OmronModel.NX1P2));
            Assert.True(Enum.IsDefined(typeof(OmronModel), OmronModel.NX102));
            Assert.True(Enum.IsDefined(typeof(OmronModel), OmronModel.CS1G));
        }

        [Fact]
        public void FinsConstants_DefaultValues()
        {
            Assert.Equal(9600, FinsConstants.DefaultTcpPort);
            Assert.Equal(9600, FinsConstants.DefaultUdpPort);
            Assert.Equal(10, FinsConstants.FinsHeaderLength);
            Assert.Equal(500, FinsConstants.MaxReadWords);
            Assert.Equal(500, FinsConstants.MaxWriteWords);
        }

        [Fact]
        public void FinsMemoryArea_Values()
        {
            Assert.Equal(0xB0, (byte)FinsMemoryArea.CIO);
            Assert.Equal(0xB1, (byte)FinsMemoryArea.WR);
            Assert.Equal(0xB2, (byte)FinsMemoryArea.HR);
            Assert.Equal(0xB3, (byte)FinsMemoryArea.AR);
            Assert.Equal(0x82, (byte)FinsMemoryArea.DM);
            Assert.Equal(0x98, (byte)FinsMemoryArea.EM);
            Assert.Equal(0x91, (byte)FinsMemoryArea.TimerPV);
            Assert.Equal(0xA1, (byte)FinsMemoryArea.CounterPV);
        }

        [Fact]
        public void FinsDiscoveredDevice_Defaults()
        {
            var device = new FinsDiscoveredDevice();
            Assert.Equal(string.Empty, device.ControllerModel);
            Assert.Equal(0, device.NetworkAddress);
            Assert.Equal(0, device.NodeNumber);
            Assert.Equal(0, device.UnitNumber);
        }
    }
}
