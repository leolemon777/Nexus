using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Xunit;
using Nexus;
using Nexus.Mitsubishi;

namespace Nexus.Mitsubishi.Tests;

public class Mc3EAddressParserTests
{
    [Theory]
    [InlineData("D100", 0xA8, 100)]
    [InlineData("D0", 0xA8, 0)]
    [InlineData("D65535", 0xA8, 65535)]
    [InlineData("M100", 0x90, 100)]
    [InlineData("M0", 0x90, 0)]
    public void Parse_WordAddresses_ParsedCorrectly(string address, byte expectedLabel, uint expectedAddr)
    {
        var (subLabel, addr) = Mc3EAddressParser.Parse(address);
        Assert.Equal(expectedLabel, subLabel);
        Assert.Equal(expectedAddr, addr);
    }

    [Theory]
    [InlineData("X0", 0x9C, 0u)]
    [InlineData("XFF", 0x9C, 255u)]
    [InlineData("X1FF", 0x9C, 511u)]
    [InlineData("Y0", 0x9D, 0u)]
    [InlineData("Y1A", 0x9D, 26u)]
    public void Parse_HexAddresses_ParsedCorrectly(string address, byte expectedLabel, uint expectedAddr)
    {
        var (subLabel, addr) = Mc3EAddressParser.Parse(address);
        Assert.Equal(expectedLabel, subLabel);
        Assert.Equal(expectedAddr, addr);
    }

    [Theory]
    [InlineData("Z0", 0xCC, 0u)]
    [InlineData("Z15", 0xCC, 15u)]
    [InlineData("R100", 0xAF, 100u)]
    [InlineData("W200", 0xB4, 200u)]
    [InlineData("L50", 0x92, 50u)]
    [InlineData("F10", 0x93, 10u)]
    [InlineData("V0", 0x94, 0u)]
    [InlineData("V100", 0x94, 100u)]
    [InlineData("S100", 0x98, 100u)]
    [InlineData("B0", 0xA0, 0u)]
    public void Parse_OtherRegisters_ParsedCorrectly(string address, byte expectedLabel, uint expectedAddr)
    {
        var (subLabel, addr) = Mc3EAddressParser.Parse(address);
        Assert.Equal(expectedLabel, subLabel);
        Assert.Equal(expectedAddr, addr);
    }

    [Theory]
    [InlineData("TS100", 0xC1, 100u)]
    [InlineData("TC50", 0xC0, 50u)]
    [InlineData("CS10", 0xC4, 10u)]
    [InlineData("CC5", 0xC3, 5u)]
    public void Parse_TimerCounterAddresses_ParsedCorrectly(string address, byte expectedLabel, uint expectedAddr)
    {
        var (subLabel, addr) = Mc3EAddressParser.Parse(address);
        Assert.Equal(expectedLabel, subLabel);
        Assert.Equal(expectedAddr, addr);
    }

    [Theory]
    [InlineData("SM0", 0x91, 0u)]
    [InlineData("SM100", 0x91, 100u)]
    [InlineData("SD0", 0xA9, 0u)]
    [InlineData("SD200", 0xA9, 200u)]
    [InlineData("DX0", 0xA2, 0u)]
    [InlineData("DXFF", 0xA2, 255u)]
    [InlineData("SW0", 0xB5, 0u)]
    [InlineData("SW100", 0xB5, 100u)]
    [InlineData("ZR0", 0xB0, 0u)]
    [InlineData("ZR1000", 0xB0, 1000u)]
    public void Parse_NewDeviceTypes_ParsedCorrectly(string address, byte expectedLabel, uint expectedAddr)
    {
        var (subLabel, addr) = Mc3EAddressParser.Parse(address);
        Assert.Equal(expectedLabel, subLabel);
        Assert.Equal(expectedAddr, addr);
    }

    [Fact]
    public void Parse_EmptyAddress_Throws()
    {
        Assert.Throws<ArgumentException>(() => Mc3EAddressParser.Parse(""));
        Assert.Throws<ArgumentException>(() => Mc3EAddressParser.Parse("   "));
    }

    [Fact]
    public void Parse_UnknownPrefix_Throws()
    {
        Assert.Throws<ArgumentException>(() => Mc3EAddressParser.Parse("A100"));
    }

    [Theory]
    [InlineData("D100", false)]
    [InlineData("W200", false)]
    [InlineData("ZR100", false)]
    [InlineData("SD100", false)]
    [InlineData("SW100", false)]
    [InlineData("M100", true)]
    [InlineData("X0", true)]
    [InlineData("Y0", true)]
    [InlineData("S10", true)]
    [InlineData("V0", true)]
    [InlineData("SM0", true)]
    [InlineData("DX0", true)]
    [InlineData("TS100", true)]
    [InlineData("TC50", true)]
    [InlineData("CS10", true)]
    [InlineData("CC5", true)]
    public void IsBitAddress_ReturnsCorrectly(string address, bool expected)
    {
        Assert.Equal(expected, Mc3EAddressParser.IsBitAddress(address));
    }
}

public class Mc3EClientServerTests
{
    private const int TestPort = 15007;

