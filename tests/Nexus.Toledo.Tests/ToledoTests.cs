using System;
using System.Text;
using Xunit;
using Nexus.Toledo;

namespace Nexus.Toledo.Tests;

public class ToledoTests
{
    #region ToledoStandardData — 标准连续输出

    [Fact]
    public void ParseFrom_StandardOutput_BasicWeight()
    {
        // 构造标准连续输出帧: 0x02 + status1 + status2 + status3 + weight(6) + tare(6) + ...
        byte[] buffer = new byte[20];
        buffer[0] = 0x02; // Standard output marker
        buffer[1] = 0x12; // dp=2 (no decimal shift)
        buffer[2] = 0x11; // Net=true(bit0), kg flag(bit4)
        buffer[3] = 0x00; // Unit=0 (kg)

        // Weight at offset 4, 6 bytes ASCII
        byte[] weightBytes = Encoding.ASCII.GetBytes("012.50");
        Array.Copy(weightBytes, 0, buffer, 4, 6);

        // Tare at offset 10, 6 bytes ASCII
        byte[] tareBytes = Encoding.ASCII.GetBytes("000.00");
        Array.Copy(tareBytes, 0, buffer, 10, 6);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(12.50f, result.Content.Weight);
        Assert.Equal(0.00f, result.Content.Tare);
        Assert.True(result.Content.IsNet);
        Assert.Equal("kg", result.Content.Unit);
    }

    [Fact]
    public void ParseFrom_StandardOutput_DecimalPlaces()
    {
        // dp=3 → divide by 10
        byte[] buffer = new byte[20];
        buffer[0] = 0x02;
        buffer[1] = 0x13; // dp=3
        buffer[2] = 0x00;
        buffer[3] = 0x00;

        byte[] weightBytes = Encoding.ASCII.GetBytes("000125");
        Array.Copy(weightBytes, 0, buffer, 4, 6);
        byte[] tareBytes = Encoding.ASCII.GetBytes("000000");
        Array.Copy(tareBytes, 0, buffer, 10, 6);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess, result.Message);
        // 125 / 10 = 12.5
        Assert.Equal(12.5f, result.Content.Weight);
    }

    [Fact]
    public void ParseFrom_StandardOutput_DynamicState()
    {
        byte[] buffer = new byte[20];
        buffer[0] = 0x02;
        buffer[1] = 0x12;
        buffer[2] = 0x08; // Dynamic=true (bit 3)
        buffer[3] = 0x00;

        byte[] weightBytes = Encoding.ASCII.GetBytes("010.00");
        Array.Copy(weightBytes, 0, buffer, 4, 6);
        byte[] tareBytes = Encoding.ASCII.GetBytes("000.00");
        Array.Copy(tareBytes, 0, buffer, 10, 6);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess);
        Assert.True(result.Content.IsDynamic);
    }

    #endregion

    #region ToledoStandardData — 扩展输出

    [Fact]
    public void ParseFrom_ExpandOutput_BasicWeight()
    {
        byte[] buffer = new byte[30];
        buffer[0] = 0x01; // Expand output marker
        buffer[2] = 0x02; // Unit = kg (low nibble)
        buffer[3] = 0x01; // Net = true (bit 0)
        buffer[4] = 0x01; // DataValid = true (bit 0)

        // Weight at offset 6, 9 bytes ASCII
        byte[] weightBytes = Encoding.ASCII.GetBytes("  125.50 ");
        Array.Copy(weightBytes, 0, buffer, 6, 9);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess, result.Message);
        Assert.True(result.Content.IsExpandOutput);
        Assert.Equal(125.50f, result.Content.Weight);
        Assert.Equal("kg", result.Content.Unit);
        Assert.True(result.Content.IsNet);
        Assert.True(result.Content.DataValid);
    }

    #endregion

    #region 错误处理

    [Fact]
    public void ParseFrom_ShortData()
    {
        var result = ToledoStandardData.ParseFrom(new byte[5]);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseFrom_NullData()
    {
        var result = ToledoStandardData.ParseFrom(null);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseFrom_UnknownMode()
    {
        byte[] buffer = new byte[20];
        buffer[0] = 0xFF; // Unknown mode
        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.False(result.IsSuccess);
    }

    #endregion

    #region 单位解码

    [Theory]
    [InlineData(0, true, "kg")]
    [InlineData(0, false, "lb")]
    [InlineData(1, false, "g")]
    [InlineData(2, false, "t")]
    [InlineData(3, false, "oz")]
    [InlineData(7, false, "newton")]
    public void UnitDecoding_StandardOutput(int code, bool isKg, string expected)
    {
        // 通过构造标准帧间接测试
        byte[] buffer = new byte[20];
        buffer[0] = 0x02;
        buffer[1] = 0x12;
        buffer[2] = isKg ? (byte)0x10 : (byte)0x00; // bit 4 = kg flag
        buffer[3] = (byte)code;

        byte[] weightBytes = Encoding.ASCII.GetBytes("000.00");
        Array.Copy(weightBytes, 0, buffer, 4, 6);
        byte[] tareBytes = Encoding.ASCII.GetBytes("000.00");
        Array.Copy(tareBytes, 0, buffer, 10, 6);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Content.Unit);
    }

    #endregion

    #region 构造

    [Fact]
    public void Constructor_SetsDefaults()
    {
        var client = new ToledoClient("192.168.1.1");
        string s = client.ToString();
        Assert.Contains("ToledoClient", s);
    }

    [Fact]
    public void DefaultConstructor_Data()
    {
        var data = new ToledoStandardData();
        Assert.Equal("kg", data.Unit);
        Assert.True(data.DataValid);
        Assert.Equal(0f, data.Weight);
    }

    #endregion
}
