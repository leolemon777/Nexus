using System;
using System.Text;
using Xunit;
using Nexus.Inovance;

namespace Nexus.Inovance.Tests;

public class EasyNetTests
{
    private const int PortBase = 25000;

    #region 地址解析

    [Theory]
    [InlineData("D0",   0x40)]
    [InlineData("D100", 0x40)]
    [InlineData("W50",  0x60)]
    [InlineData("R200", 0x50)]
    public void ParseAddress_WordType_HasCorrectTypeCode(string addr, byte expectedHigh)
    {
        var result = InovanceEasyClient.ParseAddress(addr);
        Assert.True(result.IsSuccess, result.Message);
        // type code 在 byte[2] 的高 4 位
        Assert.Equal(expectedHigh, result.Content[2] & 0xF0);
    }

    [Fact]
    public void ParseAddress_D100_ValueEncoded()
    {
        // D100 → value = 100 * 16 = 1600 = 0x0640
        var result = InovanceEasyClient.ParseAddress("D100");
        Assert.True(result.IsSuccess);
        int value = result.Content[0] | (result.Content[1] << 8) | ((result.Content[2] & 0x0F) << 16);
        Assert.Equal(1600, value);
    }

    [Fact]
    public void ParseAddress_D100_Bit5()
    {
        // D100.5 → value = 100 * 16 + 5 = 1605
        var result = InovanceEasyClient.ParseAddress("D100.5");
        Assert.True(result.IsSuccess);
        int value = result.Content[0] | (result.Content[1] << 8) | ((result.Content[2] & 0x0F) << 16);
        Assert.Equal(1605, value);
    }

    [Fact]
    public void ParseAddress_X0_Octal()
    {
        // X 使用八进制
        var result = InovanceEasyClient.ParseAddress("X0");
        Assert.True(result.IsSuccess);
        int value = result.Content[0] | (result.Content[1] << 8) | ((result.Content[2] & 0x0F) << 16);
        Assert.Equal(0, value);
    }

    [Fact]
    public void ParseAddress_X10_Octal()
    {
        // X10 = 八进制 10 = 十进制 8
        var result = InovanceEasyClient.ParseAddress("X10");
        Assert.True(result.IsSuccess);
        int value = result.Content[0] | (result.Content[1] << 8) | ((result.Content[2] & 0x0F) << 16);
        Assert.Equal(8, value);
    }

    [Fact]
    public void ParseAddress_Y0_Offset()
    {
        // Y 偏移 0x80000
        var result = InovanceEasyClient.ParseAddress("Y0");
        Assert.True(result.IsSuccess);
        int value = result.Content[0] | (result.Content[1] << 8) | ((result.Content[2] & 0x0F) << 16);
        Assert.Equal(0x80000, value);
    }

    [Fact]
    public void ParseAddress_M100_Decimal()
    {
        var result = InovanceEasyClient.ParseAddress("M100");
        Assert.True(result.IsSuccess);
        Assert.Equal(0x10, result.Content[2] & 0xF0);
        int value = result.Content[0] | (result.Content[1] << 8) | ((result.Content[2] & 0x0F) << 16);
        Assert.Equal(100, value);
    }

    [Fact]
    public void ParseAddress_S0_HasOffset()
    {
        // S 偏移 0x80000
        var result = InovanceEasyClient.ParseAddress("S0");
        Assert.True(result.IsSuccess);
        Assert.Equal(0x10, result.Content[2] & 0xF0);
        int value = result.Content[0] | (result.Content[1] << 8) | ((result.Content[2] & 0x0F) << 16);
        Assert.Equal(0x80000, value);
    }

    [Fact]
    public void ParseAddress_B3()
    {
        var result = InovanceEasyClient.ParseAddress("B3");
        Assert.True(result.IsSuccess);
        Assert.Equal(0x20, result.Content[2] & 0xF0);
        int value = result.Content[0] | (result.Content[1] << 8) | ((result.Content[2] & 0x0F) << 16);
        Assert.Equal(3, value);
    }

    [Fact]
    public void ParseAddress_UHex()
    {
        // U 系列扩展地址：4 字节直接存储值，type code 不在 byte[2] 高 nibble
        var result = InovanceEasyClient.ParseAddress("U100");
        Assert.True(result.IsSuccess, result.Message);
        // U100 → 16 进制 0x100
        int value = result.Content[0] | (result.Content[1] << 8) |
                     (result.Content[2] << 16) | (result.Content[3] << 24);
        Assert.Equal(0x100, value);
    }

