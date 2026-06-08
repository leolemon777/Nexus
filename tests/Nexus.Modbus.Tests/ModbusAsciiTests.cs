using System;
using System.Text;
using Nexus;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Modbus.Tests;

internal class AsciiFakeSerialPort : ISerialPort
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

    public void SetupAsciiResponse(byte[] responseData)
    {
        byte lrc = CrcCalculator.ComputeLrc(responseData);
        byte[] withLrc = new byte[responseData.Length + 1];
        Buffer.BlockCopy(responseData, 0, withLrc, 0, responseData.Length);
        withLrc[responseData.Length] = lrc;

        string hex = BytesToHex(withLrc);
        string frame = ":" + hex + "\r\n";
        _readBuffer = Encoding.ASCII.GetBytes(frame);
        _readPosition = 0;
    }

    public void SetupRawAsciiResponse(string asciiResponse)
    {
        _readBuffer = Encoding.ASCII.GetBytes(asciiResponse);
        _readPosition = 0;
    }

    private static string BytesToHex(byte[] data)
    {
        char[] chars = new char[data.Length * 2];
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            chars[i * 2] = "0123456789ABCDEF"[((b >> 4) & 0x0F)];
            chars[i * 2 + 1] = "0123456789ABCDEF"[(b & 0x0F)];
        }
        return new string(chars);
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

public class AsciiFrameBuildingTests
{
    [Fact]
    public void BuildFrame_ReadInt16_SendsCorrectAsciiFrame()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x02, 0x12, 0x34 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        var result = client.ReadInt16("0");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x1234, result.Content);

        byte[] sent = port.LastWrittenData!;
        string sentStr = Encoding.ASCII.GetString(sent);

        Assert.StartsWith(":", sentStr);
        Assert.EndsWith("\r\n", sentStr);

        string hex = sentStr.Substring(1).TrimEnd('\r', '\n');
        byte[] raw = HexToBytes(hex);
        Assert.Equal(0x01, raw[0]); // Station
        Assert.Equal(0x03, raw[1]); // FC03
        Assert.Equal(0x00, raw[2]); // Addr high
        Assert.Equal(0x00, raw[3]); // Addr low
        Assert.Equal(0x00, raw[4]); // Count high
        Assert.Equal(0x01, raw[5]); // Count low

        byte lrc = CrcCalculator.ComputeLrc(raw, 0, raw.Length - 1);
        Assert.Equal(lrc, raw[raw.Length - 1]);
    }

    [Fact]
    public void BuildFrame_WriteSingleCoil_SendsFC05()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x05, 0x00, 0x01, 0xFF, 0x00 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        var result = client.Write("00002", true);

        Assert.True(result.IsSuccess);

        byte[] sent = port.LastWrittenData!;
        string sentStr = Encoding.ASCII.GetString(sent);
        string hex = sentStr.Substring(1).TrimEnd('\r', '\n');
        byte[] raw = HexToBytes(hex);
        Assert.Equal(0x05, raw[1]); // FC05
        Assert.Equal(0xFF, raw[4]); // ON = 0xFF00
        Assert.Equal(0x00, raw[5]);
    }

    [Fact]
    public void BuildFrame_WriteSingleRegister_SendsFC06()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x06, 0x00, 0x00, 0x00, 0x64 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        var result = client.Write("40001", (short)100);

        Assert.True(result.IsSuccess);

        byte[] sent = port.LastWrittenData!;
        string sentStr = Encoding.ASCII.GetString(sent);
        string hex = sentStr.Substring(1).TrimEnd('\r', '\n');
        byte[] raw = HexToBytes(hex);
        Assert.Equal(0x06, raw[1]); // FC06
        Assert.Equal(0x00, raw[4]); // Value high = 0x0064
        Assert.Equal(0x64, raw[5]); // Value low
    }

    [Fact]
    public void BuildFrame_WriteMultipleRegisters_SendsFC16()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x02 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        client.Write("40001", 0x12345678);

        byte[] sent = port.LastWrittenData!;
        string sentStr = Encoding.ASCII.GetString(sent);
        string hex = sentStr.Substring(1).TrimEnd('\r', '\n');
        byte[] raw = HexToBytes(hex);
        Assert.Equal(0x10, raw[1]); // FC16
    }

    private static byte[] HexToBytes(string hex)
    {
        byte[] result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (byte)((HexVal(hex[i * 2]) << 4) | HexVal(hex[i * 2 + 1]));
        }
        return result;
    }

    private static int HexVal(char c) => c >= '0' && c <= '9' ? c - '0' :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0;
}

