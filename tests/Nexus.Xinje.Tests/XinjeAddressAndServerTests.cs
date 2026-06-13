using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Xunit;
using Nexus.Xinje;

namespace Nexus.Xinje.Tests
{
    public class XinjeAddressTests
    {
        [Theory]
        [InlineData("D100", 100, 0x03, 0x06)]
        [InlineData("D0", 0, 0x03, 0x06)]
        [InlineData("HD100", 0x8000 + 100, 0x03, 0x06)]
        [InlineData("HD0", 0x8000, 0x03, 0x06)]
        [InlineData("SD0", 0xC000, 0x03, 0x06)]
        [InlineData("SD100", 0xC000 + 100, 0x03, 0x06)]
        [InlineData("Y0", 0, 0x01, 0x05)]
        [InlineData("X10", 10, 0x02, 0x00)]
        [InlineData("M100", 0x0800 + 100, 0x01, 0x05)]
        [InlineData("C0", 0x1000, 0x03, 0x06)]
        [InlineData("T0", 0x0600, 0x03, 0x06)]
        [InlineData("S20", 20, 0x01, 0x05)]
        public void Parse_ValidAddresses(string addr, ushort expectedAddr, byte readFc, byte writeFc)
        {
            var parsed = XinjeAddress.Parse(addr);
            Assert.Equal(expectedAddr, parsed.Address);
            Assert.Equal(readFc, parsed.ReadFunctionCode);
            Assert.Equal(writeFc, parsed.WriteFunctionCode);
        }

        [Theory]
        [InlineData("d100", 100)]
        [InlineData("hd100", 0x8000 + 100)]
        [InlineData("sd0", 0xC000)]
        [InlineData("sm100", 0x1000 + 100)]
        public void Parse_CaseInsensitive(string addr, ushort expectedAddr)
        {
            var parsed = XinjeAddress.Parse(addr);
            Assert.Equal(expectedAddr, parsed.Address);
        }

        [Fact]
        public void IsReadOnly_X()
        {
            var x = XinjeAddress.Parse("X0");
            Assert.True(x.IsReadOnly);
            var d = XinjeAddress.Parse("D0");
            Assert.False(d.IsReadOnly);
        }

        [Fact]
        public void IsBitArea_Y()
        {
            var y = XinjeAddress.Parse("Y0");
            Assert.True(y.IsBitArea);
            var d = XinjeAddress.Parse("D0");
            Assert.False(d.IsBitArea);
        }

        [Fact]
        public void WithOffset()
        {
            var base_ = XinjeAddress.Parse("D100");
            var offset = base_.WithOffset(10);
            Assert.Equal(110, offset.Address);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Parse_EmptyThrows(string addr)
        {
            Assert.Throws<ArgumentException>(() => XinjeAddress.Parse(addr));
        }

        [Fact]
        public void TryParse_EmptyReturnsNull()
        {
            Assert.Null(XinjeAddress.TryParse(""));
        }

        [Fact]
        public void ToString_ContainsInfo()
        {
            var parsed = XinjeAddress.Parse("HD100");
            string s = parsed.ToString();
            Assert.Contains("HoldingRegister", s);
        }

        [Fact]
        public void SM_AreaParses()
        {
            var sm = XinjeAddress.Parse("SM50");
            Assert.Equal(0x1000 + 50, sm.Address);
            Assert.Equal(XinjeArea.SpecialCoil, sm.Area);
        }
    }

    public class XinjeVirtualServerTests : IDisposable
    {
        private readonly XinjeVirtualServer _server;

        public XinjeVirtualServerTests()
        {
            _server = new XinjeVirtualServer(0);
            _server.Start();
        }

        [Fact]
        public void Server_StartsAndStops()
        {
            Assert.True(_server.IsRunning);
        }

        [Fact]
        public void SetGetHoldingRegister()
        {
            _server.SetHoldingRegister(100, 0x1234);
            Assert.Equal(0x1234, _server.GetHoldingRegister(100));
        }

        [Fact]
        public void SetGetCoil()
        {
            _server.SetCoil(10, true);
            Assert.True(_server.GetCoil(10));
        }

