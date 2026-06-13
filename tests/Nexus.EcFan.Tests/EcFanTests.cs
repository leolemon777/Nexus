using System.Reflection;
using Nexus;
using Nexus.EcFan;
using Xunit;

namespace Nexus.EcFan.Tests;

public class EcFanTests
{
    private static EcFanClient CreateClient(byte station = 1)
    {
        var port = new FakeSerialPort();
        return new EcFanClient(port, station);
    }

    private static byte[] InvokeBuildCommand(EcFanClient client, byte fc, ushort register, ushort value)
    {
        var method = typeof(EcFanClient).GetMethod("BuildCommand", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (byte[])method.Invoke(client, new object[] { fc, register, value })!;
    }

    private static ushort InvokeParseU16Response(byte[] resp)
    {
        var method = typeof(EcFanClient).GetMethod("ParseU16Response", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OperateResult<ushort>)method.Invoke(null, new object[] { resp })!;
        Assert.True(result.IsSuccess, result.Message);
        return result.Content;
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
        Assert.Equal("EcFanClient[Station=3]", client.ToString());
    }

    [Fact]
    public void BuildCommand_ReadSpeed_IsCorrect()
    {
        var client = CreateClient(station: 1);
        byte[] cmd = InvokeBuildCommand(client, 0x03, 0x0000, 1);

        Assert.Equal(8, cmd.Length);
        Assert.Equal(0x01, cmd[0]);
        Assert.Equal(0x03, cmd[1]);
        Assert.Equal(0x00, cmd[2]);
        Assert.Equal(0x00, cmd[3]);
        Assert.Equal(0x00, cmd[4]);
        Assert.Equal(0x01, cmd[5]);
    }

    [Fact]
    public void BuildCommand_CrcIsAppended()
    {
        var client = CreateClient(station: 1);
        byte[] cmd = InvokeBuildCommand(client, 0x03, 0x0000, 1);

        ushort expectedCrc = CrcCalculator.ComputeCrc16(cmd, 0, 6);
        Assert.Equal((byte)(expectedCrc & 0xFF), cmd[6]);
        Assert.Equal((byte)((expectedCrc >> 8) & 0xFF), cmd[7]);
    }

    [Theory]
    [InlineData(0x01, 0x0001, 500)]
    [InlineData(0x02, 0x0002, 1000)]
    [InlineData(0x03, 0x0010, 0xFFFF)]
    public void BuildCommand_VariousParameters(byte station, ushort register, ushort value)
    {
        var client = CreateClient(station);
        byte[] cmd = InvokeBuildCommand(client, 0x03, register, value);

        Assert.Equal(station, cmd[0]);
        Assert.Equal(0x03, cmd[1]);
        Assert.Equal((byte)(register >> 8), cmd[2]);
        Assert.Equal((byte)(register & 0xFF), cmd[3]);
        Assert.Equal((byte)(value >> 8), cmd[4]);
        Assert.Equal((byte)(value & 0xFF), cmd[5]);
    }

    [Fact]
    public void ParseU16Response_ValidResponse_ReturnsValue()
    {
        byte[] resp = new byte[] { 0x01, 0x03, 0x02, 0x00, 0x64, 0x00, 0x00 };
        ushort val = InvokeParseU16Response(resp);
        Assert.Equal(100, val);
    }

    [Fact]
    public void ParseU16Response_MaxValue_Returns65535()
    {
        byte[] resp = new byte[] { 0x01, 0x03, 0x02, 0xFF, 0xFF, 0x00, 0x00 };
        ushort val = InvokeParseU16Response(resp);
        Assert.Equal(65535, val);
    }

    [Fact]
    public void ParseU16Response_ZeroValue_ReturnsZero()
    {
        byte[] resp = new byte[] { 0x01, 0x03, 0x02, 0x00, 0x00, 0x00, 0x00 };
        ushort val = InvokeParseU16Response(resp);
        Assert.Equal(0, val);
    }

    [Fact]
    public void ParseU16Response_TooShort_Fails()
    {
        byte[] resp = new byte[] { 0x01, 0x03, 0x02 };
        var method = typeof(EcFanClient).GetMethod("ParseU16Response", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OperateResult<ushort>)method.Invoke(null, new object[] { resp })!;
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Implements_IBatchReadWrite()
    {
        var client = CreateClient();
        Assert.IsAssignableFrom<IBatchReadWrite>(client);
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
