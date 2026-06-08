using System;
using System.Text;
using Xunit;
using Nexus.Yaskawa;

namespace Nexus.Yaskawa.Tests;

public class MemobusTests
{
    private const int PortBase = 26000;

    #region 地址判断

    [Theory]
    [InlineData("M100", true)]
    [InlineData("G50", true)]
    [InlineData("I0", true)]
    [InlineData("O10", true)]
    [InlineData("S200", true)]
    [InlineData("100", false)]
    [InlineData("x=3;100", false)]
    public void IsNamedAddress_Tests(string addr, bool expected)
    {
        Assert.Equal(expected, MemobusClient.IsNamedAddress(addr));
    }

    [Theory]
    [InlineData("MB100", true)]
    [InlineData("M100.5", true)]
    [InlineData("M100", false)]
    public void IsBitAccess_Tests(string addr, bool expected)
    {
        Assert.Equal(expected, MemobusClient.IsBitAccess(addr));
    }

    [Theory]
    [InlineData("M100", (byte)77)]
    [InlineData("G50", (byte)71)]
    [InlineData("I0", (byte)73)]
    [InlineData("O10", (byte)79)]
    [InlineData("S200", (byte)83)]
    public void GetAddressDataType_Tests(string addr, byte expected)
    {
        Assert.Equal(expected, MemobusClient.GetAddressDataType(addr));
    }

    [Fact]
    public void CalculateBoolIndex_DotNotation()
    {
        // M100.5 → 100 * 16 + 5 = 1605
        Assert.Equal(1605, MemobusClient.CalculateBoolIndex("M100.5"));
    }

    [Fact]
    public void CalculateBoolIndex_MBPrefix()
    {
        // MB100 → 100 * 16 = 1600
        Assert.Equal(1600, MemobusClient.CalculateBoolIndex("MB100"));
    }

    #endregion

    #region 命令构建

    [Fact]
    public void BuildReadCommand_StandardSFC03()
    {
        var client = new MemobusClient("127.0.0.1");
        var result = client.BuildReadCommand("100", 5);
        Assert.True(result.IsSuccess, result.Message);

        byte[] cmd = result.Content;
        // 标准 SFC 03: [len(2), MFC, SFC(03), cpuToFrom, addrHi, addrLo, countHi, countLo]
        Assert.Equal(9, cmd.Length);
        Assert.Equal(0x20, cmd[2]); // MFC
        Assert.Equal(0x03, cmd[3]); // SFC
        // address=100 big-endian: 0x00, 0x64
        Assert.Equal(0x00, cmd[5]);
        Assert.Equal(0x64, cmd[6]);
        // count=5 big-endian: 0x00, 0x05
        Assert.Equal(0x00, cmd[7]);
        Assert.Equal(0x05, cmd[8]);
    }

    [Fact]
    public void BuildReadCommand_ExtendedSFC09()
    {
        var client = new MemobusClient("127.0.0.1");
        var result = client.BuildReadCommand("x=9;200", 10);
        Assert.True(result.IsSuccess, result.Message);

        byte[] cmd = result.Content;
        Assert.Equal(10, cmd.Length);
        Assert.Equal(0x09, cmd[3]); // SFC=09
        // address=200 little-endian: 0xC8, 0x00
        Assert.Equal(0xC8, cmd[6]);
        Assert.Equal(0x00, cmd[7]);
    }

    [Fact]
    public void BuildReadCommand_NamedWord()
    {
        var client = new MemobusClient("127.0.0.1");
        var result = client.BuildReadCommand("M100", 5);
        Assert.True(result.IsSuccess, result.Message);

        byte[] cmd = result.Content;
        Assert.Equal(0x43, cmd[2]); // MFC=NamedMfc
        Assert.Equal(0x49, cmd[3]); // SFC=Named word read
        Assert.Equal((byte)'M', cmd[6]); // dataType
    }

    [Fact]
    public void BuildReadCommand_NamedBit()
    {
        var client = new MemobusClient("127.0.0.1");
        var result = client.BuildReadCommand("M100.5", 1);
        Assert.True(result.IsSuccess, result.Message);

        byte[] cmd = result.Content;
        Assert.Equal(0x43, cmd[2]); // MFC
        Assert.Equal(0x41, cmd[3]); // SFC=Named bit read
        Assert.Equal((byte)'M', cmd[6]); // dataType
    }

    [Fact]
    public void BuildWriteCommand_StandardSFC10()
    {
        var client = new MemobusClient("127.0.0.1");
        byte[] data = { 0x34, 0x12 };
        var result = client.BuildWriteCommand("100", data);
        Assert.True(result.IsSuccess, result.Message);

        byte[] cmd = result.Content;
        Assert.Equal(0x10, cmd[3]); // SFC=0x10
        // address=100 big-endian
        Assert.Equal(0x00, cmd[5]);
        Assert.Equal(0x64, cmd[6]);
        // wordCount=1 big-endian
        Assert.Equal(0x00, cmd[7]);
        Assert.Equal(0x01, cmd[8]);
        // data follows at offset 9
        Assert.Equal(0x34, cmd[9]);
        Assert.Equal(0x12, cmd[10]);
    }

