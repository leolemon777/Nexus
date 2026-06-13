using System;
using System.Linq;
using Nexus;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Modbus.Tests;

internal class FakeSerialPort : ISerialPort
{
    private byte[] _readBuffer = Array.Empty<byte>();
    private int _readPosition;
    private byte[]? _writtenData;

    public string PortName { get; set; } = "COM_TEST";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Parity Parity { get; set; } = Parity.None;
    public int ReadTimeout { get; set; } = 5000;
    public int WriteTimeout { get; set; } = 5000;
    public bool IsOpen { get; private set; }
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }

    public byte[]? LastWrittenData => _writtenData;

    public void SetupResponse(byte[] responseData)
    {
        ushort crc = CrcCalculator.ComputeCrc16(responseData);
        _readBuffer = new byte[responseData.Length + 2];
        Buffer.BlockCopy(responseData, 0, _readBuffer, 0, responseData.Length);
        _readBuffer[responseData.Length] = (byte)(crc & 0xFF);
        _readBuffer[responseData.Length + 1] = (byte)((crc >> 8) & 0xFF);
        _readPosition = 0;
    }

    public void Open() { IsOpen = true; }
    public void Close() { IsOpen = false; }

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
        _writtenData = new byte[count];
        Buffer.BlockCopy(buffer, offset, _writtenData, 0, count);
    }

    public void Dispose() { Close(); }
}

public class ModbusRtuAddressContextTests
{
    [Fact]
    public void ByteOrderPrefix_OverridesReadAndWrite()
    {
        var readPort = new FakeSerialPort();
        readPort.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0x88, 0x77, 0x66, 0x55 });
        readPort.Open();

        using (var client = new ModbusRtuClient(readPort, station: 1) { ByteOrder = Endianness.BigEndian })
        {
            var result = client.ReadInt32("bo=LittleEndian;40001");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x55667788, result.Content);
            Assert.Equal(Endianness.BigEndian, client.ByteOrder);
        }

        var writePort = new FakeSerialPort();
        writePort.SetupResponse(new byte[] { 0x01, 0x10, 0x00, 0x01, 0x00, 0x02 });
        writePort.Open();

        using (var client = new ModbusRtuClient(writePort, station: 1) { ByteOrder = Endianness.BigEndian })
        {
            var result = client.Write("bo=LittleEndian;40001", 0x11223344);
            Assert.True(result.IsSuccess, result.Message);
            byte[] sent = writePort.LastWrittenData!;
            Assert.Equal(0x44, sent[7]);
            Assert.Equal(0x33, sent[8]);
            Assert.Equal(0x22, sent[9]);
            Assert.Equal(0x11, sent[10]);
            Assert.Equal(Endianness.BigEndian, client.ByteOrder);
        }
    }

    [Fact]
    public void ReadUInt16_AcceptsAddressContextPrefix()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x07, 0x03, 0x02, 0x12, 0x34 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.ReadUInt16("unit=7;40001");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal((ushort)0x1234, result.Content);
        byte[] sent = port.LastWrittenData!;
        Assert.Equal(0x07, sent[0]);
        Assert.Equal(0x00, sent[2]);
        Assert.Equal(0x01, sent[3]);
        Assert.Equal((byte)1, client.Station);
    }
}

public class Crc16Tests
{
    [Fact]
    public void Crc16_KnownVector_01030000000A()
    {
        byte[] frame = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        ushort crc = ModbusRtuClient.Crc16(frame);
        Assert.Equal((ushort)0xCDC5, crc);
    }

    [Fact]
    public void Crc16_SingleByte_IsNonZero()
    {
        ushort crc = ModbusRtuClient.Crc16(new byte[] { 0x01 });
        Assert.NotEqual((ushort)0, crc);
    }

    [Fact]
    public void Crc16_EmptyArray_Returns0xFFFF()
    {
        ushort crc = ModbusRtuClient.Crc16(Array.Empty<byte>());
        Assert.Equal((ushort)0xFFFF, crc);
    }

