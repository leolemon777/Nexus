using System;
using Nexus;
using Xunit;

namespace Nexus.Siemens.Tests;

internal sealed class MpiFakeSerialPort : ISerialPort
{
    private byte[] _readBuffer = Array.Empty<byte>();
    private int _readPosition;

    public string PortName { get; set; } = "COM_MPI_TEST";
    public int BaudRate { get; set; } = 19200;
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

    public void SetupRawResponse(byte[] response)
    {
        _readBuffer = response;
        _readPosition = 0;
    }

    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;

    public int Read(byte[] buffer, int offset, int count)
    {
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
        frame[5] = 0x02;
        frame[6] = 0x00;
        frame[7] = functionCode;
        Buffer.BlockCopy(data, 0, frame, 8, dataLen);
        byte bcc = 0;
        for (int i = 4; i < 8 + dataLen; i++) bcc ^= frame[i];
        frame[8 + dataLen] = bcc;
        frame[9 + dataLen] = 0x16;
        return frame;
    }
}

public class SiemensMPIClientTests
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
        frame[5] = 0x00;
        frame[6] = 0x02;
        frame[7] = functionCode;
        Buffer.BlockCopy(data, 0, frame, 8, dataLen);
        byte bcc = 0;
        for (int i = 4; i < 8 + dataLen; i++) bcc ^= frame[i];
        frame[8 + dataLen] = bcc;
        frame[9 + dataLen] = 0x16;
        return frame;
    }

    [Fact]
    public void ReadInt16_MerkkerArea_BuildsCorrectFrame()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0x12, 0x34);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16("M100");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal((short)0x1234, result.Content);
        Assert.Equal(new byte[]
        {
            0x68, 0x0B, 0x0B, 0x68,
            0x00, 0x00, 0x02, 0x01,
            0x01, 0x00, 0x02, 0x83, 0x00, 0x64, 0x00,
            0xE7, 0x16
        }, port.LastWrittenData);
        Assert.Equal(0x83, port.LastWrittenData[11]);
    }

    [Fact]
    public void ReadBool_MerkkerBitArea_ExtractsTargetBit()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0b0000_0100);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBool("M10.2");

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(result.Content);
    }

    [Fact]
    public void ReadInt16_InputArea_EUses0x81()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0x00, 0x0A);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16("E0");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal((short)10, result.Content);
        Assert.Equal(0x81, port.LastWrittenData[11]);
    }

    [Fact]
    public void ReadInt16_OutputArea_AUses0x82()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0x00, 0x05);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16("A10");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0x82, port.LastWrittenData[11]);
    }

    [Fact]
    public void ReadInt16_DataBlock_AreaCode0x84()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0xAB, 0xCD);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16("DB100.DBW0");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(unchecked((short)0xABCD), result.Content);
        Assert.Equal(0x84, port.LastWrittenData[11]);
    }

    [Fact]
    public void ReadInt32_DataBlockDoubleWord()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0x01, 0x02, 0x03, 0x04);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt32("DB100.DBD20");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0x01020304, result.Content);
    }

    [Fact]
    public void ReadFloat_ParsesBigEndianPayload()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0x3F, 0xC0, 0x00, 0x00);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadFloat("M0");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1.5f, result.Content, 3);
    }

    [Fact]
    public void ReadBytes_ReturnsExactRequestedBytes()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0xDE, 0xAD, 0xBE);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBytes("M12", 3);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE }, result.Content);
    }

    [Fact]
    public void ReadString_TrimsNullTerminator()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, (byte)'A', (byte)'B', 0x00);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadString("M20", 3);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("AB", result.Content);
    }

    [Fact]
    public void WriteBool_BuildsBitWriteFrame()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x02);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("E5.7", true);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0x81, port.LastWrittenData[11]);
    }

    [Fact]
    public void WriteInt16_Merkker_BuildsWriteFrame()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x02);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.Write("M200", (short)-2);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(new byte[]
        {
            0x68, 0x0D, 0x0D, 0x68,
            0x00, 0x00, 0x02, 0x02,
            0x01, 0x00, 0x02, 0x83, 0x00, 0xC8, 0x00, 0xFF, 0xFE,
            0x49, 0x16
        }, port.LastWrittenData);
    }

    [Fact]
    public void ReadBytes_InvalidBcc_ReturnsFailure()
    {
        var port = new MpiFakeSerialPort();
        port.SetupRawResponse(new byte[]
        {
            0x68, 0x05, 0x05, 0x68,
            0x00, 0x02, 0x00, 0x01, 0xFF,
            0x00, 0x16
        });
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBytes("M0", 1);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ReadBytes_TruncatedPayload_ReturnsFailure()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0x01);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBytes("M0", 2);

        Assert.False(result.IsSuccess);
        Assert.Contains("字节响应异常", result.Message);
    }

    [Fact]
    public void ReadInt16_DeviceError_ReturnsFailure()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x03, 0xD2);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16("M0");

        Assert.False(result.IsSuccess);
        Assert.Contains("0x03", result.Message);
    }

    [Fact]
    public void ReadBytes_LengthOverSingleFrame_ReturnsFailure()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBytes("M0", 256);

        Assert.False(result.IsSuccess);
        Assert.Contains("255", result.Message);
    }

    [Fact]
    public void TimerArea_TUses0x1D()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0x00, 0x64);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16("T0");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0x1D, port.LastWrittenData[11]);
    }

    [Fact]
    public void CounterArea_CUses0x1C()
    {
        var port = new MpiFakeSerialPort();
        port.SetupResponse(0x04, 0xFF, 0x00, 0x0A);
        port.Open();

        using var client = new SiemensMPIClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadInt16("C0");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0x1C, port.LastWrittenData[11]);
    }

    [Fact]
    public void MpiAddress_InvalidFormat_ThrowsAddressParseException()
    {
        Assert.Throws<AddressParseException>(() => MpiAddress.Parse(""));
        Assert.Throws<AddressParseException>(() => MpiAddress.Parse("X100"));
    }

    [Fact]
    public void MpiAddress_TryParse_ReturnsNullOnInvalid()
    {
        Assert.False(MpiAddress.TryParse("INVALID", out _));
        Assert.True(MpiAddress.TryParse("M100", out var addr));
        Assert.NotNull(addr);
    }
}
