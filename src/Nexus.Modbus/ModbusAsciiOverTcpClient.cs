namespace Nexus.Modbus
{
    /// <summary>
    /// Modbus ASCII over TCP client.
    /// <para>ASCII-over-TCP sends standard Modbus ASCII frames through a TCP socket.</para>
    /// <para>Frame format: ':' + Hex(Station + PDU + LRC) + CR LF.</para>
    /// <para>Use this for gateways and device tools that expose ASCII framing over Ethernet.</para>
    /// </summary>
    public class ModbusAsciiOverTcpClient : ModbusAsciiClient
    {
        /// <summary>
        /// Creates a Modbus ASCII-over-TCP client.
        /// </summary>
        /// <param name="ip">Remote IP address or host name.</param>
        /// <param name="port">Remote TCP port.</param>
        /// <param name="station">Modbus station address.</param>
        /// <param name="timeout">Read/write timeout in milliseconds.</param>
        public ModbusAsciiOverTcpClient(string ip, int port = 502, byte station = 1, int timeout = 5000)
            : base(new TcpStreamSerialPortAdapter(ip, port, timeout), station, timeout)
        {
            InterFrameDelay = 0;
        }
    }
}
