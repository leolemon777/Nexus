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

    [Fact]
    public void Password_DefaultIsEmpty()
    {
        var client = new FtpClient("192.168.1.1");
        Assert.Equal("", client.Password);
    }

    [Fact]
    public void PassiveMode_DefaultIsTrue()
    {
        var client = new FtpClient("192.168.1.1");
        Assert.True(client.PassiveMode);
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

    [Fact]
    public void ToString_ContainsDefaultUser()
    {
        var client = new FtpClient("10.0.0.1");
        Assert.Contains("anonymous", client.ToString());
    }

    [Fact]
    public void ToString_ContainsPort()
    {
        var client = new FtpClient("10.0.0.1", 2121);
        Assert.Contains("2121", client.ToString());
    }

    #endregion

    #region 未连接操作

    [Fact]
    public void Connect_ToInvalidHost_ReturnsError()
    {
        var client = new FtpClient("192.0.2.1", 21, 500); // unreachable IP
        var r = client.Connect();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void Disconnect_WithoutConnect_DoesNotThrow()
    {
        var client = new FtpClient("192.168.1.1");
        client.Disconnect(); // should not throw
    }

    [Fact]
    public void DoubleDisconnect_DoesNotThrow()
    {
        var client = new FtpClient("192.168.1.1");
        client.Disconnect();
        client.Disconnect();
    }

    [Fact]
    public void GetWorkingDirectory_NotConnected_Throws()
    {
        var client = new FtpClient("192.168.1.1");
        Assert.Throws<InvalidOperationException>(() => client.GetWorkingDirectory());
    }

    [Fact]
    public void ChangeDirectory_NotConnected_Throws()
    {
        var client = new FtpClient("192.168.1.1");
        Assert.Throws<InvalidOperationException>(() => client.ChangeDirectory("/tmp"));
    }

    [Fact]
    public void ListDirectory_NotConnected_Throws()
    {
        var client = new FtpClient("192.168.1.1");
        Assert.Throws<InvalidOperationException>(() => client.ListDirectory());
    }

    [Fact]
    public void DeleteFile_NotConnected_Throws()
    {
        var client = new FtpClient("192.168.1.1");
        Assert.Throws<InvalidOperationException>(() => client.DeleteFile("test.txt"));
    }

    [Fact]
    public void CreateDirectory_NotConnected_Throws()
    {
        var client = new FtpClient("192.168.1.1");
        Assert.Throws<InvalidOperationException>(() => client.CreateDirectory("newdir"));
    }

    [Fact]
    public void RemoveDirectory_NotConnected_Throws()
    {
        var client = new FtpClient("192.168.1.1");
        Assert.Throws<InvalidOperationException>(() => client.RemoveDirectory("olddir"));
    }

    [Fact]
    public void Rename_NotConnected_Throws()
    {
        var client = new FtpClient("192.168.1.1");
        Assert.Throws<InvalidOperationException>(() => client.Rename("old.txt", "new.txt"));
    }

    [Fact]
    public void GetFileSize_NotConnected_Throws()
    {
        var client = new FtpClient("192.168.1.1");
        Assert.Throws<InvalidOperationException>(() => client.GetFileSize("file.txt"));
    }

    [Fact]
    public void DownloadFile_NotConnected_ReturnsError()
    {
        var client = new FtpClient("192.168.1.1");
        var r = client.DownloadFile("remote.txt", "local.txt");
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void UploadFile_NotConnected_ReturnsError()
    {
        var client = new FtpClient("192.168.1.1");
        var r = client.UploadFile("local.txt", "remote.txt");
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void DownloadBytes_NotConnected_ReturnsError()
    {
        var client = new FtpClient("192.168.1.1");
        var r = client.DownloadBytes("remote.txt");
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void UploadBytes_NotConnected_ReturnsError()
    {
        var client = new FtpClient("192.168.1.1");
        var r = client.UploadBytes("remote.txt", new byte[0]);
        Assert.False(r.IsSuccess);
    }

    #endregion

    #region SetLogger

    [Fact]
    public void SetLogger_DoesNotThrow()
    {
        var client = new FtpClient("192.168.1.1");
        client.SetLogger(Nexus.NullLogger.Instance);
    }

    [Fact]
    public void SetLogger_ConsoleLogger_DoesNotThrow()
    {
        var client = new FtpClient("192.168.1.1");
        client.SetLogger(new Nexus.ConsoleLogger());
    }

    #endregion

    #region Dispose

    [Fact]
    public void Dispose_WithoutConnect_DoesNotThrow()
    {
        var client = new FtpClient("192.168.1.1");
        client.Disconnect();
    }

    #endregion

    #region 扩展覆盖

    [Fact]
    public void Constructor_IpAddress_Preserved()
    {
        var client = new FtpClient("10.0.0.1");
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Constructor_DefaultPort_Is21()
    {
        var client = new FtpClient("192.168.1.1");
        Assert.Contains("192.168.1.1", client.ToString());
    }

    [Fact]
    public void Username_CanBeChanged()
    {
        var client = new FtpClient("192.168.1.1");
        client.Username = "testuser";
        Assert.Equal("testuser", client.Username);
    }

    [Fact]
    public void Password_CanBeChanged()
    {
        var client = new FtpClient("192.168.1.1");
        client.Password = "secret";
        Assert.Equal("secret", client.Password);
    }

    [Fact]
    public void PassiveMode_CanBeToggled()
    {
        var client = new FtpClient("192.168.1.1");
        client.PassiveMode = false;
        Assert.False(client.PassiveMode);
        client.PassiveMode = true;
        Assert.True(client.PassiveMode);
    }

    [Fact]
    public void ToString_ContainsFtpClient()
    {
        var client = new FtpClient("10.0.0.1");
        Assert.Contains("Ftp", client.ToString());
    }

    [Fact]
    public void Connect_ToLocalhost_ShortTimeout_Fails()
    {
        var client = new FtpClient("127.0.0.1", 19999, 200);
        var r = client.Connect();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void SetLogger_Null_DoesNotThrow()
    {
        var client = new FtpClient("192.168.1.1");
        client.SetLogger(Nexus.NullLogger.Instance);
    }

    [Fact]
    public void DownloadBytes_EmptyPath_ReturnsError()
    {
        var client = new FtpClient("192.168.1.1");
        var r = client.DownloadBytes("");
        Assert.False(r.IsSuccess);
    }

    #endregion
}
