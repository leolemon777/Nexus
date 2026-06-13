using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Xunit;
using Nexus.AllenBradley;

namespace Nexus.AllenBradley.Tests;

public class PcccTests
{
    private const int PortBase = 21000;

    #region 地址解析

    [Theory]
    [InlineData("N7:0",   0x89, 7, 0, 0)]
    [InlineData("N7:100", 0x89, 7, 100, 0)]
    [InlineData("F8:0",   0x8A, 8, 0, 0)]
    [InlineData("F8:1",   0x8A, 8, 1, 0)]
    [InlineData("B3:0",   0x85, 3, 0, 0)]
    [InlineData("T4:0",   0x86, 4, 0, 0)]
    [InlineData("C5:0",   0x87, 5, 0, 0)]
    [InlineData("R6:0",   0x88, 6, 0, 0)]
    [InlineData("S2:0",   0x84, 2, 0, 0)]
    [InlineData("ST9:0",  0x8D, 9, 0, 0)]
    [InlineData("L10:0",  0x91, 10, 0, 0)]
    [InlineData("I1:0",   0x83, 1, 0, 0)]
    [InlineData("O0:0",   0x82, 0, 0, 0)]
    [InlineData("A9:0",   0x8E, 9, 0, 0)]
    public void ParseAddress_Valid(string addr, byte expectedDataCode, int expectedFileNo, int expectedElem, int expectedSub)
    {
        var result = PcccClient.ParseAddress(addr);
        Assert.Equal(expectedDataCode, result.DataCode);
        Assert.Equal((ushort)expectedFileNo, result.FileNumber);
        Assert.Equal((ushort)expectedElem, result.Element);
        Assert.Equal((ushort)expectedSub, result.SubElement);
    }

    [Theory]
    [InlineData("B3:0/5", 0x85, 3, 0, 5)]
    [InlineData("N7:0.1", 0x89, 7, 0, 1)]
    [InlineData("N7:10/15", 0x89, 7, 10, 15)]
    public void ParseAddress_BitAddress(string addr, byte expectedDataCode, int expectedFileNo, int expectedElem, int expectedSub)
    {
        var result = PcccClient.ParseAddress(addr);
        Assert.Equal(expectedDataCode, result.DataCode);
        Assert.Equal((ushort)expectedFileNo, result.FileNumber);
        Assert.Equal((ushort)expectedElem, result.Element);
        Assert.Equal((ushort)expectedSub, result.SubElement);
    }

