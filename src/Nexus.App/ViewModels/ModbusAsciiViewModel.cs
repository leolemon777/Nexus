using Nexus.App.Services;
using Nexus.Modbus;

namespace Nexus.App.ViewModels;

public partial class ModbusAsciiViewModel : ModbusSerialViewModelBase
{
    public ModbusAsciiViewModel(PacketRecorderService packetRecorder)
        : base(packetRecorder)
    {
        DataBits = 7;
        Parity = "Even";
    }

    public override string ProtocolName => "Modbus ASCII";

    public override string SampleCode => @"using Nexus.Modbus;
using System.IO.Ports;

// 创建串口连接
var serial = new SerialPort(""COM1"", 9600, Parity.Even, 7, StopBits.One);
serial.Open();
var client = new ModbusAsciiClient(new SystemSerialPortAdapter(serial), 1);

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
        return new ModbusAsciiClient(port, station, timeout) { InterFrameDelay = 20 };
    }
}
