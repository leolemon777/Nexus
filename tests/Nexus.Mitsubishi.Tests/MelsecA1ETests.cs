using System;
using Xunit;
using Nexus.Mitsubishi;

namespace Nexus.Mitsubishi.Tests;

public class MelsecA1ETests
{
    private const int PortBase = 17200;

    #region 地址解析

    [Theory]
    [InlineData("D100",  0x4440, 0, 100)]
    [InlineData("D0",    0x4440, 0, 0)]
    [InlineData("M200",  0x4D20, 1, 200)]
    [InlineData("M0",    0x4D20, 1, 0)]
    [InlineData("S50",   0x5320, 1, 50)]
    [InlineData("B1A",   0x4220, 1, 0x1A)]
    [InlineData("R100",  0x5220, 0, 100)]
    [InlineData("W20",   0x5740, 0, 0x20)]
    [InlineData("F10",   0x4620, 1, 10)]
    [InlineData("TS100", 0x5453, 1, 100)]
    [InlineData("TC100", 0x5443, 1, 100)]
    [InlineData("TN100", 0x544E, 0, 100)]
    [InlineData("CS50",  0x4353, 1, 50)]
    [InlineData("CC50",  0x4343, 1, 50)]
    [InlineData("CN50",  0x434E, 0, 50)]
    public void AnalysisAddress_Valid(string addr, ushort expectedCode, byte expectedType, int expectedAddr)
    {
        var r = MelsecA1EClient.AnalysisAddress(addr);
        Assert.True(r.Success, r.Message);
        Assert.Equal(expectedCode, r.DataCode);
        Assert.Equal(expectedType, r.DataType);
        Assert.Equal(expectedAddr, r.Address);
    }

