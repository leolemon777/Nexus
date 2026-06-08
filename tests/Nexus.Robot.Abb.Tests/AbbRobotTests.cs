using System;
using Xunit;
using Nexus.Robot.Abb;

namespace Nexus.Robot.Abb.Tests;

public class AbbRobotTests
{
    #region JSON 解析

    [Fact]
    public void ExtractJsonValue_StringValue()
    {
        string json = @"{""ctrl-state"":""run"",""lvalue"":""123""}";
        Assert.Equal("run", AbbRobotClient.ExtractJsonValue(json, "ctrl-state"));
        Assert.Equal("123", AbbRobotClient.ExtractJsonValue(json, "lvalue"));
    }

    [Fact]
    public void ExtractJsonValue_NumericValue()
    {
        string json = @"{""speedratio"":75}";
        Assert.Equal("75", AbbRobotClient.ExtractJsonValue(json, "speedratio"));
    }

    [Fact]
    public void ExtractJsonValue_MissingKey()
    {
        string json = @"{""key1"":""val1""}";
        Assert.Equal("", AbbRobotClient.ExtractJsonValue(json, "key2"));
    }

    [Fact]
    public void ExtractJsonValue_EmptyInput()
    {
        Assert.Equal("", AbbRobotClient.ExtractJsonValue("", "key"));
        Assert.Equal("", AbbRobotClient.ExtractJsonValue(null!, "key"));
    }

    [Fact]
    public void ExtractJsonValue_NestedArray()
    {
        string json = @"{""rax_1"":""123.45"",""rax_2"":""-45.67""}";
        Assert.Equal("123.45", AbbRobotClient.ExtractJsonValue(json, "rax_1"));
        Assert.Equal("-45.67", AbbRobotClient.ExtractJsonValue(json, "rax_2"));
    }

    #endregion

    #region 构造

    [Fact]
    public void Constructor_Defaults()
    {
        var client = new AbbRobotClient("192.168.1.1");
        Assert.Null(client.Username);
    }

    [Fact]
    public void Constructor_CustomPort()
    {
        var client = new AbbRobotClient("192.168.1.1", 8080, 3000);
        // Port is protected, verify via ToString
        Assert.Contains("8080", client.ToString());
    }

    [Fact]
    public void Constructor_NullIp_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AbbRobotClient(null!));
    }

    #endregion

    #region ToString

    [Fact]
    public void ToString_ContainsIp()
    {
        var client = new AbbRobotClient("192.168.1.1");
        string s = client.ToString();
        Assert.Contains("AbbRobotClient", s);
        Assert.Contains("192.168.1.1", s);
    }

    #endregion

    #region 辅助类型

    [Fact]
    public void AbbControllerState_ToString()
    {
        var state = new AbbControllerState { State = "run" };
        Assert.Contains("run", state.ToString());
    }

    [Fact]
    public void AbbOperationMode_ToString()
    {
        var mode = new AbbOperationMode { Mode = "MANR" };
        Assert.Contains("MANR", mode.ToString());
    }

    [Fact]
    public void AbbExecutionState_ToString()
    {
        var state = new AbbExecutionState { State = "running" };
        Assert.Contains("running", state.ToString());
    }

    #endregion
}
