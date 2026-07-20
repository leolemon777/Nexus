// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

namespace Nexus.IMessage
{
    /// <summary>
    /// Mitsubishi MC3E (MELSEC QnA 3E Binary) 帧解析 — 11 字节头部。
    /// 头部 [7..10] 大端 = 后续数据长度(payload)。
    /// </summary>
    public sealed class MelsecQnA3EBinaryMessage : NetMessageBase
    {
        /// <inheritdoc />
        public override int ProtocolHeadBytesLength => 11;

        /// <inheritdoc />
        public override int GetContentLength(byte[] head)
        {
            if (head == null || head.Length < 11) return 0;
            int len = (head[7] << 8) | head[8];
            int len2 = (head[9] << 8) | head[10];
            // MC3E 头部 [7..8] 是"完成数据长度",[9..10] 是"监视定时器",后续是实际 payload。
            // 简化:payload = 完成数据长度 - 2(去掉监视定时器)。
            return len - 2;
        }
    }

    /// <summary>
    /// Mitsubishi A1E Binary 帧解析 — 2 字节头部(命令 + 子命令)。
    /// 实际长度由具体协议消息类型决定,本实现返回 0(只读头部)。
    /// </summary>
    public sealed class MelsecA1EBinaryMessage : NetMessageBase
    {
        /// <inheritdoc />
        public override int ProtocolHeadBytesLength => 2;

        /// <inheritdoc />
        public override int GetContentLength(byte[] head) => 0;
    }
}
