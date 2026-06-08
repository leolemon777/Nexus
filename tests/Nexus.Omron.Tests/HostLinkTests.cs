using System;
using System.Text;
using Xunit;
using Nexus.Omron;

namespace Nexus.Omron.Tests;

public class HostLinkTests
{
    private const int PortBase = 19700;

    #region ASCII Hex 辅助方法

    [Fact]
    public void BytesToAsciiHex_Simple()
    {
        byte[] input = { 0x01, 0x02, 0xFF, 0x0A };
        byte[] result = OmronHostLinkClient.BytesToAsciiHex(input);
        string hex = Encoding.ASCII.GetString(result);
        Assert.Equal("0102FF0A", hex);
    }

    [Fact]
    public void AsciiHexToBytes_Simple()
    {
        byte[] result = OmronHostLinkClient.AsciiHexToBytes("0102FF0A");
        Assert.Equal(new byte[] { 0x01, 0x02, 0xFF, 0x0A }, result);
    }

    [Fact]
    public void AsciiHexToBytes_Lowercase()
    {
        byte[] result = OmronHostLinkClient.AsciiHexToBytes("0a0b0c");
        Assert.Equal(new byte[] { 0x0A, 0x0B, 0x0C }, result);
    }

    [Fact]
    public void ToAsciiHexHighLow_AllNibbles()
    {
        // 0x00 → "00", 0x0F → "0F", 0xFF → "FF", 0xA5 → "A5"
        Assert.Equal((byte)'0', OmronHostLinkClient.ToAsciiHexHigh(0x00));
        Assert.Equal((byte)'0', OmronHostLinkClient.ToAsciiHexLow(0x00));
        Assert.Equal((byte)'F', OmronHostLinkClient.ToAsciiHexHigh(0xFF));
        Assert.Equal((byte)'F', OmronHostLinkClient.ToAsciiHexLow(0xFF));
        Assert.Equal((byte)'A', OmronHostLinkClient.ToAsciiHexHigh(0xA5));
        Assert.Equal((byte)'5', OmronHostLinkClient.ToAsciiHexLow(0xA5));
    }

    #endregion

    #region PackCommand 帧构建

    [Fact]
    public void PackCommand_BasicStructure()
    {
        var client = new OmronHostLinkClient("127.0.0.1", 9600);
        byte[] finsCmd = { 0x01, 0x01, 0x82, 0x00, 0x00, 0x64, 0x00, 0x00, 0x02 };

        byte[] frame = client.PackCommand(finsCmd);
        string frameStr = Encoding.ASCII.GetString(frame);

        // 帧以 @ 开头
        Assert.Equal('@', frameStr[0]);
        // 包含 FA
        Assert.Contains("FA", frameStr);
        // 以 *\r 结尾
        Assert.True(frame[frame.Length - 2] == (byte)("*")[0]);
        Assert.True(frame[frame.Length - 1] == 0x0D);
        // 包含 FINS 命令的 ASCII hex
        Assert.Contains("0101", frameStr);
        Assert.Contains("8200", frameStr);
    }

    [Fact]
    public void PackCommand_UnitNumber()
    {
        var client = new OmronHostLinkClient("127.0.0.1", 9600);
        client.UnitNumber = 5;
        byte[] finsCmd = { 0x01, 0x01 };

        byte[] frame = client.PackCommand(finsCmd);
        string frameStr = Encoding.ASCII.GetString(frame);

        // unit number = 5 → "05"
        Assert.Equal('0', frameStr[1]);
        Assert.Equal('5', frameStr[2]);
    }

    [Fact]
    public void PackCommand_FCS_Valid()
    {
        var client = new OmronHostLinkClient("127.0.0.1", 9600);
        byte[] finsCmd = { 0x01, 0x01, 0x82, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 };

        byte[] frame = client.PackCommand(finsCmd);

        // 手动验证 FCS：XOR [0..len-5]
        byte expected = 0;
        for (int i = 0; i < frame.Length - 4; i++)
            expected ^= frame[i];

        byte fcsHigh = frame[frame.Length - 4];
        byte fcsLow  = frame[frame.Length - 3];
        string fcsStr = Encoding.ASCII.GetString(new[] { fcsHigh, fcsLow });
        byte actualFcs = Convert.ToByte(fcsStr, 16);
        Assert.Equal(expected, actualFcs);
    }

    [Fact]
    public void PackCommand_ResponseWaitTime()
    {
        var client = new OmronHostLinkClient("127.0.0.1", 9600);
        client.ResponseWaitTime = (byte)'F'; // 最大等待
        byte[] finsCmd = { 0x01, 0x01 };

        byte[] frame = client.PackCommand(finsCmd);
        string frameStr = Encoding.ASCII.GetString(frame);

        // wait time 在 FA 之后
        int faIdx = frameStr.IndexOf("FA");
        Assert.True(faIdx >= 0);
        Assert.Equal('F', frameStr[faIdx + 2]);
    }

