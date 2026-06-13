using Nexus.App.Services;
using Nexus.Modbus;

namespace Nexus.App.ViewModels;

public partial class ModbusRtuViewModel : ModbusSerialViewModelBase
{
    public ModbusRtuViewModel(PacketRecorderService packetRecorder)
        : base(packetRecorder)
    {
    }

    public override string ProtocolName => "Modbus RTU";

    public override string SampleCode => @"using Nexus.Modbus;
using System.IO.Ports;

// 创建串口连接
var serial = new SerialPort(""COM1"", 9600, Parity.None, 8, StopBits.One);
serial.Open();
var client = new ModbusRtuClient(new SystemSerialPortAdapter(serial), 1);

// 读取保持寄存器 (FC03)
var result = client.ReadInt16(""40001"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入单个寄存器 (FC06)
client.Write(""40001"", (short)123);

client.Disconnect();
serial.Close();";

    protected override SerialDeviceBase CreateClient(ISerialPort port, byte station, int timeout)
    {
        return new ModbusRtuClient(port, station, timeout) { InterFrameDelay = 20 };
    }
}