    [Fact]
    public void Server_StartStop_Works()
    {
        var server = new Mc3EVirtuServer(TestPort);
        Assert.False(server.IsRunning);

        server.Start();
        Assert.True(server.IsRunning);

        server.Stop();
        Assert.False(server.IsRunning);

        server.Dispose();
    }

    [Fact]
    public void Client_ReadInt16_WithVirtualServer()
    {
        var server = new Mc3EVirtuServer(TestPort + 1);
        server.SetDRegister(0, 0x1234);
        server.SetDRegister(1, 0x5678);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 1);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var result = client.ReadInt16("D0");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((short)0x1234, result.Content);

            var result2 = client.ReadInt16("D1");
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
    public void Client_WriteInt16_ReadBack()
    {
        var server = new Mc3EVirtuServer(TestPort + 2);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 2);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var writeResult = client.Write("D100", (short)unchecked((short)0xABCD));
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt16("D100");
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
    public void Client_ReadInt32_WithVirtualServer()
    {
        var server = new Mc3EVirtuServer(TestPort + 3);
        server.SetDRegister(10, 0x1234);
        server.SetDRegister(11, 0x5678);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 3);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var result = client.ReadInt32("D10");
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
    public void Client_ReadFloat_WithVirtualServer()
    {
        var server = new Mc3EVirtuServer(TestPort + 4);
        server.SetDRegister(20, 0x4048);
        server.SetDRegister(21, 0xF5C3);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 4);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var result = client.ReadFloat("D20");
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(Math.Abs(result.Content - 3.14f) < 1.0f, $"Got {result.Content}");

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
    public void Client_NetworkNumber_StationNumber_Aliases()
    {
        var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1");
        Assert.Equal(client.NetworkNo, client.NetworkNumber);
        Assert.Equal(client.PcNo, client.StationNumber);

        client.NetworkNumber = 0x05;
        Assert.Equal(0x05, client.NetworkNo);

        client.StationNumber = 0x10;
        Assert.Equal(0x10, client.PcNo);

        client.Dispose();
    }

    [Fact]
    public void Client_ByteOrder_Int32_CDAB_RoundTrip()
    {
        var server = new Mc3EVirtuServer(TestPort + 5);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 5);
            client.SetPersistentConnection();
            client.ByteOrder = Endianness.MidLittleEndian; // CDAB

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var writeResult = client.Write("D0", 0x12345678);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt32("D0");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal(0x12345678, readResult.Content);

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
    public void Client_ByteOrder_Float_CDAB_RoundTrip()
    {
        var server = new Mc3EVirtuServer(TestPort + 6);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 6);
            client.SetPersistentConnection();
            client.ByteOrder = Endianness.MidLittleEndian;

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            float testVal = 3.14f;
            var writeResult = client.Write("D0", testVal);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadFloat("D0");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal(testVal, readResult.Content);

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
    public void Client_ByteOrder_Double_CDAB_RoundTrip()
    {
        var server = new Mc3EVirtuServer(TestPort + 7);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 7);
            client.SetPersistentConnection();
            client.ByteOrder = Endianness.MidLittleEndian;

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            double testVal = 3.14159265358979;
            var writeResult = client.Write("D0", testVal);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadDouble("D0");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal(testVal, readResult.Content);

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
    public void Client_StringEncoding_UTF8()
    {
        var server = new Mc3EVirtuServer(TestPort + 8);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 8);
            client.SetPersistentConnection();
            client.StringEncoding = Encoding.UTF8;

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var writeResult = client.WriteStringEncoded("D0", "AB");
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadStringEncoded("D0", 2);
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal("AB", readResult.Content);

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
    public void Client_StringEncoding_ASCII()
    {
        var server = new Mc3EVirtuServer(TestPort + 9);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 9);
            client.SetPersistentConnection();
            client.StringEncoding = Encoding.ASCII;

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var writeResult = client.WriteStringEncoded("D0", "Hi");
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadStringEncoded("D0", 2);
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal("Hi", readResult.Content);

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
    public void Client_BatchRead_MultipleAddresses()
    {
        var server = new Mc3EVirtuServer(TestPort + 10);
        server.SetDRegister(100, 0x1111);
        server.SetDRegister(101, 0x2222);
        server.SetDRegister(102, 0x3333);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 10);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var result = client.BatchRead(new[] { "D100", "D101", "D102" });
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(3, result.Content.Count);
            Assert.Equal((ushort)0x1111, result.Content["D100"]);
            Assert.Equal((ushort)0x2222, result.Content["D101"]);
            Assert.Equal((ushort)0x3333, result.Content["D102"]);

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
    public void Client_BatchRead_DifferentDeviceTypes()
    {
        var server = new Mc3EVirtuServer(TestPort + 11);
        server.SetDRegister(0, 0xAAAA);
        server.SetWRegister(0, 0xBBBB);
        server.SetRRegister(0, 0xCCCC);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 11);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var result = client.BatchRead(new[] { "D0", "W0", "R0" });
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(3, result.Content.Count);
            Assert.Equal((ushort)0xAAAA, result.Content["D0"]);
            Assert.Equal((ushort)0xBBBB, result.Content["W0"]);
            Assert.Equal((ushort)0xCCCC, result.Content["R0"]);

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
    public void Client_RandomRead_NonContiguous()
    {
        var server = new Mc3EVirtuServer(TestPort + 12);
        server.SetDRegister(0, 0x1111);
        server.SetDRegister(50, 0x2222);
        server.SetWRegister(10, 0x3333);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 12);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var result = client.RandomRead(new[] { "D0", "D50", "W10" });
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(3, result.Content.Count);