    [Fact]
    public void BuildWriteCommand_Named()
    {
        var client = new MemobusClient("127.0.0.1");
        byte[] data = { 0x34, 0x12 };
        var result = client.BuildWriteCommand("M100", data);
        Assert.True(result.IsSuccess, result.Message);

        byte[] cmd = result.Content;
        Assert.Equal(0x43, cmd[2]); // MFC=Named
        Assert.Equal(0x4B, cmd[3]); // SFC=Named word write
        Assert.Equal((byte)'M', cmd[6]);
        // data is word-reversed
        Assert.Equal(0x12, cmd[14]);
        Assert.Equal(0x34, cmd[15]);
    }

    [Fact]
    public void BuildWriteSingleCoilCommand_Structure()
    {
        var client = new MemobusClient("127.0.0.1");
        var result = client.BuildWriteSingleCoilCommand(100, true);
        Assert.True(result.IsSuccess);

        byte[] cmd = result.Content;
        Assert.Equal(0x05, cmd[3]); // SFC
        Assert.Equal(0xFF, cmd[7]); // ON
    }

    [Fact]
    public void BuildReadRandomCommand_Structure()
    {
        var client = new MemobusClient("127.0.0.1");
        var result = client.BuildReadRandomCommand(new ushort[] { 100, 200 });
        Assert.True(result.IsSuccess);

        byte[] cmd = result.Content;
        Assert.Equal(0x0D, cmd[3]); // SFC=random read
        int count = cmd[6] | (cmd[7] << 8);
        Assert.Equal(2, count);
    }

    [Fact]
    public void BuildWriteRandomCommand_Structure()
    {
        var client = new MemobusClient("127.0.0.1");
        var result = client.BuildWriteRandomCommand(
            new ushort[] { 100 },
            new byte[] { 0x34, 0x12 });
        Assert.True(result.IsSuccess);

        byte[] cmd = result.Content;
        Assert.Equal(0x0E, cmd[3]); // SFC=random write
    }

    [Fact]
    public void BuildReadCommand_Empty_Fails()
    {
        var client = new MemobusClient("127.0.0.1");
        var result = client.BuildReadCommand("", 1);
        Assert.False(result.IsSuccess);
    }

    #endregion

    #region 外层帧封装

    [Fact]
    public void WrapUnwrap_RoundTrip()
    {
        byte[] inner = { 0x07, 0x00, 0x20, 0x03, 0x21, 0x00, 0x64, 0x00, 0x01 };
        byte[] wrapped = MemobusClient.WrapWithOuterHeader(inner);

        Assert.Equal(0x11, wrapped[0]); // marker
        // total length at [6-7]
        int totalLen = wrapped[6] | (wrapped[7] << 8);
        Assert.Equal(wrapped.Length, totalLen);

        // Unwrap
        var unwrapResult = MemobusClient.UnwrapOuterHeader(wrapped);
        Assert.True(unwrapResult.IsSuccess);
        Assert.Equal(inner.Length, unwrapResult.Content.Length);
        Assert.Equal(inner[2], unwrapResult.Content[2]); // MFC
    }

    [Fact]
    public void UnwrapOuterHeader_TooShort()
    {
        var result = MemobusClient.UnwrapOuterHeader(new byte[5]);
        Assert.False(result.IsSuccess);
    }

    #endregion

    #region 响应解析

