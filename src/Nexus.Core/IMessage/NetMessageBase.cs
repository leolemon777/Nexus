// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

namespace Nexus.IMessage
{
    /// <summary>
    /// <see cref="INetMessage"/> 抽象基类 — 提供常见默认实现。具体协议子类只需重写
    /// <see cref="INetMessage.ProtocolHeadBytesLength"/> 和 <see cref="INetMessage.GetContentLength"/>。
    /// </summary>
    public abstract class NetMessageBase : INetMessage
    {
        /// <inheritdoc />
        public abstract int ProtocolHeadBytesLength { get; }

        /// <inheritdoc />
        public abstract int GetContentLength(byte[] head);

        /// <inheritdoc />
        /// <remarks>默认实现:头部不为 null 且长度匹配 <see cref="ProtocolHeadBytesLength"/> 即视为合法。</remarks>
        public virtual bool CheckHeadBytesLegal(byte[] head)
        {
            return head != null && head.Length >= ProtocolHeadBytesLength;
        }
    }
}
