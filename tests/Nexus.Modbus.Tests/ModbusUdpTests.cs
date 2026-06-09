using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Nexus;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Modbus.Tests;

internal sealed class ModbusUdpTestServer : IDisposable
{
    private readonly UdpClient _udp;
    private Thread? _thread;
    private volatile bool _running;
    private readonly object _lock = new object();

    private readonly bool[] _coils = new bool[65536];
    private readonly bool[] _discreteInputs = new bool[65536];
    private readonly ushort[] _holdingRegisters = new ushort[65536];
    private readonly ushort[] _inputRegisters = new ushort[65536];

    public int Port { get; }

    public ModbusUdpTestServer(int port = 0)
    {
        _udp = new UdpClient(port);
        _udp.Client.ReceiveTimeout = 5000;
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    }

    public void SetHoldingRegister(ushort address, ushort value) { lock (_lock) _holdingRegisters[address] = value; }
    public void SetInputRegister(ushort address, ushort value) { lock (_lock) _inputRegisters[address] = value; }
    public void SetCoil(ushort address, bool value) { lock (_lock) _coils[address] = value; }
    public void SetDiscreteInput(ushort address, bool value) { lock (_lock) _discreteInputs[address] = value; }

    public void Start()
    {
        _running = true;
        _thread = new Thread(ReceiveLoop) { IsBackground = true };
        _thread.Start();
    }

    public void Stop() { _running = false; }

    private void ReceiveLoop()
    {
        while (_running)
        {
            try
            {
                IPEndPoint? remote = null;
                byte[] data = _udp.Receive(ref remote);
                if (data.Length < 8) continue;

                int length = (data[4] << 8) | data[5];
                byte unitId = data[6];
                byte[] pdu = new byte[data.Length - 7];
                Buffer.BlockCopy(data, 7, pdu, 0, pdu.Length);

                byte[]? respPdu = ProcessPdu(pdu);
                if (respPdu == null) continue;

                byte[] response = new byte[7 + respPdu.Length];
                Buffer.BlockCopy(data, 0, response, 0, 6);
                response[6] = unitId;
                int respLen = respPdu.Length + 1;
                response[4] = (byte)(respLen >> 8);
                response[5] = (byte)respLen;
                Buffer.BlockCopy(respPdu, 0, response, 7, respPdu.Length);

                _udp.Send(response, response.Length, remote!);
            }
            catch { if (!_running) break; }
        }
    }

    private byte[]? ProcessPdu(byte[] pdu)
    {
        if (pdu.Length < 1) return null;
        byte fc = pdu[0];
        try
        {
            return fc switch
            {
                0x01 => ReadBits(pdu, _coils),
                0x02 => ReadBits(pdu, _discreteInputs),
                0x03 => ReadRegisters(pdu, _holdingRegisters),
                0x04 => ReadRegisters(pdu, _inputRegisters),
                0x05 => WriteSingleCoil(pdu),
                0x06 => WriteSingleRegister(pdu),
                0x0F => WriteMultipleCoils(pdu),
                0x10 => WriteMultipleRegisters(pdu),
                0x16 => MaskWriteRegister(pdu),
                0x17 => ReadWriteMultiple(pdu),
                _ => BuildException(fc, 1)
            };
        }
        catch { return BuildException(fc, 4); }
    }

