using System;
using System.Text;
using Xunit;
using Nexus.Robot.Estun;

namespace Nexus.Robot.Estun.Tests;

public class EstunTests
{
    #region EstunData 解析

    [Fact]
    public void EstunData_ParseStatusByte()
    {
        byte[] data = new byte[200];
        // Global speed at offset 2
        BitConverter.GetBytes((short)75).CopyTo(data, 2);
        // Status byte at offset 7: ManualMode=1, AutoMode=1, RemoteMode=0, Enable=1, Run=1, Error=0, ProgramRun=1, Moving=0
        data[7] = 0b01011011; // bits: 1,1,0,1,1,0,1,0
        // Project name at offset 8, word-swapped
        byte[] name = Encoding.ASCII.GetBytes("Test    "); // 8 chars, need 10 with swap
        // Word-swap and pad to 20 bytes
        byte[] padded = new byte[20];
        for (int i = 0; i < 10 && i < name.Length; i += 2)
        {
            if (i + 1 < name.Length) { padded[i] = name[i + 1]; padded[i + 1] = name[i]; }
            else padded[i] = name[i];
        }
        Array.Copy(padded, 0, data, 8, 20);

        var estunData = new EstunData(data);

        Assert.True(estunData.ManualMode);
        Assert.True(estunData.AutoMode);
        Assert.False(estunData.RemoteMode);
        Assert.True(estunData.EnableStatus);
        Assert.True(estunData.RunStatus);
        Assert.False(estunData.ErrorStatus);
        Assert.True(estunData.ProgramRunStatus);
        Assert.False(estunData.RobotMoving);
        Assert.Equal((short)75, estunData.GlobalSpeedValue);
    }

    [Fact]
    public void EstunData_ShortData_NoCrash()
    {
        // Should not crash with insufficient data
        var estunData = new EstunData(new byte[10]);
        Assert.False(estunData.ManualMode);
    }

    [Fact]
    public void EstunData_NullData_NoCrash()
    {
        var estunData = new EstunData(null);
        Assert.False(estunData.ManualMode);
    }

    [Fact]
    public void EstunData_CommandStatus()
    {
        byte[] data = new byte[200];
        BitConverter.GetBytes((ushort)2049).CopyTo(data, 36);

        var estunData = new EstunData(data);
        Assert.Equal((ushort)2049, estunData.RobotCommandStatus);
    }

    #endregion

    #region 构造

    [Fact]
    public void Constructor_SetsDefaults()
    {
        var client = new EstunClient("192.168.1.1");
        string s = client.ToString();
        Assert.Contains("EstunClient", s);
    }

    [Fact]
    public void Constructor_CustomPort()
    {
        var client = new EstunClient("10.0.0.1", 5020);
        Assert.NotNull(client);
        Assert.False(client.IsConnected);
    }

    #endregion
}
