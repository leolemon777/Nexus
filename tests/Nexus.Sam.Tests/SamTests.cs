using Nexus;
using Nexus.Sam;
using Xunit;

namespace Nexus.Sam.Tests;

public class SamUnsupportedReadTests
{
    [Fact]
    public void ReadBool_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.ReadBool("");
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持布尔读取", r.Message);
    }

    [Fact]
    public void ReadInt16_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.ReadInt16("");
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持整数读取", r.Message);
    }

    [Fact]
    public void ReadUInt16_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.ReadUInt16("");
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持整数读取", r.Message);
    }

    [Fact]
    public void ReadInt32_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.ReadInt32("");
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持整数读取", r.Message);
    }

    [Fact]
    public void ReadUInt32_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.ReadUInt32("");
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持整数读取", r.Message);
    }

    [Fact]
    public void ReadInt64_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.ReadInt64("");
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持整数读取", r.Message);
    }

    [Fact]
    public void ReadUInt64_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.ReadUInt64("");
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持整数读取", r.Message);
    }

    [Fact]
    public void ReadFloat_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.ReadFloat("");
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持浮点读取", r.Message);
    }

    [Fact]
    public void ReadDouble_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.ReadDouble("");
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持浮点读取", r.Message);
    }
}

public class SamUnsupportedWriteTests
{
    [Fact]
    public void WriteBool_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.Write("", true);
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持写入操作", r.Message);
    }

    [Fact]
    public void WriteInt16_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.Write("", (short)1);
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持写入操作", r.Message);
    }

    [Fact]
    public void WriteInt32_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.Write("", 1);
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持写入操作", r.Message);
    }

    [Fact]
    public void WriteString_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.Write("", "test");
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持写入操作", r.Message);
    }

    [Fact]
    public void WriteFloat_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.Write("", 1.0f);
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持写入操作", r.Message);
    }

    [Fact]
    public void WriteDouble_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.Write("", 1.0d);
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持写入操作", r.Message);
    }

    [Fact]
    public void WriteBytes_ReturnsNotSupported()
    {
        var client = new SamTcpClient("127.0.0.1");
        var r = client.Write("", new byte[] { 0x01 });
        Assert.False(r.IsSuccess);
        Assert.Contains("不支持写入操作", r.Message);
    }
}

public class SamClientPropertiesTests
{
    [Fact]
    public void ToString_ContainsClassNameAndEndpoint()
    {
        var client = new SamTcpClient("192.168.1.100", 6000);
        string s = client.ToString();
        Assert.Contains("SamTcpClient", s);
        Assert.Contains("192.168.1.100", s);
        Assert.Contains("6000", s);
    }
}