public class AsciiResponseParsingTests
{
    [Fact]
    public void ReadBool_FC01_ParsesCoilTrue()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x01, 0x01, 0x01 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        var result = client.ReadBool("00001");

        Assert.True(result.IsSuccess);
        Assert.True(result.Content);
    }

    [Fact]
    public void ReadBool_FC02_ParsesDiscreteInputFalse()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x02, 0x01, 0x00 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        var result = client.ReadBool("10001");

        Assert.True(result.IsSuccess);
        Assert.False(result.Content);
    }

    [Fact]
    public void ReadInt16_FC03_BigEndian()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x02, 0x12, 0x34 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        var result = client.ReadInt16("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x1234, result.Content);
    }

    [Fact]
    public void ReadInt16_FC04_InputRegister()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x04, 0x02, 0xFF, 0xFE });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        var result = client.ReadInt16("30001");

        Assert.True(result.IsSuccess);
        Assert.Equal((short)-2, result.Content);
    }

    [Fact]
    public void ReadUInt16_ParsesUnsignedValue()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x02, 0xFF, 0xFE });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        var result = client.ReadUInt16("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal((ushort)65534, result.Content);
    }

    [Fact]
    public void ReadInt32_BigEndian_ABCD()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x04, 0x12, 0x34, 0x56, 0x78 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0, ByteOrder = Endianness.BigEndian };
        var result = client.ReadInt32("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x12345678, result.Content);
    }

    [Fact]
    public void ReadInt32_LittleEndian_DCBA()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x04, 0x78, 0x56, 0x34, 0x12 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0, ByteOrder = Endianness.LittleEndian };
        var result = client.ReadInt32("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x12345678, result.Content);
    }

    [Fact]
    public void ReadInt32_MidBigEndian_BADC()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x04, 0x34, 0x12, 0x78, 0x56 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0, ByteOrder = Endianness.MidBigEndian };
        var result = client.ReadInt32("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x12345678, result.Content);
    }

    [Fact]
    public void ReadInt32_MidLittleEndian_CDAB()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x04, 0x56, 0x78, 0x12, 0x34 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0, ByteOrder = Endianness.MidLittleEndian };
        var result = client.ReadInt32("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x12345678, result.Content);
    }

    [Fact]
    public void ReadFloat_BigEndian()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x04, 0x3F, 0x80, 0x00, 0x00 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0, ByteOrder = Endianness.BigEndian };
        var result = client.ReadFloat("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(1.0f, result.Content);
    }

    [Fact]
    public void ReadDouble_BigEndian()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x08, 0x3F, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        var result = client.ReadDouble("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(1.0, result.Content);
    }

    [Fact]
    public void ReadBools_MultipleCoils()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x01, 0x02, 0x05, 0x02 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        var result = client.ReadBools("00001", 10);

        Assert.True(result.IsSuccess);
        Assert.Equal(10, result.Content.Length);
        Assert.True(result.Content[0]);
        Assert.False(result.Content[1]);
        Assert.True(result.Content[2]);
        Assert.False(result.Content[3]);
        Assert.False(result.Content[8]);
        Assert.True(result.Content[9]);
    }

    [Fact]
    public void ReadString_ParsesAsciiData()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x04, 0x41, 0x42, 0x00, 0x00 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        var result = client.ReadString("40001", 4);

        Assert.True(result.IsSuccess);
        Assert.Equal("AB", result.Content);
    }

    [Fact]
    public void ReadBytes_ReturnsRawData()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x04, 0xDE, 0xAD, 0xBE, 0xEF });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        var result = client.ReadBytes("40001", 4);

        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, result.Content);
    }
}

public class AsciiErrorHandlingTests
{
    [Fact]
    public void ExceptionResponse_FC01_ReturnsError()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x81, 0x02 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        var result = client.ReadBool("00001");

        Assert.False(result.IsSuccess);
        Assert.Contains("非法数据地址", result.Message);
        Assert.Equal(2, result.ErrorCode);
    }

    [Fact]
    public void StationMismatch_ReturnsError()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x02, 0x03, 0x02, 0x12, 0x34 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        var result = client.ReadInt16("40001");

        Assert.False(result.IsSuccess);
        Assert.Contains("站号不匹配", result.Message);
    }

    [Fact]
    public void InvalidLrc_ReturnsError()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupRawAsciiResponse(":010302123400\r\n");

        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        var result = client.ReadInt16("40001");

        Assert.False(result.IsSuccess);
        Assert.Contains("LRC", result.Message);
    }
}

