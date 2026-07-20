using Nexus.IMessage;
using Xunit;

namespace Nexus.Core.Tests
{
    /// <summary>
    /// PR #B3 回归测试 — 验证每个 <see cref="INetMessage"/> 实现的头部识别、负载长度计算、合法性校验。
    /// 测试数据来自各协议公开规范(Modbus TCP MBAP / S7 TPKT / FINS TCP / MC3E Binary)。
    /// </summary>
    public class NetMessageTests
    {
        // ── Modbus TCP MBAP ─────────────────────────

        [Fact]
        public void ModbusTcp_HeadLength_Is8()
        {
            var msg = new ModbusTcpMessage();
            Assert.Equal(8, msg.ProtocolHeadBytesLength);
        }

        [Fact]
        public void ModbusTcp_ContentLength_FromMbapLengthField()
        {
            // MBAP: [txHi txLo protoHi protoLo lenHi lenLo unit fc]
            // len = 6 表示 unit+fc+payload = 6 字节,头部已含 unit+fc = 2,故 payload = 4
            byte[] head = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03 };
            var msg = new ModbusTcpMessage();
            Assert.Equal(4, msg.GetContentLength(head));
        }

        [Fact]
        public void ModbusTcp_CheckLegal_RejectsBadProtocolId()
        {
            var msg = new ModbusTcpMessage();
            // 协议 ID 不是 0 → 非法
            byte[] badHead = { 0x00, 0x01, 0x00, 0x01, 0x00, 0x06, 0x01, 0x03 };
            Assert.False(msg.CheckHeadBytesLegal(badHead));

            byte[] goodHead = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03 };
            Assert.True(msg.CheckHeadBytesLegal(goodHead));
        }

        // ── S7 TPKT ─────────────────────────────────

        [Fact]
        public void S7_HeadLength_Is4()
        {
            Assert.Equal(4, new S7Message().ProtocolHeadBytesLength);
        }

        [Fact]
        public void S7_ContentLength_FromTpktLengthField()
        {
            // TPKT: [version=0x03 reserved=0x00 lenHi lenLo]
            // len=0x1F=31,头部 4 字节,故 payload=27
            byte[] head = { 0x03, 0x00, 0x00, 0x1F };
            Assert.Equal(27, new S7Message().GetContentLength(head));
        }

        [Fact]
        public void S7_CheckLegal_RejectsBadVersion()
        {
            var msg = new S7Message();
            Assert.False(msg.CheckHeadBytesLegal(new byte[] { 0x04, 0x00, 0x00, 0x1F }));
            Assert.True(msg.CheckHeadBytesLegal(new byte[] { 0x03, 0x00, 0x00, 0x1F }));
        }

        // ── FINS TCP ───────────────────────────────

        [Fact]
        public void Fins_HeadLength_Is16()
        {
            Assert.Equal(16, new FinsMessage().ProtocolHeadBytesLength);
        }

        [Fact]
        public void Fins_ContentLength_FromLengthField()
        {
            // FINS/TCP header: [FINS(4) length(4) command(4) error(4)]
            // length = 12 表示 command+error+payload,头部已含 command+error=8,故 payload=4
            byte[] head = {
                0x46, 0x49, 0x4E, 0x53,             // "FINS"
                0x00, 0x00, 0x00, 0x0C,             // length = 12
                0x00, 0x00, 0x00, 0x02,             // command
                0x00, 0x00, 0x00, 0x00              // error
            };
            Assert.Equal(4, new FinsMessage().GetContentLength(head));
        }

        [Fact]
        public void Fins_CheckLegal_RequiresMagic()
        {
            var msg = new FinsMessage();
            byte[] badHead = new byte[16];
            Assert.False(msg.CheckHeadBytesLegal(badHead));

            byte[] goodHead = {
                0x46, 0x49, 0x4E, 0x53,
                0x00, 0x00, 0x00, 0x0C,
                0x00, 0x00, 0x00, 0x02,
                0x00, 0x00, 0x00, 0x00
            };
            Assert.True(msg.CheckHeadBytesLegal(goodHead));
        }

        // ── MC3E Binary ───────────────────────────

        [Fact]
        public void Melsec3E_HeadLength_Is11()
        {
            Assert.Equal(11, new MelsecQnA3EBinaryMessage().ProtocolHeadBytesLength);
        }

        [Fact]
        public void Melsec3E_ContentLength_FromLengthField()
        {
            // MC3E Binary 头部 11 字节:
            // [0..1] subheader 0x5400
            // [2..3] network no
            // [4] PC no
            // [5..6] request dest module no
            // [7..8] 完成数据长度(大端)
            // [9..10] CPU 监视定时器
            // 后续 payload = 完成数据长度 - 2(去掉监视定时器)
            byte[] head = { 0x54, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x12, 0x04, 0x00 };
            // 完成数据长度 = 0x0012 = 18,payload = 16
            Assert.Equal(16, new MelsecQnA3EBinaryMessage().GetContentLength(head));
        }

        // ── A1E Binary ────────────────────────────

        [Fact]
        public void MelsecA1E_HeadLength_Is2()
        {
            Assert.Equal(2, new MelsecA1EBinaryMessage().ProtocolHeadBytesLength);
        }

        [Fact]
        public void MelsecA1E_ContentLength_Zero()
        {
            Assert.Equal(0, new MelsecA1EBinaryMessage().GetContentLength(new byte[] { 0x01, 0x02 }));
        }

        // ── FINS UDP ──────────────────────────────

        [Fact]
        public void FinsUdp_HeadLength_Is10()
        {
            Assert.Equal(10, new FinsUdpMessage().ProtocolHeadBytesLength);
        }

        [Fact]
        public void FinsUdp_CheckLegal_AcceptsCommandAndResponse()
        {
            var msg = new FinsUdpMessage();
            byte[] cmd = new byte[10]; cmd[0] = 0x80;
            byte[] resp = new byte[10]; resp[0] = 0xC1;
            byte[] bad = new byte[10]; bad[0] = 0x42;
            Assert.True(msg.CheckHeadBytesLegal(cmd));
            Assert.True(msg.CheckHeadBytesLegal(resp));
            Assert.False(msg.CheckHeadBytesLegal(bad));
        }

        // ── NetMessageBase 默认行为 ─────────────────

        [Fact]
        public void NetMessageBase_DefaultCheckLegal_NullOrShortRejected()
        {
            var msg = new MelsecA1EBinaryMessage();
            Assert.False(msg.CheckHeadBytesLegal(null));
            Assert.False(msg.CheckHeadBytesLegal(new byte[1]));  // 短于 headLen=2
            Assert.True(msg.CheckHeadBytesLegal(new byte[] { 1, 2 }));
        }
    }
}
