using System.Reflection;
using Nexus;
using Nexus.Freedom;
using Xunit;

namespace Nexus.Freedom.Tests;

public class FreedomTests
{
    private static object? InvokeParseStx(ref string address)
    {
        var method = typeof(FreedomSerialClient).GetMethod("ParseStx", BindingFlags.NonPublic | BindingFlags.Static)!;
        var parameters = new object?[] { address };
        var result = method.Invoke(null, parameters);
        address = (string)parameters[0]!;
        return result;
    }

    private static byte[] InvokeParseHex(string hex)
    {
        var method = typeof(FreedomSerialClient).GetMethod("ParseHex", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (byte[])method.Invoke(null, new object[] { hex })!;
    }

    private static byte[] InvokeStripHeader(byte[] response, int stx)
    {
        var method = typeof(FreedomSerialClient).GetMethod("StripHeader", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OperateResult<byte[]>)method.Invoke(null, new object[] { response, stx })!;
        Assert.True(result.IsSuccess, result.Message);
        return result.Content;
    }

    // ── ParseStx tests ──

    [Fact]
    public void ParseStx_WithStxPrefix_ParsesCorrectly()
    {
        string address = "stx=3;AABBCC";
        int stx = (int)InvokeParseStx(ref address)!;
        Assert.Equal(3, stx);
        Assert.Equal("AABBCC", address);
    }

    [Fact]
    public void ParseStx_WithoutPrefix_ReturnsZero()
    {
        string address = "AABBCC";
        int stx = (int)InvokeParseStx(ref address)!;
        Assert.Equal(0, stx);
        Assert.Equal("AABBCC", address);
    }

    [Theory]
    [InlineData("stx=1;FF", 1)]
    [InlineData("stx=10;00", 10)]
    [InlineData("stx=255;AB", 255)]
    public void ParseStx_VariousValues(string input, int expected)
    {
        string address = input;
        int stx = (int)InvokeParseStx(ref address)!;
        Assert.Equal(expected, stx);
    }

    // ── ParseHex tests ──

    [Fact]
    public void ParseHex_SimpleHex_ReturnsBytes()
    {
        byte[] result = InvokeParseHex("AABBCC");
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, result);
    }

    [Fact]
    public void ParseHex_WithSpaces_Ignored()
    {
        byte[] result = InvokeParseHex("AA BB CC");
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, result);
    }

    [Fact]
    public void ParseHex_EmptyString_ReturnsEmpty()
    {
        byte[] result = InvokeParseHex("");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseHex_Lowercase_Works()
    {
        byte[] result = InvokeParseHex("aabb");
        Assert.Equal(new byte[] { 0xAA, 0xBB }, result);
    }

    [Fact]
    public void ParseHex_MixedCase_Works()
    {
        byte[] result = InvokeParseHex("AaBb");
        Assert.Equal(new byte[] { 0xAA, 0xBB }, result);
    }

    [Fact]
    public void ParseHex_SingleByte_Works()
    {
        byte[] result = InvokeParseHex("FF");
        Assert.Equal(new byte[] { 0xFF }, result);
    }

    [Fact]
    public void ParseHex_NonHexChars_Skipped()
    {
        byte[] result = InvokeParseHex("AA:BB:CC");
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, result);
    }

    // ── StripHeader tests ──

    [Fact]
    public void StripHeader_ZeroStx_ReturnsFullResponse()
    {
        byte[] response = new byte[] { 0x01, 0x02, 0x03 };
        byte[] result = InvokeStripHeader(response, 0);
        Assert.Equal(response, result);
    }

    [Fact]
    public void StripHeader_Stx2_RemovesHeader()
    {
        byte[] response = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        byte[] result = InvokeStripHeader(response, 2);
        Assert.Equal(new byte[] { 0xCC, 0xDD }, result);
    }

    [Fact]
    public void StripHeader_StxEqLength_ReturnsEmpty()
    {
        byte[] response = new byte[] { 0x01, 0x02, 0x03 };
        byte[] result = InvokeStripHeader(response, 3);
        Assert.Empty(result);
    }

    [Fact]
    public void StripHeader_StxGtLength_ReturnsEmpty()
    {
        byte[] response = new byte[] { 0x01, 0x02 };
        byte[] result = InvokeStripHeader(response, 5);
        Assert.Empty(result);
    }

    // ── Interface tests ──

    [Fact]
    public void SerialClient_Implements_IBatchReadWrite()
    {
        var port = new FakeSerialPort();
        var client = new FreedomSerialClient(port);
        Assert.IsAssignableFrom<IBatchReadWrite>(client);
    }

    [Fact]
    public void TcpClient_Implements_IBatchReadWrite()
    {
        var client = new FreedomTcpClient("127.0.0.1", 5000);
        Assert.IsAssignableFrom<IBatchReadWrite>(client);
    }

    [Fact]
    public void UdpClient_Implements_IBatchReadWrite()
    {
        var client = new FreedomUdpClient("127.0.0.1", 5000);
        Assert.IsAssignableFrom<IBatchReadWrite>(client);
    }

    private class FakeSerialPort : ISerialPort
    {
        public string PortName { get; set; } = "FAKE";
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 8;
        public StopBits StopBits { get; set; } = StopBits.One;
        public Parity Parity { get; set; } = Parity.None;
        public int ReadTimeout { get; set; } = 5000;
        public int WriteTimeout { get; set; } = 5000;
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