    [Fact]
    public void Crc16_FullFrameChecksum_IsZero()
    {
        byte[] data = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        ushort crc = ModbusRtuClient.Crc16(data);
        byte[] fullFrame = new byte[data.Length + 2];
        Buffer.BlockCopy(data, 0, fullFrame, 0, data.Length);
        fullFrame[data.Length] = (byte)(crc & 0xFF);
        fullFrame[data.Length + 1] = (byte)((crc >> 8) & 0xFF);
        Assert.Equal((ushort)0, ModbusRtuClient.Crc16(fullFrame));
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x04, 0x00, 0x01, 0x00, 0x01 }, (ushort)0x0A60)]
    [InlineData(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 }, (ushort)0x0A84)]
    [InlineData(new byte[] { 0x02, 0x01, 0x00, 0x00, 0x00, 0x08 }, (ushort)0xFF3D)]
    public void Crc16_VariousVectors_MatchExpected(byte[] data, ushort expected)
    {
        Assert.Equal(expected, ModbusRtuClient.Crc16(data));
    }

    [Fact]
    public void Crc16_WithOffsetAndLength()
    {
        byte[] data = { 0xFF, 0x01, 0x03, 0x00, 0x00, 0x0A, 0xFF };
        ushort crc = ModbusRtuClient.Crc16(data, 1, 5);
        ushort expected = ModbusRtuClient.Crc16(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x0A });
        Assert.Equal(expected, crc);
    }

    [Fact]
    public void Crc16_DelegatesToCrcCalculator()
    {
        byte[] data = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        ushort viaClient = ModbusRtuClient.Crc16(data);
        ushort viaCalculator = CrcCalculator.ComputeCrc16(data);
        Assert.Equal(viaCalculator, viaClient);
    }

    [Fact]
    public void CrcCalculator_VerifyCrc16_ValidFrame()
    {
        byte[] data = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        ushort crc = CrcCalculator.ComputeCrc16(data);
        byte[] fullFrame = new byte[data.Length + 2];
        Buffer.BlockCopy(data, 0, fullFrame, 0, data.Length);
        fullFrame[data.Length] = (byte)(crc & 0xFF);
        fullFrame[data.Length + 1] = (byte)((crc >> 8) & 0xFF);
        Assert.True(CrcCalculator.VerifyCrc16(fullFrame));
    }

    [Fact]
    public void CrcCalculator_VerifyCrc16_InvalidFrame_ReturnsFalse()
    {
        byte[] badFrame = { 0x01, 0x03, 0x02, 0x12, 0x34, 0x00, 0x00 };
        Assert.False(CrcCalculator.VerifyCrc16(badFrame));
    }
}