    [Fact]
    public void ParseAddress_Empty_Fails()
    {
        var result = InovanceEasyClient.ParseAddress("");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseAddress_InvalidType_Fails()
    {
        var result = InovanceEasyClient.ParseAddress("Z100");
        Assert.False(result.IsSuccess);
    }

    #endregion

    #region 命令构建

    [Fact]
    public void BuildReadCommand_Structure()
    {
        var client = new InovanceEasyClient("127.0.0.1");
        var result = client.BuildReadCommand("D100", 10, isBit: false);
        Assert.True(result.IsSuccess);

        byte[] frame = result.Content;
        Assert.Equal(22, frame.Length);                     // 固定 22 字节
        Assert.Equal(22, frame[0] | (frame[1] << 8));      // 总长度
        Assert.Equal(0x01, frame[8]);                       // 读取命令

        // 字读取: bitCount = 10 * 16 = 160
        int bitCount = frame[18] | (frame[19] << 8) | (frame[20] << 16);
        Assert.Equal(160, bitCount);
    }

    [Fact]
    public void BuildReadCommand_BitRead()
    {
        var client = new InovanceEasyClient("127.0.0.1");
        var result = client.BuildReadCommand("M100", 8, isBit: true);
        Assert.True(result.IsSuccess);

        byte[] frame = result.Content;
        int bitCount = frame[18] | (frame[19] << 8) | (frame[20] << 16);
        Assert.Equal(8, bitCount);
    }

    [Fact]
    public void BuildWriteCommand_Structure()
    {
        var client = new InovanceEasyClient("127.0.0.1");
        byte[] data = { 0x12, 0x34, 0x56, 0x78 };
        var result = client.BuildWriteCommand("D100", data);
        Assert.True(result.IsSuccess);

        byte[] frame = result.Content;
        Assert.Equal(26, frame.Length);                     // 22 + 4
        Assert.Equal(26, frame[0] | (frame[1] << 8));      // 总长度
        Assert.Equal(0x02, frame[8]);                       // 写入命令

        // bitCount = 4 * 8 = 32
        int bitCount = frame[18] | (frame[19] << 8) | (frame[20] << 16);
        Assert.Equal(32, bitCount);

        // 数据在偏移 22
        Assert.Equal(0x12, frame[22]);
        Assert.Equal(0x78, frame[25]);
    }

    #endregion

    #region 响应解析

    [Fact]
    public void ParseResponse_Success()
    {
        byte[] response = new byte[26]; // 22 头 + 4 数据
        response[0] = 26; response[1] = 0;
        response[8] = 0x00; // 成功
        response[22] = 0x12;
        response[23] = 0x34;
        response[24] = 0x56;
        response[25] = 0x78;

        var result = InovanceEasyClient.ParseResponse(response);
        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Content.Length);
        Assert.Equal(0x12, result.Content[0]);
    }

    [Fact]
    public void ParseResponse_Error()
    {
        byte[] response = new byte[22];
        response[8] = 0x0F; // 错误标志
        response[10] = 0x01;
        response[11] = 0x00;

        var result = InovanceEasyClient.ParseResponse(response);
        Assert.False(result.IsSuccess);
        Assert.Contains("1", result.Message);
    }

