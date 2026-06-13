using System;
using Nexus.Modbus;

namespace Nexus.Dlt
{
    public class Dam3601Client : ModbusRtuClient
    {
        public Dam3601Client(ISerialPort serialPort, byte station = 1, int timeout = 1000)
            : base(serialPort, station, timeout) { }

        public OperateResult<float> ReadAnalogInput(int channel)
        {
            if (channel < 0 || channel > 7)
                return OperateResult<float>.Failed("通道号超出范围 (0-7)");

            ushort addr = (ushort)(0x0000 + channel * 2);
            return ReadFloat(addr.ToString());
        }

        public OperateResult<float> ReadAnalogInput0() => ReadAnalogInput(0);
        public OperateResult<float> ReadAnalogInput1() => ReadAnalogInput(1);
        public OperateResult<float> ReadAnalogInput2() => ReadAnalogInput(2);
        public OperateResult<float> ReadAnalogInput3() => ReadAnalogInput(3);
        public OperateResult<float> ReadAnalogInput4() => ReadAnalogInput(4);
        public OperateResult<float> ReadAnalogInput5() => ReadAnalogInput(5);
        public OperateResult<float> ReadAnalogInput6() => ReadAnalogInput(6);
        public OperateResult<float> ReadAnalogInput7() => ReadAnalogInput(7);

        public OperateResult<bool> ReadDigitalInput(int channel)
        {
            if (channel < 0 || channel > 7)
                return OperateResult<bool>.Failed("通道号超出范围 (0-7)");

            ushort addr = (ushort)(0x0010 + channel);
            return ReadBool(addr.ToString());
        }

        public OperateResult SetDigitalOutput(int channel, bool value)
        {
            if (channel < 0 || channel > 7)
                return OperateResult.Failed("通道号超出范围 (0-7)");

            ushort addr = (ushort)(0x0020 + channel);
            return Write(addr.ToString(), value);
        }

        public override string ToString() => $"Dam3601Client[Station={Station}]";
    }
}
