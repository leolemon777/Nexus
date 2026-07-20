// Reference protocol client — shows how clean a protocol becomes on the new
// DeviceCommunication/Pipe/INetMessage stack. Compare with the legacy base
// classes that required implementing OnMessageSent/Received events, lock
// plumbing, frame-length hooks, etc.

using System;
using Nexus.Device;
using Nexus.Pipe;

namespace Nexus.LengthPrefixTcp
{
    /// <summary>
    /// 极简长度前缀协议客户端 — 演示 Phase B 新基类的标准用法。
    /// </summary>
    /// <remarks>
    /// <b>对比旧基类的优势</b>:
    /// <list type="bullet">
    ///   <item>无 <c>_lock</c> / <c>_asyncLock</c> 双锁问题 — Pipe 内部统一管理。</li>
    ///   <item>无 <c>ResponseHeaderLength</c> / <c>GetResponsePayloadLength</c> 抽象方法 — 由
    ///     <see cref="LengthPrefixTcpMessage"/> 提供。</li>
    ///   <item>无 <c>throw NotImplementedException</c> — 默认返回 OperateResult.Failed。</li>
    ///   <li>支持 TCP / SSL / DTU / 串口切换 — 只需换 Pipe 注入。</li>
    /// </list>
    /// </remarks>
    public class LengthPrefixTcpClient : DeviceTcpNet
    {
        public LengthPrefixTcpClient(string ip, int port, int timeout = 5000)
            : base(ip, port, timeout)
        {
            // 关键:注入帧解析器。DeviceCommunication 会用它走两阶段读路径。
            MessageFrame = new LengthPrefixTcpMessage();
        }

        /// <summary>发送任意 payload 并接收响应。响应 payload 长度等于请求 payload 长度(echo server)。</summary>
        public OperateResult<byte[]> SendPayload(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (payload.Length > 0xFFFFFF)
                return OperateResult<byte[]>.Failed("payload 过长(>16MB)");

            byte[] request = new byte[4 + payload.Length];
            request[0] = (byte)(payload.Length >> 24);
            request[1] = (byte)(payload.Length >> 16);
            request[2] = (byte)(payload.Length >> 8);
            request[3] = (byte)payload.Length;
            Buffer.BlockCopy(payload, 0, request, 4, payload.Length);

            return ReadFromCoreServer(request);
        }
    }
}
