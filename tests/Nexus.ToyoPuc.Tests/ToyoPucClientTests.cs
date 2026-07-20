using System;
using Nexus;
using Nexus.ToyoPuc;
using Xunit;

namespace Nexus.ToyoPuc.Tests
{
    public class ToyoPucClientTests
    {
        // ── 命令构造(纯函数)──────────────

        [Fact]
        public void BuildReadWordCommand_NoPrg_FormatCorrect()
        {
            byte[] cmd = ToyoPucClient.BuildReadWordCommand(0x1234, 2);
            Assert.Equal(0x1C, cmd[0]);
            Assert.Equal(0x34, cmd[1]);  // addrLo
            Assert.Equal(0x12, cmd[2]);  // addrHi
            Assert.Equal(2, cmd[3]);     // lenLo
            Assert.Equal(0, cmd[4]);     // lenHi
        }

        [Fact]
        public void BuildReadWordCommand_WithPrg_FormatCorrect()
        {
            byte[] cmd = ToyoPucClient.BuildReadWordCommandWithPrg(3, 0x00FF, 1);
            Assert.Equal(0x94, cmd[0]);
            Assert.Equal(3, cmd[1]);     // PRG
            Assert.Equal(0xFF, cmd[2]);  // addrLo
            Assert.Equal(0x00, cmd[3]);  // addrHi
            Assert.Equal(1, cmd[4]);     // lenLo
        }

        [Fact]
        public void BuildWriteWordCommand_FormatCorrect()
        {
            byte[] cmd = ToyoPucClient.BuildWriteWordCommand(0x10, new byte[] { 0xAA, 0xBB });
            Assert.Equal(0x1D, cmd[0]);
            Assert.Equal(0x10, cmd[1]);
            Assert.Equal(0x00, cmd[2]);
            Assert.Equal(0xAA, cmd[3]);
            Assert.Equal(0xBB, cmd[4]);
        }

        [Fact]
        public void PackFrame_Adds4ByteHeader()
        {
            byte[] cmd = new byte[] { 0x1C, 0x34, 0x12, 0x02, 0x00 };
            byte[] frame = ToyoPucClient.PackFrame(cmd);
            Assert.Equal(0, frame[0]);
            Assert.Equal(0, frame[1]);
            Assert.Equal(5, frame[2]);  // lenLo = cmd.Length
            Assert.Equal(0, frame[3]);  // lenHi
            Assert.Equal(0x1C, frame[4]);
            Assert.Equal(9, frame.Length);
        }

        // ── 响应解析 ───────────────────────

        [Fact]
        public void ParseResponse_Success_ReturnsData()
        {
            byte[] resp = { 0x80, 0x00, 0x1C, 0x00, 0x00, 0x12, 0x34 };
            var r = ToyoPucClient.ParseResponse(resp);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(new byte[] { 0x12, 0x34 }, r.Content);
        }

        [Fact]
        public void ParseResponse_BadFT_ReturnsFailed()
        {
            byte[] resp = { 0x81, 0x00, 0, 0 };
            var r = ToyoPucClient.ParseResponse(resp);
            Assert.False(r.IsSuccess);
            Assert.Contains("FT", r.Message);
        }

        [Fact]
        public void ParseResponse_ErrorStatus_ReturnsFailedWithCode()
        {
            byte[] resp = { 0x80, 0x11, 0, 0 };  // 4-byte error frame
            var r = ToyoPucClient.ParseResponse(resp);
            Assert.False(r.IsSuccess);
            Assert.Contains("0x11", r.Message);
        }

        [Fact]
        public void ParseResponse_TooShort_ReturnsFailed()
        {
            var r = ToyoPucClient.ParseResponse(new byte[] { 0x80 });
            Assert.False(r.IsSuccess);
        }

        [Fact]
        public void ParseResponse_EmptyData_Success()
        {
            byte[] resp = { 0x80, 0x00, 0x1C, 0x00, 0x00 };  // No data after header
            var r = ToyoPucClient.ParseResponse(resp);
            Assert.True(r.IsSuccess);
            Assert.Empty(r.Content);
        }

        // ── 构造 ───────────────────────────

        [Fact]
        public void Constructor_StoresIpAndPort()
        {
            var client = new ToyoPucClient("192.168.1.50", port: 10001, timeout: 3000);
            string s = client.ToString();
            Assert.Contains("192.168.1.50", s);
            Assert.Contains("10001", s);
        }
    }
}
