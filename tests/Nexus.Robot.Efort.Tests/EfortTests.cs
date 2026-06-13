using System;
using System.Text;
using Xunit;
using Nexus.Robot.Efort;

namespace Nexus.Robot.Efort.Tests;

public class EfortTests
{
    #region 命令构建

    [Fact]
    public void BuildReadCommand_Length38()
    {
        var client = new EfortClient("127.0.0.1");
        byte[] cmd = client.BuildReadCommand();
        Assert.Equal(38, cmd.Length);
    }

    [Fact]
    public void BuildReadCommand_ContainsMessageHead()
    {
        var client = new EfortClient("127.0.0.1");
        byte[] cmd = client.BuildReadCommand();
        string head = Encoding.ASCII.GetString(cmd, 0, 11);
        Assert.Equal("MessageHead", head);
    }

    [Fact]
    public void BuildReadCommand_ContainsMessageTail()
    {
        var client = new EfortClient("127.0.0.1");
        byte[] cmd = client.BuildReadCommand();
        string tail = Encoding.ASCII.GetString(cmd, 22, 11);
        Assert.Equal("MessageTail", tail);
    }

    [Fact]
    public void BuildReadCommand_CommandCode1001()
    {
        var client = new EfortClient("127.0.0.1");
        byte[] cmd = client.BuildReadCommand();
        ushort code = BitConverter.ToUInt16(cmd, 18);
        Assert.Equal((ushort)1001, code);
    }

    [Fact]
    public void BuildReadCommand_TotalLength()
    {
        var client = new EfortClient("127.0.0.1");
        byte[] cmd = client.BuildReadCommand();
        ushort totalLen = BitConverter.ToUInt16(cmd, 16);
        Assert.Equal((ushort)38, totalLen);
    }

    [Fact]
    public void BuildReadCommand_HeartbeatIncrements()
    {
        var client = new EfortClient("127.0.0.1");
        byte[] cmd1 = client.BuildReadCommand();
        byte[] cmd2 = client.BuildReadCommand();
        ushort hb1 = BitConverter.ToUInt16(cmd1, 20);
        ushort hb2 = BitConverter.ToUInt16(cmd2, 20);
        Assert.Equal((ushort)1, hb2 - hb1);
    }

    #endregion

    #region EfortData 解析

    [Fact]
    public void ParseFrom_DataTooShort()
    {
        var result = EfortData.ParseFrom(new byte[100]);
        Assert.False(result.IsSuccess);
        Assert.Contains("788", result.Message);
    }

    [Fact]
    public void ParseFrom_NullData()
    {
        var result = EfortData.ParseFrom(null!);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseFrom_MinimalValidData()
    {
        byte[] data = new byte[788];
        // Write "MessageHead"
        Encoding.ASCII.GetBytes("MessageHead").CopyTo(data, 0);
        // Write PacketOrders at offset 18
        BitConverter.GetBytes((ushort)1002).CopyTo(data, 18);
        // Write PacketHeartbeat at offset 20
        BitConverter.GetBytes((ushort)42).CopyTo(data, 20);
        // Write status bytes
        data[22] = 0; // No error
        data[23] = 1; // No emergency stop
        data[24] = 1; // Has authority
        data[25] = 1; // Servo on
        data[26] = 0; // Not moving
        data[27] = 1; // Program running
        data[28] = 1; // Program loaded
        data[29] = 0; // Not held
        // Mode = 2 (auto)
        BitConverter.GetBytes((ushort)2).CopyTo(data, 30);
        // Speed = 50%
        BitConverter.GetBytes((ushort)50).CopyTo(data, 32);
        // Write "MessageTail" at offset 772
        Encoding.ASCII.GetBytes("MessageTail").CopyTo(data, 772);

        var result = EfortData.ParseFrom(data);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("MessageHead", result.Content.PacketStart);
        Assert.Equal((ushort)1002, result.Content.PacketOrders);
        Assert.Equal((ushort)42, result.Content.PacketHeartbeat);
        Assert.Equal((byte)0, result.Content.ErrorStatus);
        Assert.Equal((byte)1, result.Content.EmergencyStopStatus);
        Assert.Equal((byte)1, result.Content.ServoStatus);
        Assert.Equal((ushort)2, result.Content.ModeStatus);
        Assert.Equal((ushort)50, result.Content.SpeedStatus);
        Assert.Equal("MessageTail", result.Content.PacketEnd);
    }

    [Fact]
    public void ParseFrom_AxisPositions()
    {
        byte[] data = new byte[788];
        // Write 7 floats at offset 548
        for (int i = 0; i < 7; i++)
            BitConverter.GetBytes(10.5f + i).CopyTo(data, 548 + 4 * i);
        // Write 6 floats at offset 576 (Cartesian)
        for (int i = 0; i < 6; i++)
            BitConverter.GetBytes(100.0f + i).CopyTo(data, 576 + 4 * i);

        var result = EfortData.ParseFrom(data);
        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Content.AxisPositions.Length);
        Assert.Equal(10.5f, result.Content.AxisPositions[0]);
        Assert.Equal(6, result.Content.CartesianPositions.Length);
        Assert.Equal(100.0f, result.Content.CartesianPositions[0]);
    }

    #endregion

    #region 构造

    [Fact]
    public void Constructor_SetsDefaults()
    {
        var client = new EfortClient("192.168.1.1");
        string s = client.ToString();
        Assert.Contains("8008", s);
    }

    #endregion

    #region 扩展覆盖

    [Fact]
    public void Constructor_CustomPort()
    {
        var client = new EfortClient("10.0.0.1", 9000);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var client = new EfortClient("192.168.1.1");
        client.Dispose();
    }

    [Fact]
    public void SetLogger_DoesNotThrow()
    {
        var client = new EfortClient("192.168.1.1");
        client.SetLogger(NullLogger.Instance);
    }

    [Fact]
    public void ReadRobotData_NotConnected_ReturnsError()
    {
        var client = new EfortClient("127.0.0.1");
        var r = client.ReadRobotData();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void BuildReadCommand_ConsistentHead()
    {
        var client = new EfortClient("127.0.0.1");
        byte[] cmd = client.BuildReadCommand();
        string head = Encoding.ASCII.GetString(cmd, 0, 11);
        Assert.Equal("MessageHead", head);
    }

    [Fact]
    public void ParseFrom_EmptyData_ReturnsFailure()
    {
        var result = EfortData.ParseFrom(new byte[0]);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void EfortData_DefaultAxisPositions()
    {
        var result = EfortData.ParseFrom(new byte[788]);
        if (result.IsSuccess)
        {
            Assert.Equal(7, result.Content.AxisPositions.Length);
            Assert.Equal(6, result.Content.CartesianPositions.Length);
        }
    }

    [Fact]
    public void ToString_ContainsClassName()
    {
        var client = new EfortClient("10.0.0.1");
        Assert.Contains("Efort", client.ToString());
    }

    [Fact]
    public void BuildReadCommand_UniqueIdPerCall()
    {
        var client = new EfortClient("127.0.0.1");
        byte[] cmd1 = client.BuildReadCommand();
        byte[] cmd2 = client.BuildReadCommand();
        ushort hb1 = BitConverter.ToUInt16(cmd1, 20);
        ushort hb2 = BitConverter.ToUInt16(cmd2, 20);
        Assert.NotEqual(hb1, hb2);
    }

    #endregion
}
