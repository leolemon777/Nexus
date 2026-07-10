using Nexus.AllenBradley;

namespace Nexus.BoschRexroth
{
    /// <summary>
    /// Bosch Rexroth ctrlX PLC 客户端 — 基于 EtherNet/IP CIP（ODVA 公开标准）。
    /// </summary>
    /// <remarks>
    /// <para>Bosch Rexroth 的 ctrlX PLC（ctrlX CORE）运行 ctrlX WORKS，支持通过标准 EtherNet/IP-CIP
    /// 访问 PLC 变量（Tag），协议层级与 Allen-Bradley ControlLogix 完全一致：
    /// TCP → ENIP (Encapsulation) → CIP (Common Industrial Protocol)。</para>
    /// <para>因此本客户端直接继承成熟的 <see cref="AllenBradleyCipClient"/>，复用其完整的
    /// CIP 显式消息帧构造、Tag 路径编码、分段读写、虚拟服务器与测试覆盖。</para>
    /// <para>地址使用 ctrlX WORKS 中定义的符号变量名（Symbol/Tag），语法与 AB Tag 一致。</para>
    /// <para>默认端口 44818（EtherNet/IP 标准端口）。</para>
    /// <para><b>协议本质</b>：标准 EtherNet/IP-CIP（公开 ODVA 规范，非 Bosch 私有协议）。</para>
    /// </remarks>
    public class BoschCtrlxClient : AllenBradleyCipClient
    {
        /// <summary>
        /// 创建 Bosch Rexroth ctrlX PLC CIP 客户端实例。
        /// </summary>
        /// <param name="ipAddress">ctrlX CORE 的 IP 地址。</param>
        /// <param name="port">EtherNet/IP 端口（默认 44818）。</param>
        /// <param name="slot">CIP 目标路径槽号（ctrlX 默认 0）。</param>
        /// <param name="timeout">超时（毫秒，默认 5000）。</param>
        public BoschCtrlxClient(string ipAddress, int port = 44818, byte slot = 0, int timeout = 5000)
            : base(ipAddress, port, slot, timeout)
        {
        }

        /// <inheritdoc/>
        public override string ToString() => $"Bosch ctrlX CIP [{IpAddress}:{Port}]";
    }
}
