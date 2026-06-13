using Nexus.Modbus;

namespace Nexus.Inovance
{
    /// <summary>
    /// 汇川 Modbus RTU 客户端。
    /// 通过 Modbus RTU 串口协议访问汇川 AM/H3U/H5U 系列 PLC。
    /// 默认字节序为 MidLittleEndian (CDAB)。
    /// </summary>
    public class InovanceModbusRtuClient : ModbusRtuClient
    {
        /// <summary>汇川 PLC 系列。</summary>
        public InovanceSeries Series { get; set; }

        /// <summary>
        /// 创建汇川 Modbus RTU 客户端实例。
        /// </summary>
        /// <param name="port">串口实现。</param>
        /// <param name="station">站号（默认 1）。</param>
        /// <param name="timeout">超时时间（毫秒，默认 5000）。</param>
        /// <param name="series">汇川 PLC 系列（默认 AM）。</param>
        public InovanceModbusRtuClient(ISerialPort port, byte station = 1, int timeout = 5000, InovanceSeries series = InovanceSeries.AM)
            : base(port, station, timeout)
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
            return $"InovanceModbusRtu[{Series}]";
        }
    }
}
