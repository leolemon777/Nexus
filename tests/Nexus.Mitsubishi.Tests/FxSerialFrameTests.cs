using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Nexus;
using Xunit;
using Nexus.Mitsubishi;

namespace Nexus.Mitsubishi.Tests;

/// <summary>
/// FX 协议整合测试 — 覆盖 FxLinkClient (计算机链接协议) 和 FxFrameBuilder (编程口帧构建)。
/// FxSerialClient 需要真实/模拟串口硬件，此处仅测试帧构建和离线客户端逻辑。
/// </summary>
public sealed class FxSerialFrameTests
{
    private sealed class FxFakeSerialPort : ISerialPort
    {
        private readonly Queue<byte> _readQueue = new Queue<byte>();

        public string PortName { get; set; } = "COM_FX_TEST";
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 7;
        public StopBits StopBits { get; set; } = StopBits.One;
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
                buffer[offset + read++] = _readQueue.Dequeue();
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

    private sealed class DuplexStream : Stream
    {
        private readonly Queue<byte> _reads = new Queue<byte>();

        public DuplexStream(params byte[] reads)
        {
            foreach (byte b in reads)
                _reads.Enqueue(b);
        }

        public MemoryStream Written { get; } = new MemoryStream();
        public byte[] WrittenBytes => Written.ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_reads.Count == 0) return 0;
            int read = 0;
            while (read < count && _reads.Count > 0)
                buffer[offset + read++] = _reads.Dequeue();
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    }

    private static byte[] BuildFxResponse(string hexData)
    {
        string body = "0D0000" + hexData;
        byte[] bodyBytes = Encoding.ASCII.GetBytes(body);
        byte[] frame = new byte[1 + bodyBytes.Length + 1 + 2];
        frame[0] = 0x02;
        Buffer.BlockCopy(bodyBytes, 0, frame, 1, bodyBytes.Length);
        frame[frame.Length - 3] = 0x03;

        int sum = 0;
        for (int i = 0; i < frame.Length - 2; i++)
            sum += frame[i];
        byte[] sumBytes = Encoding.ASCII.GetBytes((sum & 0xFF).ToString("X2"));
        frame[frame.Length - 2] = sumBytes[0];
        frame[frame.Length - 1] = sumBytes[1];
        return frame;
    }

    private static byte[] WithHandshakeAck(byte[] response)
    {
        byte[] data = new byte[1 + response.Length];
        data[0] = 0x06;
        Buffer.BlockCopy(response, 0, data, 1, response.Length);
        return data;
    }

    private static byte[] BuildFxLinkDataResponse(string data, bool badSum = false)
    {
        byte[] dataBytes = Encoding.ASCII.GetBytes(data);
        byte[] frame = new byte[1 + dataBytes.Length + 1 + 2];
        frame[0] = 0x02;
        Buffer.BlockCopy(dataBytes, 0, frame, 1, dataBytes.Length);
        frame[frame.Length - 3] = 0x03;

        int sum = 0;
        for (int i = 1; i <= dataBytes.Length + 1; i++)
            sum += frame[i];
        if (badSum) sum++;

        byte[] sumBytes = Encoding.ASCII.GetBytes((sum & 0xFF).ToString("X2"));
        frame[frame.Length - 2] = sumBytes[0];
        frame[frame.Length - 1] = sumBytes[1];
        return frame;
    }

    private static byte[] BuildFxLinkRequest(byte station, string cmdAndData)
    {
        string body = station.ToString("D2") + cmdAndData;
        byte sum = 0;
        foreach (byte b in Encoding.ASCII.GetBytes(body))
            sum += b;
        return Encoding.ASCII.GetBytes("\x05" + body + sum.ToString("X2"));
    }

    // ═══════════════════════════════════════════
    //  FxFrameBuilder — 编程口帧构建基本验证
    // ═══════════════════════════════════════════

    [Fact]
    public void FxFrameBuilder_BuildReadCommand_StartsWithSTX()
    {
        byte[] frame = FxFrameBuilder.BuildReadCommand('D', 100, 2);
        Assert.Equal(0x02, frame[0]); // STX
        Assert.Equal((byte)'0', frame[1]); // Read command
        Assert.Equal((byte)'D', frame[2]); // Device code
        Assert.True(frame.Length >= 8);
    }

    [Fact]
    public void FxFrameBuilder_BuildWriteCommand_StartsWithSTX()
    {
        byte[] frame = FxFrameBuilder.BuildWriteCommand('D', 100, new byte[] { 0x12, 0x34 });
        Assert.Equal(0x02, frame[0]); // STX
        Assert.Equal((byte)'1', frame[1]); // Write command
        Assert.Equal((byte)'D', frame[2]); // Device code
        Assert.True(frame.Length >= 10);
    }

