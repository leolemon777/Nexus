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

    protected override SerialDeviceBase CreateClient(ISerialPort port, byte station, int timeout)
    {
        return new ModbusRtuClient(port, station, timeout) { InterFrameDelay = 20 };
    }
}
