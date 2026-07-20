using System;
using System.Text;
using Nexus;
using Nexus.Mitsubishi.ASeries;
using Xunit;

namespace Nexus.Mitsubishi.ASeries.Tests
{
    /// <summary>
    /// Phase C-3 单元测试 — 验证 AnA/AnS/Q02AS ASCII 协议命令字符串构建与帧编码。
    /// 不验证实际收发(那需要硬件或模拟器,AnA 已停产)。
    /// </summary>
    public class MitsubishiASeriesClientTests
    {
        private sealed class FakePort : ISerialPort
        {
            public string PortName { get; set; } = "COM_ANA";
            public int BaudRate { get; set; } = 9600;
            public int DataBits { get; set; } = 8;
            public StopBits StopBits { get; set; } = StopBits.One;
            public Parity Parity { get; set; } = Parity.None;
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

        private static MitsubishiASeriesClient CreateClient()
        {
            var port = new FakePort();
            port.Open();
            return new MitsubishiASeriesClient(port, timeout: 1000);
        }

        // ── 命令字符串构建 ─────────────────────────

        [Fact]
        public void BuildReadWordCommand_ContainsAllFields()
        {
            var client = CreateClient();
            // StationNumber 默认 0x30 = "30",PcNumber 默认 "FF"。
            string cmd = client.BuildReadWordCommand("D", "000000", "000003");

            // 命令字符串格式: 站号(2) + PC号(2) + "WR" + 设备类型 + 起始地址(6) + 终止地址(6) + "*" + 校验和(2)
            Assert.StartsWith("30FFWR", cmd);
            Assert.Contains("D", cmd);
            Assert.Contains("000000", cmd);  // 起始地址
            Assert.Contains("000003", cmd);  // 终止地址
            Assert.Contains("*", cmd);
            // 校验和是 2 字符 hex,长度 = 2+2+2+1+6+6+1+2 = 22
            Assert.Equal(22, cmd.Length);
        }

        [Fact]
        public void BuildReadBitCommand_UsesBR()
        {
            var client = CreateClient();
            string cmd = client.BuildReadBitCommand("M", "000000", "000007");
            Assert.StartsWith("30FFBR", cmd);
            Assert.Contains("M", cmd);
        }

        [Fact]
        public void BuildWriteWordCommand_IncludesData()
        {
            var client = CreateClient();
            string cmd = client.BuildWriteWordCommand("D", "000000", "000001", "1234ABCD");
            Assert.StartsWith("30FFWW", cmd);
            Assert.Contains("1234ABCD", cmd);
            Assert.Contains("*", cmd);
        }

        [Fact]
        public void BuildWriteBitCommand_IncludesData()
        {
            var client = CreateClient();
            string cmd = client.BuildWriteBitCommand("M", "000000", "000003", "1010");
            Assert.StartsWith("30FFBW", cmd);
            Assert.Contains("1010", cmd);
        }

        [Fact]
        public void BuildCommand_CustomStationAndPc()
        {
            var client = CreateClient();
            client.StationNumber = 0x05;
            client.PcNumber = "00";

            string cmd = client.BuildReadWordCommand("D", "000000", "000001");
            Assert.StartsWith("0500WR", cmd);
        }

        [Fact]
        public void BuildCommand_NoChecksum_OmitsChecksum()
        {
            var client = CreateClient();
            client.AppendChecksum = false;

            string cmd = client.BuildReadWordCommand("D", "000000", "000001");
            // 长度 = 2+2+2+1+6+6+1 = 20(无校验和)
            Assert.Equal(20, cmd.Length);
            Assert.EndsWith("*", cmd);
        }

        [Fact]
        public void BuildCommand_ChecksumCorrect()
        {
            var client = CreateClient();
            client.StationNumber = 0x30;  // '0' '0' = 0x30+0x30 = 96
            // 全部默认: "30" + "FF" + "WR" + "D" + "000000" + "000001" + "*"
            string body = "30FFWRD000000000001*";
            int sum = 0;
            foreach (char c in body) sum += c;
            int expectedChecksum = sum & 0xFF;

            string cmd = client.BuildReadWordCommand("D", "000000", "000001");
            string actualChecksum = cmd.Substring(cmd.Length - 2);
            Assert.Equal(expectedChecksum.ToString("X2"), actualChecksum);
        }

        // ── 帧编码 ───────────────────────────────

        [Fact]
        public void EncodeCommandFrame_WrapsWithEnqAndCr()
        {
            byte[] frame = MitsubishiASeriesClient.EncodeCommandFrame("30FFWRD000000000001*AB");
            // ENQ + body + CR
            Assert.Equal(0x05, frame[0]);
            Assert.Equal(0x0D, frame[frame.Length - 1]);
            // body 字节
            string bodyText = Encoding.ASCII.GetString(frame, 1, frame.Length - 2);
            Assert.Equal("30FFWRD000000000001*AB", bodyText);
        }

        // ── 占位 API 行为 ───────────────────────

        [Fact]
        public void HighLevelApi_ReturnsFailed_WithGuidance()
        {
            var client = CreateClient();

            // ReadInt16 提示用 BuildReadWordCommand。
            var r1 = client.ReadInt16("D0");
            Assert.False(r1.IsSuccess);
            Assert.Contains("BuildReadWordCommand", r1.Message);

            // Write(bool) 提示用 BuildWriteBitCommand。
            var r2 = client.Write("M0", true);
            Assert.False(r2.IsSuccess);
            Assert.Contains("BuildWriteBitCommand", r2.Message);

            // Write(byte[]) 提示用 BuildWriteWordCommand。
            var r3 = client.Write("D0", new byte[] { 0 });
            Assert.False(r3.IsSuccess);
            Assert.Contains("BuildWriteWordCommand", r3.Message);
        }

        [Fact]
        public void Constructor_Defaults()
        {
            var client = CreateClient();
            Assert.Equal(0x30, client.StationNumber);
            Assert.Equal("FF", client.PcNumber);
            Assert.True(client.AppendChecksum);
        }
    }
}
