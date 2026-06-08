using System;
using Xunit;
using Nexus.Cjt;

namespace Nexus.Cjt.Tests;

public class Cjt188Tests
{
    #region BCD 工具

    [Theory]
    [InlineData(new byte[] { 0x12, 0x34 }, "3412")]
    [InlineData(new byte[] { 0x56 }, "56")]
    public void BcdToString_Tests(byte[] data, string expected)
    {
        Assert.Equal(expected, Cjt188Client.BcdToString(data));
    }

    [Theory]
    [InlineData("901F0000", new byte[] { 0x00, 0x00, 0x1F, 0x90 })]
    [InlineData("90200000", new byte[] { 0x00, 0x00, 0x20, 0x90 })]
    public void ParseDataId_Valid(string input, byte[] expected)
    {
        var result = Cjt188Client.ParseDataId(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("ZZZZZZZZ")]
    public void ParseDataId_Invalid(string? input)
    {
        var result = Cjt188Client.ParseDataId(input!);
        Assert.Null(result);
    }

    #endregion

    #region 帧构建

    [Fact]
    public void BuildFrame_BasicStructure()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterType = Cjt188Client.TYPE_WATER_COLD;
        client.MeterAddress = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };

        byte[] frame = client.BuildFrame(0x01, new byte[] { 0x90, 0x1F, 0x00, 0x00 }, null);

        // 验证帧结构
        Assert.Equal(0x68, frame[0]);   // header
        Assert.Equal(0x68, frame[9]);   // header2
        Assert.Equal(0x16, frame[frame.Length - 1]); // end byte
        Assert.Equal(Cjt188Client.TYPE_WATER_COLD, frame[1]); // meter type
        Assert.Equal(0x01, frame[10]); // control = 0x01

        // 数据长度 = 4 (DI only, no user data)
        Assert.Equal(0x04, frame[11]);
    }

    [Fact]
    public void BuildFrame_WithData()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterAddress = new byte[7];

        byte[] frame = client.BuildFrame(0x04, new byte[] { 0x90, 0x1F, 0x00, 0x00 }, new byte[] { 0xAA, 0xBB });

        // 数据长度 = 4(DI) + 2(data) = 6
        Assert.Equal(0x06, frame[11]);

        // 数据域加密: DI0(0x90+33=0xC3)
        Assert.Equal(0xC3, frame[12]);
    }

    [Fact]
    public void BuildFrame_EndBytePresent()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterAddress = new byte[7];

        byte[] frame = client.BuildFrame(0x01, new byte[] { 0x00, 0x00, 0x00, 0x00 }, null);

        // 最后一字节必须是 0x16 (FRAME_END)
        Assert.Equal(0x16, frame[frame.Length - 1]);

        // 帧长度 = 68H(1) + T(1) + A(7) + 68H(1) + C(1) + L(1) + DATA(4) + CS(1) + 16H(1) = 18
        Assert.Equal(18, frame.Length);
    }

    #endregion

    #region 响应解析

    [Fact]
    public void ParseResponse_ShortFrame()
    {
        var result = Cjt188Client.ParseResponse(new byte[] { 0x68 }, 0x01);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseResponse_BadHeader()
    {
        var frame = new byte[15];
        frame[0] = 0x00; // wrong
        var result = Cjt188Client.ParseResponse(frame, 0x01);
        Assert.False(result.IsSuccess);
    }

    #endregion

    #region 仪表类型

    [Fact]
    public void MeterTypeConstants()
    {
        Assert.Equal(0x10, Cjt188Client.TYPE_WATER_COLD);
        Assert.Equal(0x11, Cjt188Client.TYPE_WATER_HOT);
        Assert.Equal(0x20, Cjt188Client.TYPE_HEAT);
        Assert.Equal(0x30, Cjt188Client.TYPE_GAS);
        Assert.Equal(0x40, Cjt188Client.TYPE_ELECTRIC);
    }

    #endregion

    #region ToString

    [Fact]
    public void ToString_ContainsInfo()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterType = Cjt188Client.TYPE_WATER_COLD;
        string s = client.ToString();
        Assert.Contains("Cjt188Client", s);
        Assert.Contains("10", s); // TYPE_WATER_COLD = 0x10
    }

    #endregion
}

/// <summary>
/// Mock 串口，仅用于单元测试构造。
/// </summary>
internal class MockSerialPort : ISerialPort
{
    public string PortName { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Parity Parity { get; set; } = Parity.Even;
    public int ReadTimeout { get; set; } = 1000;
    public int WriteTimeout { get; set; } = 1000;
    public bool IsOpen { get; } = false;
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }

    public void Open() { }
    public void Close() { }
    public int Read(byte[] buffer, int offset, int count) => 0;
    public void Write(byte[] buffer, int offset, int count) { }
    public void Dispose() { }
}