        [Fact]
        public void TcpClient_WriteUInt64_WritesFourHoldingRegisters()
        {
            using var server = new XinjeVirtualServer(GetFreeTcpPort());
            server.Start();
            using var client = new XinjeTcpClient("127.0.0.1", server.Port);
            client.SetPersistentConnection();
            var connect = client.Connect();
            Assert.True(connect.IsSuccess, connect.Message);

            var result = client.Write("D100", 0x1122334455667788UL);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x1122, server.GetHoldingRegister(100));
            Assert.Equal(0x3344, server.GetHoldingRegister(101));
            Assert.Equal(0x5566, server.GetHoldingRegister(102));
            Assert.Equal(0x7788, server.GetHoldingRegister(103));
        }

        [Fact]
        public void TcpClient_ReadUInt64_ReadsFourHoldingRegisters()
        {
            using var server = new XinjeVirtualServer(GetFreeTcpPort());
            server.Start();
            server.SetHoldingRegister(120, 0x1122);
            server.SetHoldingRegister(121, 0x3344);
            server.SetHoldingRegister(122, 0x5566);
            server.SetHoldingRegister(123, 0x7788);
            using var client = new XinjeTcpClient("127.0.0.1", server.Port);
            client.SetPersistentConnection();
            var connect = client.Connect();
            Assert.True(connect.IsSuccess, connect.Message);

            var result = client.ReadUInt64("D120");

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x1122334455667788UL, result.Content);
        }

        [Fact]
        public void BuildWriteMultiplePdu_WithEightBytes_HasNoPadding()
        {
            byte[] data = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };

            byte[] pdu = XinjeTcpClient.BuildWriteMultiplePdu(100, data);

            Assert.Equal(14, pdu.Length);
            Assert.Equal(0x10, pdu[0]);
            Assert.Equal(4, pdu[4]);
            Assert.Equal(8, pdu[5]);
            Assert.Equal(data, pdu[6..14]);
        }

        [Fact]
        public void ConnectionPool_ReadWrite_ReusesPersistentConnection()
        {
            using var server = new XinjeVirtualServer(GetFreeTcpPort());
            server.Start();

            using var pool = new XinjeConnectionPool("127.0.0.1", server.Port, maxPoolSize: 1);

            var write = pool.Write("D100", (short)1234);
            Assert.True(write.IsSuccess, write.Message);

            var read = pool.ReadInt16("D100");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal((short)1234, read.Content);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsPacketEvents()
        {
            using var server = new XinjeVirtualServer(GetFreeTcpPort());
            server.SetHoldingRegister(110, 0x1234);
            server.Start();

            using var pool = new XinjeConnectionPool("127.0.0.1", server.Port);
            int sent = 0;
            int received = 0;
            pool.OnMessageSent += (_, _) => Interlocked.Increment(ref sent);
            pool.OnMessageReceived += (_, _) => Interlocked.Increment(ref received);

            var read = pool.ReadUInt16("D110");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal((ushort)0x1234, read.Content);
            Assert.True(sent > 0);
            Assert.True(received > 0);
        }

        [Fact]
        public void ConnectionPool_BatchReadWrite()
        {
            using var server = new XinjeVirtualServer(GetFreeTcpPort());
            server.Start();

            using var pool = new XinjeConnectionPool("127.0.0.1", server.Port);
            var items = new[]
            {
                new KeyValuePair<string, object>("D120", (short)111),
                new KeyValuePair<string, object>("D121", (short)222),
                new KeyValuePair<string, object>("Y10", true),
            };

            var write = pool.BatchWrite(items);
            Assert.True(write.IsSuccess, write.Message);

            var wordRead = pool.BatchRead(new[] { "D120", "D121" });
            Assert.True(wordRead.IsSuccess, wordRead.Message);
            Assert.Equal((short)111, wordRead.Content["D120"]);
            Assert.Equal((short)222, wordRead.Content["D121"]);

            var boolRead = pool.ReadBool("Y10");
            Assert.True(boolRead.IsSuccess, boolRead.Message);
            Assert.True(boolRead.Content);
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose() { _server?.Stop(); _server?.Dispose(); }
    }
}