public class AsciiEndiannessWriteTests
{
    [Fact]
    public void WriteInt32_BigEndian_SendsCorrectBytes()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x02 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0, ByteOrder = Endianness.BigEndian };
        client.Write("40001", 0x12345678);

        byte[] sent = port.LastWrittenData!;
        string sentStr = Encoding.ASCII.GetString(sent);
        string hex = sentStr.Substring(1).TrimEnd('\r', '\n');
        byte[] raw = HexToBytes(hex);
        // PDU starts at raw[1]: FC(1) + Addr(2) + Count(2) + ByteCount(1) + Data(4)
        Assert.Equal(0x10, raw[1]); // FC16
        Assert.Equal(0x12, raw[7]); // Data high
        Assert.Equal(0x34, raw[8]);
        Assert.Equal(0x56, raw[9]);
        Assert.Equal(0x78, raw[10]);
    }

    [Fact]
    public void WriteInt32_LittleEndian_SendsCorrectBytes()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x02 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0, ByteOrder = Endianness.LittleEndian };
        client.Write("40001", 0x12345678);

        byte[] sent = port.LastWrittenData!;
        string sentStr = Encoding.ASCII.GetString(sent);
        string hex = sentStr.Substring(1).TrimEnd('\r', '\n');
        byte[] raw = HexToBytes(hex);
        Assert.Equal(0x78, raw[7]);
        Assert.Equal(0x56, raw[8]);
        Assert.Equal(0x34, raw[9]);
        Assert.Equal(0x12, raw[10]);
    }

    private static byte[] HexToBytes(string hex)
    {
        byte[] result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (byte)((HexVal(hex[i * 2]) << 4) | HexVal(hex[i * 2 + 1]));
        }
        return result;
    }

    private static int HexVal(char c) => c >= '0' && c <= '9' ? c - '0' :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0;
}

public class AsciiFC23Tests
{
    [Fact]
    public void ReadWriteMultipleRegisters_SendsFC23()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x17, 0x02, 0xAB, 0xCD });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        byte[] writeData = { 0x00, 0x01 };
        var result = client.ReadWriteMultipleRegisters(0, 1, 100, writeData);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Content.Length);
        Assert.Equal(0xAB, result.Content[0]);
        Assert.Equal(0xCD, result.Content[1]);

        byte[] sent = port.LastWrittenData!;
        string sentStr = Encoding.ASCII.GetString(sent);
        string hex = sentStr.Substring(1).TrimEnd('\r', '\n');
        byte[] raw = HexToBytes(hex);
        Assert.Equal(0x17, raw[1]); // FC23
    }

    private static byte[] HexToBytes(string hex)
    {
        byte[] result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (byte)((HexVal(hex[i * 2]) << 4) | HexVal(hex[i * 2 + 1]));
        }
        return result;
    }

    private static int HexVal(char c) => c >= '0' && c <= '9' ? c - '0' :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0;
}

public class AsciiConnectionTests
{
    [Fact]
    public void Connect_WhenPortOpens_Succeeds()
    {
        var port = new AsciiFakeSerialPort();
        using var client = new ModbusAsciiClient(port);

        var result = client.Connect();

        Assert.True(result.IsSuccess);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public void Disconnect_ClosesPort()
    {
        var port = new AsciiFakeSerialPort();
        port.Open();
        using var client = new ModbusAsciiClient(port);

        client.Disconnect();

        Assert.False(client.IsConnected);
    }

    [Fact]
    public void ReadBool_WhenNotConnected_Fails()
    {
        var port = new AsciiFakeSerialPort();
        using var client = new ModbusAsciiClient(port, timeout: 2000) { InterFrameDelay = 0 };

        var result = client.ReadBool("00001");

        Assert.False(result.IsSuccess);
        Assert.Contains("串口未打开", result.Message);
    }
}

public class AsciiWriteMultipleCoilsTests
{
    [Fact]
    public void WriteMultipleCoils_SendsFC15()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x0F, 0x00, 0x00, 0x00, 0x0A });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        bool[] coils = { true, false, true, false, true, false, true, false, false, true };
        var result = client.WriteMultipleCoils(0, coils);

        Assert.True(result.IsSuccess);

        byte[] sent = port.LastWrittenData!;
        string sentStr = Encoding.ASCII.GetString(sent);
        string hex = sentStr.Substring(1).TrimEnd('\r', '\n');
        byte[] raw = HexToBytes(hex);
        Assert.Equal(0x0F, raw[1]); // FC15
        Assert.Equal(0x55, raw[7]); // First coil byte
        Assert.Equal(0x02, raw[8]); // Second coil byte
    }

    private static byte[] HexToBytes(string hex)
    {
        byte[] result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (byte)((HexVal(hex[i * 2]) << 4) | HexVal(hex[i * 2 + 1]));
        }
        return result;
    }

    private static int HexVal(char c) => c >= '0' && c <= '9' ? c - '0' :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0;
}

