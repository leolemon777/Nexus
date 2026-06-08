using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nexus;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Modbus.Tests;

internal class RtuOverTcpTestServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private byte[] _responseFrame = Array.Empty<byte>();
    private byte[]? _lastReceivedData;
    private Task? _acceptTask;

    public int Port { get; }
    public byte[]? LastReceivedData => _lastReceivedData;

    public RtuOverTcpTestServer(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = AcceptLoop();
    }

    public void SetupResponse(byte[] rtuPdu)
    {
        ushort crc = CrcCalculator.ComputeCrc16(rtuPdu);
        _responseFrame = new byte[rtuPdu.Length + 2];
        Buffer.BlockCopy(rtuPdu, 0, _responseFrame, 0, rtuPdu.Length);
        _responseFrame[rtuPdu.Length] = (byte)(crc & 0xFF);
        _responseFrame[rtuPdu.Length + 1] = (byte)((crc >> 8) & 0xFF);
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = HandleClient(client);
            }
        }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleClient(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            var buffer = new byte[512];
            while (!_cts.IsCancellationRequested && client.Connected)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                if (bytesRead == 0) break;

                _lastReceivedData = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, _lastReceivedData, 0, bytesRead);

                if (_responseFrame.Length > 0)
                    await stream.WriteAsync(_responseFrame, 0, _responseFrame.Length, _cts.Token);
            }
        }
        catch { }
        finally { client.Dispose(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _listener.Stop();
    }
}

public class RtuOverTcpFrameBuildingTests
{
    [Fact]
    public void ReadInt16_SendsCorrectRtuOverTcpFrame()
    {
        using var server = new RtuOverTcpTestServer();
        // 响应: Station=1, FC=03, ByteCount=2, Data=0x1234
        server.SetupResponse(new byte[] { 0x01, 0x03, 0x02, 0x12, 0x34 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadInt16("0");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x1234, result.Content);

        // 验证发送的帧: Station(1) + FC(1) + Addr(2) + Count(2) + CRC(2) = 8 bytes
        byte[] sent = server.LastReceivedData!;
        Assert.Equal(8, sent.Length);
        Assert.Equal(0x01, sent[0]); // Station
        Assert.Equal(0x03, sent[1]); // FC03
        Assert.Equal(0x00, sent[2]); // Addr high
        Assert.Equal(0x00, sent[3]); // Addr low
        Assert.Equal(0x00, sent[4]); // Count high
        Assert.Equal(0x01, sent[5]); // Count low
        // 验证 CRC
        ushort crc = CrcCalculator.ComputeCrc16(sent, 0, 6);
        Assert.Equal((byte)(crc & 0xFF), sent[6]);
        Assert.Equal((byte)((crc >> 8) & 0xFF), sent[7]);
    }

    [Fact]
    public void WriteSingleCoil_SendsFC05()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x05, 0x00, 0x01, 0xFF, 0x00 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        var result = client.Write("00002", true);

        Assert.True(result.IsSuccess);

        byte[] sent = server.LastReceivedData!;
        Assert.Equal(8, sent.Length);
        Assert.Equal(0x01, sent[0]); // Station
        Assert.Equal(0x05, sent[1]); // FC05
        Assert.Equal(0xFF, sent[4]); // ON = 0xFF00
        Assert.Equal(0x00, sent[5]);
    }

    [Fact]
    public void WriteSingleRegister_SendsFC06()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x06, 0x00, 0x00, 0x00, 0x64 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        var result = client.Write("40001", (short)100);

        Assert.True(result.IsSuccess);

        byte[] sent = server.LastReceivedData!;
        Assert.Equal(8, sent.Length);
        Assert.Equal(0x06, sent[1]); // FC06
        Assert.Equal(0x00, sent[4]); // Value high = 0x0064
        Assert.Equal(0x64, sent[5]); // Value low
    }

    [Fact]
    public void WriteMultipleRegisters_SendsFC16()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x02 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        client.Write("40001", 0x12345678);

        byte[] sent = server.LastReceivedData!;
        Assert.Equal(0x10, sent[1]); // FC16
    }

    [Fact]
    public void Frame_ContainsCrc16_NotMbapHeader()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x03, 0x02, 0x00, 0x01 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        client.ReadInt16("40001");

        byte[] sent = server.LastReceivedData!;
        // RTU frame: no MBAP header (00 00 00 00 ...), just Station + PDU + CRC
        Assert.NotEqual(0x00, sent[0]); // Station byte, not MBAP
        Assert.Equal(0x01, sent[0]);    // Station = 1
        // Verify CRC is valid
        Assert.True(CrcCalculator.VerifyCrc16(sent));
    }
}

