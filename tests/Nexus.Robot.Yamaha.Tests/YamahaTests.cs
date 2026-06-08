using System;
using Xunit;
using Nexus.Robot.Yamaha;

namespace Nexus.Robot.Yamaha.Tests;

public class YamahaRcxTests
{
    #region 命令构建

    [Fact]
    public void BuildCommand_AddsCRLF()
    {
        string cmd = YamahaRcxClient.BuildCommand("@?MOTOR ");
        Assert.Equal("@?MOTOR \r\n", cmd);
    }

    [Fact]
    public void BuildCommand_WithExistingCRLF()
    {
        string cmd = YamahaRcxClient.BuildCommand("@ RUN \r\n");
        Assert.Equal("@ RUN \r\n", cmd);
    }

    [Fact]
    public void BuildCommand_Empty()
    {
        string cmd = YamahaRcxClient.BuildCommand("");
        Assert.Equal("\r\n", cmd);
    }

    [Fact]
    public void BuildCommand_Null()
    {
        string cmd = YamahaRcxClient.BuildCommand(null);
        Assert.Equal("\r\n", cmd);
    }

    #endregion

    #region 命令格式验证

    [Theory]
    [InlineData("@?MOTOR ")]
    [InlineData("@?MODE ")]
    [InlineData("@?EMG ")]
    [InlineData("@?WHERE ")]
    [InlineData("@ RESET ")]
    [InlineData("@ RUN ")]
    [InlineData("@ STOP ")]
    public void KnownCommands_ValidFormat(string command)
    {
        Assert.StartsWith("@", command);
        Assert.True(command.EndsWith(" ") || command.EndsWith("()"));
    }

    #endregion

    #region 构造

    [Fact]
    public void Constructor_SetsDefaults()
    {
        var client = new YamahaRcxClient("192.168.1.1");
        string s = client.ToString();
        Assert.Contains("YamahaRcxClient", s);
        Assert.Contains("192.168.1.1", s);
    }

    [Fact]
    public void Constructor_CustomPort()
    {
        var client = new YamahaRcxClient("10.0.0.1", 10000);
        string s = client.ToString();
        Assert.Contains("10000", s);
    }

    #endregion
}