            // D0 = 0x1111 -> bytes [0x11, 0x11]
            Assert.Equal(0x11, result.Content["D0"][0]);
            Assert.Equal(0x11, result.Content["D0"][1]);
            // D50 = 0x2222
            Assert.Equal(0x22, result.Content["D50"][0]);
            Assert.Equal(0x22, result.Content["D50"][1]);
            // W10 = 0x3333
            Assert.Equal(0x33, result.Content["W10"][0]);
            Assert.Equal(0x33, result.Content["W10"][1]);

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
    public void Client_RandomWrite_NonContiguous()
    {
        var server = new Mc3EVirtuServer(TestPort + 13);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 13);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var items = new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("D0", (ushort)0xAAAA),
                new KeyValuePair<string, object>("D50", (ushort)0xBBBB),
                new KeyValuePair<string, object>("D100", (ushort)0xCCCC),
            };
            var writeResult = client.RandomWrite(items);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            // Read back to verify
            var r1 = client.ReadUInt16("D0");
            Assert.True(r1.IsSuccess);
            Assert.Equal((ushort)0xAAAA, r1.Content);

            var r2 = client.ReadUInt16("D50");
            Assert.True(r2.IsSuccess);
            Assert.Equal((ushort)0xBBBB, r2.Content);

            var r3 = client.ReadUInt16("D100");
            Assert.True(r3.IsSuccess);
            Assert.Equal((ushort)0xCCCC, r3.Content);

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
    public void Client_BatchWrite_MultipleAddresses()
    {
        var server = new Mc3EVirtuServer(TestPort + 14);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 14);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var items = new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("D0", (short)100),
                new KeyValuePair<string, object>("D1", (short)200),
            };
            var writeResult = client.BatchWrite(items);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var r1 = client.ReadInt16("D0");
            Assert.True(r1.IsSuccess);
            Assert.Equal((short)100, r1.Content);

            var r2 = client.ReadInt16("D1");
            Assert.True(r2.IsSuccess);
            Assert.Equal((short)200, r2.Content);

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
    public void Client_IBatchReadWrite_Interface()
    {
        var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1");
        Assert.IsAssignableFrom<IBatchReadWrite>(client);
        client.Dispose();
    }

    [Fact]
    public void Client_ByteOrder_Default_IsBigEndian()
    {
        var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1");
        Assert.Equal(Endianness.BigEndian, client.ByteOrder);
        client.Dispose();
    }

    [Fact]
    public void Client_StringEncoding_Default_IsASCII()
    {
        var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1");
        Assert.Equal(Encoding.ASCII, client.StringEncoding);
        client.Dispose();
    }

    [Fact]
    public void Client_ReadWrite_ZR_Register()
    {
        var server = new Mc3EVirtuServer(TestPort + 15);
        server.SetZRRegister(0, 0x1234);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 15);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var result = client.ReadUInt16("ZR0");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((ushort)0x1234, result.Content);

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
    public void Client_ReadWrite_SD_Register()
    {
        var server = new Mc3EVirtuServer(TestPort + 16);
        server.SetSDRegister(0, 0x5678);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 16);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var result = client.ReadUInt16("SD0");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((ushort)0x5678, result.Content);

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
    public void Client_ReadWrite_SW_Register()
    {
        var server = new Mc3EVirtuServer(TestPort + 17);
        server.SetSWRegister(0, 0x9ABC);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 17);
            client.SetPersistentConnection();

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var result = client.ReadUInt16("SW0");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((ushort)0x9ABC, result.Content);

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
    public void Client_ByteOrder_LittleEndian_Int32_RoundTrip()
    {
        var server = new Mc3EVirtuServer(TestPort + 18);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 18);
            client.SetPersistentConnection();
            client.ByteOrder = Endianness.LittleEndian;

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var writeResult = client.Write("D0", 0x12345678);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt32("D0");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal(0x12345678, readResult.Content);

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
    public void Client_ByteOrder_MidBigEndian_Int32_RoundTrip()
    {
        var server = new Mc3EVirtuServer(TestPort + 19);
        server.Start();

        try
        {
            var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", TestPort + 19);
            client.SetPersistentConnection();
            client.ByteOrder = Endianness.MidBigEndian;

            var connResult = client.Connect();
            Assert.True(connResult.IsSuccess, connResult.Message);

            var writeResult = client.Write("D0", unchecked((int)0xDEADBEEF));
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt32("D0");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal(unchecked((int)0xDEADBEEF), readResult.Content);

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
