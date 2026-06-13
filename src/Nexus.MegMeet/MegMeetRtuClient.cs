using Nexus.Modbus;

namespace Nexus.MegMeet
{
    /// <summary>
    /// 麦格米特 PLC Modbus RTU 客户端。
    /// <para>继承 ModbusRtuClient，覆盖地址解析以支持 MegMeet 地址格式。</para>
    /// <para>位操作: X(八进制输入), Y(八进制输出), M, SM, S, T, C</para>
    /// <para>字操作: D, SD, Z, R</para>
    /// </summary>
    public class MegMeetRtuClient : ModbusRtuClient
    {
        public MegMeetRtuClient(ISerialPort port, byte station = 1, int timeout = 5000)
            : base(port, station, timeout)
        {
        }

        protected override (ushort address, byte readFc, byte writeFc) ParseAddressEx(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new System.ArgumentException("地址不能为空");

            CaptureAddressContext(address);

            var parsed = MegMeetAddress.Parse(address);
            return (parsed.Address, parsed.ReadFunctionCode, parsed.WriteFunctionCode);
        }
    }
}
