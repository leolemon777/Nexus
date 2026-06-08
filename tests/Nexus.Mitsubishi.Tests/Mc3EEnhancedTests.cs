using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nexus;
using Nexus.Mitsubishi;

namespace Nexus.Mitsubishi.Tests;

/// <summary>
/// MC-3E 增强功能测试 — 覆盖位读写、PLC控制、多长度随机读、大数据分片等。
/// 使用独立端口段 (16007+) 以避免与 Mc3EBinaryTests 冲突。
/// </summary>
public class Mc3EEnhancedTests
{
    private const int PortBase = 16007;

    // ═══════════════════════════════════════════
    //  位读写测试
    // ═══════════════════════════════════════════

    [Fact]
    public void ReadBools_M100_ReturnsCorrectBits()
    {
        var server = new Mc3EVirtuServer(PortBase);
        server.Start();
        try
        {
            server.SetMRelay(100, true);
            server.SetMRelay(101, false);
            server.SetMRelay(102, true);
            server.SetMRelay(103, true);

            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase);
            var r = client.ReadBools("M100", 4);
            Assert.True(r.IsSuccess);
            Assert.Equal(4, r.Content.Length);
            Assert.True(r.Content[0]);
            Assert.False(r.Content[1]);
            Assert.True(r.Content[2]);
            Assert.True(r.Content[3]);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void WriteBools_M100_WritesCorrectly()
    {
        var server = new Mc3EVirtuServer(PortBase + 1);
        server.Start();
        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 1);
            var values = new bool[] { true, false, true, true, false, true, false, false };
            var r = client.WriteBools("M200", values);
            Assert.True(r.IsSuccess);

            // 读回验证
            var read = client.ReadBools("M200", 8);
            Assert.True(read.IsSuccess);
            Assert.Equal(values, read.Content);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void ReadBools_X0_HexAddress_ReturnsCorrectBits()
    {
        var server = new Mc3EVirtuServer(PortBase + 2);
        server.Start();
        try
        {
            server.SetXInput(0x00, true);
            server.SetXInput(0x01, false);
            server.SetXInput(0x0F, true);

            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 2);
            var r = client.ReadBools("X0", 2);
            Assert.True(r.IsSuccess);
            Assert.True(r.Content[0]);
            Assert.False(r.Content[1]);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void ReadBools_Y0_HexAddress_ReturnsCorrectBits()
    {
        var server = new Mc3EVirtuServer(PortBase + 3);
        server.Start();
        try
        {
            server.SetYOutput(0x0A, true);
            server.SetYOutput(0x0B, false);

            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 3);
            var r = client.ReadBools("YA", 2);
            Assert.True(r.IsSuccess);
            Assert.True(r.Content[0]);
            Assert.False(r.Content[1]);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void ReadBools_B0_ReturnsCorrectBits()
    {
        var server = new Mc3EVirtuServer(PortBase + 4);
        server.Start();
        try
        {
            server.SetBRelay(0x0A, true);  // B 地址为十六进制，BA = 0x0A
            server.SetBRelay(0x0B, false);
            server.SetBRelay(0x0C, true);

            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 4);
            var r = client.ReadBools("BA", 3);
            Assert.True(r.IsSuccess);
            Assert.True(r.Content[0]);
            Assert.False(r.Content[1]);
            Assert.True(r.Content[2]);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void ReadBitsBatch_LargeCount_WorksCorrectly()
    {
        var server = new Mc3EVirtuServer(PortBase + 5);
        server.Start();
        try
        {
            // 设置 M0-M63 为交替模式
            for (int i = 0; i < 64; i++)
                server.SetMRelay((ushort)i, i % 2 == 0);

            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 5);
            var r = client.ReadBools("M0", 64);
            Assert.True(r.IsSuccess);
            Assert.Equal(64, r.Content.Length);
            for (int i = 0; i < 64; i++)
                Assert.Equal(i % 2 == 0, r.Content[i]);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    // ═══════════════════════════════════════════
    //  PLC 控制命令测试
    // ═══════════════════════════════════════════

    [Fact]
    public void RemoteRun_Succeeds()
    {
        var server = new Mc3EVirtuServer(PortBase + 10);
        server.Start();
        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 10);
            var r = client.RemoteRun();
            Assert.True(r.IsSuccess);
            Assert.True(server.IsPlcRunning);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void RemoteStop_Succeeds()
    {
        var server = new Mc3EVirtuServer(PortBase + 11);
        server.Start();
        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 11);
            var r = client.RemoteStop();
            Assert.True(r.IsSuccess);
            Assert.False(server.IsPlcRunning);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void RemoteReset_Succeeds()
    {
        var server = new Mc3EVirtuServer(PortBase + 12);
        server.Start();
        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 12);

            // 先停止 PLC
            var stopR = client.RemoteStop();
            Assert.True(stopR.IsSuccess);
            Assert.False(server.IsPlcRunning);

            var r = client.RemoteReset();
            Assert.True(r.IsSuccess);
            Assert.True(server.IsPlcRunning);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void ReadPlcType_ReturnsModelName()
    {
        var server = new Mc3EVirtuServer(PortBase + 13);
        server.Start();
        try
        {
            server.SetPlcTypeName("Q02HCPU");
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 13);
            var r = client.ReadPlcType();
            Assert.True(r.IsSuccess);
            Assert.Equal("Q02HCPU", r.Content);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void ReadPlcType_CustomName_ReturnsCorrectly()
    {
        var server = new Mc3EVirtuServer(PortBase + 14);
        server.Start();
        try
        {
            server.SetPlcTypeName("FX5U-32M");
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 14);
            var r = client.ReadPlcType();
            Assert.True(r.IsSuccess);
            Assert.Equal("FX5U-32M", r.Content);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void ErrorStateReset_Succeeds()
    {
        var server = new Mc3EVirtuServer(PortBase + 15);
        server.Start();
        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 15);
            var r = client.ErrorStateReset();
            Assert.True(r.IsSuccess);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task RemoteRunAsync_Succeeds()
    {
        var server = new Mc3EVirtuServer(PortBase + 16);
        server.Start();
        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 16);
            var r = await client.RemoteRunAsync();
            Assert.True(r.IsSuccess);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task RemoteStopAsync_Succeeds()
    {
        var server = new Mc3EVirtuServer(PortBase + 17);
        server.Start();
        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 17);
            var r = await client.RemoteStopAsync();
            Assert.True(r.IsSuccess);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task ReadPlcTypeAsync_Succeeds()
    {
        var server = new Mc3EVirtuServer(PortBase + 18);
        server.Start();
        try
        {
            server.SetPlcTypeName("iQ-R08PCPU");
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 18);
            var r = await client.ReadPlcTypeAsync();
            Assert.True(r.IsSuccess);
            Assert.Equal("iQ-R08PCPU", r.Content);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task ErrorStateResetAsync_Succeeds()
    {
        var server = new Mc3EVirtuServer(PortBase + 19);
        server.Start();
        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 19);
            var r = await client.ErrorStateResetAsync();
            Assert.True(r.IsSuccess);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    // ═══════════════════════════════════════════
    //  多长度随机读取测试
    // ═══════════════════════════════════════════

    [Fact]
    public void ReadRandomMultiLength_DifferentLengths_ReturnsCorrectData()
    {
        var server = new Mc3EVirtuServer(PortBase + 20);
        server.Start();
        try
        {
            // D0=1000, D1=2000, D2=3000
            server.SetDRegister(0, 1000);
            server.SetDRegister(1, 2000);
            server.SetDRegister(2, 3000);
            // D10=500, D11=600
            server.SetDRegister(10, 500);
            server.SetDRegister(11, 600);
            // D20=9999
            server.SetDRegister(20, 9999);

            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 20);
            var r = client.ReadRandomMultiLength(new[]
            {
                ("D0", (ushort)3),   // 读 3 个字
                ("D10", (ushort)2),  // 读 2 个字
                ("D20", (ushort)1),  // 读 1 个字
            });
            Assert.True(r.IsSuccess);

            // 验证 D0-D2 (3 words = 6 bytes)
            Assert.Equal(6, r.Content["D0"].Length);
            ushort d0 = (ushort)((r.Content["D0"][0] << 8) | r.Content["D0"][1]);
            ushort d1 = (ushort)((r.Content["D0"][2] << 8) | r.Content["D0"][3]);
            ushort d2 = (ushort)((r.Content["D0"][4] << 8) | r.Content["D0"][5]);
            Assert.Equal((ushort)1000, d0);
            Assert.Equal((ushort)2000, d1);
            Assert.Equal((ushort)3000, d2);

            // 验证 D10-D11 (2 words = 4 bytes)
            Assert.Equal(4, r.Content["D10"].Length);
            ushort d10 = (ushort)((r.Content["D10"][0] << 8) | r.Content["D10"][1]);
            ushort d11 = (ushort)((r.Content["D10"][2] << 8) | r.Content["D10"][3]);
            Assert.Equal((ushort)500, d10);
            Assert.Equal((ushort)600, d11);

            // 验证 D20 (1 word = 2 bytes)
            Assert.Equal(2, r.Content["D20"].Length);
            ushort d20 = (ushort)((r.Content["D20"][0] << 8) | r.Content["D20"][1]);
            Assert.Equal((ushort)9999, d20);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void ReadWordsRandomMultiLength_SingleAddress_WorksAsBatchRead()
    {
        var server = new Mc3EVirtuServer(PortBase + 21);
        server.Start();
        try
        {
            server.SetDRegister(50, 12345);
            server.SetDRegister(51, unchecked((ushort)67890));

            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 21);
            var (subLabel, addr) = Mc3EAddressParser.Parse("D50");
            var r = client.ReadWordsRandomMultiLength(new[] { (subLabel, addr, (ushort)2) });
            Assert.True(r.IsSuccess);
            Assert.Equal(4, r.Content.Length);

            ushort val0 = (ushort)((r.Content[0] << 8) | r.Content[1]);
            ushort val1 = (ushort)((r.Content[2] << 8) | r.Content[3]);
            Assert.Equal((ushort)12345, val0);
            Assert.Equal(unchecked((ushort)67890), val1);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    // ═══════════════════════════════════════════
    //  大数据自动分片测试
    // ═══════════════════════════════════════════

    [Fact]
    public void ReadLarge_UnderLimit_SingleRead()
    {
        var server = new Mc3EVirtuServer(PortBase + 30);
        server.Start();
        try
        {
            server.SetDRegister(0, 1111);
            server.SetDRegister(1, 2222);

            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 30)
            {
                MaxReadWordCount = 960
            };
            var r = client.ReadLarge("D0", 2);
            Assert.True(r.IsSuccess);
            Assert.Equal(4, r.Content.Length);
            ushort v0 = (ushort)((r.Content[0] << 8) | r.Content[1]);
            ushort v1 = (ushort)((r.Content[2] << 8) | r.Content[3]);
            Assert.Equal((ushort)1111, v0);
            Assert.Equal((ushort)2222, v1);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void ReadLarge_OverLimit_AutoSplits()
    {
        var server = new Mc3EVirtuServer(PortBase + 31);
        server.Start();
        try
        {
            // 预设 D0-D9 为递增值
            for (int i = 0; i < 10; i++)
                server.SetDRegister((ushort)i, (ushort)(1000 + i));

            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 31)
            {
                MaxReadWordCount = 3  // 限制每次最多读 3 个字，强制分片
            };
            var r = client.ReadLarge("D0", 10);
            Assert.True(r.IsSuccess);
            Assert.Equal(20, r.Content.Length); // 10 words * 2 bytes

            // 验证所有数据
            for (int i = 0; i < 10; i++)
            {
                ushort val = (ushort)((r.Content[i * 2] << 8) | r.Content[i * 2 + 1]);
                Assert.Equal((ushort)(1000 + i), val);
            }

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void WriteLarge_OverLimit_AutoSplits()
    {
        var server = new Mc3EVirtuServer(PortBase + 32);
        server.Start();
        try
        {
            // 准备 10 个字的数据
            byte[] writeData = new byte[20];
            for (int i = 0; i < 10; i++)
            {
                ushort val = (ushort)(5000 + i);
                writeData[i * 2] = (byte)(val >> 8);
                writeData[i * 2 + 1] = (byte)(val & 0xFF);
            }

            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 32)
            {
                MaxWriteWordCount = 3  // 限制每次最多写 3 个字，强制分片
            };
            var r = client.WriteLarge("D0", writeData);
            Assert.True(r.IsSuccess);

            // 读回验证
            for (int i = 0; i < 10; i++)
            {
                ushort expected = (ushort)(5000 + i);
                Assert.Equal(expected, server.GetDRegister((ushort)i));
            }

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    // ═══════════════════════════════════════════
    //  MaxReadWordCount/MaxWriteWordCount 属性测试
    // ═══════════════════════════════════════════

    [Fact]
    public void MaxReadWordCount_DefaultIs960()
    {
        var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", 5007);
        Assert.Equal((ushort)960, client.MaxReadWordCount);
        Assert.Equal((ushort)960, client.MaxWriteWordCount);
        client.Dispose();
    }

    [Fact]
    public void MaxReadWordCount_CanBeSet()
    {
        var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", 5007);
        client.MaxReadWordCount = 480;
        client.MaxWriteWordCount = 480;
        Assert.Equal((ushort)480, client.MaxReadWordCount);
        Assert.Equal((ushort)480, client.MaxWriteWordCount);
        client.Dispose();
    }

    // ═══════════════════════════════════════════
    //  综合场景测试
    // ═══════════════════════════════════════════

    [Fact]
    public void FullWorkflow_WriteReadBools_ThenReadType_ThenControl()
    {
        var server = new Mc3EVirtuServer(PortBase + 40);
        server.Start();
        try
        {
            server.SetPlcTypeName("Q03UDVCPU");

            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 40);

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

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void BitWriteRead_MArea_LargeRange()
    {
        var server = new Mc3EVirtuServer(PortBase + 41);
        server.Start();
        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 41);

            // 写入 32 个位
            var expected = new bool[32];
            for (int i = 0; i < 32; i++)
                expected[i] = i % 3 == 0;

            var w = client.WriteBools("M500", expected);
            Assert.True(w.IsSuccess);

            var r = client.ReadBools("M500", 32);
            Assert.True(r.IsSuccess);
            Assert.Equal(expected, r.Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void RandomMultiLength_MixedRegisters_ReturnsCorrectData()
    {
        var server = new Mc3EVirtuServer(PortBase + 42);
        server.Start();
        try
        {
            // 设置不同寄存器的值
            server.SetDRegister(0, 0x1111);
            server.SetDRegister(1, 0x2222);
            server.SetWRegister(100, 0x3333);
            server.SetRRegister(200, 0x4444);
            server.SetRRegister(201, 0x5555);
            server.SetRRegister(202, 0x6666);

            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 42);
            var r = client.ReadRandomMultiLength(new[]
            {
                ("D0", (ushort)2),    // 2 words from D
                ("W100", (ushort)1),  // 1 word from W
                ("R200", (ushort)3),  // 3 words from R
            });
            Assert.True(r.IsSuccess);

            // D0-D1: 4 bytes
            var d0Data = r.Content["D0"];
            Assert.Equal(4, d0Data.Length);
            Assert.Equal(0x11, d0Data[0]);
            Assert.Equal(0x11, d0Data[1]);

            // W100: 2 bytes
            var w100Data = r.Content["W100"];
            Assert.Equal(2, w100Data.Length);
            Assert.Equal(0x33, w100Data[0]);
            Assert.Equal(0x33, w100Data[1]);

            // R200-R202: 6 bytes
            var r200Data = r.Content["R200"];
            Assert.Equal(6, r200Data.Length);
            ushort r200 = (ushort)((r200Data[0] << 8) | r200Data[1]);
            ushort r201 = (ushort)((r200Data[2] << 8) | r200Data[3]);
            ushort r202 = (ushort)((r200Data[4] << 8) | r200Data[5]);
            Assert.Equal((ushort)0x4444, r200);
            Assert.Equal((ushort)0x5555, r201);
            Assert.Equal((ushort)0x6666, r202);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    // ═══════════════════════════════════════════
    //  新增位寄存器测试 (L/F/V/S)
    // ═══════════════════════════════════════════

    [Fact]
    public void WriteBools_LRelay_WorksCorrectly()
    {
        var server = new Mc3EVirtuServer(PortBase + 50);
        server.Start();
        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 50);
            var w = client.WriteBools("L10", new bool[] { true, false, true });
            Assert.True(w.IsSuccess);

            var r = client.ReadBools("L10", 3);
            Assert.True(r.IsSuccess);
            Assert.Equal(new bool[] { true, false, true }, r.Content);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void WriteBools_FState_WorksCorrectly()
    {
        var server = new Mc3EVirtuServer(PortBase + 51);
        server.Start();
        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 51);
            var w = client.WriteBools("F20", new bool[] { false, true, true, false });
            Assert.True(w.IsSuccess);

            var r = client.ReadBools("F20", 4);
            Assert.True(r.IsSuccess);
            Assert.Equal(new bool[] { false, true, true, false }, r.Content);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void WriteBools_VEdge_WorksCorrectly()
    {
        var server = new Mc3EVirtuServer(PortBase + 52);
        server.Start();
        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 52);
            var w = client.WriteBools("V30", new bool[] { true, true });
            Assert.True(w.IsSuccess);

            var r = client.ReadBools("V30", 2);
            Assert.True(r.IsSuccess);
            Assert.True(r.Content[0]);
            Assert.True(r.Content[1]);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void WriteBools_SStep_WorksCorrectly()
    {
        var server = new Mc3EVirtuServer(PortBase + 53);
        server.Start();
        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 53);
            var w = client.WriteBools("S40", new bool[] { true, false, true, false, true });
            Assert.True(w.IsSuccess);

            var r = client.ReadBools("S40", 5);
            Assert.True(r.IsSuccess);
            Assert.Equal(new bool[] { true, false, true, false, true }, r.Content);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    // ═══════════════════════════════════════════
    //  地址解析增强测试
    // ═══════════════════════════════════════════

    [Theory]
    [InlineData("SD100", false)]
    [InlineData("SW100", false)]
    [InlineData("ZR100", false)]
    [InlineData("SM100", true)]
    [InlineData("DX1A", true)]
    [InlineData("TS100", true)]
    [InlineData("TC100", true)]
    [InlineData("CS100", true)]
    [InlineData("CC100", true)]
    public void IsBitAddress_AllPrefixes_ReturnsCorrectly(string address, bool expected)
    {
        Assert.Equal(expected, Mc3EAddressParser.IsBitAddress(address));
    }

    [Fact]
    public async Task RemoteResetAsync_Succeeds()
    {
        var server = new Mc3EVirtuServer(PortBase + 54);
        server.Start();
        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", PortBase + 54);
            var r = await client.RemoteResetAsync();
            Assert.True(r.IsSuccess);
            client.Dispose();
        }
        finally { server.Dispose(); }
    }
}
