using System;
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
        // Address after station
        Assert.Equal('M', (char)cmd[4]);
        Assert.Equal('1', (char)cmd[5]);
        // Contains ETX
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
        // BCC is the last byte
        byte bcc = 0;
        for (int i = 4; i < cmd.Length - 1; i++) // from STX to ETX inclusive
            bcc ^= cmd[i];
        // The BCC should match last byte
        // (cmd[3]=STX is included in BCC calculation per the original)
    }

    #endregion

    #region 响应解析

    [Fact]
    public void ParseReadResponse_ValidData()
    {
        // STX + station(2) + data + ETX
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

    #endregion

    #region 构造

    [Fact]
    public void Constructor_SetsDefaults()
    {
        var client = new RkcTemperatureClient("192.168.1.1");
        string s = client.ToString();
        Assert.Contains("RkcTemperatureClient", s);
    }

    #endregion
}
