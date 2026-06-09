using System;
using System.Text;
using Nexus;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Modbus.Tests;

public class ModbusPacketParserTests
{
    [Fact]
    public void ParseTcp_ReadHoldingRegistersRequest_DecodesCommonFields()
    {
        byte[] frame = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 };

        var packet = ModbusPacketParser.ParseTcp(frame, ModbusPacketDirection.Request);

        Assert.True(packet.IsValid, packet.Error);
        Assert.Equal(ModbusPacketTransport.Tcp, packet.Transport);
        Assert.Equal(ModbusPacketDirection.Request, packet.Direction);
        Assert.Equal((ushort)1, packet.TransactionId);
        Assert.Equal((ushort)0, packet.ProtocolId);
        Assert.Equal((ushort)6, packet.Length);
        Assert.Equal((byte)1, packet.UnitId);
        Assert.Equal((byte)0x03, packet.FunctionCode);
        Assert.False(packet.IsException);
        Assert.Equal((ushort)0, packet.Address);
        Assert.Equal((ushort)1, packet.Quantity);
        Assert.Equal(ModbusChecksumStatus.NotApplicable, packet.ChecksumStatus);
        Assert.Equal(frame, packet.RawFrame);
    }

    [Fact]
    public void ParseTcp_ReadHoldingRegistersResponse_InfersResponseAndData()
    {
        byte[] frame = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x12, 0x34 };

        var packet = ModbusPacketParser.ParseTcp(frame);

        Assert.True(packet.IsValid, packet.Error);
        Assert.Equal(ModbusPacketDirection.Response, packet.Direction);
        Assert.Equal((byte)0x03, packet.FunctionCode);
        Assert.Equal((byte)2, packet.ByteCount);
        Assert.Equal(new byte[] { 0x12, 0x34 }, packet.Data);
    }

    [Fact]
    public void ParseUdp_ReadHoldingRegistersRequest_DecodesMbapFields()
    {
        byte[] frame = { 0x00, 0x05, 0x00, 0x00, 0x00, 0x06, 0x11, 0x03, 0x00, 0x10, 0x00, 0x02 };

        var packet = ModbusPacketParser.ParseUdp(frame, ModbusPacketDirection.Request);

        Assert.True(packet.IsValid, packet.Error);
        Assert.Equal(ModbusPacketTransport.Udp, packet.Transport);
        Assert.Equal((ushort)5, packet.TransactionId);
        Assert.Equal((byte)0x11, packet.UnitId);
        Assert.Equal((byte)0x03, packet.FunctionCode);
        Assert.Equal((ushort)0x0010, packet.Address);
        Assert.Equal((ushort)2, packet.Quantity);
        Assert.Equal(ModbusChecksumStatus.NotApplicable, packet.ChecksumStatus);
    }

    [Fact]
    public void ParseTcp_ExceptionResponse_DecodesExceptionCode()
    {
        byte[] frame = { 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x01, 0x83, 0x02 };

        var packet = ModbusPacketParser.ParseTcp(frame);

        Assert.True(packet.IsValid, packet.Error);
        Assert.Equal(ModbusPacketDirection.Response, packet.Direction);
        Assert.True(packet.IsException);
        Assert.Equal((byte)0x83, packet.FunctionCode);
        Assert.Equal((byte)0x03, packet.BaseFunctionCode);
        Assert.Equal((byte)0x02, packet.ExceptionCode);
    }

    [Fact]
    public void ParseRtu_ValidCrc_DecodesReadRequest()
    {
        byte[] frame = BuildRtuFrame(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 });

        var packet = ModbusPacketParser.ParseRtu(frame, ModbusPacketDirection.Request);

        Assert.True(packet.IsValid, packet.Error);
        Assert.Equal(ModbusPacketTransport.Rtu, packet.Transport);
        Assert.Equal((byte)1, packet.Station);
        Assert.Equal((byte)0x03, packet.FunctionCode);
        Assert.Equal((ushort)0, packet.Address);
        Assert.Equal((ushort)1, packet.Quantity);
        Assert.Equal(ModbusChecksumStatus.Valid, packet.ChecksumStatus);
        Assert.Equal(packet.ExpectedChecksum, packet.Checksum);
    }

    [Fact]
    public void ParseRtuOverTcp_ValidCrc_DecodesReadRequest()
    {
        byte[] frame = BuildRtuFrame(new byte[] { 0x02, 0x04, 0x00, 0x20, 0x00, 0x02 });

        var packet = ModbusPacketParser.ParseRtuOverTcp(frame, ModbusPacketDirection.Request);

        Assert.True(packet.IsValid, packet.Error);
        Assert.Equal(ModbusPacketTransport.RtuOverTcp, packet.Transport);
        Assert.Equal((byte)2, packet.Station);
        Assert.Equal((byte)0x04, packet.FunctionCode);
        Assert.Equal((ushort)0x0020, packet.Address);
        Assert.Equal((ushort)2, packet.Quantity);
        Assert.Equal(ModbusChecksumStatus.Valid, packet.ChecksumStatus);
    }

    [Fact]
    public void ParseRtu_InvalidCrc_ReturnsInvalidButStillDecodesPdu()
    {
        byte[] frame = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00 };

        var packet = ModbusPacketParser.ParseRtu(frame, ModbusPacketDirection.Request);

        Assert.False(packet.IsValid);
        Assert.Contains("CRC", packet.Error);
        Assert.Equal(ModbusChecksumStatus.Invalid, packet.ChecksumStatus);
        Assert.Equal((byte)0x03, packet.FunctionCode);
        Assert.Equal((ushort)0, packet.Address);
        Assert.Equal((ushort)1, packet.Quantity);
    }

    [Fact]
    public void ParseAscii_ValidLrc_DecodesReadResponse()
    {
        string frame = BuildAsciiFrame(new byte[] { 0x01, 0x03, 0x02, 0x12, 0x34 });

        var packet = ModbusPacketParser.ParseAscii(frame);

        Assert.True(packet.IsValid, packet.Error);
        Assert.Equal(ModbusPacketTransport.Ascii, packet.Transport);
        Assert.Equal(ModbusPacketDirection.Response, packet.Direction);
        Assert.Equal((byte)1, packet.Station);
        Assert.Equal((byte)0x03, packet.FunctionCode);
        Assert.Equal((byte)2, packet.ByteCount);
        Assert.Equal(new byte[] { 0x12, 0x34 }, packet.Data);
        Assert.Equal(ModbusChecksumStatus.Valid, packet.ChecksumStatus);
    }

    [Fact]
    public void ParseTcp_WriteMultipleRegistersRequest_DecodesByteCountAndData()
    {
        byte[] frame =
        {
            0x00, 0x03, 0x00, 0x00, 0x00, 0x0B, 0x01,
            0x10, 0x00, 0x10, 0x00, 0x02, 0x04, 0x12, 0x34, 0x56, 0x78
        };

        var packet = ModbusPacketParser.ParseTcp(frame);

        Assert.True(packet.IsValid, packet.Error);
        Assert.Equal(ModbusPacketDirection.Request, packet.Direction);
        Assert.Equal((byte)0x10, packet.FunctionCode);
        Assert.Equal((ushort)0x0010, packet.Address);
        Assert.Equal((ushort)2, packet.Quantity);
        Assert.Equal((byte)4, packet.ByteCount);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, packet.Data);
    }

    [Fact]
    public void ParseTcp_MaskWriteRegister_DecodesAddressAndMasks()
    {
        byte[] frame =
        {
            0x00, 0x05, 0x00, 0x00, 0x00, 0x08, 0x01,
            0x16, 0x00, 0x10, 0xFF, 0x00, 0x00, 0xF0
        };

        var packet = ModbusPacketParser.ParseTcp(frame, ModbusPacketDirection.Request);

        Assert.True(packet.IsValid, packet.Error);
        Assert.Equal(ModbusPacketDirection.Request, packet.Direction);
        Assert.Equal((byte)0x16, packet.FunctionCode);
        Assert.Equal((ushort)0x0010, packet.Address);
        Assert.Equal((ushort)0xFF00, packet.AndMask);
        Assert.Equal((ushort)0x00F0, packet.OrMask);
    }

    [Fact]
    public void ParseTcp_ReadWriteMultipleRegistersResponse_DecodesData()
    {
        byte[] frame = { 0x00, 0x04, 0x00, 0x00, 0x00, 0x07, 0x01, 0x17, 0x04, 0x00, 0x01, 0x00, 0x02 };

        var packet = ModbusPacketParser.ParseTcp(frame);

        Assert.True(packet.IsValid, packet.Error);
        Assert.Equal(ModbusPacketDirection.Response, packet.Direction);
        Assert.Equal((byte)0x17, packet.FunctionCode);
        Assert.Equal((byte)4, packet.ByteCount);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x02 }, packet.Data);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x01, 0x00 })]
    [InlineData(new byte[] { 0x01, 0x03, 0x00, 0x00 })]
    public void Parse_ShortBinaryFrames_ReturnsInvalid(byte[] frame)
    {
        var tcp = ModbusPacketParser.ParseTcp(frame);
        var rtu = ModbusPacketParser.ParseRtu(frame);

        Assert.False(tcp.IsValid);
        Assert.False(rtu.IsValid);
        Assert.Contains("short", tcp.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("short", rtu.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAscii_ShortFrame_ReturnsInvalid()
    {
        var packet = ModbusPacketParser.ParseAscii(":0103\r\n");

        Assert.False(packet.IsValid);
        Assert.Equal(ModbusChecksumStatus.Missing, packet.ChecksumStatus);
        Assert.Contains("short", packet.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildRtuFrame(byte[] stationAndPdu)
    {
        ushort crc = CrcCalculator.ComputeCrc16(stationAndPdu);
        byte[] frame = new byte[stationAndPdu.Length + 2];
        Buffer.BlockCopy(stationAndPdu, 0, frame, 0, stationAndPdu.Length);
        frame[stationAndPdu.Length] = (byte)(crc & 0xFF);
        frame[stationAndPdu.Length + 1] = (byte)((crc >> 8) & 0xFF);
        return frame;
    }

    private static string BuildAsciiFrame(byte[] stationAndPdu)
    {
        byte lrc = CrcCalculator.ComputeLrc(stationAndPdu);
        byte[] frame = new byte[stationAndPdu.Length + 1];
        Buffer.BlockCopy(stationAndPdu, 0, frame, 0, stationAndPdu.Length);
        frame[stationAndPdu.Length] = lrc;

        StringBuilder builder = new StringBuilder(frame.Length * 2 + 3);
        builder.Append(':');
        for (int i = 0; i < frame.Length; i++)
            builder.Append(frame[i].ToString("X2"));
        builder.Append("\r\n");
        return builder.ToString();
    }
}