    private byte[] ReadBits(byte[] pdu, bool[] store)
    {
        ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
        ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
        int byteCount = (count + 7) / 8;
        byte[] data = new byte[byteCount];
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
                if (store[addr + i]) data[i / 8] |= (byte)(1 << (i % 8));
        }
        byte[] result = new byte[2 + byteCount];
        result[0] = pdu[0]; result[1] = (byte)byteCount;
        Buffer.BlockCopy(data, 0, result, 2, byteCount);
        return result;
    }

    private byte[] ReadRegisters(byte[] pdu, ushort[] store)
    {
        ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
        ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
        int byteCount = count * 2;
        byte[] result = new byte[2 + byteCount];
        result[0] = pdu[0]; result[1] = (byte)byteCount;
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                ushort val = store[addr + i];
                result[2 + i * 2] = (byte)(val >> 8);
                result[3 + i * 2] = (byte)val;
            }
        }
        return result;
    }

    private byte[] WriteSingleCoil(byte[] pdu)
    {
        ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
        bool value = pdu[3] == 0xFF;
        lock (_lock) _coils[addr] = value;
        return pdu;
    }

    private byte[] WriteSingleRegister(byte[] pdu)
    {
        ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
        ushort value = (ushort)((pdu[3] << 8) | pdu[4]);
        lock (_lock) _holdingRegisters[addr] = value;
        return pdu;
    }

    private byte[] WriteMultipleCoils(byte[] pdu)
    {
        ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
        ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
                _coils[addr + i] = (pdu[6 + i / 8] & (1 << (i % 8))) != 0;
        }
        return new byte[] { 0x0F, pdu[1], pdu[2], pdu[3], pdu[4] };
    }

    private byte[] WriteMultipleRegisters(byte[] pdu)
    {
        ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
        ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
                _holdingRegisters[addr + i] = (ushort)((pdu[6 + i * 2] << 8) | pdu[7 + i * 2]);
        }
        return new byte[] { 0x10, pdu[1], pdu[2], pdu[3], pdu[4] };
    }

    private byte[] MaskWriteRegister(byte[] pdu)
    {
        ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
        ushort andMask = (ushort)((pdu[3] << 8) | pdu[4]);
        ushort orMask = (ushort)((pdu[5] << 8) | pdu[6]);
        lock (_lock)
        {
            ushort current = _holdingRegisters[addr];
            _holdingRegisters[addr] = (ushort)((current & andMask) | (orMask & ~andMask));
        }
        return new byte[] { 0x16, pdu[1], pdu[2], pdu[3], pdu[4], pdu[5], pdu[6] };
    }

    private byte[] ReadWriteMultiple(byte[] pdu)
    {
        ushort readAddr = (ushort)((pdu[1] << 8) | pdu[2]);
        ushort readCount = (ushort)((pdu[3] << 8) | pdu[4]);
        ushort writeAddr = (ushort)((pdu[5] << 8) | pdu[6]);
        ushort writeCount = (ushort)((pdu[7] << 8) | pdu[8]);
        byte writeByteCount = pdu[9];

        lock (_lock)
        {
            for (int i = 0; i < writeCount; i++)
                _holdingRegisters[writeAddr + i] = (ushort)((pdu[10 + i * 2] << 8) | pdu[11 + i * 2]);
        }

        int readByteCount = readCount * 2;
        byte[] result = new byte[2 + readByteCount];
        result[0] = 0x17;
        result[1] = (byte)readByteCount;
        lock (_lock)
        {
            for (int i = 0; i < readCount; i++)
            {
                ushort val = _holdingRegisters[readAddr + i];
                result[2 + i * 2] = (byte)(val >> 8);
                result[3 + i * 2] = (byte)val;
            }
        }
        return result;
    }

    private static byte[] BuildException(byte fc, byte code) => new byte[] { (byte)(fc | 0x80), code };

    public void Dispose() { _running = false; try { _udp.Close(); } catch { } }
}

public class ModbusUdpConnectionTests
{
    [Fact]
    public void Connect_Succeeds()
    {
        using var client = new ModbusUdpClient("127.0.0.1", 19990, station: 1);
        var conn = client.Connect();
        Assert.True(conn.IsSuccess, conn.Message);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public void Disconnect_SetsNotConnected()
    {
        using var client = new ModbusUdpClient("127.0.0.1", 19991, station: 1);
        client.Connect();
        client.Disconnect();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Station_DefaultIsOne()
    {
        using var client = new ModbusUdpClient("127.0.0.1", 19992);
        Assert.Equal(1, client.Station);
    }

    [Fact]
    public void ByteOrder_DefaultIsBigEndian()
    {
        using var client = new ModbusUdpClient("127.0.0.1", 19993);
        Assert.Equal(Endianness.BigEndian, client.ByteOrder);
    }
}

public class ModbusUdpReadTests
{
    [Fact]
    public void ReadInt16_HoldingRegister()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetHoldingRegister(100, 0x1234);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        var r = client.ReadInt16("100");
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal((short)0x1234, r.Content);
    }

    [Fact]
    public void ReadUInt16_HoldingRegister()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetHoldingRegister(200, 60000);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        var r = client.ReadUInt16("200");
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal((ushort)60000, r.Content);
    }

