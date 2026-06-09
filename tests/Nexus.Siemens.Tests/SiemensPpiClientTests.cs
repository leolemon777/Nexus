using System;
using Nexus;
using Xunit;

namespace Nexus.Siemens.Tests;

internal sealed class PpiFakeSerialPort : ISerialPort
{
    private byte[] _readBuffer = Array.Empty<byte>();
    private int _readPosition;

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

    private static byte[] BuildFrame(byte functionCode, byte[] data)
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
        Assert.False(result.IsSuccess);
    }
}