public class AddressParsingTests
{
    [Theory]
    [InlineData("00001", 0x01, 0x01, 0x05)]
    [InlineData("10001", 0x02, 0x01, 0x00)]
    [InlineData("30001", 0x04, 0x01, 0x00)]
    [InlineData("40001", 0x03, 0x01, 0x06)]
    public void ParseAddressEx_PrefixModes(string address, byte expectedReadFc, ushort expectedAddr, byte expectedWriteFc)
    {
        var port = new FakeSerialPort();
        port.Open();
        using var client = new ModbusRtuClient(port);

        var parsed = typeof(ModbusRtuClient)
            .GetMethod("ParseAddressEx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(client, new object[] { address });

        var tuple = ((ushort address, byte readFc, byte writeFc))parsed!;
        Assert.Equal(expectedAddr, tuple.address);
        Assert.Equal(expectedReadFc, tuple.readFc);
        Assert.Equal(expectedWriteFc, tuple.writeFc);
    }

    [Fact]
    public void ParseAddressEx_NoPrefix_DefaultsToHoldingRegister()
    {
        var port = new FakeSerialPort();
        using var client = new ModbusRtuClient(port);

        var parsed = typeof(ModbusRtuClient)
            .GetMethod("ParseAddressEx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(client, new object[] { "100" });

        var tuple = ((ushort address, byte readFc, byte writeFc))parsed!;
        Assert.Equal((ushort)100, tuple.address);
        Assert.Equal(0x03, tuple.readFc);
        Assert.Equal(0x06, tuple.writeFc);
    }

    [Fact]
    public void ParseAddressEx_ThrowsOnEmpty()
    {
        Assert.Throws<System.Reflection.TargetInvocationException>(() =>
        {
            var port = new FakeSerialPort();
            using var client = new ModbusRtuClient(port);

            typeof(ModbusRtuClient)
                .GetMethod("ParseAddressEx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(client, new object[] { "" });
        });
    }
}

public class FrameBuildingTests
{
    [Fact]
    public void ReadInt16_SendsCorrectRtuFrame()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x03, 0x02, 0x12, 0x34 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.ReadInt16("0");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x1234, result.Content);

        byte[] sent = port.LastWrittenData!;
        Assert.Equal(8, sent.Length);
        Assert.Equal(0x01, sent[0]);
        Assert.Equal(0x03, sent[1]);
        Assert.Equal(0x00, sent[2]);
        Assert.Equal(0x00, sent[3]);
        Assert.Equal(0x00, sent[4]);
        Assert.Equal(0x01, sent[5]);
        ushort crc = CrcCalculator.ComputeCrc16(sent, 0, 6);
        Assert.Equal((byte)(crc & 0xFF), sent[6]);
        Assert.Equal((byte)((crc >> 8) & 0xFF), sent[7]);
    }

    [Fact]
    public void WriteSingleCoil_SendsFC05()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x05, 0x00, 0x01, 0xFF, 0x00 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.Write("00002", true);

        Assert.True(result.IsSuccess);

        byte[] sent = port.LastWrittenData!;
        Assert.Equal(8, sent.Length);
        Assert.Equal(0x05, sent[1]);
        Assert.Equal(0xFF, sent[4]);
        Assert.Equal(0x00, sent[5]);
    }

    [Fact]
    public void WriteSingleRegister_SendsFC06()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x06, 0x00, 0x00, 0x00, 0x64 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.Write("40001", (short)100);

        Assert.True(result.IsSuccess);

        byte[] sent = port.LastWrittenData!;
        Assert.Equal(8, sent.Length);
        Assert.Equal(0x06, sent[1]);
        Assert.Equal(0x00, sent[4]);
        Assert.Equal(0x64, sent[5]);
    }

    [Fact]
    public void WriteMultipleRegisters_SendsFC16()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x02 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        client.Write("40001", 0x12345678);

        byte[] sent = port.LastWrittenData!;
        Assert.Equal(0x10, sent[1]);
    }

    [Fact]
    public void SendCustomModbus_WrapsWithStationAndCrc()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x41, 0x01, 0x02, 0x03 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        byte[] customPdu = { 0x41, 0x01, 0x02, 0x03 };
        var result = client.SendCustomModbus(customPdu);

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Content.Length);
        Assert.Equal(0x41, result.Content[0]);

        byte[] sent = port.LastWrittenData!;
        Assert.Equal(0x01, sent[0]);
        Assert.Equal(0x41, sent[1]);
    }
}

public class ResponseParsingTests
{
    [Fact]
    public void ReadBool_FC01_ParsesCoilTrue()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x01, 0x01, 0x01 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.ReadBool("00001");

