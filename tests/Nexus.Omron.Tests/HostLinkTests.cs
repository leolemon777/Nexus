using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nexus;
using Nexus.Omron;

namespace Nexus.Omron.Tests;

public class HostLinkTests
{
    private const int PortBase = 19700;

    private static byte[] BuildHostLinkResponse(string body, bool badFcs = false)
    {
        string header = "@00FA30" + "00000000";
        string raw = header + body;

        byte fcs = 0;
        byte[] rawBytes = Encoding.ASCII.GetBytes(raw);
        foreach (byte b in rawBytes)
            fcs ^= b;

        if (badFcs)
            fcs ^= 0x01;

        return Encoding.ASCII.GetBytes(raw + fcs.ToString("X2") + "*\r");
    }

    private static byte[] BuildCModeResponse(string headerCode, string responseCode, string text = "", bool badFcs = false)
    {
        string raw = "@00" + headerCode + responseCode + text;

        byte fcs = 0;
        byte[] rawBytes = Encoding.ASCII.GetBytes(raw);
        foreach (byte b in rawBytes)
            fcs ^= b;

        if (badFcs)
            fcs ^= 0x01;

        return Encoding.ASCII.GetBytes(raw + fcs.ToString("X2") + "*\r");
    }

    private sealed class CModeFakeSerialPort : ISerialPort
    {
        private readonly Queue<byte> _readQueue = new Queue<byte>();

        public string PortName { get; set; } = "COM_CMODE_TEST";
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 7;
        public StopBits StopBits { get; set; } = StopBits.Two;
        public Parity Parity { get; set; } = Parity.Even;
        public int ReadTimeout { get; set; } = 5000;
        public int WriteTimeout { get; set; } = 5000;
        public bool IsOpen { get; private set; }
        public bool DtrEnable { get; set; }
        public bool RtsEnable { get; set; }
        public List<byte[]> Writes { get; } = new List<byte[]>();

        public void LoadReadBytes(params byte[] data)
        {
            foreach (byte b in data)
                _readQueue.Enqueue(b);
        }

        public void Open() => IsOpen = true;
        public void Close() => IsOpen = false;

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_readQueue.Count == 0) return 0;

            int read = 0;
            while (read < count && _readQueue.Count > 0)
            {
                byte b = _readQueue.Dequeue();
                buffer[offset + read++] = b;
                if (b == 0x0D)
                    break;
            }