public class AsciiAddressParsingTests
{
    [Theory]
    [InlineData("00001", 0x01, 0x01, 0x05)]
    [InlineData("10001", 0x02, 0x01, 0x00)]
    [InlineData("30001", 0x04, 0x01, 0x00)]
    [InlineData("40001", 0x03, 0x01, 0x06)]
    public void ParseAddressEx_PrefixModes(string address, byte expectedReadFc, ushort expectedAddr, byte expectedWriteFc)
    {
        var parsed = typeof(ModbusAsciiClient)
            .GetMethod("ParseAddressEx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { address });

        var tuple = ((ushort address, byte readFc, byte writeFc))parsed!;
        Assert.Equal(expectedAddr, tuple.address);
        Assert.Equal(expectedReadFc, tuple.readFc);
        Assert.Equal(expectedWriteFc, tuple.writeFc);
    }

    [Fact]
    public void ParseAddressEx_NoPrefix_DefaultsToHoldingRegister()
    {
        var parsed = typeof(ModbusAsciiClient)
            .GetMethod("ParseAddressEx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { "100" });

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
            typeof(ModbusAsciiClient)
                .GetMethod("ParseAddressEx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { "" });
        });
    }
}

public class AsciiStringEncodingTests
{
    [Fact]
    public void ReadStringEncoded_Utf8()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x02, 0x48, 0x69 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0, StringEncodingOption = StringEncoding.Utf8 };
        var result = client.ReadStringEncoded("40001", 2);

        Assert.True(result.IsSuccess);
        Assert.Equal("Hi", result.Content);
    }

    [Fact]
    public void WriteStringEncoded_Utf8()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x01 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0, StringEncodingOption = StringEncoding.Utf8 };
        var result = client.WriteStringEncoded("40001", "AB");

        Assert.True(result.IsSuccess);

        byte[] sent = port.LastWrittenData!;
        string sentStr = Encoding.ASCII.GetString(sent);
        string hex = sentStr.Substring(1).TrimEnd('\r', '\n');
        byte[] raw = HexToBytes(hex);
        Assert.Equal(0x10, raw[1]); // FC16
        Assert.Equal(0x41, raw[7]); // 'A'
        Assert.Equal(0x42, raw[8]); // 'B'
    }

    private static byte[] HexToBytes(string hex)
    {
        byte[] result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (byte)((HexVal(hex[i * 2]) << 4) | HexVal(hex[i * 2 + 1]));
        }
        return result;
    }

    private static int HexVal(char c) => c >= '0' && c <= '9' ? c - '0' :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0;
}

public class AsciiCustomModbusTests
{
    [Fact]
    public void SendCustomModbus_WrapsWithStationAndLrc()
    {
        var port = new AsciiFakeSerialPort();
        port.SetupAsciiResponse(new byte[] { 0x01, 0x41, 0x01, 0x02, 0x03 });
        port.Open();

        using var client = new ModbusAsciiClient(port, station: 1, timeout: 2000) { InterFrameDelay = 0 };
        byte[] customPdu = { 0x41, 0x01, 0x02, 0x03 };
        var result = client.SendCustomModbus(customPdu);

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Content.Length);
        Assert.Equal(0x41, result.Content[0]);

        byte[] sent = port.LastWrittenData!;
        string sentStr = Encoding.ASCII.GetString(sent);
        Assert.StartsWith(":", sentStr);
        Assert.EndsWith("\r\n", sentStr);
    }
}
