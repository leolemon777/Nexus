using Nexus.Modbus;

namespace Nexus.Honeywell
{
    /// <summary>
    /// Honeywell HC900 / ControlEdge HC900 控制器 TCP 客户端。
    /// </summary>
    /// <remarks>
    /// <para>Honeywell HC900 作为 Modbus TCP Server（Slave），被上位机作为 Client 读取。</para>
    /// <para>HC900 没有厂商专属的地址语法 —— 寄存器布局由 HC Designer 软件中配置的
    /// "Custom Modbus Map" 决定。用户在 HC Designer 里把内部 PV/SP/OP 等变量映射到
    /// 标准 Modbus 的 4xxxxx（保持寄存器）/ 3xxxxx（输入寄存器）/ 0xxxxx（线圈）区。</para>
    /// <para>因此本客户端是 <b>标准 Modbus TCP 直通客户端</b>，地址使用标准 Modbus 编号
    /// （如 <c>400101</c>、<c>30010</c>、<c>00005</c> 或纯数字 <c>101</c>）。</para>
    /// <para>默认端口 502。HC900 支持双 Modbus/TCP 以太网口。</para>
    /// <para><b>协议本质</b>：标准 Modbus TCP。本库的价值是品牌入口 + 文档化 HC900 寄存器约定。</para>
    /// </remarks>
    public class HoneywellClient : ModbusTcpClient
    {
        /// <summary>
        /// 创建 Honeywell HC900 客户端实例。
        /// </summary>
        /// <param name="ip">HC900 控制器 IP 地址。</param>
        /// <param name="port">Modbus TCP 端口（默认 502）。</param>
        /// <param name="station">Modbus 从站地址（由 HC Designer 配置，通常 1）。</param>
        /// <param name="timeout">超时（毫秒，默认 5000）。</param>
        public HoneywellClient(string ip, int port = 502, byte station = 1, int timeout = 5000)
            : base(ip, port, station, timeout)
        {
        }

        /// <inheritdoc/>
        public override string ToString() => $"Honeywell HC900 [{Ip}:{Port}]";
    }
}