        Assert.True(result.IsSuccess);
        Assert.True(result.Content);
    }

    [Fact]
    public void ReadBool_FC02_ParsesDiscreteInputFalse()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x02, 0x01, 0x00 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.ReadBool("10001");

        Assert.True(result.IsSuccess);
        Assert.False(result.Content);
    }

    [Fact]
    public void ReadInt16_FC03_BigEndian()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x03, 0x02, 0x12, 0x34 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.ReadInt16("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x1234, result.Content);
    }

    [Fact]
    public void ReadInt16_FC04_InputRegister()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x04, 0x02, 0xFF, 0xFE });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.ReadInt16("30001");

        Assert.True(result.IsSuccess);
        Assert.Equal((short)-2, result.Content);
    }

    [Fact]
    public void ReadUInt16_ParsesUnsignedValue()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x03, 0x02, 0xFF, 0xFE });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.ReadUInt16("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal((ushort)65534, result.Content);
    }

    [Fact]
    public void ReadInt32_BigEndian_ABCD()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0x12, 0x34, 0x56, 0x78 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1, timeout: 2000) { ByteOrder = Endianness.BigEndian };
        var result = client.ReadInt32("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x12345678, result.Content);
    }

    [Fact]
    public void ReadInt32_LittleEndian_DCBA()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0x78, 0x56, 0x34, 0x12 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1, timeout: 2000) { ByteOrder = Endianness.LittleEndian };
        var result = client.ReadInt32("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x12345678, result.Content);
    }

    [Fact]
    public void ReadInt32_MidBigEndian_BADC()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0x34, 0x12, 0x78, 0x56 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1, timeout: 2000) { ByteOrder = Endianness.MidBigEndian };
        var result = client.ReadInt32("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x12345678, result.Content);
    }

    [Fact]
    public void ReadInt32_MidLittleEndian_CDAB()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0x56, 0x78, 0x12, 0x34 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1, timeout: 2000) { ByteOrder = Endianness.MidLittleEndian };
        var result = client.ReadInt32("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x12345678, result.Content);
    }

    [Fact]
    public void ReadFloat_BigEndian()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0x3F, 0x80, 0x00, 0x00 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1, timeout: 2000) { ByteOrder = Endianness.BigEndian };
        var result = client.ReadFloat("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(1.0f, result.Content);
    }

    [Fact]
    public void ReadDouble_BigEndian()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x03, 0x08, 0x3F, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1, timeout: 2000);
        var result = client.ReadDouble("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(1.0, result.Content);
    }

    [Fact]
    public void ReadBools_MultipleCoils()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x01, 0x02, 0x05, 0x02 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.ReadBools("00001", 10);

        Assert.True(result.IsSuccess);
        Assert.Equal(10, result.Content.Length);
        Assert.True(result.Content[0]);
        Assert.False(result.Content[1]);
        Assert.True(result.Content[2]);
        Assert.False(result.Content[3]);
        Assert.False(result.Content[7]);
        Assert.False(result.Content[8]);
        Assert.True(result.Content[9]);
    }

    [Fact]
    public void ReadString_ParsesAsciiData()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0x41, 0x42, 0x00, 0x00 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.ReadString("40001", 4);

        Assert.True(result.IsSuccess);
        Assert.Equal("AB", result.Content);
    }

    [Fact]
    public void ReadBytes_ReturnsRawData()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0xDE, 0xAD, 0xBE, 0xEF });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.ReadBytes("40001", 4);

        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, result.Content);
    }
}

public class ErrorHandlingTests
{
    [Fact]
    public void ExceptionResponse_FC01_ReturnsError()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x81, 0x02 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.ReadBool("00001");

