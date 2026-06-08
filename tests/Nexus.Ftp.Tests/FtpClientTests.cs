using System;
using Xunit;
using Nexus.Ftp;

namespace Nexus.Ftp.Tests;

public class FtpClientTests
{
    #region 构造

    [Fact]
    public void Constructor_Defaults()
    {
        var client = new FtpClient("192.168.1.1");
        Assert.Equal("anonymous", client.Username);
        Assert.True(client.PassiveMode);
    }

    [Fact]
    public void Constructor_CustomPort()
    {
        var client = new FtpClient("192.168.1.1", 2121, 5000);
        // Port is protected, verify via ToString
        Assert.Contains("2121", client.ToString());
    }

    [Fact]
    public void Constructor_NullIp_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FtpClient(null!));
    }

    #endregion

    #region 属性

    [Fact]
    public void Properties_Settable()
    {
        var client = new FtpClient("192.168.1.1");
        client.Username = "admin";
        client.Password = "pass123";
        client.PassiveMode = false;

        Assert.Equal("admin", client.Username);
        Assert.Equal("pass123", client.Password);
        Assert.False(client.PassiveMode);
    }

    [Fact]
    public void IsConnected_BeforeConnect()
    {
        var client = new FtpClient("192.168.1.1");
        Assert.False(client.IsConnected);
    }

    #endregion

    #region ToString

    [Fact]
    public void ToString_ContainsInfo()
    {
        var client = new FtpClient("192.168.1.1");
        client.Username = "admin";
        string s = client.ToString();
        Assert.Contains("FtpClient", s);
        Assert.Contains("192.168.1.1", s);
        Assert.Contains("admin", s);
    }

    #endregion
}
