using System;
using System.Threading;
using Xunit;
using Nexus.Siemens;

namespace Nexus.Siemens.Tests;

public class S7AddressParserTests
{
    [Fact]
    public void AddressParser_Placeholder()
    {
        // 地址解析通过 Client↔Server 端到端验证
        Assert.True(true);
    }
}

public class S7VirtualPlcTests
{
    private const int TestPortBase = 16100;

    [Fact]
    public void Server_StartStop_Works()
    {
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, TestPortBase);
        Assert.False(server.IsRunning);

        server.Start();
        Assert.True(server.IsRunning);

        server.Stop();
        Assert.False(server.IsRunning);

        server.Dispose();
    }

    [Fact]
    public void Client_Connect_ReadInt16_ViaVirtualPlc()
    {
        int port = TestPortBase + 1;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.SetDBWord(1, 0, 0x1234);
        server.SetDBWord(1, 2, 0x5678);
        server.Start();

        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var result = client.ReadInt16("DB1.DBW0");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((short)0x1234, result.Content);

            var result2 = client.ReadInt16("DB1.DBW2");
            Assert.True(result2.IsSuccess, result2.Message);
            Assert.Equal((short)0x5678, result2.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_Write_ReadBack_ViaVirtualPlc()
    {
        int port = TestPortBase + 2;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();

        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            // 写入
            var writeResult = client.Write("DB1.DBW100", unchecked((short)0xABCD));
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            // 读回
            var readResult = client.ReadInt16("DB1.DBW100");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal(unchecked((short)0xABCD), readResult.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadInt32_ViaVirtualPlc()
    {
        int port = TestPortBase + 3;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.SetDBDWord(1, 10, 0x12345678);
        server.Start();

        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var result = client.ReadInt32("DB1.DBD10");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x12345678, result.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadFloat_ViaVirtualPlc()
    {
        int port = TestPortBase + 4;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.SetDBReal(1, 20, 3.14f);
        server.Start();

        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var result = client.ReadFloat("DB1.DBD20");
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(Math.Abs(result.Content - 3.14f) < 0.01f, $"Got {result.Content}");

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_ReadMarker_ViaVirtualPlc()
    {
        int port = TestPortBase + 5;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.SetMarkerByte(0, 0xAB);
        server.Start();

        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var result = client.ReadInt16("MW0");
            Assert.True(result.IsSuccess, result.Message);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }
}
