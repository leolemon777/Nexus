using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nexus.Siemens;

namespace Nexus.Siemens.Tests;

public class FetchWriteTests
{
    private const int PortBase = 17100;

    #region 地址解析

    [Theory]
    [InlineData("I100", 3, 100, 0)]
    [InlineData("Q200", 4, 200, 0)]
    [InlineData("M50", 2, 50, 0)]
    [InlineData("T10", 7, 10, 0)]
    [InlineData("C5", 6, 5, 0)]
    [InlineData("DB1.100", 1, 100, 1)]
    [InlineData("DB3.0", 1, 0, 3)]
    public void AnalysisAddress_Valid(string addr, byte expectedArea, int expectedStart, ushort expectedDb)
    {
        var r = SiemensFetchWriteClient.AnalysisAddress(addr);
        Assert.True(r.Success, r.Message);
        Assert.Equal(expectedArea, r.AreaCode);
        Assert.Equal(expectedStart, r.StartAddr);
        Assert.Equal(expectedDb, r.DbNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("X100")]
    [InlineData("DB1")]
    public void AnalysisAddress_Invalid(string addr)
    {
        var r = SiemensFetchWriteClient.AnalysisAddress(addr);
        Assert.False(r.Success);
    }

    #endregion

    #region 帧构建

    [Fact]
    public void BuildReadCommand_M100()
    {
        var r = SiemensFetchWriteClient.BuildReadCommand("M100", 10);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(16, r.Content.Length);
        Assert.Equal(0x53, r.Content[0]);  // 'S'
        Assert.Equal(0x35, r.Content[1]);  // '5'
        Assert.Equal(0x05, r.Content[5]);  // 读取子命令
        Assert.Equal(2, r.Content[8]);     // M 区
        Assert.Equal(10, r.Content[13]);   // 长度低字节
    }

    [Fact]
    public void BuildReadCommand_DB1_100()
    {
        var r = SiemensFetchWriteClient.BuildReadCommand("DB1.100", 4);
        Assert.True(r.IsSuccess);
        Assert.Equal(1, r.Content[8]);     // DB 区
        Assert.Equal(1, r.Content[9]);     // DB 编号
        Assert.Equal(0, r.Content[10]);    // 地址高字节
        Assert.Equal(100, r.Content[11]);  // 地址低字节
    }

    [Fact]
    public void BuildWriteCommand_M200()
    {
        var data = new byte[] { 0x12, 0x34 };
        var r = SiemensFetchWriteClient.BuildWriteCommand("M200", data);
        Assert.True(r.IsSuccess);
        Assert.Equal(16 + 2, r.Content.Length);
        Assert.Equal(0x06, r.Content[5]);  // 写入子命令
        Assert.Equal(2, r.Content[8]);     // M 区
        Assert.Equal(0x12, r.Content[16]); // 数据
        Assert.Equal(0x34, r.Content[17]);
    }

    #endregion

    #region 响应校验

    [Fact]
    public void CheckResponse_Success()
    {
        var resp = new byte[16];
        var r = SiemensFetchWriteClient.CheckResponse(resp);
        Assert.True(r.IsSuccess);
    }

    [Fact]
    public void CheckResponse_Error()
    {
        var resp = new byte[16];
        resp[8] = 0x05;
        var r = SiemensFetchWriteClient.CheckResponse(resp);
        Assert.False(r.IsSuccess);
        Assert.Contains("0x05", r.Message);
    }

    [Fact]
    public void CheckResponse_TooShort()
    {
        var r = SiemensFetchWriteClient.CheckResponse(new byte[5]);
        Assert.False(r.IsSuccess);
    }

    #endregion

    #region 虚拟服务器 + 客户端端到端

    [Fact]
    public void Server_StartStop()
    {
        int port = PortBase;
        var server = new SiemensFetchWriteVirtualServer(port);
        Assert.False(server.IsRunning);

        server.Start();
        Assert.True(server.IsRunning);

        server.Stop();
        Assert.False(server.IsRunning);
        server.Dispose();
    }

    [Fact]
    public void Client_ReadInt16_M_Area()
    {
        int port = PortBase + 1;
        var server = new SiemensFetchWriteVirtualServer(port);
        server.SetM(100, 0x12);
        server.SetM(101, 0x34);
        server.Start();

        try
        {
            var client = new SiemensFetchWriteClient("127.0.0.1", port);
            client.SetPersistentConnection();

            var conn = client.Connect();
            Assert.True(conn.IsSuccess, conn.Message);

            var result = client.ReadInt16("M100");
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
    public void Client_WriteRead_M_Area()
    {
        int port = PortBase + 2;
        var server = new SiemensFetchWriteVirtualServer(port);
        server.Start();

        try
        {
            var client = new SiemensFetchWriteClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.Write("M200", unchecked((short)0xABCD));
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt16("M200");
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
    public void Client_ReadInt32_M_Area()
    {
        int port = PortBase + 3;
        var server = new SiemensFetchWriteVirtualServer(port);
        server.SetM(300, 0x01);
        server.SetM(301, 0x02);
        server.SetM(302, 0x03);
        server.SetM(303, 0x04);
        server.Start();

        try
        {
            var client = new SiemensFetchWriteClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadInt32("M300");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(0x01020304, r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadWrite_DB_Area()
    {
        int port = PortBase + 4;
        var server = new SiemensFetchWriteVirtualServer(port);
        server.SetDBWord(0, 0x5678);
        server.SetDBWord(2, 0x9ABC);
        server.Start();

        try
        {
            var client = new SiemensFetchWriteClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 读
            var r = client.ReadUInt16("DB1.0");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((ushort)0x5678, r.Content);

            // 写
            Assert.True(client.Write("DB1.0", (short)0x1234).IsSuccess);

            // 回读
            var r2 = client.ReadInt16("DB1.0");
            Assert.True(r2.IsSuccess);
            Assert.Equal((short)0x1234, r2.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadBool_ViaReadModifyWrite()
    {
        int port = PortBase + 5;
        var server = new SiemensFetchWriteVirtualServer(port);
        server.SetM(50, 0x05); // 0000 0101 → bit0=1, bit2=1
        server.Start();

        try
        {
            var client = new SiemensFetchWriteClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 读位
            var b0 = client.ReadBool("M50.0");
            Assert.True(b0.IsSuccess);
            Assert.True(b0.Content);

            var b1 = client.ReadBool("M50.1");
            Assert.True(b1.IsSuccess);
            Assert.False(b1.Content);

            var b2 = client.ReadBool("M50.2");
            Assert.True(b2.IsSuccess);
            Assert.True(b2.Content);

            // 写位（设置 bit1）
            Assert.True(client.Write("M50.1", true).IsSuccess);

            // 回读
            var b1After = client.ReadBool("M50.1");
            Assert.True(b1After.IsSuccess);
            Assert.True(b1After.Content);

            // bit0 不应被改变
            var b0After = client.ReadBool("M50.0");
            Assert.True(b0After.IsSuccess);
            Assert.True(b0After.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadWrite_I_Q_Areas()
    {
        int port = PortBase + 6;
        var server = new SiemensFetchWriteVirtualServer(port);
        server.SetI(10, 0xAA);
        server.Start();

        try
        {
            var client = new SiemensFetchWriteClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 读 I 区
            var r = client.ReadBytes("I10", 1);
            Assert.True(r.IsSuccess);
            Assert.Equal((byte)0xAA, r.Content[0]);

            // 写 Q 区
            Assert.True(client.Write("Q20", new byte[] { 0xBB }).IsSuccess);

            // 回读 Q 区
            var r2 = client.ReadBytes("Q20", 1);
            Assert.True(r2.IsSuccess);
            Assert.Equal((byte)0xBB, r2.Content[0]);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadString()
    {
        int port = PortBase + 7;
        var server = new SiemensFetchWriteVirtualServer(port);
        server.SetM(400, (byte)'H');
        server.SetM(401, (byte)'i');
        server.SetM(402, (byte)'!');
        server.SetM(403, (byte)0);
        server.Start();

        try
        {
            var client = new SiemensFetchWriteClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadString("M400", 4);
            Assert.True(r.IsSuccess);
            Assert.Equal("Hi!", r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadFloat()
    {
        int port = PortBase + 8;
        var server = new SiemensFetchWriteVirtualServer(port);
        float expected = 3.14f;
        // Fetch/Write 是大端序，需要按大端序存储
        int intBits = BitConverter.ToInt32(BitConverter.GetBytes(expected), 0);
        server.SetM(500, (byte)(intBits >> 24));
        server.SetM(501, (byte)(intBits >> 16));
        server.SetM(502, (byte)(intBits >> 8));
        server.SetM(503, (byte)(intBits & 0xFF));
        server.Start();

        try
        {
            var client = new SiemensFetchWriteClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadFloat("M500");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(expected, r.Content, 2);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void ConnectionPool_ReadInt16_ReusesPersistentConnection()
    {
        int port = PortBase + 20;
        var server = new SiemensFetchWriteVirtualServer(port);
        server.SetM(100, 0x12);
        server.SetM(101, 0x34);
        server.Start();

        try
        {
            using var pool = new SiemensFetchWriteConnectionPool("127.0.0.1", port);

            var first = pool.ReadInt16("M100");
            Assert.True(first.IsSuccess, first.Message);
            Assert.Equal(0x1234, (ushort)first.Content);

            var second = pool.ReadInt16("M100");
            Assert.True(second.IsSuccess, second.Message);
            Assert.Equal(0x1234, (ushort)second.Content);

            Assert.True(WaitForConnections(server, 1));
            Assert.Equal(1, server.ConnectionCount);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void ConnectionPool_WriteAndRead_RoundTrip()
    {
        int port = PortBase + 21;
        var server = new SiemensFetchWriteVirtualServer(port);
        server.Start();

        try
        {
            using var pool = new SiemensFetchWriteConnectionPool("127.0.0.1", port);

            var write = pool.Write("DB1.10", unchecked((short)0xABCD));
            Assert.True(write.IsSuccess, write.Message);

            var read = pool.ReadInt16("DB1.10");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal(unchecked((short)0xABCD), read.Content);

            Assert.True(WaitForConnections(server, 1));
            Assert.Equal(1, server.ConnectionCount);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void ConnectionPool_BatchReadAndWrite_UsesSingleConnection()
    {
        int port = PortBase + 22;
        var server = new SiemensFetchWriteVirtualServer(port);
        server.Start();

        try
        {
            using var pool = new SiemensFetchWriteConnectionPool("127.0.0.1", port);

            var write = pool.BatchWrite(new[]
            {
                new KeyValuePair<string, object>("M10", (short)123),
                new KeyValuePair<string, object>("M12", (short)456)
            });
            Assert.True(write.IsSuccess, write.Message);

            var read = pool.BatchRead(new[] { "M10", "M12" });
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal((short)123, read.Content["M10"]);
            Assert.Equal((short)456, read.Content["M12"]);

            Assert.True(WaitForConnections(server, 1));
            Assert.Equal(1, server.ConnectionCount);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public async Task ConnectionPool_ExecuteAsync_ReadInt16_ReusesPersistentConnection()
    {
        int port = PortBase + 23;
        var server = new SiemensFetchWriteVirtualServer(port);
        server.SetM(30, 0x01);
        server.SetM(31, 0x2C);
        server.Start();

        try
        {
            using var pool = new SiemensFetchWriteConnectionPool("127.0.0.1", port);

            var result = await pool.ExecuteAsync(c => Task.FromResult(c.ReadInt16("M30")));
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((short)300, result.Content);

            var second = await pool.ExecuteAsync(c => Task.FromResult(c.ReadInt16("M30")));
            Assert.True(second.IsSuccess, second.Message);
            Assert.Equal((short)300, second.Content);

            Assert.True(WaitForConnections(server, 1));
            Assert.Equal(1, server.ConnectionCount);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void ConnectionPool_ForwardsMessageEvents()
    {
        int port = PortBase + 24;
        var server = new SiemensFetchWriteVirtualServer(port);
        server.SetM(40, 0x00);
        server.SetM(41, 0x2A);
        server.Start();

        try
        {
            using var pool = new SiemensFetchWriteConnectionPool("127.0.0.1", port);
            int sent = 0;
            int received = 0;
            pool.OnMessageSent += (_, _) => sent++;
            pool.OnMessageReceived += (_, _) => received++;

            var result = pool.ReadInt16("M40");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((short)42, result.Content);

            Assert.True(sent > 0);
            Assert.True(received > 0);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    private static bool WaitForConnections(SiemensFetchWriteVirtualServer server, int expected, int timeoutMs = 1000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (server.ConnectionCount >= expected)
                return true;
            Thread.Sleep(10);
        }
        return server.ConnectionCount >= expected;
    }

    #endregion
}
