using System;
using Nexus.Modbus;

namespace Nexus.Delixi
{
    public class Dtsu6606Client : ModbusRtuClient
    {
        public Dtsu6606Client(ISerialPort serialPort, byte station = 1, int timeout = 1000)
            : base(serialPort, station, timeout) { }

        public OperateResult<float> ReadVoltageA()
        {
            var r = ReadFloat("3000");
            return r;
        }

        public OperateResult<float> ReadVoltageB()
        {
            return ReadFloat("3002");
        }

        public OperateResult<float> ReadVoltageC()
        {
            return ReadFloat("3004");
        }

        public OperateResult<float> ReadCurrentA()
        {
            return ReadFloat("3006");
        }

        public OperateResult<float> ReadCurrentB()
        {
            return ReadFloat("3008");
        }

        public OperateResult<float> ReadCurrentC()
        {
            return ReadFloat("3010");
        }

        public OperateResult<float> ReadTotalPower()
        {
            return ReadFloat("3012");
        }

        public OperateResult<float> ReadPowerA()
        {
            return ReadFloat("3014");
        }

        public OperateResult<float> ReadPowerB()
        {
            return ReadFloat("3016");
        }

        public OperateResult<float> ReadPowerC()
        {
            return ReadFloat("3018");
        }

        public OperateResult<float> ReadFrequency()
        {
            return ReadFloat("3040");
        }

        public OperateResult<float> ReadTotalEnergy()
        {
            return ReadFloat("4000");
        }

        public override string ToString() => $"Dtsu6606Client[Station={Station}]";
    }
}
