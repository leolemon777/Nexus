using Nexus.Modbus;

namespace Nexus.Toshiba
{
    /// <summary>
    /// Toshiba V200/V100 系列微型 PLC TCP 客户端。
    /// </summary>
    /// <remarks>
    /// <para>Toshiba V200 是内建以太网的微型 PLC，原生支持 Modbus TCP/IP（Client 与 Server 双角色）。</para>
    /// <para>本客户端将其作为 Modbus TCP Server 访问。V200 的数据寄存器通过 Toshiba 约定映射到
    /// 标准 Modbus 的 4xxxxx（保持寄存器）/ 0xxxxx（线圈）区，寄存器号按 V-series Ethernet Function
    /// Manual 定义。</para>
    /// <para>地址使用标准 Modbus 编号（如 <c>400101</c>、<c>00005</c> 或纯数字 <c>101</c>）。</para>
    /// <para>默认端口 502，默认站号 1（Unit ID）。</para>
    /// <para><b>协议本质</b>：标准 Modbus TCP。本库的价值是品牌入口 + 文档化 V200 寄存器区约定。</para>
    /// </remarks>
    public class ToshibaClient : ModbusTcpClient
    {
        /// <summary>
        /// 创建 Toshiba V200 PLC 客户端实例。
        /// </summary>
        /// <param name="ip">V200 PLC IP 地址。</param>
        /// <param name="port">Modbus TCP 端口（默认 502）。</param>
        /// <param name="station">Modbus Unit ID（默认 1）。</param>
        /// <param name="timeout">超时（毫秒，默认 5000）。</param>
        public ToshibaClient(string ip, int port = 502, byte station = 1, int timeout = 5000)
            : base(ip, port, station, timeout)
        {
        }

        /// <inheritdoc/>
        public override string ToString() => $"Toshiba V200 [{Ip}:{Port}]";
    }
}
