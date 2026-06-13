using System;
using System.Text;
using Xunit;
using Nexus.Robot.Yaskawa;

namespace Nexus.Robot.Yaskawa.Tests;

public class Yrc1000Tests
{
    #region 命令构建

    [Fact]
    public void BuildReadCommand_BasicStructure()
    {
        var client = new Yrc1000Client("127.0.0.1");
        byte[] cmd = client.BuildReadCommand(0x0101, 10, 5);

        // 帧: Header(16) + Addr(4) + Count(4) = 24 bytes
        Assert.Equal(24, cmd.Length);

        // DataLen in header = 8
        int dataLen = (cmd[8] << 24) | (cmd[9] << 16) | (cmd[10] << 8) | cmd[11];
        Assert.Equal(8, dataLen);

        // Address (big endian at offset 16)
        int addr = (cmd[16] << 24) | (cmd[17] << 16) | (cmd[18] << 8) | cmd[19];
        Assert.Equal(10, addr);

        // Count (big endian at offset 20)
        int count = (cmd[20] << 24) | (cmd[21] << 16) | (cmd[22] << 8) | cmd[23];
        Assert.Equal(5, count);
    }

    [Fact]
    public void BuildWriteCommand_WithData()
    {
        var client = new Yrc1000Client("127.0.0.1");
        byte[] data = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        byte[] cmd = client.BuildWriteCommand(0x0103, 5, data);

        // Header(16) + Addr(4) + Data(4) = 24 bytes
        Assert.Equal(24, cmd.Length);

        // DataLen = 4 + 4 = 8
        int dataLen = (cmd[8] << 24) | (cmd[9] << 16) | (cmd[10] << 8) | cmd[11];
        Assert.Equal(8, dataLen);

        // Address at offset 16
        int addr = (cmd[16] << 24) | (cmd[17] << 16) | (cmd[18] << 8) | cmd[19];
        Assert.Equal(5, addr);

        // Data starts at offset 20
        Assert.Equal(0x01, cmd[20]);
        Assert.Equal(0x04, cmd[23]);
    }

    [Fact]
    public void BuildReadCommand_IncrementingReqId()
    {
        var client = new Yrc1000Client("127.0.0.1");
        byte[] cmd1 = client.BuildReadCommand(0x0101, 0, 1);
        byte[] cmd2 = client.BuildReadCommand(0x0101, 0, 1);

        ushort id1 = (ushort)((cmd1[0] << 8) | cmd1[1]);
        ushort id2 = (ushort)((cmd2[0] << 8) | cmd2[1]);
        Assert.Equal(id1 + 1, id2);
    }

    [Fact]
    public void BuildReadCommand_BlockId()
    {
        var client = new Yrc1000Client("127.0.0.1");
        client.BlockId = 0x02;
        byte[] cmd = client.BuildReadCommand(0x0101, 0, 1);
        Assert.Equal(0x02, cmd[2]); // BlockId
    }

    #endregion

    #region 构造

    [Fact]
    public void Constructor_Defaults()
    {
        var client = new Yrc1000Client("192.168.1.1");
        Assert.Equal((byte)0x00, client.BlockId);
    }

    [Fact]
    public void Constructor_CustomPort()
    {
        var client = new Yrc1000Client("192.168.1.1", 10080);
        Assert.Contains("10080", client.ToString());
    }

    #endregion

    #region YrcRobotStatus

    [Fact]
    public void YrcRobotStatus_ServoOn()
    {
        var status = new YrcRobotStatus { ServoState = 1 };
        Assert.True(status.IsServoOn);
    }

    [Fact]
    public void YrcRobotStatus_ServoOff()
    {
        var status = new YrcRobotStatus { ServoState = 0 };
        Assert.False(status.IsServoOn);
    }

    [Theory]
    [InlineData(0, "停止")]
    [InlineData(1, "运行")]
    [InlineData(2, "暂停")]
    [InlineData(3, "急停")]
    public void YrcRobotStatus_RunStateDescriptions(byte state, string expected)
    {
        var status = new YrcRobotStatus { RunState = state };
        Assert.Equal(expected, status.RunStateDescription);
    }

    [Fact]
    public void YrcRobotStatus_ToString()
    {
        var status = new YrcRobotStatus
        {
            ServoState = 1,
            RunState = 1,
            AlarmCode = 0x1234,
            ErrorCode = 0x0000
        };
        string s = status.ToString();
        Assert.Contains("Servo=1", s);
        Assert.Contains("1234", s);
    }

    #endregion

    #region ToString

    [Fact]
    public void ToString_ContainsIp()
    {
        var client = new Yrc1000Client("192.168.1.1");
        string s = client.ToString();
        Assert.Contains("Yrc1000Client", s);
    }

    #endregion

    #region 扩展覆盖

    [Fact]
    public void Constructor_DefaultPort()
    {
        var client = new Yrc1000Client("10.0.0.1");
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var client = new Yrc1000Client("192.168.1.1");
        client.Dispose();
    }

    [Fact]
    public void SetLogger_DoesNotThrow()
    {
        var client = new Yrc1000Client("192.168.1.1");
        client.SetLogger(NullLogger.Instance);
    }

    [Fact]
    public void BuildReadCommand_ZeroAddress()
    {
        var client = new Yrc1000Client("127.0.0.1");
        byte[] cmd = client.BuildReadCommand(0x0101, 0, 1);
        int addr = (cmd[16] << 24) | (cmd[17] << 16) | (cmd[18] << 8) | cmd[19];
        Assert.Equal(0, addr);
    }

    [Fact]
    public void BuildWriteCommand_LargeData()
    {
        var client = new Yrc1000Client("127.0.0.1");
        byte[] data = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        byte[] cmd = client.BuildWriteCommand(0x0103, 0, data);
        Assert.True(cmd.Length >= 16 + 4 + data.Length);
    }

    [Fact]
    public void YrcRobotStatus_AllStates()
    {
        var status = new YrcRobotStatus { ServoState = 1, RunState = 2, AlarmCode = 0, ErrorCode = 0 };
        Assert.True(status.IsServoOn);
        Assert.Equal("暂停", status.RunStateDescription);
    }

    [Fact]
    public void YrcRobotStatus_ErrorCode()
    {
        var status = new YrcRobotStatus { ErrorCode = 0x5678 };
        string s = status.ToString();
        Assert.Contains("5678", s);
    }

    [Fact]
    public void BlockId_DefaultZero()
    {
        var client = new Yrc1000Client("192.168.1.1");
        Assert.Equal((byte)0x00, client.BlockId);
    }

    [Fact]
    public void BlockId_CanBeSet()
    {
        var client = new Yrc1000Client("192.168.1.1");
        client.BlockId = 0x05;
        Assert.Equal((byte)0x05, client.BlockId);
    }

    #endregion
}
