using System;
using System.Collections.Generic;
using System.Text;
using Xunit;
using Nexus.Siemens;

namespace Nexus.Siemens.Tests;

/// <summary>
/// Siemens S7 增强功能测试 — 覆盖 S7String/WString、ReadBools/WriteBools、
/// ReadLarge/WriteLarge、Int64/Double 读写、PLC 控制命令属性、批量读写。
/// </summary>
public class S7EnhancedTests
{
    private const int PortBase = 16800;

    // ── Int64 / UInt64 读写 ─────────────────────

    [Fact]
    public void Write_Read_Int64()
    {
        int port = PortBase + 1;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            long expected = 0x123456789ABCDEF0;
            var w = client.Write("DB1.DBD100", expected);
            Assert.True(w.IsSuccess, w.Message);

            var r = client.ReadInt64("DB1.DBD100");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(expected, r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void Write_Read_UInt64()
    {
        int port = PortBase + 2;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            ulong expected = 0xFEDCBA9876543210;
            var w = client.Write("DB1.DBD200", expected);
            Assert.True(w.IsSuccess, w.Message);

            var r = client.ReadUInt64("DB1.DBD200");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(expected, r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    // ── Double 读写（8字节）─────────────────────

    [Fact]
    public void Write_Read_Double()
    {
        int port = PortBase + 3;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            double expected = 3.141592653589793;
            var w = client.Write("DB1.DBD300", expected);
            Assert.True(w.IsSuccess, w.Message);

            var r = client.ReadDouble("DB1.DBD300");
            Assert.True(r.IsSuccess, r.Message);
            // IEEE 754 精确比较
            Assert.Equal(expected, r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    // ── S7 String 读写 ──────────────────────────

    [Fact]
    public void Write_Read_S7String()
    {
        int port = PortBase + 4;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            string expected = "Hello S7";
            var w = client.WriteS7String("DB1.DBD400", expected);
            Assert.True(w.IsSuccess, w.Message);

            var r = client.ReadS7String("DB1.DBD400");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(expected, r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void ReadS7String_Empty()
    {
        int port = PortBase + 5;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        // 预设空字符串: maxLen=254, actualLen=0
        server.SetDBBytes(1, 500, new byte[] { 0xFE, 0x00 });
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadS7String("DB1.DBD500");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(string.Empty, r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void WriteS7String_Truncates_WhenTooLong()
    {
        int port = PortBase + 6;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // maxLength=10, 写入超过 10-2=8 字节的字符串
            string longStr = "ABCDEFGHIJ"; // 10 chars
            var w = client.WriteS7String("DB1.DBD600", longStr, maxLength: 10);
            Assert.True(w.IsSuccess, w.Message);

            var r = client.ReadS7String("DB1.DBD600");
            Assert.True(r.IsSuccess, r.Message);
            // 应截断到 8 字节（maxLength - 2字节头）
            Assert.Equal("ABCDEFGH", r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    // ── WString 读写 ────────────────────────────

    [Fact]
    public void Write_Read_WString()
    {
        int port = PortBase + 7;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            string expected = "Test世界";
            var w = client.WriteWString("DB1.DBD700", expected);
            Assert.True(w.IsSuccess, w.Message);

            var r = client.ReadWString("DB1.DBD700");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(expected, r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    // ── ReadBools / WriteBools ──────────────────

    [Fact]
    public void ReadBools_FromByte()
    {
        int port = PortBase + 8;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        // 预设 MB0 = 0b10110100
        server.SetMarkerByte(0, 0xB4);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 读取 M0.0 ~ M0.7
            var r = client.ReadBools("M0.0", 8);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(8, r.Content.Length);

            // 0xB4 = 10110100 → bit0=0, bit1=0, bit2=1, bit3=0, bit4=1, bit5=1, bit6=0, bit7=1
            Assert.False(r.Content[0]);  // bit0
            Assert.False(r.Content[1]);  // bit1
            Assert.True(r.Content[2]);   // bit2
            Assert.False(r.Content[3]);  // bit3
            Assert.True(r.Content[4]);   // bit4
            Assert.True(r.Content[5]);   // bit5
            Assert.False(r.Content[6]);  // bit6
            Assert.True(r.Content[7]);   // bit7

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void WriteBools_ToByte()
    {
        int port = PortBase + 9;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 先写一个初始值
            client.Write("MW0", unchecked((short)0xFF00));
            // MW0 byte0 = 0xFF, byte1 = 0x00

            // 写入 M0.3 ~ M0.5 (覆盖 MW0 byte0 中的位)
            var bools = new bool[] { true, false, true };
            var w = client.WriteBools("M0.3", bools);
            Assert.True(w.IsSuccess, w.Message);

            // 读回验证: M0 = 0b11111011 = 0xFB? 不，需要先看初始值
            // 初始 M0 = 0xFF = 11111111
            // 写 M0.3=true (already 1), M0.4=false, M0.5=true (already 1)
            // 结果 M0 = 11101111? 不对。WriteBools 的 read-modify-write:
            // 读 M0 = 0xFF, 设置 bit3=true → 已是1, bit4=false → 清除, bit5=true → 已是1
            // 结果 = 11101111 = 0xEF? 不，0xFF & ~(1<<4) | (1<<3) | (1<<5)
            // = 0xFF & 0xEF | 0x08 | 0x20 = 0xEF | 0x28 = 0xFF?
            // Wait: initial M0=0xFF, bit3=1 already, bit4 set to false → clear bit4, bit5=1 already
            // 0xFF & ~(1<<4) = 0xFF & 0xEF = 0xEF
            // Then | (1<<3) → 0xEF | 0x08 = 0xEF (already set)
            // Then | (1<<5) → 0xEF | 0x20 = 0xEF (already set)
            // Hmm, 0xEF is 11101111. Bit4 is clear.
            // Wait, that's right! 0xFF initial, clear bit4 → 0xEF

            var r = client.ReadBools("M0.3", 3);
            Assert.True(r.IsSuccess);
            Assert.True(r.Content[0]);   // M0.3 = true
            Assert.False(r.Content[1]);  // M0.4 = false
            Assert.True(r.Content[2]);   // M0.5 = true

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void ReadBools_Empty()
    {
        int port = PortBase + 10;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadBools("M0.0", 0);
            Assert.True(r.IsSuccess);
            Assert.Empty(r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void WriteBools_Empty()
    {
        int port = PortBase + 11;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var w = client.WriteBools("M0.0", new bool[0]);
            Assert.True(w.IsSuccess);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    // ── ReadLarge / WriteLarge ──────────────────

    [Fact]
    public void ReadLarge_SmallData_NoSplit()
    {
        int port = PortBase + 12;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.SetDBDWord(1, 0, 0x12345678);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadLarge("DB1.DBD0", 4);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(4, r.Content.Length);
            Assert.Equal(0x12, r.Content[0]);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void WriteLarge_ReadLarge_Roundtrip()
    {
        int port = PortBase + 13;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 生成 500 字节数据（超过默认 PDU 240 → 会自动分割）
            byte[] expected = new byte[500];
            for (int i = 0; i < expected.Length; i++) expected[i] = (byte)(i & 0xFF);

            var w = client.WriteLarge("DB1.DBD1000", expected);
            Assert.True(w.IsSuccess, w.Message);

            var r = client.ReadLarge("DB1.DBD1000", 500);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(500, r.Content.Length);

            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], r.Content[i]);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    // ── ReadBytes / Write Bytes ─────────────────

    [Fact]
    public void Write_Read_Bytes()
    {
        int port = PortBase + 14;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            byte[] expected = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
            var w = client.Write("DB1.DBD50", expected);
            Assert.True(w.IsSuccess, w.Message);

            var r = client.ReadBytes("DB1.DBD50", 5);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(expected, r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    // ── 批量读写 ────────────────────────────────

    [Fact]
    public void BatchRead_MultipleAddresses()
    {
        int port = PortBase + 15;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.SetDBWord(1, 0, (short)0x1111);
        server.SetDBWord(1, 2, (short)0x2222);
        server.SetDBWord(1, 10, (short)0x3333);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.BatchRead(new[] { "DB1.DBW0", "DB1.DBW2", "DB1.DBW10" });
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(3, r.Content.Count);
            Assert.Equal((short)0x1111, r.Content["DB1.DBW0"]);
            Assert.Equal((short)0x2222, r.Content["DB1.DBW2"]);
            Assert.Equal((short)0x3333, r.Content["DB1.DBW10"]);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void BatchWrite_MultipleAddresses()
    {
        int port = PortBase + 16;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var items = new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("DB1.DBW20", unchecked((short)0xAAAA)),
                new KeyValuePair<string, object>("DB1.DBW22", unchecked((short)0xBBBB)),
            };

            var w = client.BatchWrite(items);
            Assert.True(w.IsSuccess, w.Message);

            // 验证
            Assert.Equal(unchecked((short)0xAAAA), client.ReadInt16("DB1.DBW20").Content);
            Assert.Equal(unchecked((short)0xBBBB), client.ReadInt16("DB1.DBW22").Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void RandomRead_MultipleAddresses()
    {
        int port = PortBase + 17;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.SetDBWord(1, 100, (short)0x1234);
        server.SetDBWord(1, 200, (short)0x5678);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.RandomRead(new[] { "DB1.DBW100", "DB1.DBW200" });
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(2, r.Content.Count);
            Assert.Equal(2, r.Content["DB1.DBW100"].Length);
            Assert.Equal(2, r.Content["DB1.DBW200"].Length);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    // ── 连接类型与 TSAP 属性 ────────────────────

    [Fact]
    public void ConnectionType_Default_IsS7Basic()
    {
        var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1");
        Assert.Equal((byte)0x03, client.ConnectionType);
        client.Dispose();
    }

    [Fact]
    public void LocalTSAP_DestTSAP_CanBeSet()
    {
        var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1");
        client.LocalTSAP = 0x0300;
        client.DestTSAP = 0x0301;
        Assert.Equal(0x0300, client.LocalTSAP);
        Assert.Equal(0x0301, client.DestTSAP);
        client.Dispose();
    }

    // ── MaxPduSize 协商 ────────────────────────

    [Fact]
    public void Connect_Negotiates_PduSize()
    {
        int port = PortBase + 18;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.PduSize = 960;
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();

            // 默认 PDU
            Assert.Equal((ushort)240, client.MaxPduSize);

            var conn = client.Connect();
            Assert.True(conn.IsSuccess, conn.Message);

            // 连接后协商的 PDU
            Assert.Equal((ushort)960, client.MaxPduSize);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    // ── Bool 读写 ──────────────────────────────

    [Fact]
    public void Write_Read_Bool()
    {
        int port = PortBase + 19;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var w = client.Write("M0.0", true);
            Assert.True(w.IsSuccess, w.Message);

            var r = client.ReadBool("M0.0");
            Assert.True(r.IsSuccess, r.Message);
            Assert.True(r.Content);

            var w2 = client.Write("M0.0", false);
            Assert.True(w2.IsSuccess);

            var r2 = client.ReadBool("M0.0");
            Assert.True(r2.IsSuccess);
            Assert.False(r2.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    // ── String 写读（普通编码）──────────────────

    [Fact]
    public void WriteStringEncoded_ReadStringEncoded()
    {
        int port = PortBase + 20;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // UTF-8 编码写读
            client.StringEncoding = System.Text.Encoding.UTF8;
            var w = client.WriteStringEncoded("DB1.DBD800", "测试ABC");
            Assert.True(w.IsSuccess, w.Message);

            var r = client.ReadStringEncoded("DB1.DBD800", 20);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal("测试ABC", r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    // ── 异步方法（基础验证）─────────────────────

    [Fact]
    public async System.Threading.Tasks.Task ReadInt16Async_Works()
    {
        int port = PortBase + 21;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.SetDBWord(1, 0, (short)0x4242);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            var conn = await client.ConnectAsync();
            Assert.True(conn.IsSuccess, conn.Message);

            var r = await client.ReadInt16Async("DB1.DBW0");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((short)0x4242, r.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public async System.Threading.Tasks.Task BatchReadAsync_Works()
    {
        int port = PortBase + 22;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.SetDBWord(1, 0, (short)100);
        server.SetDBWord(1, 2, (short)200);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True((await client.ConnectAsync()).IsSuccess);

            var r = await client.BatchReadAsync(new[] { "DB1.DBW0", "DB1.DBW2" });
            Assert.True(r.IsSuccess);
            Assert.Equal(2, r.Content.Count);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public async System.Threading.Tasks.Task ReadLargeAsync_Works()
    {
        int port = PortBase + 23;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True((await client.ConnectAsync()).IsSuccess);

            byte[] data = new byte[100];
            for (int i = 0; i < 100; i++) data[i] = (byte)i;
            await client.WriteLargeAsync("DB1.DBD2000", data);

            var r = await client.ReadLargeAsync("DB1.DBD2000", 100);
            Assert.True(r.IsSuccess);
            Assert.Equal(100, r.Content.Length);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    // ── 多 DB 块批量读写 ────────────────────────

    [Fact]
    public void BatchRead_DifferentDBs()
    {
        int port = PortBase + 24;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.SetDBWord(1, 0, unchecked((short)0xAAAA));
        server.SetDBWord(2, 0, unchecked((short)0xBBBB));
        server.SetDBWord(3, 0, unchecked((short)0xCCCC));
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.BatchRead(new[] { "DB1.DBW0", "DB2.DBW0", "DB3.DBW0" });
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(3, r.Content.Count);
            Assert.Equal(unchecked((short)0xAAAA), r.Content["DB1.DBW0"]);
            Assert.Equal(unchecked((short)0xBBBB), r.Content["DB2.DBW0"]);
            Assert.Equal(unchecked((short)0xCCCC), r.Content["DB3.DBW0"]);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    // ── S7-200Smart 型号默认值 ──────────────────

    [Fact]
    public void S7_200Smart_DefaultProperties()
    {
        var client = new SiemensS7Client(SiemensPLCS.S7_200Smart, "127.0.0.1");
        Assert.Equal(SiemensPLCS.S7_200Smart, client.PLCType);
        client.Dispose();
    }

    [Fact]
    public void S7_200_DefaultProperties()
    {
        var client = new SiemensS7Client(SiemensPLCS.S7_200, "127.0.0.1");
        Assert.Equal(SiemensPLCS.S7_200, client.PLCType);
        client.Dispose();
    }

    // ── UInt32 读写 ────────────────────────────

    [Fact]
    public void Write_Read_UInt32()
    {
        int port = PortBase + 25;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            uint expected = 0x87654321;
            var w = client.Write("DB1.DBD400", expected);
            Assert.True(w.IsSuccess, w.Message);

            var r = client.ReadUInt32("DB1.DBD400");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(expected, r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }
}