public class RtuOverTcpResponseParsingTests
{
    [Fact]
    public void ReadBool_FC01_ParsesCoilTrue()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x01, 0x01, 0x01 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadBool("00001");

        Assert.True(result.IsSuccess);
        Assert.True(result.Content);
    }

    [Fact]
    public void ReadBool_FC02_ParsesDiscreteInputFalse()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x02, 0x01, 0x00 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadBool("10001");

        Assert.True(result.IsSuccess);
        Assert.False(result.Content);
    }

    [Fact]
    public void ReadInt16_FC03_BigEndian()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x03, 0x02, 0x12, 0x34 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadInt16("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x1234, result.Content);
    }

    [Fact]
    public void ReadInt16_FC04_InputRegister()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x04, 0x02, 0xFF, 0xFE });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadInt16("30001");

        Assert.True(result.IsSuccess);
        Assert.Equal((short)-2, result.Content);
    }

    [Fact]
    public void ReadUInt16_ParsesUnsignedValue()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x03, 0x02, 0xFF, 0xFE });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadUInt16("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal((ushort)65534, result.Content);
    }

    [Fact]
    public void ReadInt32_BigEndian_ABCD()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0x12, 0x34, 0x56, 0x78 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1) { ByteOrder = Endianness.BigEndian };
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadInt32("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x12345678, result.Content);
    }

    [Fact]
    public void ReadInt32_LittleEndian_DCBA()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0x78, 0x56, 0x34, 0x12 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1) { ByteOrder = Endianness.LittleEndian };
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadInt32("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x12345678, result.Content);
    }

    [Fact]
    public void ReadInt32_MidBigEndian_BADC()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0x34, 0x12, 0x78, 0x56 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1) { ByteOrder = Endianness.MidBigEndian };
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadInt32("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x12345678, result.Content);
    }

    [Fact]
    public void ReadInt32_MidLittleEndian_CDAB()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0x56, 0x78, 0x12, 0x34 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1) { ByteOrder = Endianness.MidLittleEndian };
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadInt32("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(0x12345678, result.Content);
    }

    [Fact]
    public void ReadFloat_BigEndian()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0x3F, 0x80, 0x00, 0x00 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1) { ByteOrder = Endianness.BigEndian };
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadFloat("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(1.0f, result.Content);
    }

    [Fact]
    public void ReadDouble_BigEndian()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x03, 0x08, 0x3F, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadDouble("40001");

        Assert.True(result.IsSuccess);
        Assert.Equal(1.0, result.Content);
    }

    [Fact]
    public void ReadBools_MultipleCoils()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x01, 0x02, 0x05, 0x02 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadBools("00001", 10);

        Assert.True(result.IsSuccess);
        Assert.Equal(10, result.Content.Length);
        Assert.True(result.Content[0]);   // bit 0 = 1
        Assert.False(result.Content[1]);  // bit 1 = 0
        Assert.True(result.Content[2]);   // bit 2 = 1
        Assert.False(result.Content[3]);  // bit 3 = 0
        Assert.False(result.Content[7]);  // bit 7 = 0
        Assert.False(result.Content[8]);  // bit 8 = 0
        Assert.True(result.Content[9]);   // bit 9 = 1
    }

    [Fact]
    public void ReadString_ParsesAsciiData()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0x41, 0x42, 0x00, 0x00 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadString("40001", 4);

        Assert.True(result.IsSuccess);
        Assert.Equal("AB", result.Content);
    }

    [Fact]
    public void ReadBytes_ReturnsRawData()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x03, 0x04, 0xDE, 0xAD, 0xBE, 0xEF });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadBytes("40001", 4);

        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, result.Content);
    }
}

