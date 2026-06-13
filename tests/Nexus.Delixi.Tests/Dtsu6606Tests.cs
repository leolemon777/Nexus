using Nexus;
using Nexus.Delixi;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Delixi.Tests;

public class Dtsu6606Tests
{
    private static Dtsu6606Client CreateClient(byte station = 1)
    {
        var port = new FakeSerialPort();
        return new Dtsu6606Client(port, station);
    }

    [Fact]
    public void Constructor_DefaultStation_IsOne()
    {
        var client = CreateClient();
        Assert.Equal(1, client.Station);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(247)]
    public void Constructor_CustomStation(byte station)
    {
        var client = CreateClient(station);
        Assert.Equal(station, client.Station);
    }

    [Fact]
    public void ToString_ReturnsExpected()
    {
        var client = CreateClient(3);
        Assert.Equal("Dtsu6606Client[Station=3]", client.ToString());
    }

    [Theory]
    [InlineData(3000)]
    [InlineData(3002)]
    [InlineData(3004)]
    [InlineData(3006)]
    [InlineData(3008)]
    [InlineData(3010)]
    [InlineData(3012)]
    [InlineData(3014)]
    [InlineData(3016)]
    [InlineData(3018)]
    [InlineData(3040)]
    public void AddressMapping_MeterRegisters_AreValidModbusAddresses(int register)
    {
        var parser = new ModbusAddressParser();
        var addr = parser.Parse(register.ToString());
        Assert.Equal(register, addr.StartAddress);
    }

    [Fact]
    public void TotalEnergy_Address_Is4000()
    {
        var parser = new ModbusAddressParser();
        var addr = parser.Parse("4000");
        Assert.Equal(4000, addr.StartAddress);
    }

    [Fact]
    public void Inherits_ModbusRtuClient()
    {
        var client = CreateClient();
        Assert.IsAssignableFrom<ModbusRtuClient>(client);
    }

    [Fact]
    public void Implements_IReadWriteDevice()
    {
        var client = CreateClient();
        Assert.IsAssignableFrom<IReadWriteDevice>(client);
    }

    private class FakeSerialPort : ISerialPort
    {
        public string PortName { get; set; } = "FAKE";
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 8;
        public StopBits StopBits { get; set; } = StopBits.One;
        public Parity Parity { get; set; } = Parity.None;
        public int ReadTimeout { get; set; } = 1000;
        public int WriteTimeout { get; set; } = 1000;
        public bool IsOpen => false;
        public bool DtrEnable { get; set; }
        public bool RtsEnable { get; set; }
        public void Open() { }
        public void Close() { }
        public void Dispose() { }
        public int Read(byte[] buffer, int offset, int count) => 0;
        public void Write(byte[] buffer, int offset, int count) { }
    }
}
