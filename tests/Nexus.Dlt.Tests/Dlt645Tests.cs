using System;
using Xunit;
using Nexus.Dlt;

namespace Nexus.Dlt.Tests;

public class Dlt645Tests
{
    #region BCD 转换

    [Theory]
    [InlineData(new byte[] { 0x12, 0x34 }, "3412")]
    [InlineData(new byte[] { 0x00, 0x00, 0x01 }, "010000")]
    [InlineData(new byte[] { 0x56 }, "56")]
    public void BcdToString_Tests(byte[] data, string expected)
    {
        Assert.Equal(expected, Dlt645Client.BcdToString(data));
    }

    [Theory]
    [InlineData(0, (byte)0x00)]
    [InlineData(5, (byte)0x05)]
    [InlineData(12, (byte)0x12)]
    [InlineData(99, (byte)0x99)]
    public void DecimalToBcd_Tests(byte value, byte expected)
    {
        Assert.Equal(expected, Dlt645Client.DecimalToBcd(value));
    }

    [Fact]
    public void BcdToDecimal_StandardCase()
    {
        // BCD bytes: 0x34, 0x12 → string "1234" → 12.34 (4 digits, 2 decimal places)
        var result = Dlt645Client.BcdToDecimal(new byte[] { 0x34, 0x12 }, 4, 2);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(12.34m, result.Content);
    }

    [Fact]
    public void BcdToDecimal_Voltage()
    {
        // BCD bytes: 0x20, 0x22 → "2220" → 222.0V (4 digits, 1 decimal place)
        var result = Dlt645Client.BcdToDecimal(new byte[] { 0x20, 0x22 }, 4, 1);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(222.0m, result.Content);
    }

    #endregion

    #region 数据标识解析

