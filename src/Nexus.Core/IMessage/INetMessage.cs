// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.
// Rewritten for Nexus: lean frame-abstraction. Decouples "how to detect end of frame"
// from transport (Pipe) and protocol (DeviceCommunication).

using System;

namespace Nexus.IMessage
{
    /// <summary>
    /// 协议帧解析抽象 — 每个工业协议(S7/Modbus/MC3E/...)实现一个,告诉
    /// <see cref="Nexus.Pipe.CommunicationPipe"/> 如何从字节流识别一个完整帧。
    /// </summary>
    /// <remarks>
    /// <b>核心契约</b>:
    /// <list type="bullet">
    ///   <item><see cref="ProtocolHeadBytesLength"/>:协议头部固定长度。Pipe 先读这么多字节。</item>
    ///   <item><see cref="GetContentLength"/>:从读到的头部计算出后续负载长度。整帧 = 头 + 负载。</item>
    ///   <item><see cref="CheckHeadBytesLegal"/>:头部本身合法吗?(可选,默认 true)</item>
    /// </list>
    /// 负值 <see cref="ProtocolHeadBytesLength"/> 表示"无固定头,由结束符识别",这类需配合
    /// <c>SpecifiedCharacterMessage</c> 使用(本 PR 不实现,留待具体协议需要时)。
    /// </remarks>
    public interface INetMessage
    {
        /// <summary>
        /// 协议头部固定字节数。负值表示结束符模式(本接口默认实现不支持)。
        /// 例如:Modbus TCP MBAP = 8,S7 TPKT = 4,FINS TCP = 16,MC3E Binary = 11。
        /// </summary>
        int ProtocolHeadBytesLength { get; }

        /// <summary>
        /// 从已读到的头部字节计算后续负载长度。返回 &lt; 0 表示"无法判定,继续读"。
        /// </summary>
        /// <param name="head">头部字节(长度 = <see cref="ProtocolHeadBytesLength"/>)。</param>
        /// <returns>负载字节数。0 表示整帧就是头部本身。</returns>
        int GetContentLength(byte[] head);

        /// <summary>
        /// 头部本身是否合法(可选校验,用于提前识别走错协议或乱码)。
        /// 默认实现返回 true。
        /// </summary>
        bool CheckHeadBytesLegal(byte[] head);
    }
}
