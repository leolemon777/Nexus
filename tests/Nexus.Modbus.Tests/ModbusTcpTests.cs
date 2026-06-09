using System.Net;
using System.Net.Sockets;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Modbus.Tests;

public class ModbusTcpTests
{
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

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