    [Fact]
    public void ReadInt32_BigEndian()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetHoldingRegister(300, 0x1234);
        server.SetHoldingRegister(301, 0x5678);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1)
        {
            ByteOrder = Endianness.BigEndian
        };
        client.Connect();

        var r = client.ReadInt32("300");
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(0x12345678, r.Content);
    }

    [Fact]
    public void ReadFloat_BigEndian()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetHoldingRegister(400, 0x3F80);
        server.SetHoldingRegister(401, 0x0000);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1)
        {
            ByteOrder = Endianness.BigEndian
        };
        client.Connect();

        var r = client.ReadFloat("400");
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(1.0f, r.Content);
    }

    [Fact]
    public void ReadBool_Coil()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetCoil(10, true);
        server.SetCoil(11, false);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        Assert.True(client.ReadBool("00010").Content);
        Assert.False(client.ReadBool("00011").Content);
    }

    [Fact]
    public void ReadBool_DiscreteInput()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetDiscreteInput(5, true);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        Assert.True(client.ReadBool("10005").Content);
    }

    [Fact]
    public void ReadBools_MultipleCoils()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetCoil(0, true);
        server.SetCoil(2, true);
        server.SetCoil(9, true);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        var r = client.ReadBools("00000", 10);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(10, r.Content.Length);
        Assert.True(r.Content[0]);
        Assert.False(r.Content[1]);
        Assert.True(r.Content[2]);
        Assert.True(r.Content[9]);
    }

    [Fact]
    public void ReadInputRegister_WithPrefix()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetInputRegister(50, 9999);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        var r = client.ReadUInt16("30050");
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal((ushort)9999, r.Content);
    }

    [Fact]
    public void ReadString_ParsesAscii()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetHoldingRegister(600, 0x4142);
        server.SetHoldingRegister(601, 0x4300);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        var r = client.ReadString("600", 3);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal("ABC", r.Content);
    }

    [Fact]
    public void ReadBytes_ReturnsRawData()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetHoldingRegister(700, 0xDEAD);
        server.SetHoldingRegister(701, 0xBEEF);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        var r = client.ReadBytes("700", 4);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, r.Content);
    }
}

public class ModbusUdpWriteTests
{
    [Fact]
    public void WriteRead_Int16()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        var w = client.Write("200", (short)-12345);
        Assert.True(w.IsSuccess, w.Message);

        var r = client.ReadInt16("200");
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal((short)-12345, r.Content);
    }

    [Fact]
    public void WriteRead_UInt16()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        client.Write("300", (ushort)54321);
        var r = client.ReadUInt16("300");
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal((ushort)54321, r.Content);
    }

    [Fact]
    public void WriteRead_Int32()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        client.Write("400", unchecked((int)0xDEADBEEF));
        var r = client.ReadInt32("400");
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(unchecked((int)0xDEADBEEF), r.Content);
    }

    [Fact]
    public void WriteRead_Float()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        client.Write("500", 3.14f);
        var r = client.ReadFloat("500");
        Assert.True(r.IsSuccess, r.Message);
        Assert.True(Math.Abs(r.Content - 3.14f) < 0.01f, $"Got {r.Content}");
    }

    [Fact]
    public void WriteRead_Bool_Coil()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        client.Write("00100", true);
        Assert.True(client.ReadBool("00100").Content);

        client.Write("00100", false);
        Assert.False(client.ReadBool("00100").Content);
    }

    [Fact]
    public void WriteRead_String()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        client.Write("600", "HELLO");
        var r = client.ReadString("600", 5);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal("HELLO", r.Content);
    }

    [Fact]
    public void WriteRead_Bytes()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        byte[] data = { 0xAA, 0xBB, 0xCC, 0xDD };
        client.Write("700", data);
        var r = client.ReadBytes("700", 4);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(data, r.Content);
    }

    [Fact]
    public void WriteMultipleCoils_SendsFC15()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        bool[] coils = { true, false, true, false, true };
        var w = client.WriteMultipleCoils(100, coils);
        Assert.True(w.IsSuccess, w.Message);

        var readBack = client.ReadBools("00100", 5);
        Assert.True(readBack.IsSuccess);
        Assert.True(readBack.Content[0]);
        Assert.False(readBack.Content[1]);
        Assert.True(readBack.Content[2]);
        Assert.False(readBack.Content[3]);
        Assert.True(readBack.Content[4]);
    }
}

public class ModbusUdpEndiannessTests
{
    [Fact]
    public void ReadInt32_LittleEndian_DCBA()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetHoldingRegister(100, 0x7856);
        server.SetHoldingRegister(101, 0x3412);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1)
        {
            ByteOrder = Endianness.LittleEndian
        };
        client.Connect();

        var r = client.ReadInt32("100");
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(0x12345678, r.Content);
    }

    [Fact]
    public void ReadInt32_MidBigEndian_BADC()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetHoldingRegister(100, 0x3412);
        server.SetHoldingRegister(101, 0x7856);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1)
        {
            ByteOrder = Endianness.MidBigEndian
        };
        client.Connect();

        var r = client.ReadInt32("100");
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(0x12345678, r.Content);
    }

    [Fact]
    public void ReadInt32_MidLittleEndian_CDAB()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetHoldingRegister(100, 0x5678);
        server.SetHoldingRegister(101, 0x1234);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1)
        {
            ByteOrder = Endianness.MidLittleEndian
        };
        client.Connect();

        var r = client.ReadInt32("100");
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(0x12345678, r.Content);
    }

    [Fact]
    public void WriteInt32_LittleEndian_SendsCorrectBytes()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1)
        {
            ByteOrder = Endianness.LittleEndian
        };
        client.Connect();

        client.Write("200", 0x12345678);
        var r = client.ReadInt32("200");
        Assert.True(r.IsSuccess);
        Assert.Equal(0x12345678, r.Content);
    }
}

