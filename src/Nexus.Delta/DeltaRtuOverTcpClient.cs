using Nexus.Modbus;

namespace Nexus.Delta
{
    /// <summary>
    /// 台达 DVP/AS 系列 PLC Modbus RTU Over TCP 客户端。
    /// <para>继承 ModbusRtuOverTcpClient，通过 DeltaAddress 解析台达 PLC 地址。</para>
    /// <para>RTU 报文通过 TCP 传输，无 MBAP 头。</para>
    /// <para>默认字节序为 MidLittleEndian (CDAB)，与台达 PLC 一致。</para>
    /// </summary>
    public class DeltaRtuOverTcpClient : ModbusRtuOverTcpClient
    {
        /// <summary>PLC 系列（DVP 或 AS）。</summary>
        public DeltaSeries Series { get; set; }

        /// <summary>
        /// 创建台达 Modbus RTU Over TCP 客户端。
        /// </summary>
        /// <param name="ip">远程 IP 地址。</param>
        /// <param name="port">远程端口（默认 502）。</param>
        /// <param name="station">从站地址（默认 1）。</param>
        /// <param name="timeout">超时（毫秒，默认 5000）。</param>
        /// <param name="series">PLC 系列（默认 DVP）。</param>
        public DeltaRtuOverTcpClient(string ip, int port = 502, byte station = 1, int timeout = 5000, DeltaSeries series = DeltaSeries.DVP)
            : base(ip, port, station, timeout)
        {
            Series = series;
            ByteOrder = Endianness.MidLittleEndian;
        }

        /// <inheritdoc/>
        protected override (ushort address, byte readFc, byte writeFc) ParseAddressEx(string address)
        {
            CaptureAddressContext(address);
            address = AddressContext.ExtractCoreAddress(address).Trim();
            return DeltaAddress.Parse(address, Series);
        }
    }
}
