using System;
using System.Collections.Generic;
using System.Threading;
using Nexus;
using Xunit;

namespace Nexus.Siemens.Tests;

internal sealed class PpiFakeSerialPort : ISerialPort
{
    private byte[] _readBuffer = Array.Empty<byte>();
    private int _readPosition;
    private readonly Queue<byte[]> _responseQueue = new Queue<byte[]>();

    public string PortName { get; set; } = "COM_PPI_TEST";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Parity Parity { get; set; } = Parity.Even;
    public int ReadTimeout { get; set; } = 5000;
    public int WriteTimeout { get; set; } = 5000;
    public bool IsOpen { get; private set; }
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }

    public byte[] LastWrittenData { get; private set; } = Array.Empty<byte>();

    public void SetupResponse(byte functionCode, params byte[] data)
    {
        _readBuffer = BuildFrame(functionCode, data);
        _readPosition = 0;
    }

    public void EnqueueResponse(byte functionCode, params byte[] data)
    {
        _responseQueue.Enqueue(BuildFrame(functionCode, data));
    }

    public void SetupRawResponse(byte[] response)
    {
        _readBuffer = response;
        _readPosition = 0;
    }

    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;

    public int Read(byte[] buffer, int offset, int count)
    {
        if (_readPosition >= _readBuffer.Length && _responseQueue.Count > 0)
        {
            _readBuffer = _responseQueue.Dequeue();
            _readPosition = 0;
        }

        int available = _readBuffer.Length - _readPosition;
        if (available <= 0) return 0;

        int toRead = Math.Min(count, available);
        Buffer.BlockCopy(_readBuffer, _readPosition, buffer, offset, toRead);
        _readPosition += toRead;
        return toRead;
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        LastWrittenData = new byte[count];
        Buffer.BlockCopy(buffer, offset, LastWrittenData, 0, count);
    }

    public void Dispose() => Close();

    public static byte[] BuildFrame(byte functionCode, byte[] data)
    {
        int dataLen = data.Length;
        int lenField = 4 + dataLen;
        byte[] frame = new byte[4 + lenField + 2];

        frame[0] = 0x68;
        frame[1] = (byte)lenField;
        frame[2] = (byte)lenField;
        frame[3] = 0x68;
        frame[4] = 0x00;
        frame[5] = 0x01;
        frame[6] = 0x02;
        frame[7] = functionCode;
        Buffer.BlockCopy(data, 0, frame, 8, dataLen);

        byte bcc = 0;
        for (int i = 4; i < 8 + dataLen; i++)
            bcc ^= frame[i];

        frame[8 + dataLen] = bcc;
        frame[9 + dataLen] = 0x16;
        return frame;
    }
}

public class SiemensPpiClientTests
{
    private static byte[] BuildRequestFrame(byte functionCode, params byte[] data)
    {
        int dataLen = data.Length;
        int lenField = 4 + dataLen;
        byte[] frame = new byte[4 + lenField + 2];

        frame[0] = 0x68;
        frame[1] = (byte)lenField;
        frame[2] = (byte)lenField;
        frame[3] = 0x68;
        frame[4] = 0x00;
        frame[5] = 0x02;
        frame[6] = 0x01;
        frame[7] = functionCode;
        Buffer.BlockCopy(data, 0, frame, 8, dataLen);

        byte bcc = 0;
        for (int i = 4; i < 8 + dataLen; i++)
            bcc ^= frame[i];

        frame[8 + dataLen] = bcc;
        frame[9 + dataLen] = 0x16;
        return frame;
    }

