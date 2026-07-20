// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

namespace Nexus.IMessage
{
    /// <summary>
    /// Modbus TCP 帧解析 — MBAP 头 7 字节 + 1 字节(功能码),后续 payload 由 MBAP 长度字段决定。
    /// MBAP 长度字段(2 字节大端,位置 4-5)涵盖剩余(包括 1 字节功能码)。
    /// </summary>
    public sealed class ModbusTcpMessage : NetMessageBase
    {
        /// <inheritdoc />
        /// <remarks>MBAP(7) + Function Code(1) = 8。</remarks>
        public override int ProtocolHeadBytesLength => 8;

        /// <inheritdoc />
        public override int GetContentLength(byte[] head)
        {
            // MBAP Length 字段(位置 4-5,大端)涵盖从 UnitId 开始的所有字节。
            // 头部已读 8 字节(MBAP 7 + FC 1),剩余 payload = Length - 2(UnitId 1 + FC 1)。
            // 但本接口的 GetContentLength 返回"头部之后的负载",故 = Length - 1(减去已含在头里的 FC)。
            if (head == null || head.Length < 8) return 0;
            int mbapLen = (head[4] << 8) | head[5];
            // mbapLen 包含 UnitId(1) + FC(1) + payload。
            // 头部已含 UnitId+FC = 2 字节(MBAP 7 含 UnitId,头部多 1 = FC)。
            return mbapLen - 2;
        }

        /// <inheritdoc />
        public override bool CheckHeadBytesLegal(byte[] head)
        {
            if (!base.CheckHeadBytesLegal(head)) return false;
            // Modbus Protocol Id 必须为 0(位置 2-3)。
            return head[2] == 0x00 && head[3] == 0x00;
        }
    }
}