        Assert.False(result.IsSuccess);
        Assert.Contains("非法数据地址", result.Message);
        Assert.Equal(2, result.ErrorCode);
    }

    [Fact]
    public void StationMismatch_ReturnsError()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x02, 0x03, 0x02, 0x12, 0x34 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.ReadInt16("40001");

        Assert.False(result.IsSuccess);
        Assert.Contains("站号不匹配", result.Message);
    }

    [Fact]
    public void InvalidCrc_ReturnsError()
    {
        var port = new FakeSerialPort();
        byte[] badResponse = { 0x01, 0x03, 0x02, 0x12, 0x34, 0x00, 0x00 };
        typeof(FakeSerialPort)
            .GetField("_readBuffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(port, badResponse);
        typeof(FakeSerialPort)
            .GetField("_readPosition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(port, 0);
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.ReadInt16("40001");

        Assert.False(result.IsSuccess);
        Assert.Contains("CRC", result.Message);
    }
}

public class EndiannessWriteTests
{
    [Fact]
    public void WriteInt32_BigEndian_SendsCorrectBytes()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x02 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1) { ByteOrder = Endianness.BigEndian };
        client.Write("40001", 0x12345678);

        byte[] sent = port.LastWrittenData!;
        Assert.Equal(13, sent.Length);
        Assert.Equal(0x12, sent[7]);
        Assert.Equal(0x34, sent[8]);
        Assert.Equal(0x56, sent[9]);
        Assert.Equal(0x78, sent[10]);
    }

    [Fact]
    public void WriteInt32_LittleEndian_SendsCorrectBytes()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x02 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1) { ByteOrder = Endianness.LittleEndian };
        client.Write("40001", 0x12345678);

        byte[] sent = port.LastWrittenData!;
        Assert.Equal(0x78, sent[7]);
        Assert.Equal(0x56, sent[8]);
        Assert.Equal(0x34, sent[9]);
        Assert.Equal(0x12, sent[10]);
    }

    [Fact]
    public void WriteUInt64_BigEndian_SendsFourRegisters()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x04 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1) { ByteOrder = Endianness.BigEndian };
        var result = client.Write("40001", 0x1122334455667788UL);

        Assert.True(result.IsSuccess, result.Message);
        byte[] sent = port.LastWrittenData!;
        Assert.Equal(17, sent.Length);
        Assert.Equal(0x10, sent[1]);
        Assert.Equal(0x00, sent[4]);
        Assert.Equal(0x04, sent[5]);
        Assert.Equal(0x08, sent[6]);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 }, sent.Skip(7).Take(8).ToArray());
    }

    [Fact]
    public void WriteDouble_BigEndian_SendsFourRegisters()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x04 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1) { ByteOrder = Endianness.BigEndian };
        var result = client.Write("40001", 1.5d);

        Assert.True(result.IsSuccess, result.Message);
        byte[] sent = port.LastWrittenData!;
        Assert.Equal(0x10, sent[1]);
        Assert.Equal(0x00, sent[4]);
        Assert.Equal(0x04, sent[5]);
        Assert.Equal(0x08, sent[6]);
        Assert.Equal(new byte[] { 0x3F, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, sent.Skip(7).Take(8).ToArray());
    }
}

public class StringEncodingTests
{
    [Fact]
    public void ReadStringEncoded_Utf8()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x03, 0x02, 0x48, 0x69 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1) { StringEncodingOption = StringEncoding.Utf8 };
        var result = client.ReadStringEncoded("40001", 2);

        Assert.True(result.IsSuccess);
        Assert.Equal("Hi", result.Content);
    }

    [Fact]
    public void WriteStringEncoded_Utf8()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x01 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1) { StringEncodingOption = StringEncoding.Utf8 };
        var result = client.WriteStringEncoded("40001", "AB");

        Assert.True(result.IsSuccess);

        byte[] sent = port.LastWrittenData!;
        Assert.Equal(0x10, sent[1]);
        Assert.Equal(0x41, sent[7]);
        Assert.Equal(0x42, sent[8]);
    }
}

public class FC23_ReadWriteMultipleTests
{
    [Fact]
    public void ReadWriteMultipleRegisters_SendsFC23()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x17, 0x02, 0xAB, 0xCD });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        byte[] writeData = { 0x00, 0x01 };
        var result = client.ReadWriteMultipleRegisters(0, 1, 100, writeData);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Content.Length);
        Assert.Equal(0xAB, result.Content[0]);
        Assert.Equal(0xCD, result.Content[1]);

        byte[] sent = port.LastWrittenData!;
        Assert.Equal(0x17, sent[1]);
    }
}

