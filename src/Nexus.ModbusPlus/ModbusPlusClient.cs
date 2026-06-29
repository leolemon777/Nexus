using System;
using Nexus;

namespace Nexus.ModbusPlus
{
    /// <summary>
    /// Modbus Plus 客户端 — 施耐德 Modbus Plus 高速令牌总线协议。
    /// <para>通过 Modbus Plus 网关访问设备，使用标准 Modbus 功能码。</para>
    /// <para>地址格式与标准 Modbus 相同: D100, M0, 40001 等</para>
    /// </summary>
    public class ModbusPlusClient : Modbus.ModbusTcpClient
    {
        public ModbusPlusClient(string ip, int port = 502, byte station = 1, int timeout = 5000)
            : base(ip, port, station, timeout)
        {
            ByteOrder = Endianness.MidLittleEndian;
        }
    }
}
