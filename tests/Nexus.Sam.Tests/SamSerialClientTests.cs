using System;
using Nexus.Sam;
using Xunit;

namespace Nexus.Sam.Tests
{
    public class SamSerialClientTests
    {
        // ── PackToSamCommand(纯函数)───────────

        [Fact]
        public void PackToSamCommand_HeaderCorrect()
        {
            byte[] cmd = new byte[] { 0x20, 0x01 };
            byte[] frame = SamSerialClient.PackToSamCommand(cmd);
            Assert.Equal(0xAA, frame[0]);
            Assert.Equal(0xAA, frame[1]);
            Assert.Equal(0xAA, frame[2]);
            Assert.Equal(0x96, frame[3]);
            Assert.Equal(0x69, frame[4]);
            // 长度 = frame.Length - 7 = 3 (lenHi=0, lenLo=3)
            Assert.Equal(0, frame[5]);
            Assert.Equal(3, frame[6]);
            // 命令在 [7..8]
            Assert.Equal(0x20, frame[7]);
            Assert.Equal(0x01, frame[8]);
            Assert.Equal(10, frame.Length);  // 8 + 2 cmd = 10
        }

        [Fact]
        public void PackToSamCommand_XorValid()
        {
            byte[] cmd = new byte[] { 0x30, 0x01 };
            byte[] frame = SamSerialClient.PackToSamCommand(cmd);
            // 重算 XOR 验证。
            int xor = 0;
            for (int i = 5; i < frame.Length - 1; i++)
                xor ^= frame[i];
            Assert.Equal((byte)xor, frame[frame.Length - 1]);
        }

        [Fact]
        public void BuildReadCommand_Format()
        {
            byte[] cmd = SamSerialClient.BuildReadCommand(0x20, 0x01, new byte[] { 0xAB });
            Assert.Equal(0x20, cmd[0]);
            Assert.Equal(0x01, cmd[1]);
            Assert.Equal(0xAB, cmd[2]);
            Assert.Equal(3, cmd.Length);
        }

        [Fact]
        public void BuildReadCommand_NullData()
        {
            byte[] cmd = SamSerialClient.BuildReadCommand(0x20, 0x01, null);
            Assert.Equal(2, cmd.Length);
        }

        // ── CheckResponse ─────────────────────

        [Fact]
        public void CheckResponse_ValidFrame_Success()
        {
            byte[] cmd = new byte[] { 0x20, 0x01 };
            byte[] frame = SamSerialClient.PackToSamCommand(cmd);
            var r = SamSerialClient.CheckResponse(frame);
            Assert.True(r.IsSuccess, r.Message);
        }

        [Fact]
        public void CheckResponse_BadHeader_Failed()
        {
            byte[] bad = new byte[] { 0xBB, 0xAA, 0xAA, 0x96, 0x69, 0, 3, 0x20, 0x01, 0 };
            var r = SamSerialClient.CheckResponse(bad);
            Assert.False(r.IsSuccess);
            Assert.Contains("header", r.Message);
        }

        [Fact]
        public void CheckResponse_TooShort_Failed()
        {
            var r = SamSerialClient.CheckResponse(new byte[] { 1, 2, 3 });
            Assert.False(r.IsSuccess);
        }

        [Fact]
        public void CheckResponse_NullInput_Failed()
        {
            var r = SamSerialClient.CheckResponse(null!);
            Assert.False(r.IsSuccess);
        }

        // ── GetErrorDescription ───────────────

        [Theory]
        [InlineData((byte)0x90, "OK")]
        [InlineData((byte)0x91, "length")]
        [InlineData((byte)0xA4, "read card")]
        [InlineData((byte)0xFF, "Unknown")]
        public void GetErrorDescription_KnownCodes(byte code, string expectedSubstring)
        {
            string desc = SamSerialClient.GetErrorDescription(code);
            Assert.Contains(expectedSubstring, desc);
        }

        // ── IdentityCard ──────────────────────

        [Fact]
        public void IdentityCard_Parse_ShortData_ReturnsEmpty()
        {
            var card = IdentityCard.Parse(new byte[10]);
            Assert.Equal(string.Empty, card.Name);
        }

        [Fact]
        public void IdentityCard_Parse_NullData_ReturnsEmpty()
        {
            var card = IdentityCard.Parse(null!);
            Assert.Equal(string.Empty, card.Name);
        }

        [Fact]
        public void IdentityCard_ToString_ContainsName()
        {
            var card = new IdentityCard { Name = "张三", IdNumber = "110101199001011234" };
            Assert.Contains("张三", card.ToString());
            Assert.Contains("110101", card.ToString());
        }

        // ── 构造 ──────────────────────────────

        [Fact]
        public void Constructor_DoesNotThrow()
        {
            var port = new FakePort();
            var client = new SamSerialClient(port);
            Assert.Contains("SamSerial", client.ToString());
        }

        private sealed class FakePort : Nexus.ISerialPort
        {
            public string PortName { get; set; } = "COM_SAM";
            public int BaudRate { get; set; } = 115200;
            public int DataBits { get; set; } = 8;
            public Nexus.StopBits StopBits { get; set; } = Nexus.StopBits.One;
            public Nexus.Parity Parity { get; set; } = Nexus.Parity.None;
            public int ReadTimeout { get; set; } = 1000;
            public int WriteTimeout { get; set; } = 1000;
            public bool IsOpen { get; private set; }
            public bool DtrEnable { get; set; }
            public bool RtsEnable { get; set; }
            public void Open() { IsOpen = true; }
            public void Close() { IsOpen = false; }
            public int Read(byte[] buffer, int offset, int count) => 0;
            public void Write(byte[] buffer, int offset, int count) { }
            public void Dispose() => Close();
        }
    }
}