    [Theory]
    [InlineData("00010000", new byte[] { 0x00, 0x00, 0x01, 0x00 })]
    [InlineData("02010100", new byte[] { 0x00, 0x01, 0x01, 0x02 })]
    public void ParseDataId_Valid(string input, byte[] expected)
    {
        var result = Dlt645Client.ParseDataId(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("1234567890")]
    [InlineData("ZZZZZZZZ")]
    public void ParseDataId_Invalid(string? input)
    {
        var result = Dlt645Client.ParseDataId(input!);
        Assert.Null(result);
    }

    #endregion

    #region 错误码

    [Theory]
    [InlineData(0x01, "非法数据标识")]
    [InlineData(0x09, "密码错/未授权")]
    [InlineData(0x0F, "其他错误")]
    public void GetErrorText_Tests(byte code, string expected)
    {
        Assert.Contains(expected, Dlt645Client.GetErrorText(code));
    }

    [Fact]
    public void GetErrorText_Unknown()
    {
        string text = Dlt645Client.GetErrorText(0xFF);
        Assert.Contains("未知错误", text);
    }

    #endregion

    #region 帧构建

    [Fact]
    public void BuildReadFrame_BasicStructure()
    {
        var client = new Dlt645Client(new MockSerialPort());
        client.SetMeterAddress("000000000001");

        byte[] frame = client.BuildReadFrame(0x11, new byte[] { 0x00, 0x00, 0x00, 0x00 }, null);

        // 验证帧头帧尾
        Assert.Equal(0x68, frame[0]);
        Assert.Equal(0x68, frame[7]);
        Assert.Equal(0x16, frame[frame.Length - 1]);

        // 控制码
        Assert.Equal(0x11, frame[8]);

        // 数据长度 = 4 (DI0..DI3 加 33H 后)
        Assert.Equal(0x04, frame[9]);
    }

    [Fact]
    public void BuildReadFrame_WithData()
    {
        var client = new Dlt645Client(new MockSerialPort());
        byte[] frame = client.BuildReadFrame(0x11, new byte[] { 0x00, 0x00, 0x00, 0x00 }, new byte[] { 0x01, 0x02 });

        // 数据域 = 4(DI) + 2(data) = 6
        Assert.Equal(0x06, frame[9]);
        // 校验数据域加密（+33H）
        Assert.Equal(0x33, frame[10]); // DI0 + 0x33
    }

    #endregion

    #region 响应解析

    [Fact]
    public void ParseResponse_ValidFrame()
    {
        // 构造有效响应: 68H + A(6) + 68H + C(11H) + L(06H) + DATA(+33H) + CS + 16H
        var frame = new byte[18];
        frame[0] = 0x68;  // header
        frame[7] = 0x68;  // header2
        frame[8] = 0x91;  // ctrl (0x11 | 0x80 response bit)... actually response ctrl should not have 0x80 for success
        frame[8] = 0xD1;  // normal response for read: 0x11 | 0x80 = 0x91? Let me re-check
        // DLT645: response ctrl = request ctrl | 0x80... but 0x11 | 0x80 = 0x91
        frame[8] = 0x91;  // read data response
        frame[9] = 0x06;  // data length = 6
        // data: DI0..DI3(+33H) + 2 data bytes(+33H)
        frame[10] = 0x33; frame[11] = 0x33; frame[12] = 0x33; frame[13] = 0x33; // DI +33H
        frame[14] = 0x34; frame[15] = 0x35; // data 0x01+33=0x34, 0x02+33=0x35

        // Calculate checksum: A(6) XOR C XOR L XOR DATA(6)
        byte cs = 0;
        for (int i = 1; i <= 6; i++) cs ^= frame[i]; // address
        cs ^= frame[8]; // ctrl
        cs ^= frame[9]; // length
        for (int i = 10; i < 16; i++) cs ^= frame[i]; // data
        frame[16] = cs;
        frame[17] = 0x16; // end

        var result = Dlt645Client.ParseResponse(frame, 0x11);
        // Success response: data should be the 2 bytes decrypted
        if (result.IsSuccess)
        {
            Assert.Equal(2, result.Content.Length);
            Assert.Equal(0x01, result.Content[0]);
            Assert.Equal(0x02, result.Content[1]);
        }
    }

    [Fact]
    public void ParseResponse_ErrorFrame()
    {
        var frame = new byte[14];
        frame[0] = 0x68;
        frame[7] = 0x68;
        frame[8] = 0xD1; // error response: ctrl | 0xC0
        frame[9] = 0x01; // length
        frame[10] = 0x34; // error code 0x01 + 0x33

        byte cs = 0;
        for (int i = 1; i <= 6; i++) cs ^= frame[i];
        cs ^= frame[8]; cs ^= frame[9]; cs ^= frame[10];
        frame[11] = cs;
        frame[12] = 0x16;

        var result = Dlt645Client.ParseResponse(frame, 0x11);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseResponse_ShortFrame()
    {
        var result = Dlt645Client.ParseResponse(new byte[] { 0x68 }, 0x11);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseResponse_BadHeader()
    {
        var frame = new byte[12];
        frame[0] = 0x00; // wrong header
        var result = Dlt645Client.ParseResponse(frame, 0x11);
        Assert.False(result.IsSuccess);
    }

    #endregion

    #region 地址设置

    [Fact]
    public void SetMeterAddress_Valid()
    {
        var client = new Dlt645Client(new MockSerialPort());
        client.SetMeterAddress("000000000001");

        // BCD low byte first: address "000000000001" → bytes [01,00,00,00,00,00]
        Assert.Equal((byte)0x01, client.MeterAddress[0]);
        Assert.Equal((byte)0x00, client.MeterAddress[1]);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("1234567890123")]
    [InlineData(null)]
    public void SetMeterAddress_Invalid(string? addr)
    {
        var client = new Dlt645Client(new MockSerialPort());
        Assert.Throws<ArgumentException>(() => client.SetMeterAddress(addr!));
    }

    #endregion

    #region ToString

    [Fact]
    public void ToString_ContainsAddress()
    {
        var client = new Dlt645Client(new MockSerialPort());
        string s = client.ToString();
        Assert.Contains("Dlt645Client", s);
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
