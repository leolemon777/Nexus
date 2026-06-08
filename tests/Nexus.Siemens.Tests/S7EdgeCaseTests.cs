using System;
using Xunit;
using Nexus.Siemens;

namespace Nexus.Siemens.Tests;

/// <summary>
/// S7 边界场景与 PLC 型号测试。
/// </summary>
public class S7EdgeCaseTests
{
    private const int PortBase = 16400;

    [Fact]
    public void S7_1200_DefaultSlot1()
    {
        var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1");
        Assert.Equal((byte)1, client.Slot);
        Assert.Equal((byte)0, client.Rack);
        client.Dispose();
    }

    [Fact]
    public void S7_1500_DefaultSlot1()
    {
        var client = new SiemensS7Client(SiemensPLCS.S7_1500, "127.0.0.1");
        Assert.Equal((byte)1, client.Slot);
        client.Dispose();
    }

    [Fact]
    public void S7_300_DefaultSlot2()
    {
        var client = new SiemensS7Client(SiemensPLCS.S7_300, "127.0.0.1");
        Assert.Equal((byte)2, client.Slot);
        Assert.Equal((byte)0, client.Rack);
        client.Dispose();
    }

    [Fact]
    public void S7_400_DefaultSlot2()
    {
        var client = new SiemensS7Client(SiemensPLCS.S7_400, "127.0.0.1");
        Assert.Equal((byte)2, client.Slot);
        client.Dispose();
    }

    [Fact]
    public void ShortConnection_MultipleOps()
    {
        int port = PortBase + 1;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            // 不 SetPersistentConnection → 短连接模式

            var r1 = client.ReadInt16("DB1.DBW0");
            Assert.True(r1.IsSuccess, r1.Message);

            var r2 = client.ReadInt16("DB1.DBW2");
            Assert.True(r2.IsSuccess, r2.Message);

            client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void Connect_BadPort_Fails()
    {
        var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", 19999);
        var r = client.Connect();
        Assert.False(r.IsSuccess);
        client.Dispose();
    }

    [Fact]
    public void Read_UninitializedDB_ReturnsZero()
    {
        int port = PortBase + 2;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            var r = client.ReadInt16("DB1.DBW9000");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((short)0, r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void PersistentConnection_MultipleReads()
    {
        int port = PortBase + 3;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.SetDBWord(1, 0, 100);
        server.SetDBWord(1, 2, 200);
        server.SetDBWord(1, 4, 300);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.Equal((short)100, client.ReadInt16("DB1.DBW0").Content);
            Assert.Equal((short)200, client.ReadInt16("DB1.DBW2").Content);
            Assert.Equal((short)300, client.ReadInt16("DB1.DBW4").Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void WriteThenRead_DifferentAreas()
    {
        int port = PortBase + 4;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // DB → M
            client.Write("DB1.DBW10", (short)0x1234);
            Assert.Equal((short)0x1234, client.ReadInt16("DB1.DBW10").Content);

            client.Write("MW0", unchecked((short)0x5678));
            Assert.Equal(unchecked((short)0x5678), client.ReadInt16("MW0").Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void VirtualPlc_SetGet_DBWord()
    {
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200);
        server.SetDBWord(1, 0, unchecked((short)0xAAAA));
        server.SetDBWord(1, 10, (short)0x5555);
        Assert.Equal(unchecked((short)0xAAAA), server.GetDBWord(1, 0));
        Assert.Equal((short)0x5555, server.GetDBWord(1, 10));
        server.Dispose();
    }

    [Fact]
    public void VirtualPlc_SetGet_DBDWord()
    {
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200);
        server.SetDBDWord(1, 0, 0x12345678);
        Assert.Equal(0x12345678, server.GetDBDWord(1, 0));
        server.Dispose();
    }

    [Fact]
    public void VirtualPlc_MarkerInputOutput()
    {
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200);
        server.SetMarkerByte(0, 0xAB);
        server.SetInputByte(0, 0xCD);
        server.SetOutputByte(0, 0xEF);
        server.Dispose();
    }

    [Fact]
    public void MultipleDB_Blocks()
    {
        int port = PortBase + 5;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.SetDBWord(1, 0, (short)0x1111);
        server.SetDBWord(2, 0, (short)0x2222);
        server.SetDBWord(5, 0, (short)0x5555);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            Assert.Equal((short)0x1111, client.ReadInt16("DB1.DBW0").Content);
            Assert.Equal((short)0x2222, client.ReadInt16("DB2.DBW0").Content);
            Assert.Equal((short)0x5555, client.ReadInt16("DB5.DBW0").Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }
}
