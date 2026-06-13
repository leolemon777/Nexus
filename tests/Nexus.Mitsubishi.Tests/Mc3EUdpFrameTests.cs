using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;
using Nexus.Mitsubishi;

namespace Nexus.Mitsubishi.Tests;

/// <summary>
/// MC3E UDP Binary/ASCII 帧测试 — 多数据类型读写验证。
/// 覆盖 Binary 和 ASCII 两种编码模式下的增强功能。
/// </summary>
public sealed class Mc3EUdpFrameTests
{
    // ═══════════════════════════════════════════
    //  Binary 模式 — 基础读写
    // ═══════════════════════════════════════════

    [Fact]
    public void Binary_ReadInt16_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetDRegister(0, 0x0100);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var read = client.ReadInt16("D0");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((short)0x0100, read.Content);
    }

    [Fact]
    public void Binary_WriteInt16_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var write = client.Write("D5", (short)100);
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal((ushort)100, server.GetDRegister(5));
    }

    [Fact]
    public void Binary_ReadInt32_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetDRegister(10, 0xAAAA);
        server.SetDRegister(11, 0xBBBB);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var read = client.ReadInt32("D10");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(unchecked((int)0xAAAABBBB), read.Content);
    }

    [Fact]
    public void Binary_WriteInt32_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var write = client.Write("D20", unchecked((int)0x11223344));
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal(0x1122, server.GetDRegister(20));
        Assert.Equal(0x3344, server.GetDRegister(21));
    }

    [Fact]
    public void Binary_ReadFloat_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetDRegister(50, 0xBF80);
        server.SetDRegister(51, 0x0000);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var read = client.ReadFloat("D50");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(-1.0f, read.Content);
    }

    [Fact]
    public void Binary_WriteFloat_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var write = client.Write("D60", 1.0f);
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal(0x3F80, server.GetDRegister(60));
        Assert.Equal(0x0000, server.GetDRegister(61));
    }

    [Fact]
    public void Binary_ReadString_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetDRegister(100, 0x4865); // "He"
        server.SetDRegister(101, 0x6C6C); // "ll"
        server.SetDRegister(102, 0x6F00); // "o\0"
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var read = client.ReadString("D100", 4);
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal("Hell", read.Content);
    }

    [Fact]
    public void Binary_SequentialWriteRead_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);

        for (int i = 0; i < 5; i++)
        {
            var write = client.Write("D" + i, (short)(i * 100));
            Assert.True(write.IsSuccess, $"Write D{i} failed: {write.Message}");
        }

        for (int i = 0; i < 5; i++)
        {
            var read = client.ReadInt16("D" + i);
            Assert.True(read.IsSuccess, $"Read D{i} failed: {read.Message}");
            Assert.Equal((short)(i * 100), read.Content);
        }
    }

    // ═══════════════════════════════════════════
    //  Binary 模式 — 位读写
    // ═══════════════════════════════════════════

    [Fact]
    public void Binary_ReadBools_M0_ReturnsCorrectBits()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetMRelay(0, true);
        server.SetMRelay(1, false);
        server.SetMRelay(2, true);
        server.SetMRelay(3, true);
        server.SetMRelay(4, false);
        server.SetMRelay(5, true);
        server.SetMRelay(6, false);
        server.SetMRelay(7, false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var r = client.ReadBools("M0", 8);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(8, r.Content.Length);
        Assert.True(r.Content[0]);
        Assert.False(r.Content[1]);
        Assert.True(r.Content[2]);
        Assert.True(r.Content[3]);
        Assert.False(r.Content[4]);
        Assert.True(r.Content[5]);
        Assert.False(r.Content[6]);
        Assert.False(r.Content[7]);
    }

    [Fact]
    public void Binary_WriteBools_M0_WritesCorrectly()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var values = new bool[] { true, false, true, true, false, true, false, false };
        var w = client.WriteBools("M0", values);
        Assert.True(w.IsSuccess, w.Message);

        var r = client.ReadBools("M0", 8);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(values, r.Content);
    }

    [Fact]
    public void Binary_ReadBools_LargeCount_WorksCorrectly()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        for (int i = 0; i < 64; i++)
            server.SetMRelay((ushort)i, i % 2 == 0);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var r = client.ReadBools("M0", 64);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(64, r.Content.Length);
        for (int i = 0; i < 64; i++)
            Assert.Equal(i % 2 == 0, r.Content[i]);
    }

    [Fact]
    public void Binary_ReadBitsBatch_Direct_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetMRelay(100, true);
        server.SetMRelay(101, false);
        server.SetMRelay(102, true);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var (subLabel, addr) = Mc3EAddressParser.Parse("M100");
        var r = client.ReadBitsBatch(subLabel, addr, 3);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(3, r.Content.Length);
        Assert.Equal(0x01, r.Content[0]);
        Assert.Equal(0x00, r.Content[1]);
        Assert.Equal(0x01, r.Content[2]);
    }

    // ═══════════════════════════════════════════
    //  Binary 模式 — PLC 控制命令
    // ═══════════════════════════════════════════

    [Fact]
    public void Binary_RemoteRun_Succeeds()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var r = client.RemoteRun();
        Assert.True(r.IsSuccess, r.Message);
        Assert.True(server.IsPlcRunning);
    }

    [Fact]
    public void Binary_RemoteStop_Succeeds()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var r = client.RemoteStop();
        Assert.True(r.IsSuccess, r.Message);
        Assert.False(server.IsPlcRunning);
    }

    [Fact]
    public void Binary_RemoteReset_Succeeds()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var stopR = client.RemoteStop();
        Assert.True(stopR.IsSuccess);
        Assert.False(server.IsPlcRunning);

        var r = client.RemoteReset();
        Assert.True(r.IsSuccess, r.Message);
        Assert.True(server.IsPlcRunning);
    }

    [Fact]
    public void Binary_ReadPlcType_ReturnsModelName()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetPlcTypeName("Q02HCPU");
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var r = client.ReadPlcType();
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal("Q02HCPU", r.Content);
    }

    [Fact]
    public void Binary_ReadPlcType_CustomName_ReturnsCorrectly()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetPlcTypeName("FX5U-32M");
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var r = client.ReadPlcType();
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal("FX5U-32M", r.Content);
    }

    [Fact]
    public void Binary_ErrorStateReset_Succeeds()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var r = client.ErrorStateReset();
        Assert.True(r.IsSuccess, r.Message);
    }

    // ═══════════════════════════════════════════
    //  Binary 模式 — 随机读写
    // ═══════════════════════════════════════════

    [Fact]
    public void Binary_ReadWordsRandom_MultipleAddresses_ReturnsCorrectData()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetDRegister(0, 1000);
        server.SetDRegister(10, 2000);
        server.SetDRegister(20, 3000);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var r = client.ReadRandomMultiLength(new[]
        {
            ("D0", (ushort)1),
            ("D10", (ushort)1),
            ("D20", (ushort)1),
        });
        Assert.True(r.IsSuccess, r.Message);

        ushort d0 = (ushort)((r.Content["D0"][0] << 8) | r.Content["D0"][1]);
        Assert.Equal((ushort)1000, d0);

        ushort d10 = (ushort)((r.Content["D10"][0] << 8) | r.Content["D10"][1]);
        Assert.Equal((ushort)2000, d10);

        ushort d20 = (ushort)((r.Content["D20"][0] << 8) | r.Content["D20"][1]);
        Assert.Equal((ushort)3000, d20);
    }

    [Fact]
    public void Binary_WriteWordsRandom_MultipleAddresses_WritesCorrectly()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var (sl0, a0) = Mc3EAddressParser.Parse("D0");
        var (sl1, a1) = Mc3EAddressParser.Parse("D10");
        var (sl2, a2) = Mc3EAddressParser.Parse("D20");

        var w = client.WriteWordsRandom(new[]
        {
            (sl0, a0, (ushort)1111),
            (sl1, a1, (ushort)2222),
            (sl2, a2, (ushort)3333),
        });
        Assert.True(w.IsSuccess, w.Message);

        Assert.Equal((ushort)1111, server.GetDRegister(0));
        Assert.Equal((ushort)2222, server.GetDRegister(10));
        Assert.Equal((ushort)3333, server.GetDRegister(20));
    }

    [Fact]
    public void Binary_ReadWordsRandomMultiLength_DifferentLengths_ReturnsCorrectData()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetDRegister(0, 1000);
        server.SetDRegister(1, 2000);
        server.SetDRegister(2, 3000);
        server.SetDRegister(10, 500);
        server.SetDRegister(11, 600);
        server.SetDRegister(20, 9999);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var r = client.ReadRandomMultiLength(new[]
        {
            ("D0", (ushort)3),
            ("D10", (ushort)2),
            ("D20", (ushort)1),
        });
        Assert.True(r.IsSuccess, r.Message);

        // D0-D2: 6 bytes
        Assert.Equal(6, r.Content["D0"].Length);
        ushort d0 = (ushort)((r.Content["D0"][0] << 8) | r.Content["D0"][1]);
        ushort d1 = (ushort)((r.Content["D0"][2] << 8) | r.Content["D0"][3]);
        ushort d2 = (ushort)((r.Content["D0"][4] << 8) | r.Content["D0"][5]);
        Assert.Equal((ushort)1000, d0);
        Assert.Equal((ushort)2000, d1);
        Assert.Equal((ushort)3000, d2);

        // D10-D11: 4 bytes
        Assert.Equal(4, r.Content["D10"].Length);
        ushort d10 = (ushort)((r.Content["D10"][0] << 8) | r.Content["D10"][1]);
        ushort d11 = (ushort)((r.Content["D10"][2] << 8) | r.Content["D10"][3]);
        Assert.Equal((ushort)500, d10);
        Assert.Equal((ushort)600, d11);

        // D20: 2 bytes
        Assert.Equal(2, r.Content["D20"].Length);
        ushort d20 = (ushort)((r.Content["D20"][0] << 8) | r.Content["D20"][1]);
        Assert.Equal((ushort)9999, d20);
    }

    // ═══════════════════════════════════════════
    //  Binary 模式 — 大数据自动分片
    // ═══════════════════════════════════════════

    [Fact]
    public void Binary_ReadLarge_OverLimit_AutoSplits()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        for (int i = 0; i < 10; i++)
            server.SetDRegister((ushort)i, (ushort)(1000 + i));
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            MaxReadWordCount = 3
        };
        var r = client.ReadLarge("D0", 10);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(20, r.Content.Length);

        for (int i = 0; i < 10; i++)
        {
            ushort val = (ushort)((r.Content[i * 2] << 8) | r.Content[i * 2 + 1]);
            Assert.Equal((ushort)(1000 + i), val);
        }
    }

    [Fact]
    public void Binary_WriteLarge_OverLimit_AutoSplits()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        byte[] writeData = new byte[20];
        for (int i = 0; i < 10; i++)
        {
            ushort val = (ushort)(5000 + i);
            writeData[i * 2] = (byte)(val >> 8);
            writeData[i * 2 + 1] = (byte)(val & 0xFF);
        }

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            MaxWriteWordCount = 3
        };
        var r = client.WriteLarge("D0", writeData);
        Assert.True(r.IsSuccess, r.Message);

        for (int i = 0; i < 10; i++)
        {
            ushort expected = (ushort)(5000 + i);
            Assert.Equal(expected, server.GetDRegister((ushort)i));
        }
    }

    [Fact]
    public void Binary_ReadLarge_UnderLimit_SingleRead()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetDRegister(0, 1111);
        server.SetDRegister(1, 2222);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            MaxReadWordCount = 960
        };
        var r = client.ReadLarge("D0", 2);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(4, r.Content.Length);
        ushort v0 = (ushort)((r.Content[0] << 8) | r.Content[1]);
        ushort v1 = (ushort)((r.Content[2] << 8) | r.Content[3]);
        Assert.Equal((ushort)1111, v0);
        Assert.Equal((ushort)2222, v1);
    }

    // ═══════════════════════════════════════════
    //  Binary 模式 — 字符串编码
    // ═══════════════════════════════════════════

    [Fact]
    public void Binary_ReadStringEncoded_Ascii_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetDRegister(300, 0x4865); // "He"
        server.SetDRegister(301, 0x6C6C); // "ll"
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        client.StringEncoding = Encoding.ASCII;
        var r = client.ReadStringEncoded("D300", 4);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal("Hell", r.Content);
    }

    [Fact]
    public void Binary_WriteStringEncoded_Ascii_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        client.StringEncoding = Encoding.ASCII;
        var w = client.WriteStringEncoded("D300", "Hi");
        Assert.True(w.IsSuccess, w.Message);

        Assert.Equal(0x4869, server.GetDRegister(300)); // "Hi"
    }

    // ═══════════════════════════════════════════
    //  Binary 模式 — BatchRead 多区域分组
    // ═══════════════════════════════════════════

    [Fact]
    public void Binary_BatchRead_MultipleAreas_GroupsCorrectly()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetDRegister(0, 100);
        server.SetDRegister(1, 200);
        server.SetDRegister(10, 300);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var r = client.BatchRead(new[] { "D0", "D1", "D10" });
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal((ushort)100, r.Content["D0"]);
        Assert.Equal((ushort)200, r.Content["D1"]);
        Assert.Equal((ushort)300, r.Content["D10"]);
    }

    // ═══════════════════════════════════════════
    //  Binary 模式 — 综合场景
    // ═══════════════════════════════════════════

    [Fact]
    public void Binary_FullWorkflow_WriteReadBools_ThenReadType_ThenControl()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetPlcTypeName("Q03UDVCPU");
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);

        // 1. 写入 M 位
        var writeR = client.WriteBools("M0", new bool[] { true, false, true, true });
        Assert.True(writeR.IsSuccess);

        // 2. 读回 M 位
        var readR = client.ReadBools("M0", 4);
        Assert.True(readR.IsSuccess);
        Assert.Equal(new bool[] { true, false, true, true }, readR.Content);

        // 3. 读写 D 寄存器
        var dWrite = client.Write("D100", (short)12345);
        Assert.True(dWrite.IsSuccess);
        var dRead = client.ReadInt16("D100");
        Assert.True(dRead.IsSuccess);
        Assert.Equal((short)12345, dRead.Content);

        // 4. 读 PLC 型号
        var typeR = client.ReadPlcType();
        Assert.True(typeR.IsSuccess);
        Assert.Equal("Q03UDVCPU", typeR.Content);

        // 5. 停止 PLC
        var stopR = client.RemoteStop();
        Assert.True(stopR.IsSuccess);
        Assert.False(server.IsPlcRunning);

        // 6. 启动 PLC
        var runR = client.RemoteRun();
        Assert.True(runR.IsSuccess);
        Assert.True(server.IsPlcRunning);

        // 7. 错误状态复位
        var errR = client.ErrorStateReset();
        Assert.True(errR.IsSuccess);
    }

    // ═══════════════════════════════════════════
    //  ASCII 模式 — 基础读写
    // ═══════════════════════════════════════════

    [Fact]
    public void Ascii_ReadInt16_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.SetDRegister(0, 0x5678);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var read = client.ReadInt16("D0");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(unchecked((short)0x5678), read.Content);
    }

    [Fact]
    public void Ascii_WriteInt16_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var write = client.Write("D10", (short)999);
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal((ushort)999, server.GetDRegister(10));
    }

    [Fact]
    public void Ascii_ReadInt32_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.SetDRegister(20, 0xDEAD);
        server.SetDRegister(21, 0xBEEF);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var read = client.ReadInt32("D20");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(unchecked((int)0xDEADBEEF), read.Content);
    }

    [Fact]
    public void Ascii_WriteInt32_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var write = client.Write("D30", unchecked((int)0xCAFEBABE));
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal(0xCAFE, server.GetDRegister(30));
        Assert.Equal(0xBABE, server.GetDRegister(31));
    }

    [Fact]
    public void Ascii_ReadFloat_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.SetDRegister(40, 0x3F00);
        server.SetDRegister(41, 0x0000);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var read = client.ReadFloat("D40");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(0.5f, read.Content);
    }

    // ═══════════════════════════════════════════
    //  ASCII 模式 — 位读写
    // ═══════════════════════════════════════════

    [Fact]
    public void Ascii_ReadBools_M0_ReturnsCorrectBits()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.SetMRelay(0, true);
        server.SetMRelay(1, false);
        server.SetMRelay(2, true);
        server.SetMRelay(3, false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var r = client.ReadBools("M0", 4);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(4, r.Content.Length);
        Assert.True(r.Content[0]);
        Assert.False(r.Content[1]);
        Assert.True(r.Content[2]);
        Assert.False(r.Content[3]);
    }

    [Fact]
    public void Ascii_WriteBools_M0_WritesCorrectly()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var values = new bool[] { true, false, true, true, false, true, false, false };
        var w = client.WriteBools("M0", values);
        Assert.True(w.IsSuccess, w.Message);

        var r = client.ReadBools("M0", 8);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(values, r.Content);
    }

    // ═══════════════════════════════════════════
    //  ASCII 模式 — PLC 控制命令
    // ═══════════════════════════════════════════

    [Fact]
    public void Ascii_RemoteRun_Succeeds()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var r = client.RemoteRun();
        Assert.True(r.IsSuccess, r.Message);
        Assert.True(server.IsPlcRunning);
    }

    [Fact]
    public void Ascii_RemoteStop_Succeeds()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var r = client.RemoteStop();
        Assert.True(r.IsSuccess, r.Message);
        Assert.False(server.IsPlcRunning);
    }

    [Fact]
    public void Ascii_RemoteReset_Succeeds()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var stopR = client.RemoteStop();
        Assert.True(stopR.IsSuccess);
        Assert.False(server.IsPlcRunning);

        var r = client.RemoteReset();
        Assert.True(r.IsSuccess, r.Message);
        Assert.True(server.IsPlcRunning);
    }

    [Fact]
    public void Ascii_ReadPlcType_ReturnsModelName()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.SetPlcTypeName("iQ-R08PCPU");
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var r = client.ReadPlcType();
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal("iQ-R08PCPU", r.Content);
    }

    [Fact]
    public void Ascii_ErrorStateReset_Succeeds()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var r = client.ErrorStateReset();
        Assert.True(r.IsSuccess, r.Message);
    }

    // ═══════════════════════════════════════════
    //  ASCII 模式 — 随机读写
    // ═══════════════════════════════════════════

    [Fact]
    public void Ascii_ReadWordsRandomMultiLength_DifferentLengths_ReturnsCorrectData()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.SetDRegister(0, 1000);
        server.SetDRegister(1, 2000);
        server.SetDRegister(2, 3000);
        server.SetDRegister(10, 500);
        server.SetDRegister(11, 600);
        server.SetDRegister(20, 9999);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var r = client.ReadRandomMultiLength(new[]
        {
            ("D0", (ushort)3),
            ("D10", (ushort)2),
            ("D20", (ushort)1),
        });
        Assert.True(r.IsSuccess, r.Message);

        // D0-D2
        ushort d0 = (ushort)((r.Content["D0"][0] << 8) | r.Content["D0"][1]);
        ushort d1 = (ushort)((r.Content["D0"][2] << 8) | r.Content["D0"][3]);
        ushort d2 = (ushort)((r.Content["D0"][4] << 8) | r.Content["D0"][5]);
        Assert.Equal((ushort)1000, d0);
        Assert.Equal((ushort)2000, d1);
        Assert.Equal((ushort)3000, d2);

        // D10-D11
        ushort d10 = (ushort)((r.Content["D10"][0] << 8) | r.Content["D10"][1]);
        ushort d11 = (ushort)((r.Content["D10"][2] << 8) | r.Content["D10"][3]);
        Assert.Equal((ushort)500, d10);
        Assert.Equal((ushort)600, d11);

        // D20
        ushort d20 = (ushort)((r.Content["D20"][0] << 8) | r.Content["D20"][1]);
        Assert.Equal((ushort)9999, d20);
    }

    [Fact]
    public void Ascii_WriteWordsRandom_MultipleAddresses_WritesCorrectly()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var (sl0, a0) = Mc3EAddressParser.Parse("D0");
        var (sl1, a1) = Mc3EAddressParser.Parse("D10");
        var (sl2, a2) = Mc3EAddressParser.Parse("D20");

        var w = client.WriteWordsRandom(new[]
        {
            (sl0, a0, (ushort)1111),
            (sl1, a1, (ushort)2222),
            (sl2, a2, (ushort)3333),
        });
        Assert.True(w.IsSuccess, w.Message);

        Assert.Equal((ushort)1111, server.GetDRegister(0));
        Assert.Equal((ushort)2222, server.GetDRegister(10));
        Assert.Equal((ushort)3333, server.GetDRegister(20));
    }

    // ═══════════════════════════════════════════
    //  ASCII 模式 — 大数据自动分片
    // ═══════════════════════════════════════════

    [Fact]
    public void Ascii_ReadLarge_OverLimit_AutoSplits()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        for (int i = 0; i < 10; i++)
            server.SetDRegister((ushort)i, (ushort)(1000 + i));
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true,
            MaxReadWordCount = 3
        };
        var r = client.ReadLarge("D0", 10);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(20, r.Content.Length);

        for (int i = 0; i < 10; i++)
        {
            ushort val = (ushort)((r.Content[i * 2] << 8) | r.Content[i * 2 + 1]);
            Assert.Equal((ushort)(1000 + i), val);
        }
    }

    [Fact]
    public void Ascii_WriteLarge_OverLimit_AutoSplits()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.Start();

        byte[] writeData = new byte[20];
        for (int i = 0; i < 10; i++)
        {
            ushort val = (ushort)(5000 + i);
            writeData[i * 2] = (byte)(val >> 8);
            writeData[i * 2 + 1] = (byte)(val & 0xFF);
        }

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true,
            MaxWriteWordCount = 3
        };
        var r = client.WriteLarge("D0", writeData);
        Assert.True(r.IsSuccess, r.Message);

        for (int i = 0; i < 10; i++)
        {
            ushort expected = (ushort)(5000 + i);
            Assert.Equal(expected, server.GetDRegister((ushort)i));
        }
    }

    // ═══════════════════════════════════════════
    //  ASCII 模式 — 字符串编码
    // ═══════════════════════════════════════════

    [Fact]
    public void Ascii_ReadStringEncoded_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.SetDRegister(300, 0x4865); // "He"
        server.SetDRegister(301, 0x6C6C); // "ll"
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };
        client.StringEncoding = Encoding.ASCII;
        var r = client.ReadStringEncoded("D300", 4);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal("Hell", r.Content);
    }

    [Fact]
    public void Ascii_WriteStringEncoded_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };
        client.StringEncoding = Encoding.ASCII;
        var w = client.WriteStringEncoded("D300", "Hi");
        Assert.True(w.IsSuccess, w.Message);

        Assert.Equal(0x4869, server.GetDRegister(300));
    }

    // ═══════════════════════════════════════════
    //  ASCII 模式 — 综合场景
    // ═══════════════════════════════════════════

    [Fact]
    public void Ascii_FullWorkflow_WriteReadBools_ThenReadType_ThenControl()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.SetPlcTypeName("Q03UDVCPU");
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        // 1. 写入 M 位
        var writeR = client.WriteBools("M0", new bool[] { true, false, true, true });
        Assert.True(writeR.IsSuccess);

        // 2. 读回 M 位
        var readR = client.ReadBools("M0", 4);
        Assert.True(readR.IsSuccess);
        Assert.Equal(new bool[] { true, false, true, true }, readR.Content);

        // 3. 读写 D 寄存器
        var dWrite = client.Write("D100", (short)12345);
        Assert.True(dWrite.IsSuccess);
        var dRead = client.ReadInt16("D100");
        Assert.True(dRead.IsSuccess);
        Assert.Equal((short)12345, dRead.Content);

        // 4. 读 PLC 型号
        var typeR = client.ReadPlcType();
        Assert.True(typeR.IsSuccess);
        Assert.Equal("Q03UDVCPU", typeR.Content);

        // 5. 停止 PLC
        var stopR = client.RemoteStop();
        Assert.True(stopR.IsSuccess);
        Assert.False(server.IsPlcRunning);

        // 6. 启动 PLC
        var runR = client.RemoteRun();
        Assert.True(runR.IsSuccess);
        Assert.True(server.IsPlcRunning);

        // 7. 错误状态复位
        var errR = client.ErrorStateReset();
        Assert.True(errR.IsSuccess);
    }

    // ═══════════════════════════════════════════
    //  Fake Server — 增强版，支持全部指令 + Binary/ASCII 双模式
    // ═══════════════════════════════════════════

    private sealed class Mc3EUdpFakeServer : IDisposable
    {
        // Word registers
        private readonly ushort[] _dRegisters = new ushort[65536];
        private readonly ushort[] _wRegisters = new ushort[65536];
        private readonly ushort[] _rRegisters = new ushort[65536];

        // Bit registers
        private readonly bool[] _mRelays = new bool[65536];

        // PLC state
        private volatile bool _plcRunning = true;
        private string _plcTypeName = "Q02HCPU";

        private readonly bool _useAscii;
        private readonly UdpClient _udp;
        private Thread? _thread;
        private volatile bool _running;

        public int Port { get; }
        public bool IsPlcRunning => _plcRunning;

        public Mc3EUdpFakeServer(bool useAscii)
        {
            _useAscii = useAscii;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(ReceiveLoop) { IsBackground = true };
            _thread.Start();
        }

        public void SetDRegister(int address, ushort value) => _dRegisters[address] = value;
        public ushort GetDRegister(int address) => _dRegisters[address];
        public void SetMRelay(ushort address, bool value) => _mRelays[address] = value;
        public bool GetMRelay(ushort address) => _mRelays[address];
        public void SetPlcTypeName(string name) => _plcTypeName = name;

        private void ReceiveLoop()
        {
            while (_running)
            {
                try
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] requestFrame = _udp.Receive(ref remote);
                    byte[] request = _useAscii ? FromAsciiHex(requestFrame) : requestFrame;

                    byte[] response = HandleRequest(request);
                    byte[] responseFrame = _useAscii ? ToAsciiHex(response) : response;
                    _udp.Send(responseFrame, responseFrame.Length, remote);
                }
                catch
                {
                    if (!_running) return;
                }
            }
        }

        private byte[] HandleRequest(byte[] request)
        {
            if (request.Length < 12) return BuildErrorResponse(request, 0xC001);

            ushort command = (ushort)((request[8] << 8) | request[9]);
            ushort subCommand = (ushort)((request[10] << 8) | request[11]);
            byte[] data = request.Length > 12 ? new byte[request.Length - 12] : Array.Empty<byte>();
            if (data.Length > 0) Buffer.BlockCopy(request, 12, data, 0, data.Length);

            try
            {
                return command switch
                {
                    0x0401 => subCommand == 0x0001 ? ProcessBatchReadBit(request, data) : ProcessBatchRead(request, data),
                    0x1401 => subCommand == 0x0001 ? ProcessBatchWriteBit(request, data) : ProcessBatchWrite(request, data),
                    0x0403 => subCommand == 0x0002 ? ProcessRandomReadMultiLength(request, data) : ProcessRandomRead(request, data),
                    0x1402 => ProcessRandomWrite(request, data),
                    0x0101 => ProcessReadPlcType(request),
                    0x1001 => ProcessRemoteRun(request, data),
                    0x1002 => ProcessRemoteStop(request),
                    0x1006 => ProcessRemoteReset(request),
                    0x1617 => ProcessErrorStateReset(request),
                    _ => BuildErrorResponse(request, 0xC001)
                };
            }
            catch
            {
                return BuildErrorResponse(request, 0xD003);
            }
        }

        private byte[] ProcessBatchRead(byte[] request, byte[] data)
        {
            if (data.Length < 6) return BuildSuccessResponse(request, Array.Empty<byte>());
            byte subLabel = data[0];
            uint address = (uint)(data[1] | (data[2] << 8) | (data[3] << 16));
            ushort count = (ushort)(data[4] | (data[5] << 8));

            ushort[]? store = GetWordStore(subLabel);
            if (store == null) return BuildSuccessResponse(request, Array.Empty<byte>());

            byte[] result = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                ushort val = store[address + i];
                result[i * 2] = (byte)(val >> 8);
                result[i * 2 + 1] = (byte)(val & 0xFF);
            }
            return BuildSuccessResponse(request, result);
        }

        private byte[] ProcessBatchWrite(byte[] request, byte[] data)
        {
            if (data.Length < 6) return BuildSuccessResponse(request, Array.Empty<byte>());
            byte subLabel = data[0];
            uint address = (uint)(data[1] | (data[2] << 8) | (data[3] << 16));
            ushort count = (ushort)(data[4] | (data[5] << 8));

            ushort[]? store = GetWordStore(subLabel);
            if (store == null) return BuildSuccessResponse(request, Array.Empty<byte>());

            for (int i = 0; i < count; i++)
            {
                int offset = 6 + i * 2;
                if (offset + 1 >= data.Length) break;
                store[address + i] = (ushort)((data[offset] << 8) | data[offset + 1]);
            }
            return BuildSuccessResponse(request, Array.Empty<byte>());
        }

        private byte[] ProcessBatchReadBit(byte[] request, byte[] data)
        {
            if (data.Length < 6) return BuildSuccessResponse(request, Array.Empty<byte>());
            byte subLabel = data[0];
            uint address = (uint)(data[1] | (data[2] << 8) | (data[3] << 16));
            ushort count = (ushort)(data[4] | (data[5] << 8));

            bool[]? store = GetBitStore(subLabel);
            if (store == null) return BuildSuccessResponse(request, Array.Empty<byte>());

            byte[] result = new byte[count];
            for (int i = 0; i < count; i++)
                result[i] = (byte)(store[address + i] ? 0x01 : 0x00);
            return BuildSuccessResponse(request, result);
        }

        private byte[] ProcessBatchWriteBit(byte[] request, byte[] data)
        {
            if (data.Length < 6) return BuildSuccessResponse(request, Array.Empty<byte>());
            byte subLabel = data[0];
            uint address = (uint)(data[1] | (data[2] << 8) | (data[3] << 16));
            ushort count = (ushort)(data[4] | (data[5] << 8));

            bool[]? store = GetBitStore(subLabel);
            if (store == null) return BuildSuccessResponse(request, Array.Empty<byte>());

            for (int i = 0; i < count; i++)
            {
                int offset = 6 + i;
                if (offset >= data.Length) break;
                store[address + i] = data[offset] != 0;
            }
            return BuildSuccessResponse(request, Array.Empty<byte>());
        }

        private byte[] ProcessRandomRead(byte[] request, byte[] data)
        {
            if (data.Length < 2) return BuildSuccessResponse(request, Array.Empty<byte>());
            ushort count = (ushort)(data[0] | (data[1] << 8));
            byte[] result = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                int offset = 2 + i * 4;
                if (offset + 3 >= data.Length) break;
                byte subLabel = data[offset];
                uint addr = (uint)(data[offset + 1] | (data[offset + 2] << 8) | (data[offset + 3] << 16));
                ushort[]? store = GetWordStore(subLabel);
                if (store != null && addr < store.Length)
                {
                    ushort val = store[addr];
                    result[i * 2] = (byte)(val >> 8);
                    result[i * 2 + 1] = (byte)(val & 0xFF);
                }
            }
            return BuildSuccessResponse(request, result);
        }

        private byte[] ProcessRandomWrite(byte[] request, byte[] data)
        {
            if (data.Length < 2) return BuildSuccessResponse(request, Array.Empty<byte>());
            ushort count = (ushort)(data[0] | (data[1] << 8));
            for (int i = 0; i < count; i++)
            {
                int offset = 2 + i * 6;
                if (offset + 5 >= data.Length) break;
                byte subLabel = data[offset];
                uint addr = (uint)(data[offset + 1] | (data[offset + 2] << 8) | (data[offset + 3] << 16));
                ushort value = (ushort)((data[offset + 4] << 8) | data[offset + 5]);
                ushort[]? store = GetWordStore(subLabel);
                if (store != null && addr < store.Length)
                    store[addr] = value;
            }
            return BuildSuccessResponse(request, Array.Empty<byte>());
        }

        private byte[] ProcessRandomReadMultiLength(byte[] request, byte[] data)
        {
            if (data.Length < 2) return BuildSuccessResponse(request, Array.Empty<byte>());
            ushort count = (ushort)(data[0] | (data[1] << 8));

            int totalWords = 0;
            for (int i = 0; i < count; i++)
            {
                int off = 2 + i * 6;
                if (off + 5 >= data.Length) break;
                ushort len = (ushort)(data[off + 4] | (data[off + 5] << 8));
                totalWords += len;
            }

            byte[] result = new byte[totalWords * 2];
            int resultOffset = 0;
            for (int i = 0; i < count; i++)
            {
                int offset = 2 + i * 6;
                if (offset + 5 >= data.Length) break;
                byte subLabel = data[offset];
                uint addr = (uint)(data[offset + 1] | (data[offset + 2] << 8) | (data[offset + 3] << 16));
                ushort len = (ushort)(data[offset + 4] | (data[offset + 5] << 8));

                ushort[]? store = GetWordStore(subLabel);
                for (int w = 0; w < len; w++)
                {
                    if (store != null && addr + w < store.Length)
                    {
                        ushort val = store[addr + w];
                        result[resultOffset++] = (byte)(val >> 8);
                        result[resultOffset++] = (byte)(val & 0xFF);
                    }
                    else
                    {
                        resultOffset += 2;
                    }
                }
            }
            return BuildSuccessResponse(request, result);
        }

        private byte[] ProcessReadPlcType(byte[] request)
        {
            byte[] result = new byte[16];
            byte[] nameBytes = Encoding.ASCII.GetBytes(_plcTypeName);
            Buffer.BlockCopy(nameBytes, 0, result, 0, Math.Min(nameBytes.Length, 16));
            return BuildSuccessResponse(request, result);
        }

        private byte[] ProcessRemoteRun(byte[] request, byte[] data)
        {
            if (data.Length >= 1) _plcRunning = true;
            return BuildSuccessResponse(request, Array.Empty<byte>());
        }

        private byte[] ProcessRemoteStop(byte[] request)
        {
            _plcRunning = false;
            return BuildSuccessResponse(request, Array.Empty<byte>());
        }

        private byte[] ProcessRemoteReset(byte[] request)
        {
            _plcRunning = true;
            return BuildSuccessResponse(request, Array.Empty<byte>());
        }

        private byte[] ProcessErrorStateReset(byte[] request)
        {
            return BuildSuccessResponse(request, Array.Empty<byte>());
        }

        private ushort[]? GetWordStore(byte subLabel)
        {
            return subLabel switch
            {
                0xA8 => _dRegisters,  // D
                0xB4 => _wRegisters,  // W
                0xAF => _rRegisters,  // R
                _ => null
            };
        }

        private bool[]? GetBitStore(byte subLabel)
        {
            return subLabel switch
            {
                0x90 => _mRelays,  // M
                _ => null
            };
        }

        private static byte[] BuildSuccessResponse(byte[] request, byte[] payload)
        {
            byte[] response = new byte[9 + payload.Length];
            response[0] = 0xD0; response[1] = 0x00;
            response[2] = request[2]; response[3] = request[3];
            response[4] = request[4]; response[5] = request[5];
            response[6] = 0x00; response[7] = 0x00; response[8] = 0x00;
            Buffer.BlockCopy(payload, 0, response, 9, payload.Length);
            return response;
        }

        private static byte[] BuildErrorResponse(byte[] request, ushort code)
        {
            byte[] response = BuildSuccessResponse(request, Array.Empty<byte>());
            response[7] = (byte)(code >> 8);
            response[8] = (byte)(code & 0xFF);
            return response;
        }

        public void Dispose()
        {
            _running = false;
            try { _udp.Close(); } catch { }
            _udp.Dispose();
        }
    }

    private static byte[] ToAsciiHex(byte[] bytes)
        => Encoding.ASCII.GetBytes(BitConverter.ToString(bytes).Replace("-", string.Empty));

    private static byte[] FromAsciiHex(byte[] asciiBytes)
    {
        string hex = Encoding.ASCII.GetString(asciiBytes);
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
