using Xunit;
using Nexus.Delta;

namespace Nexus.Delta.Tests
{
    public class DeltaDvpTests
    {
        private sealed class DuplexStream : System.IO.Stream
        {
            private readonly System.Collections.Generic.Queue<byte> _reads = new System.Collections.Generic.Queue<byte>();

            public DuplexStream(params byte[] reads)
            {
                foreach (byte b in reads)
                    _reads.Enqueue(b);
            }

            public byte[] WrittenBytes => _written.ToArray();

            private readonly System.IO.MemoryStream _written = new System.IO.MemoryStream();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new System.NotSupportedException();
            public override long Position { get => throw new System.NotSupportedException(); set => throw new System.NotSupportedException(); }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_reads.Count == 0) return 0;
                int read = 0;
                while (read < count && _reads.Count > 0)
                    buffer[offset + read++] = _reads.Dequeue();
                return read;
            }

            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new System.NotSupportedException();
            public override void SetLength(long value) => throw new System.NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);
        }

        private static byte[] BuildRtuFrame(params byte[] stationAndPdu)
        {
            byte[] frame = new byte[stationAndPdu.Length + 2];
            System.Buffer.BlockCopy(stationAndPdu, 0, frame, 0, stationAndPdu.Length);
            ushort crc = Crc16(frame, 0, stationAndPdu.Length);
            frame[frame.Length - 2] = (byte)(crc & 0xFF);
            frame[frame.Length - 1] = (byte)(crc >> 8);
            return frame;
        }

        private static byte[] BuildReadResponse(byte station, byte fc, params byte[] data)
        {
            byte[] pdu = new byte[3 + data.Length];
            pdu[0] = station;
            pdu[1] = fc;
            pdu[2] = (byte)data.Length;
            System.Buffer.BlockCopy(data, 0, pdu, 3, data.Length);
            return BuildRtuFrame(pdu);
        }

        private static byte[] BuildWriteResponse(byte station, byte fc, ushort address, ushort countOrValue)
            => BuildRtuFrame(station, fc, (byte)(address >> 8), (byte)address, (byte)(countOrValue >> 8), (byte)countOrValue);

        private static ushort Crc16(byte[] data, int offset, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                    crc = (crc & 0x0001) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
            return crc;
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Constructor_SetsDefaults()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            Assert.True(client.IsConnected); // MemoryStream is always readable+writeable
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            client.Dispose();
        }

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            client.Dispose();
            client.Dispose();
        }

        [Fact]
        public void Constructor_NullStream_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new DeltaDvpClient(null!));
        }

        [Fact]
        public void Constructor_WithStation_SetsStation()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms, station: 5);
            Assert.Equal((byte)5, client.Station);
        }

        [Fact]
        public void Station_CanBeSet()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            client.Station = 3;
            Assert.Equal((byte)3, client.Station);
        }

        [Fact]
        public void Timeout_CanBeSet()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            client.Timeout = 10000;
            Assert.Equal(10000, client.Timeout);
        }

        [Fact]
        public void IsConnected_WithOpenStream_ReturnsTrue()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            Assert.True(client.IsConnected);
        }

        [Fact]
        public void IsConnected_AfterDispose_ReturnsFalse()
        {
            var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            client.Dispose();
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void ReadUInt64_ReadsFourRegistersAndBuildsRtuFrame()
        {
            using var stream = new DuplexStream(BuildReadResponse(5, 0x03, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88));
            using var client = new DeltaDvpClient(stream, station: 5);

            var result = client.ReadUInt64("D100");

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x1122334455667788UL, result.Content);
            Assert.Equal(BuildRtuFrame(5, 0x03, 0x10, 0x64, 0x00, 0x04), stream.WrittenBytes);
        }

        [Fact]
        public void WriteUInt64_WritesFourRegistersWithoutTruncation()
        {
            using var stream = new DuplexStream(BuildWriteResponse(1, 0x10, 0x1064, 4));
            using var client = new DeltaDvpClient(stream);

            var result = client.Write("D100", 0x1122334455667788UL);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(BuildRtuFrame(1, 0x10, 0x10, 0x64, 0x00, 0x04, 0x08, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88), stream.WrittenBytes);
        }

        [Fact]
        public void WriteInt32_WritesTwoRegistersWithoutPduLengthError()
        {
            using var stream = new DuplexStream(BuildWriteResponse(1, 0x10, 0x1064, 2));
            using var client = new DeltaDvpClient(stream);

            var result = client.Write("D100", 0x11223344);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(BuildRtuFrame(1, 0x10, 0x10, 0x64, 0x00, 0x02, 0x04, 0x11, 0x22, 0x33, 0x44), stream.WrittenBytes);
        }

        [Fact]
        public void ReadDouble_ReadsIeee754Bits()
        {
            using var stream = new DuplexStream(BuildReadResponse(1, 0x03, 0x3F, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00));
            using var client = new DeltaDvpClient(stream);

            var result = client.ReadDouble("D100");

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(1.5d, result.Content);
        }

        [Fact]
        public void WriteDouble_WritesIeee754Bits()
        {
            using var stream = new DuplexStream(BuildWriteResponse(1, 0x10, 0x1064, 4));
            using var client = new DeltaDvpClient(stream);

            var result = client.Write("D100", 1.5d);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(BuildRtuFrame(1, 0x10, 0x10, 0x64, 0x00, 0x04, 0x08, 0x3F, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00), stream.WrittenBytes);
        }

        [Fact]
        public void ReadBytes_TruncatedPayload_ReturnsFailure()
        {
            using var stream = new DuplexStream(BuildReadResponse(1, 0x03, 0xAA));
            using var client = new DeltaDvpClient(stream);

            var result = client.ReadBytes("D20", 2);

            Assert.False(result.IsSuccess);
            Assert.Contains("数据不足", result.Message);
        }

        [Fact]
        public void ReadInt16_BadCrc_ReturnsFailure()
        {
            byte[] response = BuildReadResponse(1, 0x03, 0x12, 0x34);
            response[response.Length - 1] ^= 0xFF;
            using var stream = new DuplexStream(response);
            using var client = new DeltaDvpClient(stream);

            var result = client.ReadInt16("D100");

            Assert.False(result.IsSuccess);
            Assert.Contains("CRC", result.Message);
        }

        [Fact]
        public void ReadInt16_StationMismatch_ReturnsFailure()
        {
            using var stream = new DuplexStream(BuildReadResponse(2, 0x03, 0x12, 0x34));
            using var client = new DeltaDvpClient(stream, station: 1);

            var result = client.ReadInt16("D100");

            Assert.False(result.IsSuccess);
            Assert.Contains("站号", result.Message);
        }

        [Fact]
        public void ReadInt16_ExceptionResponsePreservesExceptionCode()
        {
            using var stream = new DuplexStream(BuildRtuFrame(1, 0x83, 0x02));
            using var client = new DeltaDvpClient(stream);

            var result = client.ReadInt16("D100");

            Assert.False(result.IsSuccess);
            Assert.Equal(0x02, result.ErrorCode);
            Assert.Contains("0x02", result.Message);
        }

        [Fact]
        public void WriteBool_ReadOnlyInput_ReturnsFailureBeforeSending()
        {
            using var stream = new DuplexStream();
            using var client = new DeltaDvpClient(stream);

            var result = client.Write("X0", true);

            Assert.False(result.IsSuccess);
            Assert.Contains("只读", result.Message);
            Assert.Empty(stream.WrittenBytes);
        }

        [Fact]
        public void ReadBool_StepRelayUsesSharedAddressParser()
        {
            using var stream = new DuplexStream(BuildReadResponse(1, 0x01, 0x01));
            using var client = new DeltaDvpClient(stream);

            var result = client.ReadBool("S20");

            Assert.True(result.IsSuccess, result.Message);
            Assert.True(result.Content);
            Assert.Equal(BuildRtuFrame(1, 0x01, 0x10, 0x14, 0x00, 0x01), stream.WrittenBytes);
        }

        [Fact]
        public void WriteBytes_Null_ReturnsFailure()
        {
            using var stream = new DuplexStream();
            using var client = new DeltaDvpClient(stream);

            var result = client.Write("D100", (byte[])null!);

            Assert.False(result.IsSuccess);
            Assert.Contains("不能为空", result.Message);
        }
    }
}