    [Theory]
    [InlineData("X0",    0x5820, 1, 0)]
    [InlineData("X17",   0x5820, 1, 23)]   // 十六进制 17 = 23
    [InlineData("X10",   0x5820, 1, 16)]   // 不以 0 开头 → 十六进制
    [InlineData("Y0",    0x5920, 1, 0)]
    [InlineData("Y10",   0x5920, 1, 16)]   // 十六进制
    [InlineData("Y017",  0x5920, 1, 15)]   // 八进制 017 = 15
    public void AnalysisAddress_OctalHex(string addr, ushort expectedCode, byte expectedType, int expectedAddr)
    {
        var r = MelsecA1EClient.AnalysisAddress(addr);
        Assert.True(r.Success, r.Message);
        Assert.Equal(expectedCode, r.DataCode);
        Assert.Equal(expectedType, r.DataType);
        Assert.Equal(expectedAddr, r.Address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Z100")]
    [InlineData("T100")]   // T 需要第二字符 S/C/N
    [InlineData("C100")]   // C 需要第二字符 S/C/N
    public void AnalysisAddress_Invalid(string addr)
    {
        var r = MelsecA1EClient.AnalysisAddress(addr);
        Assert.False(r.Success);
    }

    #endregion

    #region 帧构建

    [Fact]
    public void BuildReadCommand_WordRead_D100()
    {
        var r = MelsecA1EClient.BuildReadCommand("D100", 5, false, 0xFF);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(12, r.Content.Length);
        Assert.Equal(1, r.Content[0]);     // 字读取子命令
        Assert.Equal(0xFF, r.Content[1]);  // PLC 编号
        Assert.Equal(10, r.Content[2]);    // 看门狗
        // 地址：100（小端序）
        Assert.Equal(100, r.Content[4]);
        Assert.Equal(0, r.Content[5]);
        // 数据类型码：0x4440（小端序）
        Assert.Equal(0x40, r.Content[8]);
        Assert.Equal(0x44, r.Content[9]);
        // 长度：5
        Assert.Equal(5, r.Content[10]);
        Assert.Equal(0, r.Content[11]);
    }

    [Fact]
    public void BuildReadCommand_BitRead_M100()
    {
        var r = MelsecA1EClient.BuildReadCommand("M100", 10, true, 0xFF);
        Assert.True(r.IsSuccess);
        Assert.Equal(0, r.Content[0]);     // 位读取子命令
        // M 数据类型码：0x4D20（小端序）
        Assert.Equal(0x20, r.Content[8]);
        Assert.Equal(0x4D, r.Content[9]);
        // 地址：100
        Assert.Equal(100, r.Content[4]);
        // 长度：10 个位
        Assert.Equal(10, r.Content[10]);
    }

    [Fact]
    public void BuildWriteWordCommand_D100()
    {
        var data = new byte[] { 0x12, 0x34 };
        var r = MelsecA1EClient.BuildWriteWordCommand("D100", data, 0xFF);
        Assert.True(r.IsSuccess);
        Assert.Equal(14, r.Content.Length); // 12 + 2
        Assert.Equal(3, r.Content[0]);      // 字写入子命令
        Assert.Equal(1, r.Content[10]);     // 1 个字
        Assert.Equal(0x12, r.Content[12]);  // 数据
        Assert.Equal(0x34, r.Content[13]);
    }

    [Fact]
    public void BuildWriteBoolCommand_M100()
    {
        var bools = new bool[] { true, false, true, true };
        var r = MelsecA1EClient.BuildWriteBoolCommand("M100", bools, 0xFF);
        Assert.True(r.IsSuccess);
        Assert.Equal(2, r.Content[0]);      // 位写入子命令
        Assert.Equal(4, r.Content[10]);     // 4 个位
        // 打包：[0]=true(0x10),[1]=false → 0x10; [2]=true(0x10),[3]=true(0x01) → 0x11
        Assert.Equal(0x10, r.Content[12]);
        Assert.Equal(0x11, r.Content[13]);
    }

    #endregion

    #region 响应校验

    [Fact]
    public void CheckResponse_Success()
    {
        var resp = new byte[] { 1, 0, 0x12, 0x34 };
        var r = MelsecA1EClient.CheckResponse(resp);
        Assert.True(r.IsSuccess);
    }

    [Fact]
    public void CheckResponse_Error()
    {
        var resp = new byte[] { 1, 0x55 };
        var r = MelsecA1EClient.CheckResponse(resp);
        Assert.False(r.IsSuccess);
        Assert.Contains("0x55", r.Message);
    }

    [Fact]
    public void CheckResponse_ExtendedError()
    {
        var resp = new byte[] { 1, 0x5B, 0x42 };
        var r = MelsecA1EClient.CheckResponse(resp);
        Assert.False(r.IsSuccess);
        Assert.Contains("0x42", r.Message);
    }

    [Fact]
    public void CheckResponse_TooShort()
    {
        var r = MelsecA1EClient.CheckResponse(new byte[1]);
        Assert.False(r.IsSuccess);
    }

    #endregion

    #region 数据提取

    [Fact]
    public void ExtractActualData_WordMode()
    {
        var resp = new byte[] { 1, 0, 0x12, 0x34, 0x56, 0x78 };
        var data = MelsecA1EClient.ExtractActualData(resp, false);
        Assert.Equal(4, data.Length);
        Assert.Equal(0x12, data[0]);
        Assert.Equal(0x34, data[1]);
        Assert.Equal(0x56, data[2]);
        Assert.Equal(0x78, data[3]);
    }

    [Fact]
    public void ExtractActualData_BitMode()
    {
        // 1 个数据字节 = 2 个位：0x11 = bit0=1, bit1=1
        var resp = new byte[] { 0, 0, 0x11 };
        var bits = MelsecA1EClient.ExtractActualData(resp, true);
        Assert.Equal(2, bits.Length);
        Assert.Equal(1, bits[0]);
        Assert.Equal(1, bits[1]);
    }

    [Fact]
    public void ExtractActualData_BitMode_Multiple()
    {
        // 2 个数据字节 = 4 个位：0x10, 0x01
        var resp = new byte[] { 0, 0, 0x10, 0x01 };
        var bits = MelsecA1EClient.ExtractActualData(resp, true);
        Assert.Equal(4, bits.Length);
        Assert.Equal(1, bits[0]);  // 0x10 高半字节
        Assert.Equal(0, bits[1]);  // 0x10 低半字节
        Assert.Equal(0, bits[2]);  // 0x01 高半字节
        Assert.Equal(1, bits[3]);  // 0x01 低半字节
    }

    #endregion

    #region 虚拟服务器生命周期

    [Fact]
    public void Server_StartStop()
    {
        int port = PortBase;
        var server = new MelsecA1EVirtualServer(port);
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
        var server = new MelsecA1EVirtualServer(port);
        server.SetDWord(100, 0x1234);
        server.Start();

        try
        {
            var client = new MelsecA1EClient("127.0.0.1", port);
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
        var server = new MelsecA1EVirtualServer(port);
        server.Start();

        try
        {
            var client = new MelsecA1EClient("127.0.0.1", port);
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
        var server = new MelsecA1EVirtualServer(port);
        server.SetDWord(50, 0x5678);
        server.Start();

        try
        {
            var client = new MelsecA1EClient("127.0.0.1", port);
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
    public void Client_ReadInt32_D()
    {
        int port = PortBase + 4;
        var server = new MelsecA1EVirtualServer(port);
        server.SetDWord(300, 0x0102);
        server.SetDWord(301, 0x0304);
        server.Start();

        try
        {
            var client = new MelsecA1EClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadInt32("D300");
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
    public void Client_ReadWrite_R()
    {
        int port = PortBase + 5;
        var server = new MelsecA1EVirtualServer(port);
        server.Start();

        try
        {
            var client = new MelsecA1EClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 写
            Assert.True(client.Write("R10", unchecked((short)0x9988)).IsSuccess);

            // 读
            var r = client.ReadInt16("R10");
            Assert.True(r.IsSuccess);
            Assert.Equal(unchecked((short)0x9988), r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadBool_M()
    {
        int port = PortBase + 6;
        var server = new MelsecA1EVirtualServer(port);
        server.SetM(100, 1);
        server.SetM(101, 0);
        server.SetM(102, 1);
        server.Start();

        try
        {
            var client = new MelsecA1EClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var b0 = client.ReadBool("M100");
            Assert.True(b0.IsSuccess);
            Assert.True(b0.Content);

            var b1 = client.ReadBool("M101");
            Assert.True(b1.IsSuccess);
            Assert.False(b1.Content);

            var b2 = client.ReadBool("M102");
            Assert.True(b2.IsSuccess);
            Assert.True(b2.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_WriteBool_M()
    {
        int port = PortBase + 7;
        var server = new MelsecA1EVirtualServer(port);
        server.Start();

        try
        {
            var client = new MelsecA1EClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 写 true
            Assert.True(client.Write("M200", true).IsSuccess);

            var b = client.ReadBool("M200");
            Assert.True(b.IsSuccess);
            Assert.True(b.Content);

            // 写 false
            Assert.True(client.Write("M200", false).IsSuccess);

            var b2 = client.ReadBool("M200");
            Assert.True(b2.IsSuccess);
            Assert.False(b2.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadBools_Batch_M()
    {
        int port = PortBase + 8;
        var server = new MelsecA1EVirtualServer(port);
        server.SetM(300, 1);
        server.SetM(301, 0);
        server.SetM(302, 1);
        server.SetM(303, 1);
        server.Start();

        try
        {
            var client = new MelsecA1EClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var bools = client.ReadBools("M300", 4);
            Assert.True(bools.IsSuccess, bools.Message);
            Assert.Equal(4, bools.Content.Length);
            Assert.True(bools.Content[0]);
            Assert.False(bools.Content[1]);
            Assert.True(bools.Content[2]);
            Assert.True(bools.Content[3]);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_WriteBools_M()
    {
        int port = PortBase + 9;
        var server = new MelsecA1EVirtualServer(port);
        server.Start();

        try
        {
            var client = new MelsecA1EClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var values = new bool[] { true, false, true, true, false };
            Assert.True(client.WriteBools("M400", values).IsSuccess);

            var read = client.ReadBools("M400", 5);
            Assert.True(read.IsSuccess);
            Assert.Equal(values, read.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadString_D()
    {
        int port = PortBase + 10;
        var server = new MelsecA1EVirtualServer(port);
        server.SetDBytes(0, new byte[] { (byte)'H', (byte)'i', (byte)'!', (byte)0 });
        server.Start();

        try
        {
            var client = new MelsecA1EClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadString("D0", 4);
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
    public void Client_ReadFloat_D()
    {
        int port = PortBase + 11;
        var server = new MelsecA1EVirtualServer(port);
        float expected = 3.14f;
        // 大端序存储 float
        int intBits = BitConverter.ToInt32(BitConverter.GetBytes(expected), 0);
        server.SetDBytes(500, new byte[] {
            (byte)(intBits >> 24), (byte)(intBits >> 16),
            (byte)(intBits >> 8), (byte)(intBits & 0xFF)
        });
        server.Start();

        try
        {
            var client = new MelsecA1EClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadFloat("D500");
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
    public void Client_ReadWrite_M_WordAccess()
    {
        // 通过 ReadInt16 读取 M 区域（位类型区域的字读取）
        int port = PortBase + 12;
        var server = new MelsecA1EVirtualServer(port);
        // 设置 M600=1 (bit 0 of first word)
        server.SetM(600, 1);
        server.Start();

        try
        {
            var client = new MelsecA1EClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // ReadInt16 读取 2 字节 = 1 个字
            var r = client.ReadInt16("M600");
            Assert.True(r.IsSuccess, r.Message);
            // M600=1 → bit 0 → word 值为 1
            Assert.Equal((short)1, r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_PLCNumber_Custom()
    {
        int port = PortBase + 13;
        var server = new MelsecA1EVirtualServer(port);
        server.SetDWord(0, 0x1111);
        server.Start();

        try
        {
            var client = new MelsecA1EClient("127.0.0.1", port);
            client.PLCNumber = 0x01;
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadInt16("D0");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(0x1111, (ushort)r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    #endregion
}