    [Fact]
    public void ParseResponse_TooShort()
    {
        var result = InovanceEasyClient.ParseResponse(new byte[5]);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseResponse_Null()
    {
        var result = InovanceEasyClient.ParseResponse(null!);
        Assert.False(result.IsSuccess);
    }

    #endregion

    #region 虚拟服务器生命周期

    [Fact]
    public void Server_StartStop()
    {
        int port = PortBase;
        var server = new InovanceEasyVirtualServer(port);
        Assert.False(server.IsRunning);

        server.Start();
        Assert.True(server.IsRunning);

        server.Stop();
        Assert.False(server.IsRunning);
        server.Dispose();
    }

    #endregion

    #region 端到端测试

    [Fact]
    public void Client_ReadInt16_D()
    {
        int port = PortBase + 1;
        var server = new InovanceEasyVirtualServer(port);
        server.SetDWord(100, 0x1234);
        server.Start();

        try
        {
            var client = new InovanceEasyClient("127.0.0.1", port);
            client.SetPersistentConnection();

            var conn = client.Connect();
            Assert.True(conn.IsSuccess, conn.Message);

            var result = client.ReadInt16("D100");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x1234, (ushort)result.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_WriteReadInt16_D()
    {
        int port = PortBase + 2;
        var server = new InovanceEasyVirtualServer(port);
        server.Start();

        try
        {
            var client = new InovanceEasyClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.Write("D200", unchecked((short)0xABCD));
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt16("D200");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal(unchecked((short)0xABCD), readResult.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadUInt16_D()
    {
        int port = PortBase + 3;
        var server = new InovanceEasyVirtualServer(port);
        server.SetDWord(50, 0x5678);
        server.Start();

        try
        {
            var client = new InovanceEasyClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadUInt16("D50");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((ushort)0x5678, r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadWrite_Int32()
    {
        int port = PortBase + 4;
        var server = new InovanceEasyVirtualServer(port);
        server.Start();

        try
        {
            var client = new InovanceEasyClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            int expected = 0x12345678;
            Assert.True(client.Write("D300", expected).IsSuccess);

            var r = client.ReadInt32("D300");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(expected, r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadWrite_Float()
    {
        int port = PortBase + 5;
        var server = new InovanceEasyVirtualServer(port);
        server.Start();

        try
        {
            var client = new InovanceEasyClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            float expected = 3.14f;
            Assert.True(client.Write("D400", expected).IsSuccess);

            var r = client.ReadFloat("D400");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(expected, r.Content, 3);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadWrite_String()
    {
        int port = PortBase + 6;
        var server = new InovanceEasyVirtualServer(port);
        server.Start();

        try
        {
            var client = new InovanceEasyClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // "Hello" = 5 字节，写入后读取 3 个字 (6 字节) 包含完整字符串
            Assert.True(client.Write("D500", "Hello").IsSuccess);

            var r = client.ReadString("D500", 3);
            Assert.True(r.IsSuccess, r.Message);
            // 读取 3 字 = 6 字节，前 5 字节是 "Hello"，第 6 字节是 \0
            Assert.Equal("Hello", r.Content.TrimEnd('\0'));
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadWrite_UInt32()
    {
        int port = PortBase + 7;
        var server = new InovanceEasyVirtualServer(port);
        server.Start();

        try
        {
            var client = new InovanceEasyClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            uint expected = 0xAABBCCDD;
            Assert.True(client.Write("D600", expected).IsSuccess);

            var r = client.ReadUInt32("D600");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(expected, r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadWrite_Int64()
    {
        int port = PortBase + 8;
        var server = new InovanceEasyVirtualServer(port);
        server.Start();

        try
        {
            var client = new InovanceEasyClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            long expected = 0x0102030405060708;
            Assert.True(client.Write("D700", expected).IsSuccess);

            var r = client.ReadInt64("D700");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(expected, r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadWrite_W()
    {
        int port = PortBase + 9;
        var server = new InovanceEasyVirtualServer(port);
        server.Start();

        try
        {
            var client = new InovanceEasyClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.True(client.Write("W100", (short)0x1111).IsSuccess);

            var r = client.ReadInt16("W100");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((short)0x1111, r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadWrite_R()
    {
        int port = PortBase + 10;
        var server = new InovanceEasyVirtualServer(port);
        server.Start();

        try
        {
            var client = new InovanceEasyClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.True(client.Write("R50", (short)0x2222).IsSuccess);

            var r = client.ReadInt16("R50");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((short)0x2222, r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadWrite_Double()
    {
        int port = PortBase + 11;
        var server = new InovanceEasyVirtualServer(port);
        server.Start();

        try
        {
            var client = new InovanceEasyClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            double expected = 2.718;
            Assert.True(client.Write("D800", expected).IsSuccess);

            var r = client.ReadDouble("D800");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(expected, r.Content, 3);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_NonPersistent_ReadWrite()
    {
        // EasyNet 无需握手，非持久模式每次操作自动连接/断开
        int port = PortBase + 12;
        var server = new InovanceEasyVirtualServer(port);
        server.Start();

        try
        {
            var client = new InovanceEasyClient("127.0.0.1", port);
            // 不调用 SetPersistentConnection → 非持久模式

            Assert.True(client.Write("D100", (short)0x1234).IsSuccess);
            var r = client.ReadInt16("D100");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((short)0x1234, r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_MultipleReadWrite_Sequence()
    {
        int port = PortBase + 13;
        var server = new InovanceEasyVirtualServer(port);
        server.Start();

        try
        {
            var client = new InovanceEasyClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 连续多次读写
            Assert.True(client.Write("D10", (short)100).IsSuccess);
            Assert.True(client.Write("D11", (short)200).IsSuccess);

            Assert.Equal((short)100, client.ReadInt16("D10").Content);
            Assert.Equal((short)200, client.ReadInt16("D11").Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadBytes_Multiple()
    {
        int port = PortBase + 14;
        var server = new InovanceEasyVirtualServer(port);
        server.SetDWord(50, 0x1122);
        server.SetDWord(51, 0x3344);
        server.SetDWord(52, 0x5566);
        server.Start();

        try
        {
            var client = new InovanceEasyClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadBytes("D50", 3);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(6, r.Content.Length);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    #endregion
}
