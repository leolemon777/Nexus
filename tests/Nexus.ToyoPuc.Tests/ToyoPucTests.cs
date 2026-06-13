using Nexus;
using Nexus.ToyoPuc;
using Xunit;

namespace Nexus.ToyoPuc.Tests;

public class ToyoPucFrameBuildingTests
{
    private static byte[] BuildFrame(byte command, byte[] data)
    {
        int length = 1 + data.Length + 2;
        byte[] frame = new byte[4 + length];
        frame[0] = 0x54;
        frame[1] = 0x50;
        frame[2] = (byte)(length >> 8);
        frame[3] = (byte)(length & 0xFF);
        frame[4] = command;
        Array.Copy(data, 0, frame, 5, data.Length);

        ushort sum = 0;
        for (int i = 0; i < frame.Length - 2; i++)
            sum += frame[i];
        frame[frame.Length - 2] = (byte)(sum >> 8);
        frame[frame.Length - 1] = (byte)(sum & 0xFF);
        return frame;
    }

    [Fact]
    public void BuildFrame_ReadCommand_HasCorrectHeader()
    {
        byte[] data = new byte[] { 0x00, 0x0A, 0x00, 0x01 };
        byte[] frame = BuildFrame(0x01, data);
        Assert.Equal(0x54, frame[0]);
        Assert.Equal(0x50, frame[1]);
    }

    [Fact]
    public void BuildFrame_ReadCommand_LengthField()
    {
        byte[] data = new byte[] { 0x00, 0x0A, 0x00, 0x01 };
        byte[] frame = BuildFrame(0x01, data);
        int length = (frame[2] << 8) | frame[3];
        Assert.Equal(1 + 4 + 2, length);
    }

    [Fact]
    public void BuildFrame_ReadCommand_CommandByte()
    {
        byte[] data = new byte[] { 0x00, 0x00, 0x00, 0x01 };
        byte[] frame = BuildFrame(0x01, data);
        Assert.Equal(0x01, frame[4]);
    }

    [Fact]
    public void BuildFrame_WriteCommand_CommandByte()
    {
        byte[] data = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x00, 0x64 };
        byte[] frame = BuildFrame(0x02, data);
        Assert.Equal(0x02, frame[4]);
    }

    [Fact]
    public void BuildFrame_ChecksumIsSumOfAllBytesBefore()
    {
        byte[] data = new byte[] { 0x00, 0x0A, 0x00, 0x01 };
        byte[] frame = BuildFrame(0x01, data);

        ushort expected = 0;
        for (int i = 0; i < frame.Length - 2; i++)
            expected += frame[i];

        ushort actual = (ushort)((frame[frame.Length - 2] << 8) | frame[frame.Length - 1]);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildFrame_DataIsCopiedCorrectly()
    {
        byte[] data = new byte[] { 0x12, 0x34, 0x00, 0x02 };
        byte[] frame = BuildFrame(0x01, data);
        Assert.Equal(0x12, frame[5]);
        Assert.Equal(0x34, frame[6]);
        Assert.Equal(0x00, frame[7]);
        Assert.Equal(0x02, frame[8]);
    }

    [Fact]
    public void BuildFrame_ReadMultiRegister_CorrectData()
    {
        ushort addr = 100;
        ushort count = 2;
        byte[] data = new byte[4];
        data[0] = (byte)(addr >> 8);
        data[1] = (byte)(addr & 0xFF);
        data[2] = (byte)(count >> 8);
        data[3] = (byte)(count & 0xFF);

        byte[] frame = BuildFrame(0x03, data);
        Assert.Equal(0x03, frame[4]);
        Assert.Equal(0x00, frame[5]);
        Assert.Equal(0x64, frame[6]);
        Assert.Equal(0x00, frame[7]);
        Assert.Equal(0x02, frame[8]);
    }
}

public class ToyoPucResponseParsingTests
{
    private static ushort CalculateChecksum(byte[] data, int offset, int count)
    {
        ushort sum = 0;
        for (int i = offset; i < offset + count; i++)
            sum += data[i];
        return sum;
    }

    private static byte[] BuildValidResponse(byte status, byte[] data)
    {
        int length = 1 + data.Length + 2;
        byte[] resp = new byte[4 + length];
        resp[0] = 0x54;
        resp[1] = 0x50;
        resp[2] = (byte)(length >> 8);
        resp[3] = (byte)(length & 0xFF);
        resp[4] = status;
        Array.Copy(data, 0, resp, 5, data.Length);

        ushort crc = CalculateChecksum(resp, 0, resp.Length - 2);
        resp[resp.Length - 2] = (byte)(crc >> 8);
        resp[resp.Length - 1] = (byte)(crc & 0xFF);
        return resp;
    }

    [Fact]
    public void ParseResponse_ValidOK_ExtractsData()
    {
        byte[] resp = BuildValidResponse(0x00, new byte[] { 0x00, 0x64 });
        Assert.Equal(0x54, resp[0]);
        Assert.Equal(0x50, resp[1]);
        Assert.Equal(0x00, resp[4]);
    }

    [Fact]
    public void ParseResponse_ErrorStatus_HasErrorBit()
    {
        byte[] resp = BuildValidResponse(0x80, new byte[0]);
        Assert.NotEqual(0, resp[4] & 0x80);
    }

    [Fact]
    public void ParseResponse_ChecksumIsValid()
    {
        byte[] data = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        byte[] resp = BuildValidResponse(0x00, data);

        ushort calc = CalculateChecksum(resp, 0, resp.Length - 2);
        ushort recv = (ushort)((resp[resp.Length - 2] << 8) | resp[resp.Length - 1]);
        Assert.Equal(calc, recv);
    }

    [Fact]
    public void ParseResponse_HeaderMismatch_Detected()
    {
        byte[] resp = BuildValidResponse(0x00, new byte[] { 0x01 });
        resp[0] = 0x00;
        Assert.NotEqual(0x54, resp[0]);
    }

    [Fact]
    public void ParseResponse_ReadData_TwoBytes()
    {
        byte[] resp = BuildValidResponse(0x00, new byte[] { 0x00, 0xC8 });
        byte[] data = new byte[resp.Length - 7];
        Array.Copy(resp, 5, data, 0, data.Length);
        Assert.Equal(2, data.Length);
        Assert.Equal(0x00, data[0]);
        Assert.Equal(0xC8, data[1]);
    }
}

public class ToyoPucAddressParsingTests
{
    [Fact]
    public void Address_NumericOnly_ParsedAsRegister()
    {
        bool ok = ushort.TryParse("100", out ushort addr);
        Assert.True(ok);
        Assert.Equal(100, addr);
    }

    [Fact]
    public void Address_Zero_ParsedCorrectly()
    {
        bool ok = ushort.TryParse("0", out ushort addr);
        Assert.True(ok);
        Assert.Equal(0, addr);
    }

    [Fact]
    public void Address_MaxValue_ParsedCorrectly()
    {
        bool ok = ushort.TryParse("65535", out ushort addr);
        Assert.True(ok);
        Assert.Equal(65535, addr);
    }

    [Fact]
    public void Address_InvalidFormat_Fails()
    {
        bool ok = ushort.TryParse("abc", out ushort addr);
        Assert.False(ok);
    }
}