public class ConnectionTests
{
    [Fact]
    public void Connect_WhenPortOpens_Succeeds()
    {
        var port = new FakeSerialPort();
        using var client = new ModbusRtuClient(port);

        var result = client.Connect();

        Assert.True(result.IsSuccess);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public void Disconnect_ClosesPort()
    {
        var port = new FakeSerialPort();
        port.Open();
        using var client = new ModbusRtuClient(port);

        client.Disconnect();

        Assert.False(client.IsConnected);
    }

    [Fact]
    public void ReadBool_WhenNotConnected_Fails()
    {
        var port = new FakeSerialPort();
        using var client = new ModbusRtuClient(port);

        var result = client.ReadBool("00001");

        Assert.False(result.IsSuccess);
        Assert.Contains("串口未打开", result.Message);
    }
}

public class WriteMultipleCoilsTests
{
    [Fact]
    public void WriteMultipleCoils_SendsFC15()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x0F, 0x00, 0x00, 0x00, 0x0A });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        bool[] coils = { true, false, true, false, true, false, true, false, false, true };
        var result = client.WriteMultipleCoils(0, coils);

        Assert.True(result.IsSuccess);

        byte[] sent = port.LastWrittenData!;
        Assert.Equal(0x0F, sent[1]);
        Assert.Equal(0x55, sent[7]);
        Assert.Equal(0x02, sent[8]);
    }
}

public class RtuFrameCrcIntegrationTests
{
    [Fact]
    public void RequestFrame_HasValidCrc()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x03, 0x02, 0x00, 0x0A });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        client.ReadInt16("40001");

        byte[] sent = port.LastWrittenData!;
        Assert.True(CrcCalculator.VerifyCrc16(sent));
    }

    [Fact]
    public void ReadUInt32_DelegatesToInt32()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0x00, 0x01, 0x00, 0x00 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        var result = client.ReadUInt32("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal((uint)0x00010000, result.Content);
    }

    [Fact]
    public void WriteFloat_SendsFC16()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x02 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1) { ByteOrder = Endianness.BigEndian };
        client.Write("40001", 1.0f);

        byte[] sent = port.LastWrittenData!;
        Assert.Equal(0x10, sent[1]);
        Assert.Equal(0x3F, sent[7]);
        Assert.Equal(0x80, sent[8]);
        Assert.Equal(0x00, sent[9]);
        Assert.Equal(0x00, sent[10]);
    }

    [Fact]
    public void WriteBytes_SendsFC16()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x02 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        client.Write("40001", new byte[] { 0xCA, 0xFE, 0xBA, 0xBE });

        byte[] sent = port.LastWrittenData!;
        Assert.Equal(0x10, sent[1]);
        Assert.Equal(0xCA, sent[7]);
        Assert.Equal(0xFE, sent[8]);
        Assert.Equal(0xBA, sent[9]);
        Assert.Equal(0xBE, sent[10]);
    }

    [Fact]
    public void WriteString_SendsFC16()
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x01 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: 1);
        client.Write("40001", "AB");

        byte[] sent = port.LastWrittenData!;
        Assert.Equal(0x10, sent[1]);
        Assert.Equal(0x41, sent[7]);
        Assert.Equal(0x42, sent[8]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(247)]
    public void DifferentStationNumbers_AreCorrect(byte station)
    {
        var port = new FakeSerialPort();
        port.SetupResponse(new byte[] { station, 0x03, 0x02, 0x00, 0x01 });
        port.Open();

        using var client = new ModbusRtuClient(port, station: station);
        client.ReadInt16("40001");

        byte[] sent = port.LastWrittenData!;
        Assert.Equal(station, sent[0]);
    }
}
