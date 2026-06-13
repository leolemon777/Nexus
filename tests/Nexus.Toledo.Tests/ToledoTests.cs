using System;
using System.Collections.Generic;
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
        var result = ToledoStandardData.ParseFrom(null!);
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
    public void Constructor_CustomPort()
    {
        var client = new ToledoClient("192.168.1.1", 9000, 3000);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void DefaultConstructor_Data()
    {
        var data = new ToledoStandardData();
        Assert.Equal("kg", data.Unit);
        Assert.True(data.DataValid);
        Assert.Equal(0f, data.Weight);
    }

    [Fact]
    public void ToString_ContainsWeightAndUnit()
    {
        var data = new ToledoStandardData { Weight = 42.5f, Unit = "kg" };
        Assert.Contains("42.5", data.ToString());
        Assert.Contains("kg", data.ToString());
    }

    #endregion

    #region ToledoStandardData — 更多标准输出场景

    [Fact]
    public void ParseFrom_StandardOutput_NegativeWeight()
    {
        byte[] buffer = new byte[20];
        buffer[0] = 0x02;
        buffer[1] = 0x12;
        buffer[2] = 0x00; // Positive = false (bit 1 clear)
        buffer[3] = 0x00;

        byte[] weightBytes = Encoding.ASCII.GetBytes("-05.20");
        Array.Copy(weightBytes, 0, buffer, 4, 6);
        byte[] tareBytes = Encoding.ASCII.GetBytes("000.00");
        Array.Copy(tareBytes, 0, buffer, 10, 6);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(-5.20f, result.Content.Weight);
    }

    [Fact]
    public void ParseFrom_StandardOutput_BeyondScope()
    {
        byte[] buffer = new byte[20];
        buffer[0] = 0x02;
        buffer[1] = 0x12;
        buffer[2] = 0x04; // BeyondScope = true (bit 2)
        buffer[3] = 0x00;

        byte[] weightBytes = Encoding.ASCII.GetBytes("999.99");
        Array.Copy(weightBytes, 0, buffer, 4, 6);
        byte[] tareBytes = Encoding.ASCII.GetBytes("000.00");
        Array.Copy(tareBytes, 0, buffer, 10, 6);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess);
        Assert.True(result.Content.BeyondScope);
    }

    [Fact]
    public void ParseFrom_StandardOutput_PrintFlag()
    {
        byte[] buffer = new byte[20];
        buffer[0] = 0x02;
        buffer[1] = 0x12;
        buffer[2] = 0x00;
        buffer[3] = 0x08; // IsPrint = true (bit 3)

        byte[] weightBytes = Encoding.ASCII.GetBytes("010.00");
        Array.Copy(weightBytes, 0, buffer, 4, 6);
        byte[] tareBytes = Encoding.ASCII.GetBytes("000.00");
        Array.Copy(tareBytes, 0, buffer, 10, 6);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess);
        Assert.True(result.Content.IsPrint);
    }

    [Fact]
    public void ParseFrom_StandardOutput_TenExtend()
    {
        byte[] buffer = new byte[20];
        buffer[0] = 0x02;
        buffer[1] = 0x12;
        buffer[2] = 0x00;
        buffer[3] = 0x10; // IsTenExtend = true (bit 4)

        byte[] weightBytes = Encoding.ASCII.GetBytes("010.00");
        Array.Copy(weightBytes, 0, buffer, 4, 6);
        byte[] tareBytes = Encoding.ASCII.GetBytes("000.00");
        Array.Copy(tareBytes, 0, buffer, 10, 6);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess);
        Assert.True(result.Content.IsTenExtend);
    }

    [Fact]
    public void ParseFrom_StandardOutput_GrossWeight()
    {
        byte[] buffer = new byte[20];
        buffer[0] = 0x02;
        buffer[1] = 0x12;
        buffer[2] = 0x00; // IsNet = false
        buffer[3] = 0x00;

        byte[] weightBytes = Encoding.ASCII.GetBytes("015.75");
        Array.Copy(weightBytes, 0, buffer, 4, 6);
        byte[] tareBytes = Encoding.ASCII.GetBytes("005.75");
        Array.Copy(tareBytes, 0, buffer, 10, 6);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess);
        Assert.False(result.Content.IsNet);
        Assert.Equal(15.75f, result.Content.Weight);
        Assert.Equal(5.75f, result.Content.Tare);
    }

    [Fact]
    public void ParseFrom_StandardOutput_DecimalPlaces_Dp0()
    {
        // dp=0: weight *= 100
        byte[] buffer = new byte[20];
        buffer[0] = 0x02;
        buffer[1] = 0x10; // dp=0
        buffer[2] = 0x00;
        buffer[3] = 0x00;

        byte[] weightBytes = Encoding.ASCII.GetBytes("000125");
        Array.Copy(weightBytes, 0, buffer, 4, 6);
        byte[] tareBytes = Encoding.ASCII.GetBytes("000000");
        Array.Copy(tareBytes, 0, buffer, 10, 6);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(12500f, result.Content.Weight); // 125 * 100
    }

    [Fact]
    public void ParseFrom_StandardOutput_SourceData_Preserved()
    {
        byte[] buffer = new byte[20];
        buffer[0] = 0x02;
        buffer[1] = 0x12;
        buffer[2] = 0x00;
        buffer[3] = 0x00;

        byte[] weightBytes = Encoding.ASCII.GetBytes("010.00");
        Array.Copy(weightBytes, 0, buffer, 4, 6);
        byte[] tareBytes = Encoding.ASCII.GetBytes("000.00");
        Array.Copy(tareBytes, 0, buffer, 10, 6);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Content.SourceData);
        Assert.Equal(buffer, result.Content.SourceData);
    }

    #endregion

    #region 扩展输出更多场景

    [Fact]
    public void ParseFrom_ExpandOutput_DynamicFlag()
    {
        byte[] buffer = new byte[30];
        buffer[0] = 0x01;
        buffer[2] = 0x42; // unit=2(kg), dynamic=true (bit 6)
        buffer[3] = 0x01; // Net=true
        buffer[4] = 0x01; // DataValid=true

        byte[] weightBytes = Encoding.ASCII.GetBytes("  100.50 ");
        Array.Copy(weightBytes, 0, buffer, 6, 9);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess, result.Message);
        Assert.True(result.Content.IsDynamic);
        Assert.True(result.Content.IsExpandOutput);
    }

    [Fact]
    public void ParseFrom_ExpandOutput_BeyondScope()
    {
        byte[] buffer = new byte[30];
        buffer[0] = 0x01;
        buffer[2] = 0x02;
        buffer[3] = 0x00;
        buffer[4] = 0x02; // BeyondScope=true (bit 1)

        byte[] weightBytes = Encoding.ASCII.GetBytes("  999.99 ");
        Array.Copy(weightBytes, 0, buffer, 6, 9);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess);
        Assert.True(result.Content.BeyondScope);
    }

    [Fact]
    public void ParseFrom_ExpandOutput_TareType()
    {
        byte[] buffer = new byte[30];
        buffer[0] = 0x01;
        buffer[2] = 0x02;
        buffer[3] = 0x05; // Net=true(bit0), TareType=2(bits 1-2): 0b101 → TareType = (5&6)>>1 = 2
        buffer[4] = 0x01;

        byte[] weightBytes = Encoding.ASCII.GetBytes("  050.00 ");
        Array.Copy(weightBytes, 0, buffer, 6, 9);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess);
        Assert.True(result.Content.IsNet);
        Assert.Equal(2, result.Content.TareType);
    }

    [Fact]
    public void ParseFrom_ExpandOutput_WithTare()
    {
        byte[] buffer = new byte[30];
        buffer[0] = 0x01;
        buffer[2] = 0x02;
        buffer[3] = 0x01;
        buffer[4] = 0x01;

        byte[] weightBytes = Encoding.ASCII.GetBytes("  100.00 ");
        Array.Copy(weightBytes, 0, buffer, 6, 9);
        byte[] tareBytes = Encoding.ASCII.GetBytes("  10.00 ");
        Array.Copy(tareBytes, 0, buffer, 15, 8);

        var result = ToledoStandardData.ParseFrom(buffer);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(100.00f, result.Content.Weight);
        Assert.Equal(10.00f, result.Content.Tare);
    }

    #endregion

    #region 操作未连接

    [Fact]
    public void ReadWeight_NotConnected_ReturnsError()
    {
        var client = new ToledoClient("192.168.1.1");
        var r = client.ReadWeight();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void ReadRaw_NotConnected_ReturnsError()
    {
        var client = new ToledoClient("192.168.1.1");
        var r = client.ReadRaw();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void BatchOperations_EmptyInput_ReturnsError()
    {
        var client = new ToledoClient("192.168.1.1");
        Assert.False(client.BatchRead(new string[0]).IsSuccess);
        Assert.False(client.RandomRead(new string[0]).IsSuccess);
        Assert.False(client.BatchWrite(Array.Empty<KeyValuePair<string, object>>()).IsSuccess);
    }

    #endregion

    #region ISubscribeDevice

    [Fact]
    public void Subscribe_Unsubscribe_DoesNotThrow()
    {
        var client = new ToledoClient("192.168.1.1");
        client.Subscribe("weight", 1000, "Float");
        client.Unsubscribe("weight");
        client.StartSubscriptions();
        client.StopSubscriptions();
        client.Dispose();
    }

    #endregion
}
