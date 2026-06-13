using Nexus.Modbus;

namespace Nexus.Delta
{
    /// <summary>
    /// 台达 DVP/AS 系列 PLC Modbus ASCII over TCP 客户端。
    /// <para>继承 DeltaAsciiClient，通过 TCP 连接发送 Modbus ASCII 帧。</para>
    /// <para>帧格式: ':' + Station(2hex) + PDU + LRC(2hex) + CR LF</para>
    /// <para>默认字节序为 MidLittleEndian (CDAB)，与台达 PLC 一致。</para>
    /// </summary>
    public class DeltaAsciiOverTcpClient : DeltaAsciiClient
    {
        /// <summary>
        /// 创建台达 Modbus ASCII over TCP 客户端。
        /// </summary>
        /// <param name="ip">远程 IP 地址。</param>
        /// <param name="port">远程端口（默认 502）。</param>
        /// <param name="station">从站地址（默认 1）。</param>
        /// <param name="timeout">超时（毫秒，默认 5000）。</param>
        /// <param name="series">PLC 系列（默认 DVP）。</param>
        public DeltaAsciiOverTcpClient(string ip, int port = 502, byte station = 1, int timeout = 5000, DeltaSeries series = DeltaSeries.DVP)
            : base(new TcpStreamSerialPortAdapter(ip, port, timeout), station, timeout, series)
        {
            InterFrameDelay = 0;
        }
    }
}