public class RtuOverTcpErrorHandlingTests
{
    [Fact]
    public void ExceptionResponse_FC01_ReturnsError()
    {
        using var server = new RtuOverTcpTestServer();
        // Error: Station=1, FC=01|0x80=0x81, ExceptionCode=0x02
        server.SetupResponse(new byte[] { 0x01, 0x81, 0x02 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadBool("00001");

        Assert.False(result.IsSuccess);
        Assert.Contains("非法数据地址", result.Message);
        Assert.Equal(2, result.ErrorCode);
    }

    [Fact]
    public void StationMismatch_ReturnsError()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x02, 0x03, 0x02, 0x12, 0x34 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadInt16("40001");

        Assert.False(result.IsSuccess);
        Assert.Contains("站号不匹配", result.Message);
    }

    [Fact]
    public void InvalidCrc_ReturnsError()
    {
        using var server = new RtuOverTcpTestServer();
        // Setup correct response first, then manually set bad CRC
        // Response PDU: Station=1, FC=03, ByteCount=2, Data=0x1234
        // With wrong CRC: 0x00, 0x00
        byte[] badFrame = { 0x01, 0x03, 0x02, 0x12, 0x34, 0x00, 0x00 };
        // Directly set the response frame without CRC calculation
        typeof(RtuOverTcpTestServer)
            .GetField("_responseFrame", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(server, badFrame);

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadInt16("40001");

        Assert.False(result.IsSuccess);
        Assert.Contains("CRC", result.Message);
    }

    [Fact]
    public void AllExceptionCodes_ReturnCorrectMessage()
    {
        // FC=1 (illegal function)
        using var server1 = new RtuOverTcpTestServer();
        server1.SetupResponse(new byte[] { 0x01, 0x81, 0x01 });
        using var client1 = new ModbusRtuOverTcpClient("127.0.0.1", server1.Port, station: 1);
        client1.SetPersistentConnection();
        client1.Connect();
        var r1 = client1.ReadBool("00001");
        Assert.False(r1.IsSuccess);
        Assert.Contains("非法功能码", r1.Message);

        // FC=3 (illegal data value)
        using var server3 = new RtuOverTcpTestServer();
        server3.SetupResponse(new byte[] { 0x01, 0x83, 0x03 });
        using var client3 = new ModbusRtuOverTcpClient("127.0.0.1", server3.Port, station: 1);
        client3.SetPersistentConnection();
        client3.Connect();
        var r3 = client3.ReadInt16("40001");
        Assert.False(r3.IsSuccess);
        Assert.Contains("非法数据值", r3.Message);

        // FC=4 (server device failure)
        using var server4 = new RtuOverTcpTestServer();
        server4.SetupResponse(new byte[] { 0x01, 0x84, 0x04 });
        using var client4 = new ModbusRtuOverTcpClient("127.0.0.1", server4.Port, station: 1);
        client4.SetPersistentConnection();
        client4.Connect();
        var r4 = client4.ReadInt16("40001");
        Assert.False(r4.IsSuccess);
        Assert.Contains("从站设备故障", r4.Message);
    }
}

public class RtuOverTcpAddressParsingTests
{
    [Theory]
    [InlineData("00001", 0x01, 0x01, 0x05)]
    [InlineData("10001", 0x02, 0x01, 0x00)]
    [InlineData("30001", 0x04, 0x01, 0x00)]
    [InlineData("40001", 0x03, 0x01, 0x06)]
    public void ParseAddressEx_PrefixModes(string address, byte expectedReadFc, ushort expectedAddr, byte expectedWriteFc)
    {
        var parsed = typeof(ModbusRtuOverTcpClient)
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
        var parsed = typeof(ModbusRtuOverTcpClient)
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
            typeof(ModbusRtuOverTcpClient)
                .GetMethod("ParseAddressEx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { "" });
        });
    }
}

public class RtuOverTcpEndiannessWriteTests
{
    [Fact]
    public void WriteInt32_BigEndian_SendsCorrectBytes()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x02 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1) { ByteOrder = Endianness.BigEndian };
        client.SetPersistentConnection();
        client.Connect();

        client.Write("40001", 0x12345678);

