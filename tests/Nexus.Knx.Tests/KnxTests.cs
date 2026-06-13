using System.Reflection;
using Nexus;
using Nexus.Knx;
using Xunit;

namespace Nexus.Knx.Tests;

public class KnxTests
{
    private static ushort? InvokeParseGroupAddress(string address)
    {
        var method = typeof(KnxClient).GetMethod("ParseGroupAddress", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (ushort?)method.Invoke(null, new object[] { address });
    }

    private static byte[] InvokeBuildKnxFrame(KnxClient client, ushort serviceType, byte[] data)
    {
        var method = typeof(KnxClient).GetMethod("BuildKnxFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (byte[])method.Invoke(client, new object[] { serviceType, data })!;
    }

    private static byte[] InvokeBuildCemiFrame(KnxClient client, byte messageCode, ushort groupAddr, byte[] value)
    {
        var method = typeof(KnxClient).GetMethod("BuildCemiFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (byte[])method.Invoke(client, new object[] { messageCode, groupAddr, value })!;
    }

    // ── ParseGroupAddress tests ──

    [Fact]
    public void ParseGroupAddress_ValidAddress_ReturnsCorrectValue()
    {
        ushort? result = InvokeParseGroupAddress("1/2/3");
        Assert.NotNull(result);
        Assert.Equal((ushort)((1 << 11) | (2 << 8) | 3), result.Value);
    }

    [Fact]
    public void ParseGroupAddress_ZeroAddress_ReturnsZero()
    {
        ushort? result = InvokeParseGroupAddress("0/0/0");
        Assert.NotNull(result);
        Assert.Equal((ushort)0, result.Value);
    }

    [Fact]
    public void ParseGroupAddress_MaxValues_ReturnsCorrect()
    {
        ushort? result = InvokeParseGroupAddress("31/7/255");
        Assert.NotNull(result);
        Assert.Equal((ushort)((31 << 11) | (7 << 8) | 255), result.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1/2")]
    [InlineData("1/2/3/4")]
    [InlineData("abc")]
    [InlineData("32/0/0")]
    [InlineData("0/8/0")]
    [InlineData("0/0/256")]
    public void ParseGroupAddress_InvalidFormats_ReturnsNull(string? address)
    {
        ushort? result = InvokeParseGroupAddress(address!);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("0/0/1", 1)]
    [InlineData("0/1/0", 256)]
    [InlineData("1/0/0", 2048)]
    public void ParseGroupAddress_ComponentPositions(string address, ushort expected)
    {
        ushort? result = InvokeParseGroupAddress(address);
        Assert.NotNull(result);
        Assert.Equal(expected, result.Value);
    }

    // ── BuildKnxFrame tests ──

    [Fact]
    public void BuildKnxFrame_CorrectHeader()
    {
        var client = new KnxClient("127.0.0.1", 3671);
        byte[] frame = InvokeBuildKnxFrame(client, 0x0420, new byte[] { 0x01 });

        Assert.Equal(0x10, frame[0]);
        Assert.Equal(0x00, frame[1]);
        Assert.Equal(0x04, frame[2]);
        Assert.Equal(0x20, frame[3]);
    }

    [Fact]
    public void BuildKnxFrame_LengthFieldIsCorrect()
    {
        var client = new KnxClient("127.0.0.1", 3671);
        byte[] data = new byte[] { 0x01, 0x02, 0x03 };
        byte[] frame = InvokeBuildKnxFrame(client, 0x0420, data);

        int expectedLen = 6 + 4 + data.Length;
        Assert.Equal((byte)(expectedLen >> 8), frame[4]);
        Assert.Equal((byte)(expectedLen & 0xFF), frame[5]);
    }

    [Fact]
    public void BuildKnxFrame_HostProtocolInfo()
    {
        var client = new KnxClient("127.0.0.1", 3671);
        byte[] frame = InvokeBuildKnxFrame(client, 0x0420, new byte[] { 0x00 });

        Assert.Equal(0x08, frame[6]);
        Assert.Equal(0x01, frame[7]);
    }

    [Fact]
    public void BuildKnxFrame_DataCopied()
    {
        var client = new KnxClient("127.0.0.1", 3671);
        byte[] data = new byte[] { 0xAA, 0xBB, 0xCC };
        byte[] frame = InvokeBuildKnxFrame(client, 0x0420, data);

        Assert.Equal(0xAA, frame[10]);
        Assert.Equal(0xBB, frame[11]);
        Assert.Equal(0xCC, frame[12]);
    }

    // ── BuildCemiFrame tests ──

    [Fact]
    public void BuildCemiFrame_CorrectStructure()
    {
        var client = new KnxClient("127.0.0.1", 3671);
        ushort ga = (ushort)((1 << 11) | (2 << 8) | 3);
        byte[] cemi = InvokeBuildCemiFrame(client, 0x00, ga, new byte[] { 0x01 });

        Assert.Equal(0x11, cemi[0]);
        Assert.Equal(0x00, cemi[1]);
        Assert.Equal(0xBC, cemi[2]);
        Assert.Equal(0xE0, cemi[3]);
        Assert.Equal(0x00, cemi[4]);
        Assert.Equal(0x00, cemi[5]);
        Assert.Equal(0x0A, cemi[6]);
        Assert.Equal(0x03, cemi[7]);
        Assert.Equal(0x01, cemi[8]);
        Assert.Equal(0x01, cemi[9]);
    }

    // ── Client tests ──

    [Fact]
    public void ToString_ReturnsExpected()
    {
        var client = new KnxClient("192.168.1.100", 3671);
        Assert.Equal("KnxClient[192.168.1.100:3671]", client.ToString());
    }

    [Fact]
    public void Implements_IBatchReadWrite()
    {
        var client = new KnxClient("127.0.0.1", 3671);
        Assert.IsAssignableFrom<IBatchReadWrite>(client);
    }

    [Fact]
    public void Implements_IReadWriteDevice()
    {
        var client = new KnxClient("127.0.0.1", 3671);
        Assert.IsAssignableFrom<IReadWriteDevice>(client);
    }

    [Fact]
    public void ReadFloat_Unsupported()
    {
        var client = new KnxClient("127.0.0.1", 3671);
        var result = client.ReadFloat("0/0/1");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void WriteFloat_Unsupported()
    {
        var client = new KnxClient("127.0.0.1", 3671);
        var result = client.Write("0/0/1", 1.5f);
        Assert.False(result.IsSuccess);
    }
}
