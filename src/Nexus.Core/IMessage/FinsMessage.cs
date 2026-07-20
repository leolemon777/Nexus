// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

namespace Nexus.IMessage
{
    /// <summary>
    /// Omron FINS TCP 帧解析 — FINS/TCP 帧头 16 字节,包含命令/长度字段。
    /// 头部 [8..11] 大端 = FINS 数据长度(payload 字节数)。
    /// </summary>
    public sealed class FinsMessage : NetMessageBase
    {
        /// <inheritdoc />
        public override int ProtocolHeadBytesLength => 16;

        /// <inheritdoc />
        public override int GetContentLength(byte[] head)
        {
            if (head == null || head.Length < 16) return 0;
            // FINS/TCP header: magic "FINS"(4) + length(4) + command(4) + error code(4)
            // length 字段(位置 4-7,大端)是 FINS payload 长度。
            int len = (head[4] << 24) | (head[5] << 16) | (head[6] << 8) | head[7];
            return len - 8; // 减去后续的 command(4) + error code(4),它们已在头部 16 字节内
        }

        /// <inheritdoc />
        public override bool CheckHeadBytesLegal(byte[] head)
        {
            if (!base.CheckHeadBytesLegal(head)) return false;
            // 前 4 字节是 "FINS" = 0x46 0x49 0x4E 0x53。
            return head[0] == 0x46 && head[1] == 0x49 && head[2] == 0x4E && head[3] == 0x53;
        }
    }

    /// <summary>
    /// Omron FINS UDP 帧解析 — UDP 帧头 10 字节(无 FINS/TCP magic/length)。
    /// </summary>
    public sealed class FinsUdpMessage : NetMessageBase
    {
        /// <inheritdoc />
        public override int ProtocolHeadBytesLength => 10;

        /// <inheritdoc />
        public override int GetContentLength(byte[] head) => 0;

        /// <inheritdoc />
        public override bool CheckHeadBytesLegal(byte[] head)
        {
            if (!base.CheckHeadBytesLegal(head)) return false;
            // FINS/UDP ICF 字节常见值:0x80(命令)/0xC1(响应)。
            return head[0] == 0x80 || head[0] == 0xC1;
        }
    }
}
