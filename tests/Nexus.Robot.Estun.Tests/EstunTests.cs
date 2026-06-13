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
        var estunData = new EstunData(null!);
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

    [Fact]
    public void Station_DefaultsToOne()
    {
        var client = new EstunClient("192.168.1.1");
        Assert.Equal((byte)1, client.Station);
    }

    [Fact]
    public void Station_CanBeChanged()
    {
        var client = new EstunClient("192.168.1.1") { Station = 3 };
        Assert.Equal((byte)3, client.Station);
    }

    [Fact]
    public void IsConnected_DefaultFalse()
    {
        var client = new EstunClient("192.168.1.1");
        Assert.False(client.IsConnected);
    }

    #endregion

    #region EstunData 详细解析

    [Fact]
    public void EstunData_AllStatusBitsSet()
    {
        byte[] data = new byte[200];
        data[7] = 0xFF; // all bits on
        var estunData = new EstunData(data);

        Assert.True(estunData.ManualMode);
        Assert.True(estunData.AutoMode);
        Assert.True(estunData.RemoteMode);
        Assert.True(estunData.EnableStatus);
        Assert.True(estunData.RunStatus);
        Assert.True(estunData.ErrorStatus);
        Assert.True(estunData.ProgramRunStatus);
        Assert.True(estunData.RobotMoving);
    }

    [Fact]
    public void EstunData_AllStatusBitsClear()
    {
        byte[] data = new byte[200];
        data[7] = 0x00;
        var estunData = new EstunData(data);

        Assert.False(estunData.ManualMode);
        Assert.False(estunData.AutoMode);
        Assert.False(estunData.RemoteMode);
        Assert.False(estunData.EnableStatus);
        Assert.False(estunData.RunStatus);
        Assert.False(estunData.ErrorStatus);
        Assert.False(estunData.ProgramRunStatus);
        Assert.False(estunData.RobotMoving);
    }

    [Fact]
    public void EstunData_ProjectName_WordSwapDecoded()
    {
        byte[] data = new byte[200];
        // Word-swap encode "AB" at offset 8: padded[0]='A', padded[1]='B' → swapped = 'B','A'
        data[8] = (byte)'B';
        data[9] = (byte)'A';

        var estunData = new EstunData(data);
        Assert.Equal("AB", estunData.ProjectName);
    }

    [Fact]
    public void EstunData_DigitalOutputs_8BitsFromByte()
    {
        byte[] data = new byte[200];
        data[28] = 0xAA; // alternating bits: 10101010

        var estunData = new EstunData(data);
        Assert.True(estunData.DigitalOutputs[1]);
        Assert.False(estunData.DigitalOutputs[0]);
        Assert.True(estunData.DigitalOutputs[3]);
        Assert.False(estunData.DigitalOutputs[2]);
    }

    [Fact]
    public void EstunData_DigitalInputs_8BitsFromByte()
    {
        byte[] data = new byte[200];
        data[126] = 0x55; // alternating bits: 01010101

        var estunData = new EstunData(data);
        Assert.True(estunData.DigitalInputs[0]);
        Assert.False(estunData.DigitalInputs[1]);
        Assert.True(estunData.DigitalInputs[2]);
        Assert.False(estunData.DigitalInputs[3]);
    }

    [Fact]
    public void EstunData_AnalogOutputs_ParsedAsFloat()
    {
        byte[] data = new byte[200];
        BitConverter.GetBytes(3.14f).CopyTo(data, 38);

        var estunData = new EstunData(data);
        Assert.InRange(estunData.AnalogOutputs[0], 3.13f, 3.15f);
        Assert.Equal(0f, estunData.AnalogOutputs[1]); // not set
    }

    [Fact]
    public void EstunData_AnalogInputs_ParsedAsFloat()
    {
        byte[] data = new byte[200];
        BitConverter.GetBytes(-1.5f).CopyTo(data, 134);

        var estunData = new EstunData(data);
        Assert.InRange(estunData.AnalogInputs[0], -1.51f, -1.49f);
    }

    [Fact]
    public void EstunData_ReadWriteFlag_Parsed()
    {
        byte[] data = new byte[202];
        BitConverter.GetBytes((short)42).CopyTo(data, 198);

        var estunData = new EstunData(data);
        Assert.Equal((short)42, estunData.ReadWriteFlag);
    }

    [Fact]
    public void EstunData_Defaults()
    {
        var d = new EstunData(new byte[0]);
        Assert.Equal("", d.ProjectName);
        Assert.Equal(64, d.DigitalOutputs.Length);
        Assert.Equal(64, d.DigitalInputs.Length);
        Assert.Equal(16, d.AnalogOutputs.Length);
        Assert.Equal(16, d.AnalogInputs.Length);
        Assert.Equal((ushort)0, d.RobotCommandStatus);
    }

    #endregion

    #region Operations when not connected

    [Fact]
    public void ReadRobotData_NotConnected_ReturnsError()
    {
        var client = new EstunClient("192.168.1.1");
        var r = client.ReadRobotData();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void RobotStart_NotConnected_ReturnsError()
    {
        var client = new EstunClient("192.168.1.1");
        var r = client.RobotStart();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void RobotStop_NotConnected_ReturnsError()
    {
        var client = new EstunClient("192.168.1.1");
        var r = client.RobotStop();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void RobotResetError_NotConnected_ReturnsError()
    {
        var client = new EstunClient("192.168.1.1");
        var r = client.RobotResetError();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void SetGlobalSpeed_NotConnected_ReturnsError()
    {
        var client = new EstunClient("192.168.1.1");
        var r = client.SetGlobalSpeed(50);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void RobotLoadProject_NotConnected_ReturnsError()
    {
        var client = new EstunClient("192.168.1.1");
        var r = client.RobotLoadProject("test");
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void RobotUnloadProject_NotConnected_ReturnsError()
    {
        var client = new EstunClient("192.168.1.1");
        var r = client.RobotUnloadProject();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void CommandStatusRestart_NotConnected_ReturnsError()
    {
        var client = new EstunClient("192.168.1.1");
        var r = client.CommandStatusRestart();
        Assert.False(r.IsSuccess);
    }

    #endregion

    #region 扩展覆盖

    [Fact]
    public void Disconnect_DoesNotThrow()
    {
        var client = new EstunClient("192.168.1.1");
        client.Disconnect();
    }

    [Fact]
    public void DoubleDisconnect_DoesNotThrow()
    {
        var client = new EstunClient("192.168.1.1");
        client.Disconnect();
        client.Disconnect();
    }

    [Fact]
    public void EstunData_GlobalSpeed_DefaultZero()
    {
        var estunData = new EstunData(new byte[200]);
        Assert.Equal((short)0, estunData.GlobalSpeedValue);
    }

    [Fact]
    public void EstunData_GlobalSpeed_Parsed()
    {
        byte[] data = new byte[200];
        BitConverter.GetBytes((short)75).CopyTo(data, 2);
        var estunData = new EstunData(data);
        Assert.Equal((short)75, estunData.GlobalSpeedValue);
    }

    [Fact]
    public void EstunData_RobotCommandStatus_Parsed()
    {
        byte[] data = new byte[200];
        BitConverter.GetBytes((ushort)100).CopyTo(data, 36);
        var estunData = new EstunData(data);
        Assert.Equal((ushort)100, estunData.RobotCommandStatus);
    }

    [Fact]
    public void ToString_ContainsIp()
    {
        var client = new EstunClient("10.0.0.1");
        Assert.Contains("10.0.0.1", client.ToString());
    }

    [Fact]
    public void EstunData_AnalogOutputs_DefaultZero()
    {
        var estunData = new EstunData(new byte[200]);
        Assert.Equal(0f, estunData.AnalogOutputs[0]);
    }

    [Fact]
    public void EstunData_AnalogInputs_DefaultZero()
    {
        var estunData = new EstunData(new byte[200]);
        Assert.Equal(0f, estunData.AnalogInputs[0]);
    }

    [Fact]
    public void EstunData_DigitalOutputs_DefaultFalse()
    {
        var estunData = new EstunData(new byte[200]);
        Assert.False(estunData.DigitalOutputs[0]);
    }

    [Fact]
    public void EstunData_ReadWriteFlag_DefaultZero()
    {
        var estunData = new EstunData(new byte[200]);
        Assert.Equal((short)0, estunData.ReadWriteFlag);
    }

    #endregion
}
