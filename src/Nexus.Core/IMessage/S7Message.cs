// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

namespace Nexus.IMessage
{
    /// <summary>
    /// Siemens S7 帧解析 — TPKT 头部 4 字节(版本 + reserved + 长度大端)。
    /// 整帧长度由 TPKT 长度字段(位置 2-3)给出,故 payload = TPKT Length - 4。
    /// </summary>
    public sealed class S7Message : NetMessageBase
    {
        /// <inheritdoc />
        public override int ProtocolHeadBytesLength => 4;

        /// <inheritdoc />
        public override int GetContentLength(byte[] head)
        {
            if (head == null || head.Length < 4) return 0;
            int tpktLen = (head[2] << 8) | head[3];
            return tpktLen - 4;
        }

        /// <inheritdoc />
        public override bool CheckHeadBytesLegal(byte[] head)
        {
            if (!base.CheckHeadBytesLegal(head)) return false;
            // TPKT 版本必须为 3。
            return head[0] == 0x03;
        }
    }
}
