using OpenIndustrialComm.Modbus;
using Xunit;

namespace OpenIndustrialComm.Tests;

public sealed class ModbusTests
{
    [Fact]
    public void Crc16_KnownVector()
    {
        byte[] request = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        Assert.Equal(0xCDC5, ModbusRtuFrameCodec.Crc16(request));
    }

    [Theory]
    [InlineData("hr:0", ModbusArea.HoldingRegister, 0)]
    [InlineData("ir:10", ModbusArea.InputRegister, 10)]
    [InlineData("co:5", ModbusArea.Coil, 5)]
    [InlineData("di:7", ModbusArea.DiscreteInput, 7)]
    [InlineData("40001", ModbusArea.HoldingRegister, 0)]
    public void AddressParser_Works(string text, ModbusArea area, ushort offset)
    {
        var parsed = new ModbusAddressParser().Parse(text);
        Assert.Equal(area, parsed.Area);
        Assert.Equal(offset, parsed.Offset);
    }
}
