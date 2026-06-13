using System;
using System.Threading.Tasks;
using Xunit;
using Nexus.Siemens;

namespace Nexus.Siemens.Tests;

/// <summary>
/// S7 定时器/计数器测试 — 验证 ReadTimer/WriteTimer/ReadCounter/WriteCounter
/// 通过 SiemensS7VirtualPlc 端到端验证。
/// </summary>
public class S7TimerCounterTests
{
    private const int PortBase = 16900;

    // ── S7Area 枚举常量验证 ──────────────────────

    [Fact]
    public void S7Area_Timer_Is0x1D()
    {
        Assert.Equal(0x1D, (byte)S7Area.TM);
    }

    [Fact]
    public void S7Area_Counter_Is0x1C()
    {
        Assert.Equal(0x1C, (byte)S7Area.CT);
    }

    // ── 虚拟 PLC Timer/Counter 存储验证 ──────────

    [Fact]
    public void VirtualPlc_SetGetTimerByte_Roundtrip()
    {
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, PortBase);

        server.SetTimerByte(0, 0xAB);
        server.SetTimerByte(1, 0xCD);

        Assert.Equal(0xAB, server.GetTimerByte(0));
        Assert.Equal(0xCD, server.GetTimerByte(1));

        server.Dispose();
    }

    [Fact]
    public void VirtualPlc_SetGetCounterByte_Roundtrip()
    {
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, PortBase);

        server.SetCounterByte(0, 0x12);
        server.SetCounterByte(1, 0x34);

        Assert.Equal(0x12, server.GetCounterByte(0));
        Assert.Equal(0x34, server.GetCounterByte(1));

        server.Dispose();
    }

    [Fact]
    public void VirtualPlc_Timer_MultipleOffsets()
    {
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, PortBase);

        server.SetTimerByte(0, 0x00); server.SetTimerByte(1, 0x0A);
        server.SetTimerByte(2, 0x00); server.SetTimerByte(3, 0x14);
        server.SetTimerByte(4, 0x00); server.SetTimerByte(5, 0x64);

        Assert.Equal(0x000A, (server.GetTimerByte(0) << 8) | server.GetTimerByte(1));
        Assert.Equal(0x0014, (server.GetTimerByte(2) << 8) | server.GetTimerByte(3));
        Assert.Equal(0x0064, (server.GetTimerByte(4) << 8) | server.GetTimerByte(5));

        server.Dispose();
    }

    [Fact]
    public void VirtualPlc_Counter_MultipleOffsets()
    {
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, PortBase);

        server.SetCounterByte(0, 0x01); server.SetCounterByte(1, 0x00);
        server.SetCounterByte(2, 0x02); server.SetCounterByte(3, 0x55);

        Assert.Equal(0x0100, (server.GetCounterByte(0) << 8) | server.GetCounterByte(1));
        Assert.Equal(0x0255, (server.GetCounterByte(2) << 8) | server.GetCounterByte(3));

        server.Dispose();
    }

    // ── ReadTimer / ReadCounter 通过 S7 协议 ─────

    [Fact]
    public void ReadTimer_ReadsFromVirtualPlc()
    {
        int port = PortBase + 1;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        // 预设定时器 T0: 2字节 BCD 格式 0x000A
        server.SetTimerByte(0, 0x00);
        server.SetTimerByte(1, 0x0A);
        server.Start();

        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadTimer(0);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x000A, result.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void ReadCounter_ReadsFromVirtualPlc()
    {
        int port = PortBase + 2;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        // 预设计数器 C0: 2字节 BCD 格式 0x0255
        server.SetCounterByte(0, 0x02);
        server.SetCounterByte(1, 0x55);
        server.Start();

        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadCounter(0);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x0255, result.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    // ── WriteTimer / WriteCounter 通过 S7 协议 ────

    [Fact]
    public void WriteTimer_WritesAndReadsBack()
    {
        int port = PortBase + 3;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();

        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.WriteTimer(0, 0x0064);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadTimer(0);
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal(0x0064, readResult.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void WriteCounter_WritesAndReadsBack()
    {
        int port = PortBase + 4;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();

        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.WriteCounter(0, 0x0100);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadCounter(0);
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal(0x0100, readResult.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    // ── 异步测试 ─────────────────────────────────

    [Fact]
    public async Task ReadTimer_Async_Works()
    {
        int port = PortBase + 5;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.SetTimerByte(0, 0x00);
        server.SetTimerByte(1, 0x32);
        server.Start();

        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            var conn = await client.ConnectAsync();
            Assert.True(conn.IsSuccess, conn.Message);

            var result = await client.ReadTimerAsync(0);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x0032, result.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public async Task ReadCounter_Async_Works()
    {
        int port = PortBase + 6;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.SetCounterByte(0, 0x05);
        server.SetCounterByte(1, 0x00);
        server.Start();

        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            var conn = await client.ConnectAsync();
            Assert.True(conn.IsSuccess, conn.Message);

            var result = await client.ReadCounterAsync(0);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x0500, result.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public async Task WriteTimer_Async_ReadBack()
    {
        int port = PortBase + 7;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();

        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            var conn = await client.ConnectAsync();
            Assert.True(conn.IsSuccess, conn.Message);

            var w = await client.WriteTimerAsync(1, 0x0100);
            Assert.True(w.IsSuccess, w.Message);

            var r = await client.ReadTimerAsync(1);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(0x0100, r.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public async Task WriteCounter_Async_ReadBack()
    {
        int port = PortBase + 8;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();

        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            var conn = await client.ConnectAsync();
            Assert.True(conn.IsSuccess, conn.Message);

            var w = await client.WriteCounterAsync(1, 0x0200);
            Assert.True(w.IsSuccess, w.Message);

            var r = await client.ReadCounterAsync(1);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(0x0200, r.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }
}
