using Nexus.Modbus;

namespace Nexus.Hitachi
{
    /// <summary>
    /// 日立 EH-150 系列 PLC RTU-over-TCP 客户端。
    /// </summary>
    /// <remarks>
    /// <para>EH-150 CPU 本身不内建 Modbus；Modbus 能力由 <b>EH-SIO 串口通信模块</b>提供（仅 RTU，无原生 TCP）。</para>
    /// <para>本客户端通过 <b>RTU-over-TCP</b>（串口服务器/网关透传 RTU 帧到 TCP）访问 EH-150：
    /// RTU ADU（Station+FC+Data+CRC16）通过 TCP socket 传输，无 MBAP 头。</para>
    /// <para>覆盖日系 operand（D/X/Y/M/T/C/W/R）地址解析。</para>
    /// <para>继承 <see cref="ModbusRtuOverTcpClient"/>，仅 override <c>ParseAddressEx</c> 注入日立地址映射。</para>
    /// <para><b>协议本质</b>：Modbus RTU-over-TCP variant。需要现场有 EH-SIO 模块 + RTU 透传网关。</para>
    /// </remarks>
    public class HitachiClient : ModbusRtuOverTcpClient
    {
        /// <summary>
        /// 创建日立 EH-150 RTU-over-TCP 客户端实例。
        /// </summary>
        /// <param name="ip">串口服务器/网关 IP 地址。</param>
        /// <param name="port">TCP 端口（取决于网关配置，常见 502 或网关自定义）。</param>
        /// <param name="station">EH-SIO Modbus 从站地址（默认 1）。</param>
        /// <param name="timeout">超时（毫秒，默认 5000）。</param>
        public HitachiClient(string ip, int port = 502, byte station = 1, int timeout = 5000)
            : base(ip, port, station, timeout)
        {
        }

        /// <inheritdoc/>
        protected override (ushort address, byte readFc, byte writeFc) ParseAddressEx(string address)
        {
            CaptureAddressContext(address);
            return HitachiAddress.Parse(address);
        }

        /// <inheritdoc/>
        public override string ToString() => $"Hitachi EH-150 RTU-over-TCP [{Ip}:{Port}]";
    }
}