    [Fact]
    public void FxFrameBuilder_BuildReadCommand_ContainsAddressAndCount()
    {
        byte[] frame = FxFrameBuilder.BuildReadCommand('D', 100, 2);
        // STX + "0D010002" + ETX + SUM → ASCII payload starts at index 1
        // Command body is frame[1..^3] (before ETX and SUM)
        int bodyLen = frame.Length - 3; // exclude ETX + SUM(2)
        string body = Encoding.ASCII.GetString(frame, 1, bodyLen);
        Assert.StartsWith("0D0100", body); // Read + D + addr 0100
        Assert.EndsWith("02", body);       // count = 2
    }

    [Fact]
    public void FxFrameBuilder_BuildReadCommand_SumIncludesEtx()
    {
        byte[] frame = FxFrameBuilder.BuildReadCommand('D', 100, 2);

        int sum = 0;
        for (int i = 0; i < frame.Length - 2; i++)
            sum += frame[i];

        string expected = (sum & 0xFF).ToString("X2");
        string actual = Encoding.ASCII.GetString(frame, frame.Length - 2, 2);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FxFrameBuilder_BuildReadCommand_RejectsOutOfRangeFields()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FxFrameBuilder.BuildReadCommand('D', -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FxFrameBuilder.BuildReadCommand('D', 10000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FxFrameBuilder.BuildReadCommand('D', 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FxFrameBuilder.BuildReadCommand('D', 0, 256));
    }

    [Fact]
    public void FxFrameBuilder_BuildWriteCommand_RejectsInvalidData()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FxFrameBuilder.BuildWriteCommand('D', 10000, new byte[] { 0x12, 0x34 }));
        Assert.Throws<ArgumentException>(() => FxFrameBuilder.BuildWriteCommand('D', 100, null!));
        Assert.Throws<ArgumentException>(() => FxFrameBuilder.BuildWriteCommand('D', 100, Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => FxFrameBuilder.BuildWriteCommand('D', 100, new byte[] { 0x12 }));
    }

    [Fact]
    public void FxFrameBuilder_VerifyResponse_AcceptsAckOnly()
    {
        bool ok = FxFrameBuilder.VerifyResponse(new byte[] { 0x06 }, out byte[] data);

        Assert.True(ok);
        Assert.Empty(data);
    }

    [Fact]
    public void FxFrameBuilder_VerifyResponse_RejectsEmptyWithoutThrowing()
    {
        bool nullOk = FxFrameBuilder.VerifyResponse(null!, out byte[] nullData);
        bool emptyOk = FxFrameBuilder.VerifyResponse(Array.Empty<byte>(), out byte[] emptyData);

        Assert.False(nullOk);
        Assert.Empty(nullData);
        Assert.False(emptyOk);
        Assert.Empty(emptyData);
    }

    [Fact]
    public void FxFrameBuilder_VerifyResponse_RejectsInvalidHexPayloadWithoutThrowing()
    {
        byte[] response = BuildFxResponse("ZZ");

        bool ok = FxFrameBuilder.VerifyResponse(response, out byte[] data);

        Assert.False(ok);
        Assert.Empty(data);
    }

    [Fact]
    public void FxFrameBuilder_VerifyResponse_RejectsNak()
    {
        byte[] response = { 0x15 }; // NAK
        bool ok = FxFrameBuilder.VerifyResponse(response, out _);
        Assert.False(ok);
    }

    [Fact]
    public void FxFrameBuilder_VerifyResponse_RejectsTooShort()
    {
        byte[] response = { 0x02 }; // STX only, too short
        bool ok = FxFrameBuilder.VerifyResponse(response, out _);
        Assert.False(ok);
    }

    // ═══════════════════════════════════════════
    //  FxLinkClient — 计算机链接协议客户端 (离线)
    // ═══════════════════════════════════════════

    [Fact]
    public void FxLinkClient_Constructor_SetsDefaults()
    {
        using var ms = new MemoryStream();
        using var client = new FxLinkClient(ms);
        Assert.True(client.IsConnected);
        Assert.Equal((byte)0, client.Station);
        Assert.Equal(5000, client.Timeout);
    }

    [Fact]
    public void FxLinkClient_Constructor_WithStationAndTimeout()
    {
        using var ms = new MemoryStream();
        using var client = new FxLinkClient(ms, station: 5, timeout: 3000);
        Assert.Equal((byte)5, client.Station);
        Assert.Equal(3000, client.Timeout);
    }

    [Fact]
    public void FxLinkClient_SetLogger_DoesNotThrow()
    {
        using var ms = new MemoryStream();
        using var client = new FxLinkClient(ms);
        client.SetLogger(NullLogger.Instance);
    }

    [Fact]
    public void FxLinkClient_Dispose_DoesNotThrow()
    {
        using var ms = new MemoryStream();
        var client = new FxLinkClient(ms);
        client.Dispose();
    }

    [Fact]
    public void FxLinkClient_Connect_ReturnsSuccess()
    {
        using var ms = new MemoryStream();
        using var client = new FxLinkClient(ms);
        var result = client.Connect();
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void FxLinkClient_ReadInt16_BuildsFrameAndParsesResponse()
    {
        using var stream = new DuplexStream(BuildFxLinkDataResponse("1234"));
        using var client = new FxLinkClient(stream, station: 5);

        var result = client.ReadInt16("D100");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal((short)0x1234, result.Content);
        Assert.Equal(BuildFxLinkRequest(5, "0D006401"), stream.WrittenBytes);
    }

    [Fact]
    public void FxLinkClient_ReadBytes_ReturnsExactRequestedLength()
    {
        using var stream = new DuplexStream(BuildFxLinkDataResponse("AABBCCDD"));
        using var client = new FxLinkClient(stream);

        var result = client.ReadBytes("D20", 3);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, result.Content);
        Assert.Equal(BuildFxLinkRequest(0, "0D001402"), stream.WrittenBytes);
    }

    [Fact]
    public void FxLinkClient_ReadBytes_TruncatedPayload_ReturnsFailure()
    {
        using var stream = new DuplexStream(BuildFxLinkDataResponse("AA"));
        using var client = new FxLinkClient(stream);

        var result = client.ReadBytes("D20", 2);

        Assert.False(result.IsSuccess);
        Assert.Contains("数据不足", result.Message);
    }

    [Fact]
    public void FxLinkClient_ReadBytes_BadSum_ReturnsFailure()
    {
        using var stream = new DuplexStream(BuildFxLinkDataResponse("AABB", badSum: true));
        using var client = new FxLinkClient(stream);

        var result = client.ReadBytes("D20", 2);

        Assert.False(result.IsSuccess);
        Assert.Contains("Sum", result.Message);
    }

    [Fact]
    public void FxLinkClient_Nak_ReturnsFailure()
    {
        using var stream = new DuplexStream(0x15, (byte)'E', (byte)'1');
        using var client = new FxLinkClient(stream);

        var result = client.ReadInt16("D100");

        Assert.False(result.IsSuccess);
        Assert.Contains("NAK", result.Message);
        Assert.Contains("E1", result.Message);
    }

    [Fact]
    public void FxLinkClient_ReadUInt64_ReadsFourWords()
    {
        using var stream = new DuplexStream(BuildFxLinkDataResponse("1122334455667788"));
        using var client = new FxLinkClient(stream);

        var result = client.ReadUInt64("D100");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0x1122334455667788UL, result.Content);
        Assert.Equal(BuildFxLinkRequest(0, "0D006404"), stream.WrittenBytes);
    }

