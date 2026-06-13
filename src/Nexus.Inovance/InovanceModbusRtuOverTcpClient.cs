using Nexus.Modbus;

namespace Nexus.Inovance
{
    /// <summary>
    /// 汇川 Modbus RTU Over TCP 客户端。
    /// 通过 TCP 传输 RTU 格式报文访问汇川 AM/H3U/H5U 系列 PLC。
    /// 默认字节序为 MidLittleEndian (CDAB)。
    /// </summary>
    public class InovanceModbusRtuOverTcpClient : ModbusRtuOverTcpClient
    {
        /// <summary>汇川 PLC 系列。</summary>
        public InovanceSeries Series { get; set; }

        /// <summary>
        /// 创建汇川 Modbus RTU Over TCP 客户端实例。
        /// </summary>
        /// <param name="ip">PLC IP 地址。</param>
        /// <param name="port">端口号（默认 502）。</param>
        /// <param name="station">站号（默认 1）。</param>
        /// <param name="timeout">超时时间（毫秒，默认 5000）。</param>
        /// <param name="series">汇川 PLC 系列（默认 AM）。</param>
        public InovanceModbusRtuOverTcpClient(string ip, int port = 502, byte station = 1, int timeout = 5000, InovanceSeries series = InovanceSeries.AM)
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
            return InovanceModbusAddress.Parse(address, Series);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return $"InovanceModbusRtuOverTcp[{Series}][{Ip}:{Port}]";
        }
    }
}
