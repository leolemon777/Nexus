using System;
using Xunit;
using Nexus.Siemens;

namespace Nexus.Siemens.Tests;

/// <summary>
/// S7 数据类型读写测试 — 全类型覆盖。
/// </summary>
public class S7DataTypeTests
{
    private const int PortBase = 16300;

    [Fact]
    public void ReadWrite_Int16()
    {
        int port = PortBase + 1;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("DB1.DBW0", (short)-12345);
            Assert.Equal((short)-12345, client.ReadInt16("DB1.DBW0").Content);

            client.Write("DB1.DBW0", short.MaxValue);
            Assert.Equal(short.MaxValue, client.ReadInt16("DB1.DBW0").Content);

            client.Write("DB1.DBW0", short.MinValue);
            Assert.Equal(short.MinValue, client.ReadInt16("DB1.DBW0").Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void ReadWrite_UInt16()
    {
        int port = PortBase + 2;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("DB1.DBW10", (short)0x1234);
            var r = client.ReadUInt16("DB1.DBW10");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((ushort)0x1234, r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void ReadWrite_Int32()
    {
        int port = PortBase + 3;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("DB1.DBD20", -999999);
            Assert.Equal(-999999, client.ReadInt32("DB1.DBD20").Content);

            client.Write("DB1.DBD20", int.MaxValue);
            Assert.Equal(int.MaxValue, client.ReadInt32("DB1.DBD20").Content);

            client.Write("DB1.DBD20", int.MinValue);
            Assert.Equal(int.MinValue, client.ReadInt32("DB1.DBD20").Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void ReadWrite_UInt32()
    {
        int port = PortBase + 4;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("DB1.DBD30", unchecked((int)0xDEADBEEF));
            var r = client.ReadUInt32("DB1.DBD30");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(unchecked((uint)0xDEADBEEF), r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void ReadWrite_Float()
    {
        int port = PortBase + 5;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("DB1.DBD40", 3.14159f);
            var r = client.ReadFloat("DB1.DBD40");
            Assert.True(r.IsSuccess, r.Message);
            Assert.True(Math.Abs(r.Content - 3.14159f) < 0.0001f, $"Got {r.Content}");

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void ReadWrite_Float_Negative()
    {
        int port = PortBase + 6;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("DB1.DBD50", -100.5f);
            var r = client.ReadFloat("DB1.DBD50");
            Assert.True(r.IsSuccess, r.Message);
            Assert.True(Math.Abs(r.Content - (-100.5f)) < 0.01f, $"Got {r.Content}");

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void ReadWrite_String()
    {
        int port = PortBase + 7;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("DB1.DBW60", "HELLO");
            var r = client.ReadString("DB1.DBW60", 5);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal("HELLO", r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void ReadWrite_Bytes()
    {
        int port = PortBase + 8;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            byte[] data = { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF };
            client.Write("DB1.DBW70", data);
            var r = client.ReadBytes("DB1.DBW70", 8);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(data, r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void ReadWrite_Bool()
    {
        int port = PortBase + 9;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            client.Write("DB1.DBX80.0", true);
            Assert.True(client.ReadBool("DB1.DBX80.0").Content);

            client.Write("DB1.DBX80.0", false);
            Assert.False(client.ReadBool("DB1.DBX80.0").Content);

            client.Write("DB1.DBX80.7", true);
            Assert.True(client.ReadBool("DB1.DBX80.7").Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void ReadWrite_Int64()
    {
        int port = PortBase + 10;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // Write as bytes for Int64
            byte[] data = { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
            client.Write("DB1.DBW90", data);
            var r = client.ReadInt64("DB1.DBW90");
            Assert.True(r.IsSuccess, r.Message);
            // Big-endian: 0x0001020304050607
            Assert.Equal(0x0001020304050607L, r.Content);

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }

    [Fact]
    public void ReadWrite_Double()
    {
        int port = PortBase + 11;
        var server = new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port);
        server.Start();
        try
        {
            var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", port);
            client.SetPersistentConnection();
            Assert.True(client.Connect().IsSuccess);

            // Write double as bytes since Write(double) truncates to float
            var bytes = BitConverter.GetBytes(3.14159265358979);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            client.Write("DB1.DBW100", bytes);
            var r = client.ReadDouble("DB1.DBW100");
            Assert.True(r.IsSuccess, r.Message);
            Assert.True(Math.Abs(r.Content - 3.14159265358979) < 0.0001, $"Got {r.Content}");

            client.Disconnect(); client.Dispose();
        }
        finally { server.Stop(); server.Dispose(); }
    }
}