    [Theory]
    [InlineData("S:0", 0x84, 2)]  // S defaults to file 2
    [InlineData("I:0", 0x83, 1)]  // I defaults to file 1
    [InlineData("O:0", 0x82, 0)]  // O defaults to file 0
    [InlineData("ST:0", 0x8D, 1)] // ST defaults to file 1
    public void ParseAddress_DefaultFileNumber(string addr, byte expectedDataCode, int expectedFileNo)
    {
        var result = PcccClient.ParseAddress(addr);
        Assert.Equal(expectedDataCode, result.DataCode);
        Assert.Equal((ushort)expectedFileNo, result.FileNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("X1:0")]
    [InlineData("N7")]
    public void ParseAddress_Invalid(string addr)
    {
        Assert.Throws<ArgumentException>(() => PcccClient.ParseAddress(addr));
    }

    #endregion

    #region CIP Execute PCCC 封装

    [Fact]
    public void WrapInCipExecutePccc_BasicStructure()
    {
        byte[] pcccData = { 0x0F, 0x00, 0x01, 0x00, 0xA2, 0x02, 0x07, 0x89, 0x00, 0x00 };
        byte[] result = PcccClient.WrapInCipExecutePccc(pcccData);

        Assert.Equal(12 + pcccData.Length, result.Length);
        Assert.Equal(0x4B, result[0]);  // Execute PCCC service
        Assert.Equal(0x02, result[1]);  // Path size
        Assert.Equal(0x20, result[2]);  // Logical segment
        Assert.Equal(0x67, result[3]);  // Class ID
        Assert.Equal(0x24, result[4]);  // Instance segment
        Assert.Equal(0x01, result[5]);  // Instance 1
        // Connection params
        Assert.Equal(0x09, result[6]);
        Assert.Equal(0x10, result[7]);
        Assert.Equal(0x0B, result[8]);
        Assert.Equal(0x46, result[9]);
        Assert.Equal(0xA5, result[10]);
        Assert.Equal(0xC1, result[11]);
        // PCCC data starts at offset 12
        Assert.Equal(0x0F, result[12]);
    }

    #endregion

    #region PCCC 长度编码

    [Fact]
    public void ReadPcccLength_Short()
    {
        byte[] data = { 0x07, 0x00, 0x00 };
        int bytesRead;
        int value = PcccClient.ReadPcccLength(data, 0, out bytesRead);
        Assert.Equal(7, value);
        Assert.Equal(1, bytesRead);
    }

    [Fact]
    public void ReadPcccLength_Extended()
    {
        byte[] data = { 0xFF, 0x01, 0x00 }; // 256+1 = 257... no, this is 0x0001 = 1
        // Actually: 0xFF means extended, then 2 bytes LE = value
        // data[1]=0x01, data[2]=0x00 → value = 1
        int bytesRead;
        int value = PcccClient.ReadPcccLength(data, 0, out bytesRead);
        Assert.Equal(1, value);
        Assert.Equal(3, bytesRead);
    }

    [Fact]
    public void ReadPcccLength_Extended255()
    {
        byte[] data = { 0xFF, 0xFF, 0x00 }; // value = 0x00FF = 255
        int bytesRead;
        int value = PcccClient.ReadPcccLength(data, 0, out bytesRead);
        Assert.Equal(255, value);
        Assert.Equal(3, bytesRead);
    }

    #endregion

    #region PCCC 响应解析

    [Fact]
    public void ParsePcccResponse_Success_WithData()
    {
        byte[] response = { 0x0F, 0x00, 0x01, 0x00, 0x00, 0x12, 0x34 };
        var result = PcccClient.ParsePcccResponse(response);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, result.Content.Length);
        Assert.Equal(0x12, result.Content[0]);
        Assert.Equal(0x34, result.Content[1]);
    }

    [Fact]
    public void ParsePcccResponse_Success_NoData()
    {
        byte[] response = { 0x0F, 0x00, 0x01, 0x00, 0x00 };
        var result = PcccClient.ParsePcccResponse(response);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Empty(result.Content);
    }

    [Fact]
    public void ParsePcccResponse_Error()
    {
        byte[] response = { 0x0F, 0x10, 0x01, 0x00, 0x00 };
        var result = PcccClient.ParsePcccResponse(response);
        Assert.False(result.IsSuccess);
        Assert.Contains("0x10", result.Message);
    }