        byte[] sent = server.LastReceivedData!;
        // FC16 frame: Station(1)+FC(1)+Addr(2)+Count(2)+ByteCount(1)+Data(4)+CRC(2) = 13
        Assert.Equal(13, sent.Length);
        Assert.Equal(0x12, sent[7]); // Data starts at offset 7
        Assert.Equal(0x34, sent[8]);
        Assert.Equal(0x56, sent[9]);
        Assert.Equal(0x78, sent[10]);
    }

    [Fact]
    public void WriteInt32_LittleEndian_SendsCorrectBytes()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x02 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1) { ByteOrder = Endianness.LittleEndian };
        client.SetPersistentConnection();
        client.Connect();

        client.Write("40001", 0x12345678);

        byte[] sent = server.LastReceivedData!;
        Assert.Equal(0x78, sent[7]);
        Assert.Equal(0x56, sent[8]);
        Assert.Equal(0x34, sent[9]);
        Assert.Equal(0x12, sent[10]);
    }
}

public class RtuOverTcpWriteMultipleCoilsTests
{
    [Fact]
    public void WriteMultipleCoils_SendsFC15()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x0F, 0x00, 0x00, 0x00, 0x0A });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        bool[] coils = { true, false, true, false, true, false, true, false, false, true };
        var result = client.WriteMultipleCoils(0, coils);

        Assert.True(result.IsSuccess);

        byte[] sent = server.LastReceivedData!;
        Assert.Equal(0x0F, sent[1]); // FC15
        Assert.Equal(0x55, sent[7]); // First coil byte
        Assert.Equal(0x02, sent[8]); // Second coil byte
    }
}

public class RtuOverTcpFC23Tests
{
    [Fact]
    public void ReadWriteMultipleRegisters_SendsFC23()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x17, 0x02, 0xAB, 0xCD });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        byte[] writeData = { 0x00, 0x01 };
        var result = client.ReadWriteMultipleRegisters(0, 1, 100, writeData);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Content.Length);
        Assert.Equal(0xAB, result.Content[0]);
        Assert.Equal(0xCD, result.Content[1]);

        byte[] sent = server.LastReceivedData!;
        Assert.Equal(0x17, sent[1]); // FC23
    }
}

public class RtuOverTcpStringEncodingTests
{
    [Fact]
    public void ReadStringEncoded_Utf8()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x03, 0x02, 0x48, 0x69 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1)
        {
            StringEncodingOption = StringEncoding.Utf8
        };
        client.SetPersistentConnection();
        client.Connect();

        var result = client.ReadStringEncoded("40001", 2);

        Assert.True(result.IsSuccess);
        Assert.Equal("Hi", result.Content);
    }

    [Fact]
    public void WriteStringEncoded_Utf8()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x10, 0x00, 0x00, 0x00, 0x01 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1)
        {
            StringEncodingOption = StringEncoding.Utf8
        };
        client.SetPersistentConnection();
        client.Connect();

        var result = client.WriteStringEncoded("40001", "AB");

        Assert.True(result.IsSuccess);

        byte[] sent = server.LastReceivedData!;
        Assert.Equal(0x10, sent[1]); // FC16
        Assert.Equal(0x41, sent[7]); // 'A'
        Assert.Equal(0x42, sent[8]); // 'B'
    }
}

public class RtuOverTcpConnectionTests
{
    [Fact]
    public void Connect_Succeeds()
    {
        using var server = new RtuOverTcpTestServer();
        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port);

        var result = client.Connect();

        Assert.True(result.IsSuccess);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public void Disconnect_ClosesConnection()
    {
        using var server = new RtuOverTcpTestServer();
        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port);
        client.Connect();

        client.Disconnect();

        Assert.False(client.IsConnected);
    }

    [Fact]
    public void CustomModbus_WrapsWithStationAndCrc()
    {
        using var server = new RtuOverTcpTestServer();
        server.SetupResponse(new byte[] { 0x01, 0x41, 0x01, 0x02, 0x03 });

        using var client = new ModbusRtuOverTcpClient("127.0.0.1", server.Port, station: 1);
        client.SetPersistentConnection();
        client.Connect();

        byte[] customPdu = { 0x41, 0x01, 0x02, 0x03 };
        var result = client.SendCustomModbus(customPdu);

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Content.Length);
        Assert.Equal(0x41, result.Content[0]);

        byte[] sent = server.LastReceivedData!;
        Assert.Equal(0x01, sent[0]); // Station
        Assert.Equal(0x41, sent[1]); // Custom FC
        Assert.True(CrcCalculator.VerifyCrc16(sent));
    }
}