    [Fact]
    public void ReadInt16_BuildsPpiFrameAndParsesResponse()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0x12, 0x34);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16("V100");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal((short)0x1234, result.Content);
        Assert.Equal(new byte[]
        {
            0x68, 0x0B, 0x0B, 0x68,
            0x00, 0x02, 0x01, 0x01,
            0x01, 0x00, 0x02, 0x85, 0x00, 0x64, 0x00,
            0xE0, 0x16
        }, port.LastWrittenData);
    }

    [Fact]
    public void ReadBool_UsesBitAddressAndExtractsTargetBit()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0b0000_0100);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBool("M10.2");

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(result.Content);
        Assert.Equal(new byte[]
        {
            0x68, 0x0B, 0x0B, 0x68,
            0x00, 0x02, 0x01, 0x01,
            0x01, 0x00, 0x01, 0x83, 0x00, 0x0A, 0x02,
            0x89, 0x16
        }, port.LastWrittenData);
    }

    [Fact]
    public void ReadFloat_ParsesBigEndianPayload()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0x3F, 0xC0, 0x00, 0x00);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadFloat("V10");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1.5f, result.Content, 3);
        Assert.Equal(BuildRequestFrame(0x01, 0x01, 0x00, 0x04, 0x85, 0x00, 0x0A, 0x00), port.LastWrittenData);
    }

    [Fact]
    public void ReadBytes_ReturnsExactRequestedBytes()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0xDE, 0xAD, 0xBE);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBytes("V12", 3);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE }, result.Content);
        Assert.Equal(BuildRequestFrame(0x01, 0x01, 0x00, 0x03, 0x85, 0x00, 0x0C, 0x00), port.LastWrittenData);
    }

    [Fact]
    public void ReadString_TrimsNullTerminator()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, (byte)'A', (byte)'B', 0x00);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadString("V20", 3);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("AB", result.Content);
    }

    [Fact]
    public void WriteBool_BuildsBitWriteFrame()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x02);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("Q5.7", true);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(BuildRequestFrame(0x02, 0x01, 0x00, 0x01, 0x82, 0x00, 0x05, 0x07, 0x80), port.LastWrittenData);
    }

    [Fact]
    public void WriteInt16_BuildsWriteFrame()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x02);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("V200", (short)-2);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(new byte[]
        {
            0x68, 0x0D, 0x0D, 0x68,
            0x00, 0x02, 0x01, 0x02,
            0x01, 0x00, 0x02, 0x85, 0x00, 0xC8, 0x00, 0xFF, 0xFE,
            0x4E, 0x16
        }, port.LastWrittenData);
    }

    [Fact]
    public void ReadBytes_InvalidBcc_ReturnsFailure()
    {
        var port = new PpiFakeSerialPort();
        port.SetupRawResponse(new byte[]
        {
            0x68, 0x05, 0x05, 0x68,
            0x00, 0x01, 0x02, 0x01, 0xFF,
            0x00, 0x16
        });
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBytes("V0", 1);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ReadBytes_TruncatedPayload_ReturnsFailure()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0x01);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBytes("V0", 2);

        Assert.False(result.IsSuccess);
        Assert.Contains("字节响应异常", result.Message);
    }

    [Fact]
    public void ReadInt16_DeviceError_ReturnsFailure()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x03, 0xD2);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16("V0");

        Assert.False(result.IsSuccess);
        Assert.Contains("0x03", result.Message);
    }

    [Fact]
    public void ReadBytes_LengthMirrorMismatch_ReturnsFailure()
    {
        var response = PpiFakeSerialPort.BuildFrame(0x04, new byte[] { 0xFF, 0x11 });
        response[2] = 0x00;
        var port = new PpiFakeSerialPort();
        port.SetupRawResponse(response);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBytes("V0", 1);

        Assert.False(result.IsSuccess);
        Assert.Contains("格式", result.Message);
    }

    [Fact]
    public void ReadBytes_LengthOverSingleFrame_ReturnsFailure()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBytes("V0", 256);

        Assert.False(result.IsSuccess);
        Assert.Contains("255", result.Message);
    }

    // ── 新增测试 ──────────────────────────────────

    [Fact]
    public void ReadInt64_Parses8ByteBigEndian()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt64("V100");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0x0000000100000000L, result.Content);
    }

    [Fact]
    public void ReadDouble_ViaInt64AndBitConverter()
    {
        var port = new PpiFakeSerialPort();
        // 3.141592653589793 的 IEEE 754 双精度大端字节
        double expected = 3.141592653589793;
        byte[] doubleBytes = BitConverter.GetBytes(expected);
        // 需要大端序 → 反转
        Array.Reverse(doubleBytes);
        byte[] response = new byte[1 + doubleBytes.Length];
        response[0] = 0xFF;
        Buffer.BlockCopy(doubleBytes, 0, response, 1, doubleBytes.Length);
        port.SetupResponse(0x04, response);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadDouble("V200");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public void ReadUInt16_UnsignedConversion()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0xFF, 0xFE);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadUInt16("V0");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal((ushort)0xFFFE, result.Content);
    }

    [Fact]
    public void ReadUInt32_UnsignedConversion()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0x80, 0x00, 0x00, 0x01);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadUInt32("V0");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0x80000001u, result.Content);
    }

    [Fact]
    public void ReadUInt64_UnsignedConversion()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadUInt64("V0");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0xFFFFFFFFFFFFFFFEUL, result.Content);
    }

    [Fact]
    public void WriteInt32_VerifiesBigEndianEncoding()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x02);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("V300", 0x12345678);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(BuildRequestFrame(0x02,
            0x01, 0x00, 0x04, 0x85, 0x01, 0x2C, 0x00,
            0x12, 0x34, 0x56, 0x78), port.LastWrittenData);
    }

    [Fact]
    public void WriteFloat_ConvertsToInt32AndEncodes()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x02);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("V400", 1.5f);

        Assert.True(result.IsSuccess, result.Message);
        // 1.5f = 0x3FC00000, 大端 → 3F C0 00 00
        Assert.Equal(BuildRequestFrame(0x02,
            0x01, 0x00, 0x04, 0x85, 0x01, 0x90, 0x00,
            0x3F, 0xC0, 0x00, 0x00), port.LastWrittenData);
    }

    [Fact]
    public void WriteString_VerifiesAsciiEncodingAndPadding()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x02);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("V500", "AB");

        Assert.True(result.IsSuccess, result.Message);
        // "AB" = 0x41, 0x42, 2字节已是偶数无需填充
        Assert.Equal(BuildRequestFrame(0x02,
            0x01, 0x00, 0x02, 0x85, 0x01, 0xF4, 0x00,
            0x41, 0x42), port.LastWrittenData);
    }

    [Fact]
    public void WriteString_OddLengthPadsToEven()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x02);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("V600", "ABC");

        Assert.True(result.IsSuccess, result.Message);
        // "ABC" = 3字节 → 补0 → 4字节
        Assert.Equal(BuildRequestFrame(0x02,
            0x01, 0x00, 0x04, 0x85, 0x02, 0x58, 0x00,
            0x41, 0x42, 0x43, 0x00), port.LastWrittenData);
    }

    [Fact]
    public void WriteBytes_VerifiesFrameStructure()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x02);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("V700", new byte[] { 0xCA, 0xFE });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(BuildRequestFrame(0x02,
            0x01, 0x00, 0x02, 0x85, 0x02, 0xBC, 0x00,
            0xCA, 0xFE), port.LastWrittenData);
    }

    [Fact]
    public void WriteInt64_Verifies8ByteBigEndian()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x02);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("V800", 0x0102030405060708L);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(BuildRequestFrame(0x02,
            0x01, 0x00, 0x08, 0x85, 0x03, 0x20, 0x00,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08), port.LastWrittenData);
    }

    [Fact]
    public void WriteDouble_ConvertsToInt64AndEncodes()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x02);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        double expected = 3.14;
        var result = client.Write("V900", expected);

        Assert.True(result.IsSuccess, result.Message);
        // 验证帧包含正确的 8 字节大端双精度
        byte[] doubleBytes = BitConverter.GetBytes(expected);
        Array.Reverse(doubleBytes); // 转为大端
        byte[] expectedFrame = BuildRequestFrame(0x02,
            0x01, 0x00, 0x08, 0x85, 0x03, 0x84, 0x00,
            doubleBytes[0], doubleBytes[1], doubleBytes[2], doubleBytes[3],
            doubleBytes[4], doubleBytes[5], doubleBytes[6], doubleBytes[7]);
        Assert.Equal(expectedFrame, port.LastWrittenData);
    }

    [Fact]
    public void WriteUInt16_DelegatesToWriteInt16()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x02);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("V100", (ushort)0xABCD);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(BuildRequestFrame(0x02,
            0x01, 0x00, 0x02, 0x85, 0x00, 0x64, 0x00,
            0xAB, 0xCD), port.LastWrittenData);
    }

    [Fact]
    public void WriteUInt32_DelegatesToWriteInt32()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x02);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("V200", 0xDEADBEEFu);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(BuildRequestFrame(0x02,
            0x01, 0x00, 0x04, 0x85, 0x00, 0xC8, 0x00,
            0xDE, 0xAD, 0xBE, 0xEF), port.LastWrittenData);
    }

    [Fact]
    public void WriteUInt64_DelegatesToWriteInt64()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x02);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("V300", 0xFFFFFFFFFFFFFFFEUL);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(BuildRequestFrame(0x02,
            0x01, 0x00, 0x08, 0x85, 0x01, 0x2C, 0x00,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE), port.LastWrittenData);
    }

    [Fact]
    public void BatchRead_ReadsMultipleAddresses()
    {
        var port = new PpiFakeSerialPort();
        port.EnqueueResponse(0x04, 0xFF, 0x00, 0x64);   // V0 = 100
        port.EnqueueResponse(0x04, 0xFF, 0x00, 0xC8);   // V2 = 200
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.BatchRead(new[] { "V0", "V2" });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, result.Content.Count);
        Assert.Equal((short)100, result.Content["V0"]);
        Assert.Equal((short)200, result.Content["V2"]);
    }

    [Fact]
    public void BatchRead_EmptyAddresses_ReturnsFailure()
    {
        var port = new PpiFakeSerialPort();
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.BatchRead(new string[0]);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void BatchWrite_WritesMultipleAddresses()
    {
        var port = new PpiFakeSerialPort();
        port.EnqueueResponse(0x02);  // 第一个写入成功
        port.EnqueueResponse(0x02);  // 第二个写入成功
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var items = new List<KeyValuePair<string, object>>
        {
            new KeyValuePair<string, object>("V0", (short)100),
            new KeyValuePair<string, object>("V2", (short)200),
        };
        var result = client.BatchWrite(items);

        Assert.True(result.IsSuccess, result.Message);
    }

    [Fact]
    public void BatchWrite_EmptyItems_ReturnsFailure()
    {
        var port = new PpiFakeSerialPort();
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.BatchWrite(new List<KeyValuePair<string, object>>());

        Assert.False(result.IsSuccess);
    }

    // ── 地址解析边界测试 ──────────────────────────

    [Theory]
    [InlineData("V0", 0x85)]
    [InlineData("I0", 0x81)]
    [InlineData("Q0", 0x82)]
    [InlineData("M0", 0x83)]
    [InlineData("S0", 0x84)]
    [InlineData("SM0", 0x86)]
    [InlineData("C0", 0x1C)]
    public void ParseAddress_AllAreaCodes_CorrectAreaCode(string address, byte expectedAreaCode)
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0x00, 0x01);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16(address);

        Assert.True(result.IsSuccess, result.Message);
        // 验证帧中 area code 字节（帧偏移 11 = 地址区码）
        Assert.Equal(expectedAreaCode, port.LastWrittenData[11]);
    }

    [Fact]
    public void ParseAddress_InvalidAddress_ThrowsArgumentException()
    {
        var port = new PpiFakeSerialPort();
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        Assert.Throws<ArgumentException>(() => client.ReadInt16("X100"));
    }

    [Fact]
    public void ParseAddress_BitOffsetOutOfRange_ThrowsException()
    {
        var port = new PpiFakeSerialPort();
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => client.ReadBool("M0.8"));
    }

    // ── BCC 校验测试 ─────────────────────────────

    [Fact]
    public void BCC_CorrectlyComputedForVariousFrames()
    {
        // 验证 BuildFrame 计算的 BCC 正确
        var frame1 = PpiFakeSerialPort.BuildFrame(0x04, new byte[] { 0xFF, 0x12, 0x34 });
        // 手动计算 BCC: 从 frame[4] 到 frame[len-2]
        byte expectedBcc = 0;
        for (int i = 4; i < frame1.Length - 2; i++)
            expectedBcc ^= frame1[i];
        Assert.Equal(expectedBcc, frame1[frame1.Length - 2]);
        Assert.Equal(0x16, frame1[frame1.Length - 1]);
    }

    [Fact]
    public void BCC_VerificationDetectsCorruption()
    {
        var validFrame = PpiFakeSerialPort.BuildFrame(0x04, new byte[] { 0xFF, 0xAA });
        // 篡改数据字节（不更新 BCC）
        validFrame[8] ^= 0xFF;

        var port = new PpiFakeSerialPort();
        port.SetupRawResponse(validFrame);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBytes("V0", 1);

        Assert.False(result.IsSuccess);
        Assert.Contains("BCC", result.Message);
    }

    // ── 设备错误响应测试 ──────────────────────────

    [Fact]
    public void DeviceError_FunctionCode0x01_ReturnsFailure()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x01, 0xD2);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16("V0");

        Assert.False(result.IsSuccess);
        Assert.Contains("0x01", result.Message);
    }

    [Fact]
    public void DeviceError_FunctionCode0x03_ReturnsFailure()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x03, 0x00);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16("V0");

        Assert.False(result.IsSuccess);
        Assert.Contains("0x03", result.Message);
    }

    // ── Subscribe 订阅测试 ────────────────────────

    [Fact]
    public void Subscribe_OnDataChanged_Fires()
    {
        var port = new PpiFakeSerialPort();
        // 第一次读返回 100，后续返回 200（模拟数据变化）
        port.EnqueueResponse(0x04, 0xFF, 0x00, 0x64);
        port.EnqueueResponse(0x04, 0xFF, 0x00, 0xC8);
        port.EnqueueResponse(0x04, 0xFF, 0x00, 0xC8);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        DataChangeEventArgs? receivedArgs = null;
        using var eventFired = new ManualResetEventSlim(false);

        client.OnDataChanged += (_, args) =>
        {
            receivedArgs = args;
            eventFired.Set();
        };

        client.Subscribe("V0", intervalMs: 50, dataType: "Int16");
        client.StartSubscriptions(globalIntervalMs: 50);

        Assert.True(eventFired.Wait(3000), "OnDataChanged 事件未触发");

        client.StopSubscriptions();

        Assert.NotNull(receivedArgs);
        Assert.Equal("V0", receivedArgs!.Address);
        Assert.Equal((short)100, receivedArgs.OldValue);
        Assert.Equal((short)200, receivedArgs.NewValue);
    }

    [Fact]
    public void Unsubscribe_StopsMonitoring()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0x00, 0x64);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        int eventCount = 0;
        client.OnDataChanged += (_, _) => Interlocked.Increment(ref eventCount);

        client.Subscribe("V0", intervalMs: 50, dataType: "Int16");
        client.StartSubscriptions(globalIntervalMs: 50);
        Thread.Sleep(100);
        client.Unsubscribe("V0");
        Thread.Sleep(200);

        client.StopSubscriptions();

        // 取消后不应再有新事件（或极少量）
        Assert.True(Volatile.Read(ref eventCount) <= 1);
    }

    // ── WriteBool false 测试 ──────────────────────

    [Fact]
    public void WriteBool_False_ClearsBit()
    {
        var port = new PpiFakeSerialPort();
        port.SetupResponse(0x02);
        port.Open();

        using var client = new SiemensPpiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("Q5.7", false);

        Assert.True(result.IsSuccess, result.Message);
        // 写 false 时值字节应为 0x00
        Assert.Equal(BuildRequestFrame(0x02, 0x01, 0x00, 0x01, 0x82, 0x00, 0x05, 0x07, 0x00), port.LastWrittenData);
    }
}