    [Fact]
    public void ParsePcccResponse_TooShort()
    {
        var r = PcccClient.ParsePcccResponse(new byte[1]);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void ParsePcccResponse_NullInput()
    {
        var r = PcccClient.ParsePcccResponse(null!);
        Assert.False(r.IsSuccess);
    }

    #endregion

    #region 错误描述

    [Fact]
    public void GetStatusDescription_KnownError()
    {
        byte[] resp = { 0x0F, 0x10, 0x00, 0x00, 0x00 };
        string desc = PcccClient.GetStatusDescription(resp);
        Assert.Contains("Illegal command", desc);
    }

    [Fact]
    public void GetStatusDescription_ExtError()
    {
        byte[] resp = { 0x0F, 0xF0, 0x00, 0x00, 0x04 };
        string desc = PcccClient.GetStatusDescription(resp);
        Assert.Contains("Symbol not found", desc);
    }

    #endregion

    #region 虚拟服务器生命周期

    [Fact]
    public void Server_StartStop()
    {
        int port = PortBase;
        var server = new PcccVirtualServer(port);
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
    public void Client_ReadInt16_N7()
    {
        int port = PortBase + 1;
        var server = new PcccVirtualServer(port);
        server.SetN7Word(100, 0x1234);
        server.Start();

        try
        {
            var client = new PcccClient("127.0.0.1", port);
            client.SetPersistentConnection();

            var conn = client.Connect();
            Assert.True(conn.IsSuccess, conn.Message);

            var result = client.ReadInt16("N7:100");
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
    public void Client_WriteReadInt16_N7()
    {
        int port = PortBase + 2;
        var server = new PcccVirtualServer(port);
        server.Start();

        try
        {
            var client = new PcccClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.Write("N7:200", unchecked((short)0xABCD));
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt16("N7:200");
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
    public void Client_ReadUInt16_N7()
    {
        int port = PortBase + 3;
        var server = new PcccVirtualServer(port);
        server.SetN7Word(50, 0x5678);
        server.Start();

        try
        {
            var client = new PcccClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadUInt16("N7:50");
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
    public void Client_ReadInt32_N7()
    {
        int port = PortBase + 4;
        var server = new PcccVirtualServer(port);
        // 写入 Int32 (2 个 word) — 小端序，使用小元素号避免超出存储
        server.SetN7Word(10, 0x0204); // low word
        server.SetN7Word(11, 0x0608); // high word
        server.Start();

        try
        {
            var client = new PcccClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadInt32("N7:10");
            Assert.True(r.IsSuccess, r.Message);
            // LE: low word at offset 0, high word at offset 2
            // word 0x0204 → bytes 04 02, word 0x0608 → bytes 08 06
            // Int32 = 0x06080204
            Assert.Equal(0x06080204, r.Content);
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
        var server = new PcccVirtualServer(port);
        server.Start();

        try
        {
            var client = new PcccClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            float expected = 3.14f;
            Assert.True(client.Write("F8:10", expected).IsSuccess);

            var r = client.ReadFloat("F8:10");
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
    public void Client_ReadWrite_Bit()
    {
        int port = PortBase + 6;
        var server = new PcccVirtualServer(port);
        server.Start();

        try
        {
            var client = new PcccClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 写 true 到 B3:0/5
            Assert.True(client.Write("B3:0/5", true).IsSuccess);

            var b5 = client.ReadBool("B3:0/5");
            Assert.True(b5.IsSuccess, b5.Message);
            Assert.True(b5.Content);

            // 写 false 到 B3:0/5
            Assert.True(client.Write("B3:0/5", false).IsSuccess);

            var b5Again = client.ReadBool("B3:0/5");
            Assert.True(b5Again.IsSuccess);
            Assert.False(b5Again.Content);

            // 其他位应该不受影响（B3:0/3）
            var b3 = client.ReadBool("B3:0/3");
            Assert.True(b3.IsSuccess);
            Assert.False(b3.Content);
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
        int port = PortBase + 7;
        var server = new PcccVirtualServer(port);
        server.Start();

        try
        {
            var client = new PcccClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 写入字符串到 N7 区域（非 ST 格式 — 直接写入 ASCII）
            byte[] helloBytes = Encoding.ASCII.GetBytes("Hello");
            Assert.True(client.Write("N7:0", helloBytes).IsSuccess);

            var r = client.ReadString("N7:0", 5);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal("Hello", r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadWrite_T4()
    {
        int port = PortBase + 8;
        var server = new PcccVirtualServer(port);
        server.Start();

        try
        {
            var client = new PcccClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.True(client.Write("T4:10", (short)0x1111).IsSuccess);

            var r = client.ReadInt16("T4:10");
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
    public void Client_ReadWrite_C5()
    {
        int port = PortBase + 9;
        var server = new PcccVirtualServer(port);
        server.Start();

        try
        {
            var client = new PcccClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.True(client.Write("C5:20", (short)0x2222).IsSuccess);

            var r = client.ReadInt16("C5:20");
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
    public void Client_ReadDouble_FromFloat()
    {
        int port = PortBase + 10;
        var server = new PcccVirtualServer(port);
        server.Start();

        try
        {
            var client = new PcccClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.True(client.Write("F8:0", 2.718f).IsSuccess);

            var r = client.ReadDouble("F8:0");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(2.718f, (float)r.Content, 3);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_MultipleBit_MaskWrite()
    {
        int port = PortBase + 11;
        var server = new PcccVirtualServer(port);
        server.Start();

        try
        {
            var client = new PcccClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 设置多个位
            Assert.True(client.Write("B3:0/0", true).IsSuccess);
            Assert.True(client.Write("B3:0/5", true).IsSuccess);
            Assert.True(client.Write("B3:0/10", true).IsSuccess);

            // 验证各个位
            Assert.True(client.ReadBool("B3:0/0").Content);
            Assert.True(client.ReadBool("B3:0/5").Content);
            Assert.True(client.ReadBool("B3:0/10").Content);

            // 未设置的位应为 false
            Assert.False(client.ReadBool("B3:0/3").Content);
            Assert.False(client.ReadBool("B3:0/15").Content);

            // 清除一个位
            Assert.True(client.Write("B3:0/5", false).IsSuccess);
            Assert.False(client.ReadBool("B3:0/5").Content);
            // 其他位不受影响
            Assert.True(client.ReadBool("B3:0/0").Content);
            Assert.True(client.ReadBool("B3:0/10").Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_WriteUInt32_N7()
    {
        int port = PortBase + 12;
        var server = new PcccVirtualServer(port);
        server.Start();

        try
        {
            var client = new PcccClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            uint expected = 0x12345678;
            Assert.True(client.Write("N7:50", expected).IsSuccess);

            var r = client.ReadUInt32("N7:50");
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
    public void Client_Reconnect_ReadWrite()
    {
        // PCCC over ENIP 需要会话注册，必须使用持久连接模式。
        // 测试断开后重新连接的能力。
        int port = PortBase + 13;
        var server = new PcccVirtualServer(port);
        server.Start();

        try
        {
            var client = new PcccClient("127.0.0.1", port);
            client.SetPersistentConnection();

            // 第一次连接
            Assert.True(client.Connect().IsSuccess);
            Assert.True(client.Write("N7:100", (short)0x1234).IsSuccess);
            var r1 = client.ReadInt16("N7:100");
            Assert.True(r1.IsSuccess, r1.Message);
            Assert.Equal((short)0x1234, r1.Content);

            // 断开并重连
            client.Disconnect();
            Assert.True(client.Connect().IsSuccess);
            var r2 = client.ReadInt16("N7:100");
            Assert.True(r2.IsSuccess, r2.Message);
            Assert.Equal((short)0x1234, r2.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadWrite_S2()
    {
        int port = PortBase + 14;
        var server = new PcccVirtualServer(port);
        server.Start();

        try
        {
            var client = new PcccClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.True(client.Write("S2:10", (short)0x5555).IsSuccess);

            var r = client.ReadInt16("S2:10");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((short)0x5555, r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void ConnectionPool_ReadWrite_ReusesPersistentSession()
    {
        int port = GetFreeTcpPort();
        using var server = new PcccVirtualServer(port);
        server.Start();

        using var pool = new PcccConnectionPool("127.0.0.1", port, maxPoolSize: 1);

        var write = pool.Write("N7:20", (short)4321);
        Assert.True(write.IsSuccess, write.Message);

        var read = pool.ReadInt16("N7:20");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((short)4321, read.Content);
        Assert.Equal(0, pool.ActiveCount);
        Assert.Equal(1, pool.IdleCount);
    }

    [Fact]
    public void ConnectionPool_ForwardsPacketEvents()
    {
        int port = GetFreeTcpPort();
        using var server = new PcccVirtualServer(port);
        server.SetN7Word(10, 0x1234);
        server.Start();

        using var pool = new PcccConnectionPool("127.0.0.1", port, maxPoolSize: 1);
        int sent = 0;
        int received = 0;
        pool.OnMessageSent += (_, hex) =>
        {
            if (!string.IsNullOrWhiteSpace(hex)) Interlocked.Increment(ref sent);
        };
        pool.OnMessageReceived += (_, hex) =>
        {
            if (!string.IsNullOrWhiteSpace(hex)) Interlocked.Increment(ref received);
        };

        var read = pool.ReadUInt16("N7:10");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((ushort)0x1234, read.Content);
        Assert.True(sent > 0);
        Assert.True(received > 0);
    }

    [Fact]
    public void ConnectionPool_BatchReadWrite()
    {
        int port = GetFreeTcpPort();
        using var server = new PcccVirtualServer(port);
        server.Start();

        using var pool = new PcccConnectionPool("127.0.0.1", port, maxPoolSize: 1);
        var write = pool.BatchWrite(new[]
        {
            new KeyValuePair<string, object>("N7:30", (short)111),
            new KeyValuePair<string, object>("N7:31", (short)222)
        });
        Assert.True(write.IsSuccess, write.Message);

        var read = pool.BatchRead(new[] { "N7:30", "N7:31" });
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((short)111, read.Content["N7:30"]);
        Assert.Equal((short)222, read.Content["N7:31"]);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    #endregion
}