    [Fact]
    public void FxLinkClient_WriteUInt64_WritesFullPayload()
    {
        using var stream = new DuplexStream(0x06);
        using var client = new FxLinkClient(stream);

        var result = client.Write("D100", 0x1122334455667788UL);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(BuildFxLinkRequest(0, "1D0064041122334455667788"), stream.WrittenBytes);
    }

    [Fact]
    public void FxLinkClient_ReadDouble_ReadsIeee754Bits()
    {
        using var stream = new DuplexStream(BuildFxLinkDataResponse("3FF8000000000000"));
        using var client = new FxLinkClient(stream);

        var result = client.ReadDouble("D100");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1.5d, result.Content);
    }

    [Fact]
    public void FxLinkClient_WriteDouble_WritesIeee754Bits()
    {
        using var stream = new DuplexStream(0x06);
        using var client = new FxLinkClient(stream);

        var result = client.Write("D100", 1.5d);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(BuildFxLinkRequest(0, "1D0064043FF8000000000000"), stream.WrittenBytes);
    }

    [Fact]
    public void FxLinkClient_WriteBytes_Null_ReturnsFailure()
    {
        using var stream = new DuplexStream(0x06);
        using var client = new FxLinkClient(stream);

        var result = client.Write("D100", (byte[])null!);

        Assert.False(result.IsSuccess);
        Assert.Contains("不能为空", result.Message);
    }

    [Fact]
    public void FxLinkClient_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FxLinkClient(null!));
    }

    // ═══════════════════════════════════════════
    //  FxSerialClient — 编程口协议握手与响应解析
    // ═══════════════════════════════════════════

    [Fact]
    public void FxSerialClient_ReadInt16_UsesHandshakeAndParsesLittleEndianWord()
    {
        using var port = new FxFakeSerialPort();
        port.Open();
        port.LoadReadBytes(WithHandshakeAck(BuildFxResponse("3412")));
        using var client = new FxSerialClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16("D100");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal((short)0x1234, result.Content);
        Assert.Equal(new byte[] { 0x05 }, port.Writes[0]);
        Assert.Equal(FxFrameBuilder.BuildReadCommand('D', 100, 1), port.Writes[1]);
    }

    [Fact]
    public void FxSerialClient_ReadInt16_CounterAddress_UsesCDeviceCode()
    {
        using var port = new FxFakeSerialPort();
        port.Open();
        port.LoadReadBytes(WithHandshakeAck(BuildFxResponse("3412")));
        using var client = new FxSerialClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16("C100");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal((short)0x1234, result.Content);
        Assert.Equal(FxFrameBuilder.BuildReadCommand('C', 100, 1), port.Writes[1]);
    }

    [Fact]
    public void FxSerialClient_ReadInt16_InvalidAddress_ReturnsFailureWithoutWriting()
    {
        using var port = new FxFakeSerialPort();
        port.Open();
        using var client = new FxSerialClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16("Q100");

        Assert.False(result.IsSuccess);
        Assert.Contains("无效的 FX 地址格式", result.Message);
        Assert.Empty(port.Writes);
    }

    [Fact]
    public void FxSerialClient_ReadBytes_TooLargeLength_ReturnsFailureWithoutWriting()
    {
        using var port = new FxFakeSerialPort();
        port.Open();
        using var client = new FxSerialClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBytes("D0", 512);

        Assert.False(result.IsSuccess);
        Assert.Contains("FX 读取字数必须在", result.Message);
        Assert.Empty(port.Writes);
    }

    [Fact]
    public void FxSerialClient_WriteBytes_EmptyPayload_ReturnsFailureWithoutWriting()
    {
        using var port = new FxFakeSerialPort();
        port.Open();
        using var client = new FxSerialClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("D100", Array.Empty<byte>());

        Assert.False(result.IsSuccess);
        Assert.Contains("FX 写入数据不能为空", result.Message);
        Assert.Empty(port.Writes);
    }

    [Fact]
    public void FxSerialClient_WriteInt16_UsesAckOnlyResponse()
    {
        using var port = new FxFakeSerialPort();
        port.Open();
        port.LoadReadBytes(0x06, 0x06);
        using var client = new FxSerialClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("D100", (short)0x1234);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(new byte[] { 0x05 }, port.Writes[0]);
        Assert.Equal(FxFrameBuilder.BuildWriteCommand('D', 100, new byte[] { 0x34, 0x12 }), port.Writes[1]);
    }

    [Fact]
    public void FxSerialClient_ReadBytes_ReturnsExactRequestedLength()
    {
        using var port = new FxFakeSerialPort();
        port.Open();
        port.LoadReadBytes(WithHandshakeAck(BuildFxResponse("AABBCCDD")));
        using var client = new FxSerialClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBytes("D20", 3);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, result.Content);
    }

    [Fact]
    public void FxSerialClient_ReadBytes_TruncatedPayload_ReturnsFailure()
    {
        using var port = new FxFakeSerialPort();
        port.Open();
        port.LoadReadBytes(WithHandshakeAck(BuildFxResponse("AA")));
        using var client = new FxSerialClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBytes("D20", 2);

        Assert.False(result.IsSuccess);
        Assert.Contains("数据不足", result.Message);
    }

    [Fact]
    public void FxSerialClient_ReadInt16_NakPreservesTransportError()
    {
        using var port = new FxFakeSerialPort();
        port.Open();
        port.LoadReadBytes(0x15);
        using var client = new FxSerialClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16("D100");

        Assert.False(result.IsSuccess);
        Assert.Contains("NAK", result.Message);
    }

    [Fact]
    public void FxSerialClient_ReadBytes_BadSumPreservesChecksumError()
    {
        byte[] response = BuildFxResponse("AABB");
        response[response.Length - 1] = (byte)(response[response.Length - 1] == (byte)'0' ? '1' : '0');
        using var port = new FxFakeSerialPort();
        port.Open();
        port.LoadReadBytes(WithHandshakeAck(response));
        using var client = new FxSerialClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBytes("D20", 2);

        Assert.False(result.IsSuccess);
        Assert.Contains("SUM", result.Message);
    }
}