    #endregion

    #region ParseResponse 响应解析

    [Fact]
    public void ParseResponse_Success_WithData()
    {
        // 构建一个有效的响应帧：15 头 + 0101(cmd) + 0000(endCode) + 1234(data) + FCS + * + CR
        string header = "@00FA30" + "00000000"; // @ + unit(00) + FA + wait_hex(30) + ICF(00) + DA2(00) + SA2(00) + SID(00) = 15 chars
        string body = "0101" + "0000" + "1234";  // cmdCode + endCode + data
        string raw = header + body;

        // 计算 FCS
        byte fcs = 0;
        byte[] rawBytes = Encoding.ASCII.GetBytes(raw);
        foreach (byte b in rawBytes) fcs ^= b;
        string fcsStr = fcs.ToString("X2");

        string fullFrame = raw + fcsStr + "*\r";
        byte[] response = Encoding.ASCII.GetBytes(fullFrame);

        var result = OmronHostLinkClient.ParseResponse(response);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, result.Content.Length);
        Assert.Equal(0x12, result.Content[0]);
        Assert.Equal(0x34, result.Content[1]);
    }

    [Fact]
    public void ParseResponse_Success_NoData()
    {
        string header = "@00FA30" + "00000000";
        string body = "0102" + "0000"; // write response: cmdCode + endCode
        string raw = header + body;

        byte fcs = 0;
        byte[] rawBytes = Encoding.ASCII.GetBytes(raw);
        foreach (byte b in rawBytes) fcs ^= b;

        string fullFrame = raw + fcs.ToString("X2") + "*\r";
        byte[] response = Encoding.ASCII.GetBytes(fullFrame);

        var result = OmronHostLinkClient.ParseResponse(response);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Empty(result.Content);
    }

    [Fact]
    public void ParseResponse_Error()
    {
        string header = "@00FA30" + "00000000";
        string body = "0101" + "0301"; // endCode = 0x0301
        string raw = header + body;

        byte fcs = 0;
        byte[] rawBytes = Encoding.ASCII.GetBytes(raw);
        foreach (byte b in rawBytes) fcs ^= b;

        string fullFrame = raw + fcs.ToString("X2") + "*\r";
        byte[] response = Encoding.ASCII.GetBytes(fullFrame);

        var result = OmronHostLinkClient.ParseResponse(response);
        Assert.False(result.IsSuccess);
        Assert.Contains("0x0301", result.Message);
    }

    [Fact]
    public void ParseResponse_TooShort()
    {
        var r = OmronHostLinkClient.ParseResponse(new byte[10]);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void ParseResponse_NullInput()
    {
        var r = OmronHostLinkClient.ParseResponse(null!);
        Assert.False(r.IsSuccess);
    }

    #endregion

    #region 虚拟服务器生命周期

    [Fact]
    public void Server_StartStop()
    {
        int port = PortBase;
        var server = new OmronHostLinkVirtualServer(port);
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
    public void Client_ReadInt16_DM()
    {
        int port = PortBase + 1;
        var server = new OmronHostLinkVirtualServer(port);
        server.SetDmWord(100, 0x1234);
        server.Start();

        try
        {
            var client = new OmronHostLinkClient("127.0.0.1", port);
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
    public void Client_WriteReadInt16_DM()
    {
        int port = PortBase + 2;
        var server = new OmronHostLinkVirtualServer(port);
        server.Start();

        try
        {
            var client = new OmronHostLinkClient("127.0.0.1", port);
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
    public void Client_ReadUInt16_DM()
    {
        int port = PortBase + 3;
        var server = new OmronHostLinkVirtualServer(port);
        server.SetDmWord(50, 0x5678);
        server.Start();

        try
        {
            var client = new OmronHostLinkClient("127.0.0.1", port);
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
    public void Client_ReadInt32_DM()
    {
        int port = PortBase + 4;
        var server = new OmronHostLinkVirtualServer(port);
        server.SetDmWord(300, 0x0102);
        server.SetDmWord(301, 0x0304);
        server.Start();

        try
        {
            var client = new OmronHostLinkClient("127.0.0.1", port);
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
    public void Client_ReadWrite_CIO()
    {
        int port = PortBase + 5;
        var server = new OmronHostLinkVirtualServer(port);
        server.Start();

        try
        {
            var client = new OmronHostLinkClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.True(client.Write("CIO100", unchecked((short)0x9988)).IsSuccess);

            var r = client.ReadInt16("CIO100");
            Assert.True(r.IsSuccess);
            Assert.Equal(unchecked(unchecked((short)0x9988)), r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadWrite_WR()
    {
        int port = PortBase + 6;
        var server = new OmronHostLinkVirtualServer(port);
        server.Start();

        try
        {
            var client = new OmronHostLinkClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.True(client.Write("W50", (short)0x1111).IsSuccess);

            var r = client.ReadInt16("W50");
            Assert.True(r.IsSuccess);
            Assert.Equal((short)0x1111, r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadWrite_HR()
    {
        int port = PortBase + 7;
        var server = new OmronHostLinkVirtualServer(port);
        server.Start();

        try
        {
            var client = new OmronHostLinkClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.True(client.Write("H30", (short)0x2222).IsSuccess);

            var r = client.ReadInt16("H30");
            Assert.True(r.IsSuccess);
            Assert.Equal((short)0x2222, r.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadBool_DM()
    {
        int port = PortBase + 8;
        var server = new OmronHostLinkVirtualServer(port);
        server.SetDmBit(100, 3, true);
        server.SetDmBit(100, 5, false);
        server.Start();

        try
        {
            var client = new OmronHostLinkClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var b3 = client.ReadBool("D100.03");
            Assert.True(b3.IsSuccess);
            Assert.True(b3.Content);

            var b5 = client.ReadBool("D100.05");
            Assert.True(b5.IsSuccess);
            Assert.False(b5.Content);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_WriteBool_DM()
    {
        int port = PortBase + 9;
        var server = new OmronHostLinkVirtualServer(port);
        server.Start();

        try
        {
            var client = new OmronHostLinkClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // 写 true
            Assert.True(client.Write("D200.07", true).IsSuccess);
            var b = client.ReadBool("D200.07");
            Assert.True(b.IsSuccess);
            Assert.True(b.Content);

            // 写 false
            Assert.True(client.Write("D200.07", false).IsSuccess);
            var b2 = client.ReadBool("D200.07");
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
    public void Client_ReadString_DM()
    {
        int port = PortBase + 10;
        var server = new OmronHostLinkVirtualServer(port);
        server.SetDmBytes(0, new byte[] { (byte)'H', (byte)'i', (byte)'!', (byte)0 });
        server.Start();

        try
        {
            var client = new OmronHostLinkClient("127.0.0.1", port);
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
    public void Client_ReadFloat_DM()
    {
        int port = PortBase + 11;
        var server = new OmronHostLinkVirtualServer(port);
        float expected = 3.14f;
        int intBits = BitConverter.ToInt32(BitConverter.GetBytes(expected), 0);
        server.SetDmBytes(500, new byte[] {
            (byte)(intBits >> 24), (byte)(intBits >> 16),
            (byte)(intBits >> 8), (byte)(intBits & 0xFF)
        });
        server.Start();

        try
        {
            var client = new OmronHostLinkClient("127.0.0.1", port);
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
    public void Client_UnitNumber_Custom()
    {
        int port = PortBase + 12;
        var server = new OmronHostLinkVirtualServer(port);
        server.SetDmWord(0, 0x1111);
        server.Start();

        try
        {
            var client = new OmronHostLinkClient("127.0.0.1", port);
            client.UnitNumber = 5;
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

    [Fact]
    public void Client_ReadWriteInt64_DM()
    {
        int port = PortBase + 13;
        var server = new OmronHostLinkVirtualServer(port);
        server.Start();

        try
        {
            var client = new OmronHostLinkClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            long expected = 0x0102030405060708;
            Assert.True(client.Write("D600", expected).IsSuccess);

            var r = client.ReadInt64("D600");
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
    public void Client_ReadBool_CIO()
    {
        int port = PortBase + 14;
        var server = new OmronHostLinkVirtualServer(port);
        server.SetCioBit(50, 0, true);
        server.SetCioBit(50, 15, true);
        server.SetCioBit(50, 7, false);
        server.Start();

        try
        {
            var client = new OmronHostLinkClient("127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var b0 = client.ReadBool("CIO50.00");
            Assert.True(b0.IsSuccess);
            Assert.True(b0.Content);

            var b15 = client.ReadBool("CIO50.15");
            Assert.True(b15.IsSuccess);
            Assert.True(b15.Content);

            var b7 = client.ReadBool("CIO50.07");
            Assert.True(b7.IsSuccess);
            Assert.False(b7.Content);
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
        // 非持久连接模式：每次操作后自动断开
        int port = PortBase + 15;
        var server = new OmronHostLinkVirtualServer(port);
        server.Start();

        try
        {
            var client = new OmronHostLinkClient("127.0.0.1", port);
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

    #endregion
}
