using System.Net;
using System.Net.Sockets;
using Nexus;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Modbus.Tests;

public class ModbusTcpTests
{
    [Fact]
    public void AddressContextByteOrder_OverridesReadAndWrite()
    {
        int port = GetFreeTcpPort();
        using var server = new ModbusTcpServer(port);
        server.SetHoldingRegister(0, 0x8877);
        server.SetHoldingRegister(1, 0x6655);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port, station: 1) { ByteOrder = Endianness.BigEndian };

        var read = client.ReadInt32("bo=LittleEndian;40001");
        var write = client.Write("bo=LittleEndian;40003", 0x11223344);

        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(0x55667788, read.Content);
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal((ushort)0x4433, server.GetHoldingRegister(2));
        Assert.Equal((ushort)0x2211, server.GetHoldingRegister(3));
        Assert.Equal(Endianness.BigEndian, client.ByteOrder);
    }

    [Fact]
    public void MaskWriteRegister_UpdatesHoldingRegisterAtomically()
    {
        int port = GetFreeTcpPort();
        using var server = new ModbusTcpServer(port);
        server.SetHoldingRegister(0x0010, 0x1234);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);

        var write = client.MaskWriteRegister("16", 0xFF00, 0x00F0);
        var read = client.ReadUInt16("16");

        Assert.True(write.IsSuccess, write.Message);
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((ushort)0x12F0, read.Content);
        Assert.Equal((ushort)0x12F0, server.GetHoldingRegister(0x0010));
    }

    [Fact]
    public void WriteUInt64_WritesFourHoldingRegisters()
    {
        int port = GetFreeTcpPort();
        using var server = new ModbusTcpServer(port);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);

        var write = client.Write("40001", 0x1122334455667788UL);

        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal((ushort)0x1122, server.GetHoldingRegister(0));
        Assert.Equal((ushort)0x3344, server.GetHoldingRegister(1));
        Assert.Equal((ushort)0x5566, server.GetHoldingRegister(2));
        Assert.Equal((ushort)0x7788, server.GetHoldingRegister(3));
    }

    [Fact]
    public void WriteDouble_WritesFourHoldingRegisters()
    {
        int port = GetFreeTcpPort();
        using var server = new ModbusTcpServer(port);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);

        var write = client.Write("40001", 1.5d);

        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal((ushort)0x3FF8, server.GetHoldingRegister(0));
        Assert.Equal((ushort)0x0000, server.GetHoldingRegister(1));
        Assert.Equal((ushort)0x0000, server.GetHoldingRegister(2));
        Assert.Equal((ushort)0x0000, server.GetHoldingRegister(3));
    }

    [Fact]
    public void ReadUInt64_UsesConfiguredByteOrder()
    {
        int port = GetFreeTcpPort();
        using var server = new ModbusTcpServer(port);
        server.SetHoldingRegister(0, 0x8877);
        server.SetHoldingRegister(1, 0x6655);
        server.SetHoldingRegister(2, 0x4433);
        server.SetHoldingRegister(3, 0x2211);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port, station: 1) { ByteOrder = Endianness.LittleEndian };

        var read = client.ReadUInt64("40001");

        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(0x1122334455667788UL, read.Content);
    }

    [Fact]
    public void ReadUInt16_AcceptsAddressContextPrefix()
    {
        int port = GetFreeTcpPort();
        using var server = new ModbusTcpServer(port);
        server.SetHoldingRegister(0, 0x1234);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);
        string sentHex = string.Empty;
        client.OnMessageSent += (_, hex) => sentHex = hex;

        var read = client.ReadUInt16("unit=7;40001");

        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((ushort)0x1234, read.Content);
        string normalized = sentHex.Replace(" ", "");
        Assert.Equal("07", normalized.Substring(12, 2));
        Assert.Equal((byte)1, client.Station);
    }

    [Fact]
    public void WriteUInt16_AcceptsAddressContextPrefix()
    {
        int port = GetFreeTcpPort();
        using var server = new ModbusTcpServer(port);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);
        string sentHex = string.Empty;
        client.OnMessageSent += (_, hex) => sentHex = hex;

        var write = client.Write("unit=7;40001", (ushort)0x5678);

        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal((ushort)0x5678, server.GetHoldingRegister(0));
        string normalized = sentHex.Replace(" ", "");
        Assert.Equal("07", normalized.Substring(12, 2));
        Assert.Equal((byte)1, client.Station);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