            return read;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            byte[] data = new byte[count];
            Buffer.BlockCopy(buffer, offset, data, 0, count);
            Writes.Add(data);
        }

        public void Dispose() => Close();
    }

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

    [Fact]
    public void ParseResponse_InvalidStart_ReturnsFailure()
    {
        byte[] response = BuildHostLinkResponse("010100001234");
        response[0] = (byte)'#';

        var result = OmronHostLinkClient.ParseResponse(response);

        Assert.False(result.IsSuccess);
        Assert.Contains("帧头", result.Message);
    }

    [Fact]
    public void ParseResponse_InvalidTrailer_ReturnsFailure()
    {
        byte[] response = BuildHostLinkResponse("010100001234");
        response[response.Length - 2] = (byte)'!';

        var result = OmronHostLinkClient.ParseResponse(response);

        Assert.False(result.IsSuccess);
        Assert.Contains("帧尾", result.Message);
    }

    [Fact]
    public void ParseResponse_BadFcs_ReturnsFailure()
    {
        byte[] response = BuildHostLinkResponse("010100001234", badFcs: true);

        var result = OmronHostLinkClient.ParseResponse(response);

        Assert.False(result.IsSuccess);
        Assert.Contains("FCS", result.Message);
    }

    [Fact]
    public void ParseResponse_InvalidFcsHex_ReturnsFailure()
    {
        byte[] response = BuildHostLinkResponse("010100001234");
        response[response.Length - 4] = (byte)'Z';

        var result = OmronHostLinkClient.ParseResponse(response);

        Assert.False(result.IsSuccess);
        Assert.Contains("FCS 格式", result.Message);
    }

    [Fact]
    public void ParseResponse_InvalidDataHex_ReturnsFailure()
    {
        byte[] response = BuildHostLinkResponse("01010000ZZ");

        var result = OmronHostLinkClient.ParseResponse(response);

        Assert.False(result.IsSuccess);
        Assert.Contains("非法十六进制", result.Message);
    }

    [Fact]
    public void ParseResponse_OddDataHexLength_ReturnsFailure()
    {
        byte[] response = BuildHostLinkResponse("010100001");

        var result = OmronHostLinkClient.ParseResponse(response);

        Assert.False(result.IsSuccess);
        Assert.Contains("长度", result.Message);
    }

    #endregion

    #region C-Mode ParseResponse 响应解析

    [Fact]
    public void CMode_PackFrame_UsesTwoDigitStationAndNoTrailingNulls()
    {
        using var client = new OmronHostLinkCModeClient(new CModeFakeSerialPort());
        byte[] frame = client.PackFrame(Encoding.ASCII.GetBytes("RD"), Encoding.ASCII.GetBytes("ABC"));

        Assert.Equal(12, frame.Length);
        Assert.Equal((byte)'@', frame[0]);
        Assert.Equal((byte)'0', frame[1]);
        Assert.Equal((byte)'0', frame[2]);
        Assert.Equal((byte)'R', frame[3]);
        Assert.Equal((byte)'D', frame[4]);
        Assert.Equal((byte)'*', frame[frame.Length - 2]);
        Assert.Equal(0x0D, frame[frame.Length - 1]);

        byte expectedFcs = 0;
        for (int i = 0; i < frame.Length - 4; i++)
            expectedFcs ^= frame[i];

        byte actualFcs = Convert.ToByte(Encoding.ASCII.GetString(frame, frame.Length - 4, 2), 16);
        Assert.Equal(expectedFcs, actualFcs);
    }

    [Fact]
    public void CModeClient_ReadInt16_UsesSerialFrameAndParsesResponse()
    {
        using var port = new CModeFakeSerialPort();
        port.Open();
        port.LoadReadBytes(BuildCModeResponse("RD", "0000", "1234"));
        using var client = new OmronHostLinkCModeClient(port, timeout: 100);

        var result = client.ReadInt16("D100");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal((short)0x1234, result.Content);
        Assert.Single(port.Writes);

        byte[] request = port.Writes[0];
        Assert.Equal(16, request.Length);
        Assert.Equal((byte)'@', request[0]);
        Assert.Equal((byte)'0', request[1]);
        Assert.Equal((byte)'0', request[2]);
        Assert.Equal((byte)'R', request[3]);
        Assert.Equal((byte)'D', request[4]);
        Assert.Equal((byte)'*', request[request.Length - 2]);
        Assert.Equal(0x0D, request[request.Length - 1]);
    }

    [Fact]
    public void CModeClient_WriteInt16_UsesSerialFrameAndAcceptsSuccessResponse()
    {
        using var port = new CModeFakeSerialPort();
        port.Open();
        port.LoadReadBytes(BuildCModeResponse("WD", "0000"));
        using var client = new OmronHostLinkCModeClient(port, timeout: 100);

        var result = client.Write("D100", (short)0x1234);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Single(port.Writes);

        byte[] request = port.Writes[0];
        Assert.Equal(20, request.Length);
        Assert.Equal((byte)'@', request[0]);
        Assert.Equal((byte)'0', request[1]);
        Assert.Equal((byte)'0', request[2]);
        Assert.Equal((byte)'W', request[3]);
        Assert.Equal((byte)'D', request[4]);
        Assert.Equal((byte)'*', request[request.Length - 2]);
        Assert.Equal(0x0D, request[request.Length - 1]);
    }

    [Fact]
    public void CModeClient_ReadBool_BitAddress_UsesRequestedBit()
    {
        using var port = new CModeFakeSerialPort();
        port.Open();
        port.LoadReadBytes(BuildCModeResponse("RD", "0000", "0008"));
        using var client = new OmronHostLinkCModeClient(port, timeout: 100);

        var result = client.ReadBool("D100.03");

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(result.Content);
        Assert.Single(port.Writes);
    }

    [Fact]
    public void CModeClient_WriteBool_BitAddress_PreservesOtherBits()
    {
        using var port = new CModeFakeSerialPort();
        port.Open();
        port.LoadReadBytes(BuildCModeResponse("RD", "0000", "00A0"));
        port.LoadReadBytes(BuildCModeResponse("WD", "0000"));
        using var client = new OmronHostLinkCModeClient(port, timeout: 100);

        var result = client.Write("D100.03", true);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, port.Writes.Count);

        byte[] writeRequest = port.Writes[1];
        Assert.Equal((byte)'W', writeRequest[3]);
        Assert.Equal((byte)'D', writeRequest[4]);
        Assert.Equal("00A8", Encoding.ASCII.GetString(writeRequest, 12, 4));
    }

    [Fact]
    public void CMode_ParseResponse_Success_WithData()
    {
        byte[] response = BuildCModeResponse("RD", "0000", "1234");

        var result = OmronHostLinkCModeClient.ParseResponse(response);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(new byte[] { 0x12, 0x34 }, result.Content);
    }

    [Fact]
    public void CMode_ParseResponse_Success_NoData()
    {
        byte[] response = BuildCModeResponse("WD", "0000");

        var result = OmronHostLinkCModeClient.ParseResponse(response);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Empty(result.Content);
    }

    [Fact]
    public void CMode_ParseResponse_ErrorCode_ReturnsFailure()
    {
        byte[] response = BuildCModeResponse("RD", "0301");

        var result = OmronHostLinkCModeClient.ParseResponse(response);

        Assert.False(result.IsSuccess);
        Assert.Contains("0x0301", result.Message);
    }

    [Fact]
    public void CMode_ParseResponse_InvalidStart_ReturnsFailure()
    {
        byte[] response = BuildCModeResponse("RD", "0000", "1234");
        response[0] = (byte)'#';

        var result = OmronHostLinkCModeClient.ParseResponse(response);

        Assert.False(result.IsSuccess);
        Assert.Contains("帧头", result.Message);
    }

    [Fact]
    public void CMode_ParseResponse_InvalidTrailer_ReturnsFailure()
    {
        byte[] response = BuildCModeResponse("RD", "0000", "1234");
        response[response.Length - 2] = (byte)'!';

        var result = OmronHostLinkCModeClient.ParseResponse(response);

        Assert.False(result.IsSuccess);
        Assert.Contains("帧尾", result.Message);
    }

    [Fact]
    public void CMode_ParseResponse_BadFcs_ReturnsFailure()
    {
        byte[] response = BuildCModeResponse("RD", "0000", "1234", badFcs: true);

        var result = OmronHostLinkCModeClient.ParseResponse(response);

        Assert.False(result.IsSuccess);
        Assert.Contains("FCS", result.Message);
    }

    [Fact]
    public void CMode_ParseResponse_InvalidFcsHex_ReturnsFailure()
    {
        byte[] response = BuildCModeResponse("RD", "0000", "1234");
        response[response.Length - 4] = (byte)'Z';

        var result = OmronHostLinkCModeClient.ParseResponse(response);

        Assert.False(result.IsSuccess);
        Assert.Contains("FCS 格式", result.Message);
    }

    [Fact]
    public void CMode_ParseResponse_InvalidDataHex_ReturnsFailure()
    {
        byte[] response = BuildCModeResponse("RD", "0000", "ZZ");

        var result = OmronHostLinkCModeClient.ParseResponse(response);

        Assert.False(result.IsSuccess);
        Assert.Contains("非法十六进制", result.Message);
    }

    [Fact]
    public void CMode_ParseResponse_OddDataHexLength_ReturnsFailure()
    {
        byte[] response = BuildCModeResponse("RD", "0000", "1");

        var result = OmronHostLinkCModeClient.ParseResponse(response);

        Assert.False(result.IsSuccess);
        Assert.Contains("长度", result.Message);
    }

    [Fact]
    public void CMode_ParseResponse_TooShort_ReturnsFailure()
    {
        var result = OmronHostLinkCModeClient.ParseResponse(new byte[12]);

        Assert.False(result.IsSuccess);
        Assert.Contains("过短", result.Message);
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

    [Fact]
    public void CModeOverTcpClient_ReadInt16_DM()
    {
        int port = PortBase + 40;
        var server = new OmronHostLinkVirtualServer(port);
        server.SetDmWord(120, 0x2468);
        server.Start();

        try
        {
            using var client = new OmronHostLinkCModeOverTcpClient("127.0.0.1", port, timeout: 1000);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadInt16("D120");

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((short)0x2468, result.Content);
            Assert.True(WaitForConnections(server, 1));
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void CModeOverTcpClient_WriteReadInt16_DM()
    {
        int port = PortBase + 41;
        var server = new OmronHostLinkVirtualServer(port);
        server.Start();

        try
        {
            using var client = new OmronHostLinkCModeOverTcpClient("127.0.0.1", port, timeout: 1000);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var write = client.Write("D121", unchecked((short)0xBEEF));
            Assert.True(write.IsSuccess, write.Message);

            var read = client.ReadInt16("D121");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal(unchecked((short)0xBEEF), read.Content);
            Assert.True(WaitForConnections(server, 1));
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void CModeOverTcpClient_CustomUnitNumber_UsesStationInRequest()
    {
        int port = PortBase + 42;
        var server = new OmronHostLinkVirtualServer(port);
        server.SetDmWord(0, 0x1357);
        server.Start();

        try
        {
            using var client = new OmronHostLinkCModeOverTcpClient("127.0.0.1", port, timeout: 1000)
            {
                UnitNumber = 5
            };
            string? sentHex = null;
            client.OnMessageSent += (_, hex) => sentHex = hex;
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var result = client.ReadInt16("D0");

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((short)0x1357, result.Content);
            Assert.StartsWith("40 30 35 52 44", sentHex ?? string.Empty);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void CModeOverTcpClient_WriteReadBool_BitAddress_PreservesOtherBits()
    {
        int port = PortBase + 43;
        var server = new OmronHostLinkVirtualServer(port);
        server.SetDmWord(130, 0x00A0);
        server.Start();

        try
        {
            using var client = new OmronHostLinkCModeOverTcpClient("127.0.0.1", port, timeout: 1000);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var set = client.Write("D130.03", true);
            Assert.True(set.IsSuccess, set.Message);

            var readSet = client.ReadUInt16("D130");
            Assert.True(readSet.IsSuccess, readSet.Message);
            Assert.Equal((ushort)0x00A8, readSet.Content);

            var bit3 = client.ReadBool("D130.03");
            Assert.True(bit3.IsSuccess, bit3.Message);
            Assert.True(bit3.Content);

            var clear = client.Write("D130.03", false);
            Assert.True(clear.IsSuccess, clear.Message);

            var readClear = client.ReadUInt16("D130");
            Assert.True(readClear.IsSuccess, readClear.Message);
            Assert.Equal((ushort)0x00A0, readClear.Content);
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
        int port = PortBase + 30;
        var server = new OmronHostLinkVirtualServer(port);
        server.SetDmWord(100, 0x1234);
        server.Start();

        try
        {
            using var pool = new OmronHostLinkConnectionPool("127.0.0.1", port);

            var first = pool.ReadInt16("D100");
            Assert.True(first.IsSuccess, first.Message);
            Assert.Equal(0x1234, (ushort)first.Content);

            var second = pool.ReadInt16("D100");
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
    public void ConnectionPool_WriteAndReadWord_RoundTrip()
    {
        int port = PortBase + 31;
        var server = new OmronHostLinkVirtualServer(port);
        server.Start();

        try
        {
            using var pool = new OmronHostLinkConnectionPool("127.0.0.1", port);

            var write = pool.Write("D200", unchecked((short)0xABCD));
            Assert.True(write.IsSuccess, write.Message);

            var read = pool.ReadInt16("D200");
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
    public void ConnectionPool_WriteAndReadBool_RoundTrip()
    {
        int port = PortBase + 32;
        var server = new OmronHostLinkVirtualServer(port);
        server.Start();

        try
        {
            using var pool = new OmronHostLinkConnectionPool("127.0.0.1", port);

            var write = pool.Write("D200.07", true);
            Assert.True(write.IsSuccess, write.Message);

            var read = pool.ReadBool("D200.07");
            Assert.True(read.IsSuccess, read.Message);
            Assert.True(read.Content);

            var clear = pool.Write("D200.07", false);
            Assert.True(clear.IsSuccess, clear.Message);

            var readClear = pool.ReadBool("D200.07");
            Assert.True(readClear.IsSuccess, readClear.Message);
            Assert.False(readClear.Content);

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
        int port = PortBase + 33;
        var server = new OmronHostLinkVirtualServer(port);
        server.Start();

        try
        {
            using var pool = new OmronHostLinkConnectionPool("127.0.0.1", port);

            var write = pool.BatchWrite(new[]
            {
                new KeyValuePair<string, object>("D10", (short)123),
                new KeyValuePair<string, object>("D11", (short)456)
            });
            Assert.True(write.IsSuccess, write.Message);

            var read = pool.BatchRead(new[] { "D10", "D11" });
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal((short)123, read.Content["D10"]);
            Assert.Equal((short)456, read.Content["D11"]);

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
        int port = PortBase + 34;
        var server = new OmronHostLinkVirtualServer(port);
        server.SetDmWord(30, 0x012C);
        server.Start();

        try
        {
            using var pool = new OmronHostLinkConnectionPool("127.0.0.1", port);

            var result = await pool.ExecuteAsync(c => Task.FromResult(c.ReadInt16("D30")));
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((short)300, result.Content);

            var second = await pool.ExecuteAsync(c => Task.FromResult(c.ReadInt16("D30")));
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
    public void ConnectionPool_CustomUnitNumber_ReadsSuccessfully()
    {
        int port = PortBase + 35;
        var server = new OmronHostLinkVirtualServer(port);
        server.SetDmWord(0, 0x1111);
        server.Start();

        try
        {
            using var pool = new OmronHostLinkConnectionPool("127.0.0.1", port, unitNumber: 5);

            var result = pool.ReadInt16("D0");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x1111, (ushort)result.Content);

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
        int port = PortBase + 36;
        var server = new OmronHostLinkVirtualServer(port);
        server.SetDmWord(40, 0x002A);
        server.Start();

        try
        {
            using var pool = new OmronHostLinkConnectionPool("127.0.0.1", port);
            int sent = 0;
            int received = 0;
            pool.OnMessageSent += (_, _) => sent++;
            pool.OnMessageReceived += (_, _) => received++;

            var result = pool.ReadInt16("D40");
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

    private static bool WaitForConnections(OmronHostLinkVirtualServer server, int expected, int timeoutMs = 1000)
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

    #region 批量接口契约

    [Fact]
    public void BatchOperations_EmptyInput_ReturnsError()
    {
        var client = new OmronHostLinkClient("127.0.0.1", 9600);

        Assert.False(client.BatchRead(new string[0]).IsSuccess);
        Assert.False(client.RandomRead(new string[0]).IsSuccess);
        Assert.False(client.BatchWrite(System.Array.Empty<System.Collections.Generic.KeyValuePair<string, object>>()).IsSuccess);
    }

    #endregion
}
