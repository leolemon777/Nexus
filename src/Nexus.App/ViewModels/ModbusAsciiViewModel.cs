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

    protected override SerialDeviceBase CreateClient(ISerialPort port, byte station, int timeout)
    {
        return new ModbusAsciiClient(port, station, timeout) { InterFrameDelay = 20 };
    }
}
