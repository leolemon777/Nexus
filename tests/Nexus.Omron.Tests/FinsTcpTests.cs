using System;
using System.Threading;
using Xunit;
using Nexus.Omron;

namespace Nexus.Omron.Tests;

/// <summary>
/// FINS-TCP 客户端集成测试 — 通过 FinsVirtualServer 验证端到端通讯。
/// </summary>
public class FinsTcpTests
{
    private const int TestPortBase = 19600;

    // ── 服务器启动/停止 ──────────────────────

    [Fact]
    public void Server_StartStop_Works()
    {
        var server = new FinsVirtualServer(TestPortBase);
        Assert.False(server.IsRunning);

        server.Start();
        Assert.True(server.IsRunning);

        server.Stop();
        Assert.False(server.IsRunning);

        server.Dispose();
    }

    // ── 连接握手 ──────────────────────────────

    [Fact]
    public void Client_Connect_Handshake()
    {
        int port = TestPortBase + 1;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            var result = client.Connect();
            Assert.True(result.IsSuccess, result.Message);

            Assert.NotEqual((byte)0, client.ServerNode);
            Assert.NotEqual((byte)0, client.ClientNode);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_Connect_BadPort_Fails()
    {
        var client = new FinsTcpClient("127.0.0.1", TestPortBase + 99);
        var result = client.Connect();
        Assert.False(result.IsSuccess);
        client.Dispose();
    }

    // ── DM 区域读取 ──────────────────────────

    [Fact]
    public void Client_ReadInt16_DM_PreSet()
    {
        int port = TestPortBase + 10;
        var server = new FinsVirtualServer(port);
        server.SetDMWord(100, (short)0x1234);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();

            var conn = client.Connect();
            Assert.True(conn.IsSuccess, conn.Message);

            var result = client.ReadInt16("D100");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((short)0x1234, result.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadUInt16_DM()
    {
        int port = TestPortBase + 11;
        var server = new FinsVirtualServer(port);
        server.SetDMWord(200, (ushort)0xABCD);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadUInt16("D200");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((ushort)0xABCD, result.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadInt32_DM()
    {
        int port = TestPortBase + 12;
        var server = new FinsVirtualServer(port);
        server.SetDMDWord(50, 0x12345678);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadInt32("D50");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x12345678, result.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadFloat_DM()
    {
        int port = TestPortBase + 13;
        var server = new FinsVirtualServer(port);
        server.SetDMReal(30, 3.14f);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadFloat("D30");
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(Math.Abs(result.Content - 3.14f) < 0.01f, $"Got {result.Content}");

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    // ── 写入 + 读回验证 ──────────────────────

    [Fact]
    public void Client_WriteInt16_ReadBack()
    {
        int port = TestPortBase + 20;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.Write("D100", (short)-12345);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt16("D100");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal((short)-12345, readResult.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_WriteUInt16_ReadBack()
    {
        int port = TestPortBase + 21;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.Write("D150", (ushort)60000);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadUInt16("D150");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal((ushort)60000, readResult.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_WriteInt32_ReadBack()
    {
        int port = TestPortBase + 22;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.Write("D200", unchecked((int)0x87654321));
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt32("D200");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal(unchecked((int)0x87654321), readResult.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_WriteFloat_ReadBack()
    {
        int port = TestPortBase + 23;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.Write("D250", 2.718f);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadFloat("D250");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.True(Math.Abs(readResult.Content - 2.718f) < 0.001f, $"Got {readResult.Content}");

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_WriteString_ReadBack()
    {
        int port = TestPortBase + 24;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.Write("D300", "HELLO");
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadString("D300", 5);
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal("HELLO", readResult.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_WriteBytes_ReadBack()
    {
        int port = TestPortBase + 25;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            byte[] writeData = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
            var writeResult = client.Write("D400", writeData);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadBytes("D400", 4);
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal(writeData, readResult.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    // ── CIO 区域 ──────────────────────────────

    [Fact]
    public void Client_ReadWrite_CIO()
    {
        int port = TestPortBase + 30;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.Write("CIO100", (short)0x5678);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt16("CIO100");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal((short)0x5678, readResult.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    // ── WR 区域 ──────────────────────────────

    [Fact]
    public void Client_ReadWrite_WR()
    {
        int port = TestPortBase + 31;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.Write("W50", (short)0x1234);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt16("W50");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal((short)0x1234, readResult.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    // ── HR 区域 ──────────────────────────────

    [Fact]
    public void Client_ReadWrite_HR()
    {
        int port = TestPortBase + 32;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.Write("H30", unchecked((short)0xAAAA));
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt16("H30");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal(unchecked((short)0xAAAA), readResult.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    // ── AR 区域 ──────────────────────────────

    [Fact]
    public void Client_ReadWrite_AR()
    {
        int port = TestPortBase + 33;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.Write("A20", (short)0x5555);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt16("A20");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal(unchecked((short)0x5555), readResult.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    // ── 短连接模式 ──────────────────────────

    [Fact]
    public void Client_ShortConnect_MultipleReads()
    {
        int port = TestPortBase + 40;
        var server = new FinsVirtualServer(port);
        server.SetDMWord(10, (short)100);
        server.SetDMWord(11, (short)200);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            // 不调用 SetPersistentConnection → 短连接模式

            var r1 = client.ReadInt16("D10");
            Assert.True(r1.IsSuccess, r1.Message);
            Assert.Equal((short)100, r1.Content);

            var r2 = client.ReadInt16("D11");
            Assert.True(r2.IsSuccess, r2.Message);
            Assert.Equal((short)200, r2.Content);

            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    // ── VirtualServer 预设数据验证 ──────────────

    [Fact]
    public void Server_SetDMWord_GetDMWord()
    {
        var server = new FinsVirtualServer(TestPortBase + 50);
        server.SetDMWord(100, (ushort)0xABCD);
        Assert.Equal((ushort)0xABCD, server.GetDMWord(100));
        server.Dispose();
    }

    [Fact]
    public void Server_SetDMWordSigned_GetDMWordSigned()
    {
        var server = new FinsVirtualServer(TestPortBase + 51);
        server.SetDMWord(100, (short)-1234);
        Assert.Equal((short)-1234, server.GetDMWordSigned(100));
        server.Dispose();
    }

    // ── UInt32 / UInt64 / Double ──────────────

    [Fact]
    public void Client_ReadWrite_UInt32()
    {
        int port = TestPortBase + 60;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.Write("D500", unchecked((int)0xDEADBEEF));
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadUInt32("D500");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal(unchecked((uint)0xDEADBEEF), readResult.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_WriteInt_ReadUInt32_Matches()
    {
        int port = TestPortBase + 61;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("D600", unchecked((int)0xCAFEBABE));
            var r = client.ReadUInt32("D600");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(unchecked((uint)0xCAFEBABE), r.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    // ── 多次连续写入 ──────────────────────────

    [Fact]
    public void Client_MultipleWrites_SameAddress()
    {
        int port = TestPortBase + 70;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            for (int i = 0; i < 10; i++)
            {
                var w = client.Write("D100", (short)i);
                Assert.True(w.IsSuccess, $"Write #{i} failed: {w.Message}");
            }

            var read = client.ReadInt16("D100");
            Assert.True(read.IsSuccess);
            Assert.Equal((short)9, read.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    // ── 地址前缀变体 ──────────────────────────

    [Fact]
    public void Client_DM_PrefixVariant()
    {
        int port = TestPortBase + 80;
        var server = new FinsVirtualServer(port);
        server.SetDMWord(100, (short)0x1111);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // DM100 和 D100 应该是同一个地址
            var r1 = client.ReadInt16("DM100");
            var r2 = client.ReadInt16("D100");
            Assert.True(r1.IsSuccess, r1.Message);
            Assert.True(r2.IsSuccess, r2.Message);
            Assert.Equal(r1.Content, r2.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_PlainNumber_DefaultsToDM()
    {
        int port = TestPortBase + 81;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("100", (short)0x2222);
            var r1 = client.ReadInt16("100");
            var r2 = client.ReadInt16("D100");
            Assert.Equal(r1.Content, r2.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    // ══════════════════════════════════════════════
    //  增强测试 — Bool 数组读写
    // ══════════════════════════════════════════════

    [Fact]
    public void Client_ReadBools_DM_Bits()
    {
        int port = TestPortBase + 100;
        var server = new FinsVirtualServer(port);
        // 预设 D100 = 0xFF00 → bit0-7 = 0, bit8-15 = 1
        server.SetDMWord(100, unchecked((ushort)0xFF00));
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadBools("D100.00", 16);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(16, result.Content.Length);

            // bit0-7 应该为 0, bit8-15 应该为 1
            for (int i = 0; i < 8; i++)
                Assert.False(result.Content[i], $"bit{i} should be false");
            for (int i = 8; i < 16; i++)
                Assert.True(result.Content[i], $"bit{i} should be true");

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_WriteBools_DM_Bits()
    {
        int port = TestPortBase + 101;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 写入 8 个位: true, false, true, false, true, false, true, false
            bool[] values = { true, false, true, false, true, false, true, false };
            var writeResult = client.WriteBools("D200.00", values);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            // 读回验证
            var readResult = client.ReadBools("D200.00", 8);
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal(values, readResult.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadBools_CIO_Bits()
    {
        int port = TestPortBase + 102;
        var server = new FinsVirtualServer(port);
        // CIO word 50 = 0x00FF → bit0-7 = 1, bit8-15 = 0
        server.SetCIOWord(50, unchecked((ushort)0x00FF));
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadBools("CIO50.00", 16);
            Assert.True(result.IsSuccess, result.Message);

            for (int i = 0; i < 8; i++)
                Assert.True(result.Content[i], $"bit{i} should be true");
            for (int i = 8; i < 16; i++)
                Assert.False(result.Content[i], $"bit{i} should be false");

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadBools_RequiresBitOffset()
    {
        int port = TestPortBase + 103;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadBools("D100", 8);
            Assert.False(result.IsSuccess);
            Assert.Contains("位偏移", result.Message);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_WriteBools_PreservesOtherBits()
    {
        int port = TestPortBase + 104;
        var server = new FinsVirtualServer(port);
        // D300 = 0xFFFF 所有位为 1
        server.SetDMWord(300, unchecked((ushort)0xFFFF));
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 写入 bit4-7 为 false
            bool[] values = { false, false, false, false };
            var writeResult = client.WriteBools("D300.04", values);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            // 读回全部 16 位
            var readResult = client.ReadBools("D300.00", 16);
            Assert.True(readResult.IsSuccess, readResult.Message);

            // bit0-3 应保持 true, bit4-7 应为 false, bit8-15 应保持 true
            for (int i = 0; i < 4; i++)
                Assert.True(readResult.Content[i], $"bit{i} should be true (preserved)");
            for (int i = 4; i < 8; i++)
                Assert.False(readResult.Content[i], $"bit{i} should be false (written)");
            for (int i = 8; i < 16; i++)
                Assert.True(readResult.Content[i], $"bit{i} should be true (preserved)");

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    // ══════════════════════════════════════════════
    //  增强测试 — PLC 控制命令
    // ══════════════════════════════════════════════

    [Fact]
    public void Client_RemoteRun_Succeeds()
    {
        int port = TestPortBase + 110;
        var server = new FinsVirtualServer(port);
        server.SetPlcRunning(false);
        Assert.False(server.IsPlcRunning);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.Run();
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(server.IsPlcRunning);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_RemoteStop_Succeeds()
    {
        int port = TestPortBase + 111;
        var server = new FinsVirtualServer(port);
        Assert.True(server.IsPlcRunning);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.Stop();
            Assert.True(result.IsSuccess, result.Message);
            Assert.False(server.IsPlcRunning);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_RemoteRunAsync_Succeeds()
    {
        int port = TestPortBase + 112;
        var server = new FinsVirtualServer(port);
        server.SetPlcRunning(false);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.RunAsync().Result;
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(server.IsPlcRunning);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_RemoteStopAsync_Succeeds()
    {
        int port = TestPortBase + 113;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.StopAsync().Result;
            Assert.True(result.IsSuccess, result.Message);
            Assert.False(server.IsPlcRunning);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    // ══════════════════════════════════════════════
    //  增强测试 — CPU 状态/数据/型号
    // ══════════════════════════════════════════════

    [Fact]
    public void Client_ReadCpuStatus_Running()
    {
        int port = TestPortBase + 120;
        var server = new FinsVirtualServer(port);
        server.SetPlcRunning(true);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadCpuStatus();
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((byte)0x00, result.Content); // 0x00 = Running

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadCpuStatus_Stopped()
    {
        int port = TestPortBase + 121;
        var server = new FinsVirtualServer(port);
        server.SetPlcRunning(false);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadCpuStatus();
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((byte)0x01, result.Content); // 0x01 = Stopped

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadCpuUnitData_Succeeds()
    {
        int port = TestPortBase + 122;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadCpuUnitData();
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(result.Content.Length >= 20, $"Expected >= 20 bytes, got {result.Content.Length}");

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadPlcModel_Succeeds()
    {
        int port = TestPortBase + 123;
        var server = new FinsVirtualServer(port);
        server.PlcModel = "CJ2M-CPU33";
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadPlcModel();
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal("CJ2M-CPU33", result.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    // ══════════════════════════════════════════════
    //  增强测试 — CPU 时钟
    // ══════════════════════════════════════════════

    [Fact]
    public void Client_ReadCpuTime_Succeeds()
    {
        int port = TestPortBase + 130;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadCpuTime();
            Assert.True(result.IsSuccess, result.Message);

            // 验证时间合理（应该在当前年份附近）
            var now = DateTime.Now;
            Assert.True(Math.Abs((result.Content - now).TotalMinutes) < 2,
                $"PLC time {result.Content} too far from now {now}");

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_WriteCpuTime_Succeeds()
    {
        int port = TestPortBase + 131;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var time = new DateTime(2025, 6, 15, 10, 30, 45);
            var result = client.WriteCpuTime(time);
            Assert.True(result.IsSuccess, result.Message);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadCpuTimeAsync_Succeeds()
    {
        int port = TestPortBase + 132;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadCpuTimeAsync().Result;
            Assert.True(result.IsSuccess, result.Message);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_WriteCpuTimeAsync_Succeeds()
    {
        int port = TestPortBase + 133;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var time = new DateTime(2025, 12, 25, 8, 0, 0);
            var result = client.WriteCpuTimeAsync(time).Result;
            Assert.True(result.IsSuccess, result.Message);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    // ══════════════════════════════════════════════
    //  增强测试 — Run/Stop → 状态验证
    // ══════════════════════════════════════════════

    [Fact]
    public void Client_RunThenStop_StatusChanges()
    {
        int port = TestPortBase + 140;
        var server = new FinsVirtualServer(port);
        server.Start();

        try
        {
            var client = new FinsTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // PLC 初始运行
            var status1 = client.ReadCpuStatus();
            Assert.True(status1.IsSuccess);
            Assert.Equal((byte)0x00, status1.Content);

            // 停止
            var stopResult = client.Stop();
            Assert.True(stopResult.IsSuccess, stopResult.Message);

            var status2 = client.ReadCpuStatus();
            Assert.True(status2.IsSuccess);
            Assert.Equal((byte)0x01, status2.Content);

            // 重新启动
            var runResult = client.Run();
            Assert.True(runResult.IsSuccess, runResult.Message);

            var status3 = client.ReadCpuStatus();
            Assert.True(status3.IsSuccess);
            Assert.Equal((byte)0x00, status3.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    // ══════════════════════════════════════════════
    //  增强测试 — 虚拟服务器公共 API
    // ══════════════════════════════════════════════

    [Fact]
    public void Server_PlcModel_GetSet()
    {
        var server = new FinsVirtualServer(TestPortBase + 150);
        Assert.Equal("CJ2M-CPU33", server.PlcModel);
        server.PlcModel = "CP1H-X40DR-A";
        Assert.Equal("CP1H-X40DR-A", server.PlcModel);
        server.Dispose();
    }

    [Fact]
    public void Server_SetPlcRunning_Toggle()
    {
        var server = new FinsVirtualServer(TestPortBase + 151);
        Assert.True(server.IsPlcRunning);
        server.SetPlcRunning(false);
        Assert.False(server.IsPlcRunning);
        server.SetPlcRunning(true);
        Assert.True(server.IsPlcRunning);
        server.Dispose();
    }

    [Fact]
    public void Server_GetSetDMBytes()
    {
        var server = new FinsVirtualServer(TestPortBase + 152);
        byte[] data = { 0x12, 0x34, 0x56, 0x78 };
        server.SetDMBytes(100, data);
        var result = server.GetDMBytes(100, 2);
        Assert.Equal(data, result);
        server.Dispose();
    }

    [Fact]
    public void Server_GetSetCIOBytes()
    {
        var server = new FinsVirtualServer(TestPortBase + 153);
        byte[] data = { 0xAA, 0xBB };
        server.SetCIOBytes(50, data);
        var result = server.GetCIOBytes(50, 1);
        Assert.Equal(data, result);
        server.Dispose();
    }
}
