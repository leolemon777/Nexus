using Nexus.Modbus;

namespace Nexus.Delta
{
    /// <summary>
    /// 台达 DVP/AS 系列 PLC Modbus RTU 客户端（串口）。
    /// <para>继承 ModbusRtuClient，通过 DeltaAddress 解析台达 PLC 地址。</para>
    /// <para>默认字节序为 MidLittleEndian (CDAB)，与台达 PLC 一致。</para>
    /// </summary>
    public class DeltaRtuClient : ModbusRtuClient
    {
        /// <summary>PLC 系列（DVP 或 AS）。</summary>
        public DeltaSeries Series { get; set; }

        /// <summary>
        /// 创建台达 Modbus RTU 客户端。
        /// </summary>
        /// <param name="port">串口实现。</param>
        /// <param name="station">从站地址（默认 1）。</param>
        /// <param name="timeout">超时（毫秒，默认 5000）。</param>
        /// <param name="series">PLC 系列（默认 DVP）。</param>
        public DeltaRtuClient(ISerialPort port, byte station = 1, int timeout = 5000, DeltaSeries series = DeltaSeries.DVP)
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
            return DeltaAddress.Parse(address, Series);
        }
    }
}
