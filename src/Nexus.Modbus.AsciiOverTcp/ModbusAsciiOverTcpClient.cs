using Nexus.Modbus;

namespace Nexus.Modbus.AsciiOverTcp
{
    public class ModbusAsciiOverTcpClient : ModbusTcpClient
    {
        public ModbusAsciiOverTcpClient(string ip, int port = 502, byte station = 1, int timeout = 5000)
            : base(ip, port, station, timeout) { }
    }
}
