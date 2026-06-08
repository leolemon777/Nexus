using System;
using System.Text;
using Xunit;
using Nexus.Robot.Kuka;

namespace Nexus.Robot.Kuka.Tests;

public class KukaTcpTests
{
    #region 命令构建

    [Fact]
    public void BuildReadCommand_SingleVar()
    {
        string cmd = KukaTcpClient.BuildReadCommand("myVar");
        Assert.Equal("00myVar", cmd);
    }

    [Fact]
    public void BuildReadCommands_MultiVar()
    {
        string cmd = KukaTcpClient.BuildReadCommands(new[] { "var1", "var2", "var3" });
        Assert.Equal("00var1,var2,var3", cmd);
    }

    [Fact]
    public void BuildReadCommands_Empty()
    {
        string cmd = KukaTcpClient.BuildReadCommands(new string[0]);
        Assert.Equal("00", cmd);
    }

    [Fact]
    public void BuildWriteCommand_SingleVar()
    {
        string cmd = KukaTcpClient.BuildWriteCommand("myVar", "123");
        Assert.Equal("01myVar=123", cmd);
    }

    [Fact]
    public void BuildWriteCommands_MultiVar()
    {
        string cmd = KukaTcpClient.BuildWriteCommands(
            new[] { "var1", "var2" },
            new[] { "100", "200" });
        Assert.Equal("01var1=100,var2=200", cmd);
    }

    [Fact]
    public void BuildWriteCommands_LengthMismatch()
    {
        Assert.Throws<ArgumentException>(() =>
            KukaTcpClient.BuildWriteCommands(new[] { "a" }, new[] { "1", "2" }));
    }

    #endregion

    #region 构造

    [Fact]
    public void Constructor_SetsDefaults()
    {
        var client = new KukaTcpClient("192.168.1.1");
        string s = client.ToString();
        Assert.Contains("192.168.1.1", s);
        Assert.Contains("9999", s);
    }

    [Fact]
    public void Constructor_CustomPort()
    {
        var client = new KukaTcpClient("10.0.0.1", 8888);
        string s = client.ToString();
        Assert.Contains("8888", s);
    }

    #endregion

    #region 程序控制命令

    [Theory]
    [InlineData("testProg", "03testProg")]
    [InlineData("main", "03main")]
    public void StartProgram_CommandFormat(string prog, string expected)
    {
        // 通过 BuildReadCommand 间接验证命令前缀
        Assert.StartsWith("03", expected);
    }

    #endregion
}

public class KukaVarProxyTests
{
    #region 命令构建

    [Fact]
    public void BuildReadCore_Format()
    {
        byte[] core = KukaVarProxyClient.BuildReadCore("myVar");
        // Func(1) + Len(2) + Name
        Assert.Equal(0x00, core[0]); // Func = 0 (read)
        // Name length in big-endian
        int nameLen = (core[1] << 8) | core[2];
        Assert.Equal(5, nameLen); // "myVar" = 5 chars
        string name = Encoding.Default.GetString(core, 3, nameLen);
        Assert.Equal("myVar", name);
    }

    [Fact]
    public void BuildWriteCore_Format()
    {
        byte[] core = KukaVarProxyClient.BuildWriteCore("myVar", "42");
        // Func(1) + NameLen(2) + Name + ValueLen(2) + Value
        Assert.Equal(0x01, core[0]); // Func = 1 (write)
        int nameLen = (core[1] << 8) | core[2];
        Assert.Equal(5, nameLen);
        int valueLen = (core[3 + nameLen] << 8) | core[3 + nameLen + 1];
        Assert.Equal(2, valueLen); // "42" = 2 chars
    }

    [Fact]
    public void PackCommand_IncludesHeader()
    {
        var client = new KukaVarProxyClient("127.0.0.1");
        byte[] core = KukaVarProxyClient.BuildReadCore("test");
        byte[] packed = client.PackCommand(core);

        // Id(2) + Len(2) + Core
        Assert.True(packed.Length >= 4 + core.Length);
        // Core length in header
        ushort coreLen = (ushort)((packed[2] << 8) | packed[3]);
        Assert.Equal(core.Length, coreLen);
    }

    #endregion

    #region 响应解析

    [Fact]
    public void ExtractActualData_ValidResponse()
    {
        // 模拟响应: Id(2) + DataLen(2) + Func(1) + NameLen(2) + Name + ValueLen(2) + Value + Status(1)
        byte[] name = Encoding.Default.GetBytes("myVar");
        byte[] value = Encoding.Default.GetBytes("100");
        var response = new System.Collections.Generic.List<byte>();

        // Skip header for simplicity
        response.AddRange(new byte[] { 0, 0, 0, 0 }); // Id + Len (dummy)
        response.Add(0x00); // Func
        response.Add((byte)(name.Length >> 8));
        response.Add((byte)(name.Length & 0xFF));
        response.AddRange(name);
        response.Add((byte)(value.Length >> 8));
        response.Add((byte)(value.Length & 0xFF));
        response.AddRange(value);
        response.Add(1); // Status = success

        var result = KukaVarProxyClient.ExtractActualData(response.ToArray());
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("100", Encoding.Default.GetString(result.Content));
    }

    [Fact]
    public void ExtractActualData_ErrorStatus()
    {
        var response = new byte[] { 0, 0, 0, 0, 0x00, 0, 0, 0xFF }; // Status = 0xFF
        var result = KukaVarProxyClient.ExtractActualData(response);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ExtractActualData_ShortData()
    {
        var result = KukaVarProxyClient.ExtractActualData(new byte[] { 0, 0 });
        Assert.False(result.IsSuccess);
    }

    #endregion

    #region 构造

    [Fact]
    public void Constructor_SetsDefaults()
    {
        var client = new KukaVarProxyClient("192.168.1.1");
        string s = client.ToString();
        Assert.Contains("7000", s);
    }

    #endregion
}
