using Nexus.Modbus;

namespace Nexus.Xinje
{
    /// <summary>
    /// 信捷 Xinje RTU Over TCP 客户端。
    /// <para>通过 TCP 传输 RTU 格式报文访问信捷 XC/XG/XL 系列 PLC。</para>
    /// <para>继承 ModbusRtuOverTcpClient，覆盖地址解析以支持信捷地址格式（D/HD/SD/SM/M/Y/X/C/T/S）。</para>
    /// </summary>
    public class XinjeRtuOverTcpClient : ModbusRtuOverTcpClient
    {
        /// <summary>
        /// 创建信捷 RTU Over TCP 客户端实例。
        /// </summary>
        /// <param name="ip">PLC IP 地址。</param>
        /// <param name="port">端口号（默认 502）。</param>
        /// <param name="station">站号（默认 1）。</param>
        /// <param name="timeout">超时时间（毫秒，默认 5000）。</param>
        public XinjeRtuOverTcpClient(string ip, int port = 502, byte station = 1, int timeout = 5000)
            : base(ip, port, station, timeout)
        {
        }

        /// <inheritdoc/>
        protected override (ushort address, byte readFc, byte writeFc) ParseAddressEx(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new System.ArgumentException("地址不能为空");

            CaptureAddressContext(address);

            var parsed = XinjeAddress.Parse(address);
            return (parsed.Address, parsed.ReadFunctionCode, parsed.WriteFunctionCode);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return $"XinjeRtuOverTcp[{Ip}:{Port}]";
        }
    }
}
