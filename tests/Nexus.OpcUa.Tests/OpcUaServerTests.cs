using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nexus.OpcUa;
using Xunit;

namespace Nexus.OpcUa.Tests;

public class OpcUaServerTests
{
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void Start_Stop_IsRunning_Toggles()
    {
        var port = GetFreePort();
        using var server = new OpcUaServer();
        server.Start(port);
        Assert.True(server.IsRunning);
        Assert.Contains(port.ToString(), server.EndpointUrl);
        server.Stop();
        Assert.False(server.IsRunning);
    }

    [Fact]
    public void AddNode_IncreasesNodeCount()
    {
        using var server = new OpcUaServer();
        server.Start(GetFreePort());
        int before = server.NodeCount;
        server.AddNode("Temperature", "Temperature", 25.0, "Float");
        Assert.Equal(before + 1, server.NodeCount);
        server.Stop();
    }

    [Fact]
    public void UpdateNode_ChangesValue()
    {
        using var server = new OpcUaServer();
        server.Start(GetFreePort());
        var nodeId = server.AddNode("Test", "Test", 100, "Int16");
        Assert.True(server.UpdateNode(nodeId, 200));
        var node = server.GetNode(nodeId);
        Assert.Equal(200, node!.Value);
        server.Stop();
    }

    [Fact]
    public void RemoveNode_DecreasesNodeCount()
    {
        using var server = new OpcUaServer();
        server.Start(GetFreePort());
        var nodeId = server.AddNode("Test", "Test", 0);
        int before = server.NodeCount;
        Assert.True(server.RemoveNode(nodeId));
        Assert.Equal(before - 1, server.NodeCount);
        server.Stop();
    }

    [Fact]
    public void GetNode_ReturnsCorrectNode()
    {
        using var server = new OpcUaServer();
        server.Start(GetFreePort());
        var nodeId = server.AddNode("Pressure", "Pressure", 1013.25, "Double");
        var node = server.GetNode(nodeId);
        Assert.NotNull(node);
        Assert.Equal("Pressure", node.BrowseName);
        Assert.Equal(1013.25, node.Value);
        Assert.Equal("Double", node.DataType);
        server.Stop();
    }

    [Fact]
    public void GetNode_NonExistent_ReturnsNull()
    {
        using var server = new OpcUaServer();
        server.Start(GetFreePort());
        Assert.Null(server.GetNode("ns=1;i=99999"));
        server.Stop();
    }

    [Fact]
    public void GetAllNodes_ReturnsDefaultPlusAdded()
    {
        using var server = new OpcUaServer();
        server.Start(GetFreePort());
        server.AddNode("A", "A", 1);
        server.AddNode("B", "B", 2);
        var nodes = server.GetAllNodes();
        Assert.True(nodes.Count >= 2);
        server.Stop();
    }

    [Fact]
    public void ServerName_DefaultValue()
    {
        using var server = new OpcUaServer();
        Assert.Equal("Nexus OPC UA Server", server.ServerName);
    }

    [Fact]
    public void SessionCount_InitiallyZero()
    {
        using var server = new OpcUaServer();
        server.Start(GetFreePort());
        Assert.Equal(0, server.SessionCount);
        server.Stop();
    }

    [Fact]
    public void MultipleStartStop_NoError()
    {
        using var server = new OpcUaServer();
        var port = GetFreePort();
        server.Start(port);
        server.Stop();
        server.Start(port);
        Assert.True(server.IsRunning);
        server.Stop();
    }

    [Fact]
    public async Task HelloHandshake_ReturnsAck()
    {
        var port = GetFreePort();
        using var server = new OpcUaServer();
        server.Start(port);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var stream = client.GetStream();

        var hello = new byte[28];
        hello[0] = (byte)'H'; hello[1] = (byte)'E'; hello[2] = (byte)'L';
        hello[3] = (byte)'F';
        hello[4] = 0; hello[5] = 0; hello[6] = 0; hello[7] = 28;
        await stream.WriteAsync(hello, 0, hello.Length);

        var buffer = new byte[28];
        int read = await stream.ReadAsync(buffer, 0, 28);
        Assert.Equal(28, read);
        Assert.Equal((byte)'A', buffer[0]);
        Assert.Equal((byte)'C', buffer[1]);
        Assert.Equal((byte)'K', buffer[2]);

        server.Stop();
    }

    [Fact]
    public void UpdateNode_RecordsHistory()
    {
        using var server = new OpcUaServer();
        server.Start(GetFreePort());
        var nodeId = server.AddNode("Hist", "Hist", 10);
        server.UpdateNode(nodeId, 20);
        server.UpdateNode(nodeId, 30);
        var history = server.GetHistory(nodeId);
        Assert.Equal(2, history.Count);
        Assert.Equal(20, history[0].Value);
        Assert.Equal(30, history[1].Value);
        server.Stop();
    }

    [Fact]
    public void GetHistory_WithTimeRange_Filters()
    {
        using var server = new OpcUaServer();
        server.Start(GetFreePort());
        var nodeId = server.AddNode("Range", "Range", 0);

        server.UpdateNode(nodeId, 1);
        var mid = DateTime.UtcNow;
        server.UpdateNode(nodeId, 2);

        var after = server.GetHistory(nodeId, startTime: mid);
        Assert.Single(after);
        Assert.Equal(2, after[0].Value);

        var before = server.GetHistory(nodeId, endTime: mid);
        Assert.Single(before);
        Assert.Equal(1, before[0].Value);
        server.Stop();
    }

    [Fact]
    public void GetHistory_NonExistent_ReturnsEmpty()
    {
        using var server = new OpcUaServer();
        server.Start(GetFreePort());
        var history = server.GetHistory("ns=1;i=99999");
        Assert.Empty(history);
        server.Stop();
    }
}