public class ModbusUdpFC23Tests
{
    [Fact]
    public void MaskWriteRegister_FC22()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetHoldingRegister(50, 0x1234);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        var result = client.MaskWriteRegister("50", 0xFF00, 0x00F0);
        Assert.True(result.IsSuccess, result.Message);

        var readBack = client.ReadUInt16("50");
        Assert.True(readBack.IsSuccess, readBack.Message);
        Assert.Equal((ushort)0x12F0, readBack.Content);
    }

    [Fact]
    public void ReadWriteMultipleRegisters_FC23()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetHoldingRegister(50, 0xABCD);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        byte[] writeData = { 0x00, 0x01 };
        var result = client.ReadWriteMultipleRegisters(50, 1, 200, writeData);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, result.Content.Length);
        Assert.Equal(0xAB, result.Content[0]);
        Assert.Equal(0xCD, result.Content[1]);

        var readBack = client.ReadInt16("200");
        Assert.True(readBack.IsSuccess);
        Assert.Equal(1, readBack.Content);
    }
}

public class ModbusUdpAddressPrefixTests
{
    [Fact]
    public void Prefix_0xxxx_ReadsCoils()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetCoil(42, true);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        Assert.True(client.ReadBool("00042").Content);
    }

    [Fact]
    public void Prefix_1xxxx_ReadsDiscreteInputs()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetDiscreteInput(7, true);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        Assert.True(client.ReadBool("10007").Content);
    }

    [Fact]
    public void Prefix_3xxxx_ReadsInputRegisters()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetInputRegister(33, 42);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        Assert.Equal((short)42, client.ReadInt16("30033").Content);
    }

    [Fact]
    public void Prefix_4xxxx_ReadsHoldingRegisters()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetHoldingRegister(10, 12345);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        Assert.Equal((short)12345, client.ReadInt16("40010").Content);
    }

    [Fact]
    public void NoPrefix_DefaultsToHoldingRegister()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetHoldingRegister(0, 999);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        Assert.Equal((short)999, client.ReadInt16("0").Content);
    }
}

public class ModbusUdpStringEncodedTests
{
    [Fact]
    public void ReadStringEncoded_Utf8()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetHoldingRegister(0, 0x4869);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1)
        {
            StringEncodingOption = StringEncoding.Utf8
        };
        client.Connect();

        var r = client.ReadStringEncoded("0", 2);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal("Hi", r.Content);
    }

    [Fact]
    public void WriteStringEncoded_Utf8()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1)
        {
            StringEncodingOption = StringEncoding.Utf8
        };
        client.Connect();

        var w = client.WriteStringEncoded("0", "AB");
        Assert.True(w.IsSuccess, w.Message);

        var r = client.ReadStringEncoded("0", 2);
        Assert.True(r.IsSuccess);
        Assert.Equal("AB", r.Content);
    }
}

public class ModbusUdpMultipleSequentialTests
{
    [Fact]
    public void MultipleSequentialOperations()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        for (int i = 0; i < 5; i++)
        {
            client.Write("800", (short)i);
            var r = client.ReadInt16("800");
            Assert.True(r.IsSuccess);
            Assert.Equal((short)i, r.Content);
        }
    }
}

public class ModbusUdpCustomPduTests
{
    [Fact]
    public void SendCustomModbus_ReturnsResponsePdu()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.SetHoldingRegister(0, 0x1234);
        server.Start();

        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.Connect();

        byte[] pdu = { 0x03, 0x00, 0x00, 0x00, 0x01 };
        var result = client.SendCustomModbus(pdu);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0x03, result.Content[0]);
    }
}

public class ModbusUdpEventTests
{
    [Fact]
    public void OnConnected_FiresOnConnect()
    {
        int port = 18101;
        bool connected = false;
        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.OnConnected += (_, _) => connected = true;

        client.Connect();
        Assert.True(connected);
    }

    [Fact]
    public void OnDisconnected_FiresOnDisconnect()
    {
        int port = 18102;
        bool disconnected = false;
        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.OnDisconnected += (_, _) => disconnected = true;

        client.Connect();
        client.Disconnect();
        Assert.True(disconnected);
    }

    [Fact]
    public void OnMessageSent_FiresOnWrite()
    {
        using var server = new ModbusUdpTestServer();
        int port = server.Port;
        server.Start();

        string? sentHex = null;
        using var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
        client.OnMessageSent += (_, hex) => sentHex = hex;
        client.Connect();

        client.Write("0", (short)1);
        Assert.NotNull(sentHex);
        Assert.Contains("06", sentHex);
    }
}
