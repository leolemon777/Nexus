using System;
using Xunit;
using Nexus.Siemens;

namespace Nexus.Siemens.Tests;

/// <summary>
/// S7 地址解析 — 通过 Client↔VirtualPlc 端到端验证。
/// ParseS7Address 是 private 方法，通过读写操作间接测试地址路由。
/// </summary>
public class S7AddressTests
{
    private const int PortBase = 16200;

    [Fact]
    public void Address_DB_Word_ReadWrite()
    {
        int port = PortBase + 1;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("DB1.DBW0", (short)0x1111);
            Assert.Equal((short)0x1111, client.ReadInt16("DB1.DBW0").Content);

            client.Write("DB1.DBW100", (short)0x2222);
            Assert.Equal((short)0x2222, client.ReadInt16("DB1.DBW100").Content);

            client.Write("DB1.DBW500", (short)0x3333);
            Assert.Equal((short)0x3333, client.ReadInt16("DB1.DBW500").Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void Address_DB_DWord_ReadWrite()
    {
        int port = PortBase + 2;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("DB1.DBD10", unchecked((int)0x12345678));
            Assert.Equal(unchecked((int)0x12345678), client.ReadInt32("DB1.DBD10").Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void Address_DB_Byte_ReadWrite()
    {
        int port = PortBase + 3;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("DB1.DBW20", unchecked((short)0xABCD));
            Assert.Equal(unchecked((short)0xABCD), client.ReadInt16("DB1.DBW20").Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void Address_Marker_ReadWrite()
    {
        int port = PortBase + 4;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("MW10", unchecked((short)0x4567));
            Assert.Equal(unchecked((short)0x4567), client.ReadInt16("MW10").Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void Address_Marker_DWord()
    {
        int port = PortBase + 5;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("MD20", unchecked((int)0xAABBCCDD));
            var r = client.ReadInt32("MD20");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(unchecked((int)0xAABBCCDD), r.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void Address_Input_Read()
    {
        int port = PortBase + 6;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.SetInputByte(0, 0x12);
        server.SetInputByte(1, 0x34);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadInt16("IW0");
            Assert.True(r.IsSuccess, r.Message);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void Address_Output_ReadWrite()
    {
        int port = PortBase + 7;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("QW0", unchecked((short)0x9988));
            var r = client.ReadInt16("QW0");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(unchecked((short)0x9988), r.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void Address_V_Area_S7_200()
    {
        int port = PortBase + 8;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("VW100", unchecked((short)0x5678));
            var r = client.ReadInt16("VW100");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(unchecked((short)0x5678), r.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void Address_Invalid_Format_Throws()
    {
        int port = PortBase + 9;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.ThrowsAny<Exception>(() => client.ReadInt16("INVALID"));

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void Address_Empty_Throws()
    {
        int port = PortBase + 10;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.ThrowsAny<Exception>(() => client.ReadInt16(""));

            client.Disconnect();
            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }
}
