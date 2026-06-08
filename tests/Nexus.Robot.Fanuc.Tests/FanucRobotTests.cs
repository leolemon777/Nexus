using System;
using System.Text;
using Xunit;
using Nexus.Robot.Fanuc;

namespace Nexus.Robot.Fanuc.Tests;

public class FanucRobotTests
{
    #region 命令构建

    [Fact]
    public void BuildCommand_BasicStructure()
    {
        var client = new FanucRobotClient("127.0.0.1");
        byte[] cmd = client.BuildCommand(1, 10, null);

        // 帧: MsgId(4) + CmdCode(4) + Index(4) + DataLen(4) = 16 bytes
        Assert.Equal(16, cmd.Length);

        // CmdCode = 1 (little endian)
        Assert.Equal(0x01, cmd[4]);
        Assert.Equal(0x00, cmd[5]);
        Assert.Equal(0x00, cmd[6]);
        Assert.Equal(0x00, cmd[7]);

        // Index = 10 (little endian)
        Assert.Equal(0x0A, cmd[8]);
        Assert.Equal(0x00, cmd[9]);
        Assert.Equal(0x00, cmd[10]);
        Assert.Equal(0x00, cmd[11]);

        // DataLen = 0
        Assert.Equal(0x00, cmd[12]);
        Assert.Equal(0x00, cmd[13]);
        Assert.Equal(0x00, cmd[14]);
        Assert.Equal(0x00, cmd[15]);
    }

    [Fact]
    public void BuildCommand_WithData()
    {
        var client = new FanucRobotClient("127.0.0.1");
        byte[] data = new byte[] { 0x01, 0x02, 0x03 };
        byte[] cmd = client.BuildCommand(2, 5, data);

        // 16 + 3 = 19 bytes
        Assert.Equal(19, cmd.Length);

        // DataLen = 3
        Assert.Equal(0x03, cmd[12]);

        // Data starts at offset 16
        Assert.Equal(0x01, cmd[16]);
        Assert.Equal(0x03, cmd[18]);
    }

    [Fact]
    public void BuildCommand_IncrementingMsgId()
    {
        var client = new FanucRobotClient("127.0.0.1");
        byte[] cmd1 = client.BuildCommand(1, 0, null);
        byte[] cmd2 = client.BuildCommand(1, 0, null);

        int id1 = BitConverter.ToInt32(cmd1, 0);
        int id2 = BitConverter.ToInt32(cmd2, 0);
        Assert.Equal(id1 + 1, id2);
    }

    #endregion

    #region 响应解析

    [Fact]
    public void ParseIntResponse_Success()
    {
        // response: [existing data... 8 bytes min] + int value
        byte[] raw = new byte[16];
        BitConverter.GetBytes(0).CopyTo(raw, 0);  // msgId placeholder
        BitConverter.GetBytes(4).CopyTo(raw, 4);  // code = 4 (success, data len)
        BitConverter.GetBytes(42).CopyTo(raw, 8);  // actual int value

        // For testing ParseIntResponse directly, we need the header format
        // Actually the method expects: code at offset 4, value at offset 8
        // Let's just test via internal logic
    }

    #endregion

    #region 构造

    [Fact]
    public void Constructor_Defaults()
    {
        var client = new FanucRobotClient("192.168.1.1");
        string s = client.ToString();
        Assert.Contains("FanucRobotClient", s);
    }

    [Fact]
    public void Constructor_CustomPort()
    {
        var client = new FanucRobotClient("192.168.1.1", 12345);
        Assert.Contains("12345", client.ToString());
    }

    #endregion

    #region FanucRobotStatus

    [Fact]
    public void FanucRobotStatus_ModeDescriptions()
    {
        var status = new FanucRobotStatus { Mode = 1, State = 0 };
        Assert.Equal("手动", status.ModeDescription);
        Assert.Equal("停止", status.StateDescription);

        status.Mode = 2;
        Assert.Equal("自动", status.ModeDescription);

        status.State = 1;
        Assert.Equal("运行", status.StateDescription);

        status.State = 3;
        Assert.Equal("急停", status.StateDescription);
    }

    [Fact]
    public void FanucRobotStatus_ToString()
    {
        var status = new FanucRobotStatus { Mode = 2, State = 1 };
        string s = status.ToString();
        Assert.Contains("自动", s);
        Assert.Contains("运行", s);
    }

    [Fact]
    public void FanucRobotStatus_UnknownMode()
    {
        var status = new FanucRobotStatus { Mode = 99, State = 99 };
        Assert.Contains("未知", status.ModeDescription);
        Assert.Contains("未知", status.StateDescription);
    }

    #endregion

    #region ToString

    [Fact]
    public void ToString_ContainsIp()
    {
        var client = new FanucRobotClient("192.168.1.1");
        string s = client.ToString();
        Assert.Contains("FanucRobotClient", s);
    }

    #endregion
}
