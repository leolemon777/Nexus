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

    #region JSON 解析 — 更多场景

    [Fact]
    public void ExtractJsonValue_BooleanTrue()
    {
        string json = @"{""active"":true}";
        Assert.Equal("true", AbbRobotClient.ExtractJsonValue(json, "active"));
    }

    [Fact]
    public void ExtractJsonValue_BooleanFalse()
    {
        string json = @"{""enabled"":false}";
        Assert.Equal("false", AbbRobotClient.ExtractJsonValue(json, "enabled"));
    }

    [Fact]
    public void ExtractJsonValue_SpecialCharsInKey()
    {
        string json = @"{""ctrl-state"":""init""}";
        Assert.Equal("init", AbbRobotClient.ExtractJsonValue(json, "ctrl-state"));
    }

    [Fact]
    public void ExtractJsonValue_WhitespaceAroundColon()
    {
        string json = @"{""key"" : ""value""}";
        Assert.Equal("value", AbbRobotClient.ExtractJsonValue(json, "key"));
    }

    [Fact]
    public void ExtractJsonValue_DuplicateKey_ReturnsFirst()
    {
        string json = @"{""k"":""first"",""k"":""second""}";
        Assert.Equal("first", AbbRobotClient.ExtractJsonValue(json, "k"));
    }

    [Fact]
    public void ExtractJsonValue_EmptyStringValue()
    {
        string json = @"{""name"":""""}";
        Assert.Equal("", AbbRobotClient.ExtractJsonValue(json, "name"));
    }

    [Fact]
    public void ExtractJsonValue_LastField()
    {
        // Last field in JSON — no trailing comma
        string json = @"{""a"":1,""b"":""hello""}";
        Assert.Equal("hello", AbbRobotClient.ExtractJsonValue(json, "b"));
    }

    #endregion

    #region Model defaults

    [Fact]
    public void AbbControllerState_Defaults()
    {
        var state = new AbbControllerState();
        Assert.Equal("", state.State);
        Assert.Equal("", state.RawJson);
    }

    [Fact]
    public void AbbOperationMode_Defaults()
    {
        var mode = new AbbOperationMode();
        Assert.Equal("", mode.Mode);
        Assert.Equal("", mode.RawJson);
    }

    [Fact]
    public void AbbExecutionState_Defaults()
    {
        var state = new AbbExecutionState();
        Assert.Equal("", state.State);
        Assert.Equal("", state.RawJson);
    }

    #endregion

    #region Operations not connected

    [Fact]
    public void ReadDigitalInput_NotConnected_ReturnsError()
    {
        var client = new AbbRobotClient("192.168.1.1");
        var r = client.ReadDigitalInput("di01");
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void ReadDigitalOutput_NotConnected_ReturnsError()
    {
        var client = new AbbRobotClient("192.168.1.1");
        var r = client.ReadDigitalOutput("do01");
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void ReadAnalogInput_NotConnected_ReturnsError()
    {
        var client = new AbbRobotClient("192.168.1.1");
        var r = client.ReadAnalogInput("ai01");
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void ReadControllerState_NotConnected_ReturnsError()
    {
        var client = new AbbRobotClient("192.168.1.1");
        var r = client.ReadControllerState();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void ReadOperationMode_NotConnected_ReturnsError()
    {
        var client = new AbbRobotClient("192.168.1.1");
        var r = client.ReadOperationMode();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void ReadSpeedRatio_NotConnected_ReturnsError()
    {
        var client = new AbbRobotClient("192.168.1.1");
        var r = client.ReadSpeedRatio();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void ReadJointTargets_NotConnected_ReturnsError()
    {
        var client = new AbbRobotClient("192.168.1.1");
        var r = client.ReadJointTargets();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void ReadTcpPosition_NotConnected_ReturnsError()
    {
        var client = new AbbRobotClient("192.168.1.1");
        var r = client.ReadTcpPosition();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void MotorsOn_NotConnected_ReturnsError()
    {
        var client = new AbbRobotClient("192.168.1.1");
        var r = client.MotorsOn();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void StartExecution_NotConnected_ReturnsError()
    {
        var client = new AbbRobotClient("192.168.1.1");
        var r = client.StartExecution();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void SetSpeedRatio_OutOfRange_ReturnsError()
    {
        var client = new AbbRobotClient("192.168.1.1");
        Assert.False(client.SetSpeedRatio(-1).IsSuccess);
        Assert.False(client.SetSpeedRatio(101).IsSuccess);
    }

    [Fact]
    public void ListFiles_NotConnected_ReturnsError()
    {
        var client = new AbbRobotClient("192.168.1.1");
        var r = client.ListFiles();
        Assert.False(r.IsSuccess);
    }

    #endregion

    #region Credentials

    [Fact]
    public void UsernamePassword_CanBeSet()
    {
        var client = new AbbRobotClient("192.168.1.1");
        client.Username = "admin";
        client.Password = "pass";
        Assert.Equal("admin", client.Username);
        Assert.Equal("pass", client.Password);
    }

    [Fact]
    public void SetLogger_DoesNotThrow()
    {
        var client = new AbbRobotClient("192.168.1.1");
        client.SetLogger(Nexus.NullLogger.Instance);
    }

    #endregion
}
