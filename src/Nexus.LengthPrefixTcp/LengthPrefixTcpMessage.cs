// Reference protocol demonstrating Phase B new bases.
// Protocol format (trivial 4-byte length prefix):
//   Request:  [length:4 BE][payload:N]
//   Response: [length:4 BE][payload:N]
// Server echoes the payload back unchanged.

using Nexus.IMessage;

namespace Nexus.LengthPrefixTcp
{
    /// <summary>
    /// 4 字节大端长度前缀帧解析器 — 帧头 4 字节(长度 N),后跟 N 字节 payload。
    /// </summary>
    public sealed class LengthPrefixTcpMessage : NetMessageBase
    {
        /// <inheritdoc />
        public override int ProtocolHeadBytesLength => 4;

        /// <inheritdoc />
        public override int GetContentLength(byte[] head)
        {
            if (head == null || head.Length < 4) return 0;
            return (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];
        }
    }
}
