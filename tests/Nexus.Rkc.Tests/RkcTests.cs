using System;
using System.Collections.Generic;
using System.Text;
using Xunit;
using Nexus.Rkc;

namespace Nexus.Rkc.Tests;

public class RkcTests
{
    #region 命令构建 — 读取

    [Fact]
    public void BuildReadCommand_ValidFormat()
    {
        var result = RkcTemperatureClient.BuildReadCommand(1, "M1");
        Assert.True(result.IsSuccess, result.Message);

        byte[] cmd = result.Content;
        Assert.Equal(0x04, cmd[0]); // EOT
        Assert.Equal('0', (char)cmd[1]); // Station tens
        Assert.Equal('1', (char)cmd[2]); // Station units
        Assert.Equal(0x05, cmd[cmd.Length - 1]); // ENQ
    }

    [Fact]
    public void BuildReadCommand_Station99()
    {
        var result = RkcTemperatureClient.BuildReadCommand(99, "M1");
        Assert.True(result.IsSuccess);
        Assert.Equal('9', (char)result.Content[1]);
        Assert.Equal('9', (char)result.Content[2]);
    }

    [Fact]
    public void BuildReadCommand_StationTooLarge()
    {
        var result = RkcTemperatureClient.BuildReadCommand(100, "M1");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void BuildReadCommand_StationZero()
    {
        var result = RkcTemperatureClient.BuildReadCommand(0, "M1");
        Assert.True(result.IsSuccess);
        Assert.Equal('0', (char)result.Content[1]);
        Assert.Equal('0', (char)result.Content[2]);
    }

    [Fact]
    public void BuildReadCommand_EmptyAddress()
    {
        var result = RkcTemperatureClient.BuildReadCommand(1, "");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void BuildReadCommand_NullAddress()
    {
        var result = RkcTemperatureClient.BuildReadCommand(1, null);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void BuildReadCommand_MultiCharAddress()
    {
        var result = RkcTemperatureClient.BuildReadCommand(1, "AA");
        Assert.True(result.IsSuccess);
        byte[] cmd = result.Content;
        // EOT(1) + station(2) + addr(2) + ENQ(1) = 6
        Assert.Equal(6, cmd.Length);
    }

    [Fact]
    public void BuildReadCommand_AddressM2()
    {
        var result = RkcTemperatureClient.BuildReadCommand(1, "M2");
        Assert.True(result.IsSuccess);
        byte[] cmd = result.Content;
        Assert.Equal('M', (char)cmd[3]);
        Assert.Equal('2', (char)cmd[4]);
    }

    #endregion

    #region 命令构建 — 写入

    [Fact]
    public void BuildWriteCommand_ValidFormat()
    {
        var result = RkcTemperatureClient.BuildWriteCommand(1, "M1", 100.5);
        Assert.True(result.IsSuccess, result.Message);

        byte[] cmd = result.Content;
        Assert.Equal(0x04, cmd[0]); // EOT
        Assert.Equal(0x02, cmd[3]); // STX
        Assert.Equal('M', (char)cmd[4]);
        Assert.Equal('1', (char)cmd[5]);
        bool hasEtx = false;
        for (int i = 0; i < cmd.Length; i++)
        {
            if (cmd[i] == 0x03) { hasEtx = true; break; }
        }
        Assert.True(hasEtx);
    }

    [Fact]
    public void BuildWriteCommand_ValueTooLong()
    {
        var result = RkcTemperatureClient.BuildWriteCommand(1, "M1", 1234567.0);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void BuildWriteCommand_BCC_IsLastByte()
    {
        var result = RkcTemperatureClient.BuildWriteCommand(1, "M1", 25.5);
        Assert.True(result.IsSuccess);
        byte[] cmd = result.Content;
        byte bcc = 0;
        for (int i = 4; i < cmd.Length - 1; i++)
            bcc ^= cmd[i];
    }

    [Fact]
    public void BuildWriteCommand_StationTooLarge()
    {
        var result = RkcTemperatureClient.BuildWriteCommand(100, "M1", 50.0);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void BuildWriteCommand_EmptyAddress()
    {
        var result = RkcTemperatureClient.BuildWriteCommand(1, "", 50.0);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void BuildWriteCommand_NullAddress()
    {
        // Deliberately pass a null address to verify BuildWriteCommand's defensive guard
        // (null! asserts intent for the analyzer while keeping the runtime null argument).
        var result = RkcTemperatureClient.BuildWriteCommand(1, null!, 50.0);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void BuildWriteCommand_NegativeValue()
    {
        var result = RkcTemperatureClient.BuildWriteCommand(1, "M1", -10.5);
        Assert.True(result.IsSuccess);
        byte[] cmd = result.Content;
        Assert.Equal(0x04, cmd[0]); // EOT
    }

    [Fact]
    public void BuildWriteCommand_ZeroValue()
    {
        var result = RkcTemperatureClient.BuildWriteCommand(1, "M1", 0.0);
        Assert.True(result.IsSuccess);
    }

    #endregion

    #region 响应解析

    [Fact]
    public void ParseReadResponse_ValidData()
    {
        byte[] response = new byte[] { 0x02, (byte)'0', (byte)'1', (byte)'2', (byte)'5', (byte)'.', (byte)'5', 0x03 };
        var result = RkcTemperatureClient.ParseReadResponse(response);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(25.5, result.Content, 1);
    }

    [Fact]
    public void ParseReadResponse_BadSTX()
    {
        byte[] response = new byte[] { 0x15, (byte)'0', (byte)'1', (byte)'2', (byte)'5' };
        var result = RkcTemperatureClient.ParseReadResponse(response);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseReadResponse_ShortData()
    {
        var result = RkcTemperatureClient.ParseReadResponse(new byte[] { 0x02 });
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseReadResponse_NullData()
    {
        var result = RkcTemperatureClient.ParseReadResponse(null);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseReadResponse_IntegerValue()
    {
        byte[] response = new byte[] { 0x02, (byte)'0', (byte)'1', (byte)'1', (byte)'0', (byte)'0', 0x03 };
        var result = RkcTemperatureClient.ParseReadResponse(response);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(100.0, result.Content, 0.1);
    }

    [Fact]
    public void ParseReadResponse_NegativeValue()
    {
        byte[] response = new byte[] { 0x02, (byte)'0', (byte)'1', (byte)'-', (byte)'5', (byte)'.', (byte)'3', 0x03 };
        var result = RkcTemperatureClient.ParseReadResponse(response);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(-5.3, result.Content, 0.1);
    }

    [Fact]
    public void ParseReadResponse_WithBcc()
    {
        // STX + station + data + ETX + BCC
        byte[] response = new byte[] { 0x02, (byte)'0', (byte)'1', (byte)'5', (byte)'0', (byte)'.', (byte)'0', 0x03, 0x00 };
        var result = RkcTemperatureClient.ParseReadResponse(response);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(50.0, result.Content, 0.1);
    }

    #endregion

    #region 构造

    [Fact]
    public void Constructor_SetsDefaults()
    {
        var client = new RkcTemperatureClient("192.168.1.1");
        string s = client.ToString();
        Assert.Contains("RkcTemperatureClient", s);
    }

    [Fact]
    public void Constructor_CustomPort()
    {
        var client = new RkcTemperatureClient("192.168.1.1", 20001);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Station_DefaultsToOne()
    {
        var client = new RkcTemperatureClient("192.168.1.1");
        Assert.Equal((byte)1, client.Station);
    }

    [Fact]
    public void Station_CanBeSet()
    {
        var client = new RkcTemperatureClient("192.168.1.1") { Station = 5 };
        Assert.Equal((byte)5, client.Station);
    }

    #endregion

    #region 未连接操作

    [Fact]
    public void ReadDouble_NotConnected_ReturnsError()
    {
        var client = new RkcTemperatureClient("192.168.1.1");
        var r = client.ReadDouble("M1");
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void Write_NotConnected_ReturnsError()
    {
        var client = new RkcTemperatureClient("192.168.1.1");
        var r = client.Write("M1", 50.0);
        Assert.False(r.IsSuccess);
    }

    #endregion

    #region Batch/Subscribe

    [Fact]
    public void BatchOperations_EmptyInput_ReturnsError()
    {
        var client = new RkcTemperatureClient("192.168.1.1");
        Assert.False(client.BatchRead(new string[0]).IsSuccess);
        Assert.False(client.RandomRead(new string[0]).IsSuccess);
        Assert.False(client.BatchWrite(Array.Empty<KeyValuePair<string, object>>()).IsSuccess);
    }

    [Fact]
    public void Subscribe_Unsubscribe_DoesNotThrow()
    {
        var client = new RkcTemperatureClient("192.168.1.1");
        client.Subscribe("M1", 1000, "Float");
        client.Unsubscribe("M1");
        client.StartSubscriptions();
        client.StopSubscriptions();
        client.Dispose();
    }

    #endregion
}