    [Fact]
    public void ParseResponse_StandardRead_Success()
    {
        byte[] sendInner = { 0x07, 0x00, 0x20, 0x03, 0x21, 0x00, 0x64, 0x00, 0x01 };
        // Response: [len(2), MFC, SFC, cpuToFrom, data(2 bytes)]
        byte[] recvInner = { 0x05, 0x00, 0x20, 0x03, 0x21, 0x34, 0x12 };

        var result = MemobusClient.ParseResponse(sendInner, recvInner);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, result.Content.Length);
        Assert.Equal(0x34, result.Content[0]);
        Assert.Equal(0x12, result.Content[1]);
    }

    [Fact]
    public void ParseResponse_ErrorResponse()
    {
        byte[] sendInner = { 0x07, 0x00, 0x20, 0x03, 0x21, 0x00, 0x64, 0x00, 0x01 };
        // Error: SFC=0x83 (03+0x80), errorCode=0x02
        byte[] recvInner = { 0x04, 0x00, 0x20, 0x83, 0x21, 0x02 };

        var result = MemobusClient.ParseResponse(sendInner, recvInner);
        Assert.False(result.IsSuccess);
        Assert.Contains("非法数据地址", result.Message);
    }

    [Fact]
    public void ParseResponse_SfcMismatch()
    {
        byte[] sendInner = { 0x07, 0x00, 0x20, 0x03, 0x21, 0x00, 0x64, 0x00, 0x01 };
        byte[] recvInner = { 0x05, 0x00, 0x20, 0x04, 0x21, 0x34, 0x12 };

        var result = MemobusClient.ParseResponse(sendInner, recvInner);
        Assert.False(result.IsSuccess);
        Assert.Contains("SFC 不匹配", result.Message);
    }

    [Fact]
    public void ParseResponse_WriteSuccess()
    {
        byte[] sendInner = { 0x07, 0x00, 0x20, 0x10, 0x21, 0x00, 0x64, 0x00, 0x01 };
        byte[] recvInner = { 0x03, 0x00, 0x20, 0x10, 0x21 };

        var result = MemobusClient.ParseResponse(sendInner, recvInner);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Content);
    }

    #endregion

    #region 工具方法

    [Fact]
    public void ReverseWords_Test()
    {
        byte[] data = { 0x01, 0x02, 0x03, 0x04 };
        byte[] reversed = MemobusClient.ReverseWords(data);
        Assert.Equal(new byte[] { 0x02, 0x01, 0x04, 0x03 }, reversed);
    }

    [Fact]
    public void ReverseWords_OddLength()
    {
        byte[] data = { 0x01, 0x02, 0x03 };
        byte[] reversed = MemobusClient.ReverseWords(data);
        Assert.Equal(new byte[] { 0x02, 0x01, 0x03 }, reversed);
    }

    [Fact]
    public void BoolArrayToBytes_Test()
    {
        bool[] values = { true, false, true, true, false, false, false, false };
        byte[] bytes = MemobusClient.BoolArrayToBytes(values);
        Assert.Equal(1, bytes.Length);
        Assert.Equal(0x0D, bytes[0]); // bits: 1101 = 0x0D
    }

    [Fact]
    public void BytesToBoolArray_Test()
    {
        byte[] data = { 0x0D };
        bool[] result = MemobusClient.BytesToBoolArray(data, 4);
        Assert.True(result[0]);   // bit 0
        Assert.False(result[1]);  // bit 1
        Assert.True(result[2]);   // bit 2
        Assert.True(result[3]);   // bit 3
    }

    [Fact]
    public void GetErrorText_KnownCodes()
    {
        Assert.Equal("非法功能码", MemobusClient.GetErrorText(0x01));
        Assert.Equal("非法数据地址", MemobusClient.GetErrorText(0x02));
        Assert.Equal("非法数据值", MemobusClient.GetErrorText(0x03));
        Assert.Contains("未知错误", MemobusClient.GetErrorText(0xFF));
    }

    #endregion

    #region 虚拟服务器生命周期

    [Fact]
    public void Server_StartStop()
    {
        int port = PortBase;
        var server = new MemobusVirtualServer(port);
        Assert.False(server.IsRunning);

        server.Start();
        Assert.True(server.IsRunning);

        server.Stop();
        Assert.False(server.IsRunning);
        server.Dispose();
    }

    #endregion

    #region 端到端测试 — 标准寄存器

    [Fact]
    public void Client_ReadInt16_Holding()
    {
        int port = PortBase + 1;
        var server = new MemobusVirtualServer(port);
        server.SetHolding(100, 0x1234);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadInt16("100");
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
    public void Client_WriteReadInt16_Holding()
    {
        int port = PortBase + 2;
        var server = new MemobusVirtualServer(port);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var writeResult = client.Write("200", unchecked((short)0xABCD));
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var r = client.ReadInt16("200");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(unchecked((short)0xABCD), r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadUInt16_Holding()
    {
        int port = PortBase + 3;
        var server = new MemobusVirtualServer(port);
        server.SetHolding(50, 0x5678);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadUInt16("50");
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
        var server = new MemobusVirtualServer(port);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            int expected = 0x12345678;
            Assert.True(client.Write("300", expected).IsSuccess);

            var r = client.ReadInt32("300");
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
        var server = new MemobusVirtualServer(port);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            float expected = 3.14f;
            Assert.True(client.Write("400", expected).IsSuccess);

            var r = client.ReadFloat("400");
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
        var server = new MemobusVirtualServer(port);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // "Hello" = 5 字节，补齐到 6 字节（3 字）确保 word 对齐
            Assert.True(client.Write("500", "Hello\0").IsSuccess);

            var r = client.ReadString("500", 3);
            Assert.True(r.IsSuccess, r.Message);
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
        var server = new MemobusVirtualServer(port);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            uint expected = 0xAABBCCDD;
            Assert.True(client.Write("600", expected).IsSuccess);

            var r = client.ReadUInt32("600");
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
        var server = new MemobusVirtualServer(port);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            long expected = 0x0102030405060708;
            Assert.True(client.Write("700", expected).IsSuccess);

            var r = client.ReadInt64("700");
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
    public void Client_ReadWrite_Double()
    {
        int port = PortBase + 9;
        var server = new MemobusVirtualServer(port);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            double expected = 2.718;
            Assert.True(client.Write("800", expected).IsSuccess);

            var r = client.ReadDouble("800");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(expected, r.Content, 3);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    #endregion

    #region 端到端测试 — 线圈

    [Fact]
    public void Client_WriteRead_Bool_Coil()
    {
        int port = PortBase + 10;
        var server = new MemobusVirtualServer(port);
        server.SetCoil(10, false);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 写入线圈 10 (SFC=0x05)
            Assert.True(client.Write("10", true).IsSuccess);

            // 通过线圈读取验证 (x=1 指定 SFC=01 读线圈)
            var cmdResult = client.BuildReadCommand("x=1;10", 1);
            Assert.True(cmdResult.IsSuccess, cmdResult.Message);

            byte[] innerCmd = cmdResult.Content;
            byte[] fullCmd = MemobusClient.WrapWithOuterHeader(innerCmd);
            var sendResult = client.SendCustomMessage(fullCmd);
            Assert.True(sendResult.IsSuccess, sendResult.Message);

            var unwrap = MemobusClient.UnwrapOuterHeader(sendResult.Content);
            Assert.True(unwrap.IsSuccess, unwrap.Message);

            var parseResult = MemobusClient.ParseResponse(innerCmd, unwrap.Content);
            Assert.True(parseResult.IsSuccess, parseResult.Message);

            bool[] bits = MemobusClient.BytesToBoolArray(parseResult.Content, 1);
            Assert.True(bits[0]);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadBool_FromRegister()
    {
        int port = PortBase + 17;
        var server = new MemobusVirtualServer(port);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 向保持寄存器 100 写入值 1, ReadBool 读取最低位
            Assert.True(client.Write("100", (short)1).IsSuccess);
            var r = client.ReadBool("100");
            Assert.True(r.IsSuccess, r.Message);
            Assert.True(r.Content);

            // 写入值 0
            Assert.True(client.Write("100", (short)0).IsSuccess);
            var r2 = client.ReadBool("100");
            Assert.True(r2.IsSuccess, r2.Message);
            Assert.False(r2.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    #endregion

    #region 端到端测试 — 非持久连接

    [Fact]
    public void Client_NonPersistent_ReadWrite()
    {
        int port = PortBase + 11;
        var server = new MemobusVirtualServer(port);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            // 不设置持久连接

            Assert.True(client.Write("100", (short)0x1234).IsSuccess);
            var r = client.ReadInt16("100");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((short)0x1234, r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    #endregion

    #region 端到端测试 — 连续多次读写

    [Fact]
    public void Client_MultipleReadWrite_Sequence()
    {
        int port = PortBase + 12;
        var server = new MemobusVirtualServer(port);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.True(client.Write("10", (short)100).IsSuccess);
            Assert.True(client.Write("11", (short)200).IsSuccess);

            Assert.Equal((short)100, client.ReadInt16("10").Content);
            Assert.Equal((short)200, client.ReadInt16("11").Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    #endregion

    #region 端到端测试 — 命名区域

    [Fact]
    public void Client_ReadWrite_NamedM()
    {
        int port = PortBase + 13;
        var server = new MemobusVirtualServer(port);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.True(client.Write("M100", (short)0x5678).IsSuccess);

            var r = client.ReadInt16("M100");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((short)0x5678, r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadWrite_NamedG()
    {
        int port = PortBase + 14;
        var server = new MemobusVirtualServer(port);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.True(client.Write("G50", (short)0x1111).IsSuccess);

            var r = client.ReadInt16("G50");
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
    public void Client_ReadBytes_Multiple()
    {
        int port = PortBase + 15;
        var server = new MemobusVirtualServer(port);
        server.SetHolding(50, 0x1122);
        server.SetHolding(51, 0x3344);
        server.SetHolding(52, 0x5566);
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadBytes("50", 3);
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

    #region 端到端测试 — CpuTo/CpuFrom

    [Fact]
    public void Client_CpuToFrom_Custom()
    {
        int port = PortBase + 16;
        var server = new MemobusVirtualServer(port);
        server.CpuTo = 3;
        server.CpuFrom = 2;
        server.Start();

        try
        {
            var client = new MemobusClient("127.0.0.1", port);
            client.CpuTo = 3;
            client.CpuFrom = 2;
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.True(client.Write("100", unchecked((short)0x9999)).IsSuccess);
            var r = client.ReadInt16("100");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(unchecked((short)0x9999), r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    #endregion
}
