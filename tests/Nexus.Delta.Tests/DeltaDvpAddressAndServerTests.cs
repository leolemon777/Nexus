using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Xunit;
using Nexus.Delta;

namespace Nexus.Delta.Tests
{
    public class DeltaDvpAddressTests
    {
        [Theory]
        [InlineData("D100", 0x1000 + 100, 0x03, 0x06)]
        [InlineData("D0", 0x1000, 0x03, 0x06)]
        [InlineData("D9999", 0x1000 + 9999, 0x03, 0x06)]
        [InlineData("Y0", 0x0000, 0x01, 0x05)]
        [InlineData("Y10", 0x0000 + 10, 0x01, 0x05)]
        [InlineData("X0", 0x0000, 0x02, 0x00)]
        [InlineData("X20", 0x0000 + 20, 0x02, 0x00)]
        [InlineData("M0", 0x0800, 0x01, 0x05)]
        [InlineData("M100", 0x0800 + 100, 0x01, 0x05)]
        [InlineData("T0", 0x0C00, 0x01, 0x05)]
        [InlineData("C0", 0x1000, 0x01, 0x05)]
        [InlineData("S20", (ushort)(0x0800 + 2048 + 20), 0x01, 0x05)]
        public void Parse_ValidAddresses(string addr, ushort expectedAddr, byte readFc, byte writeFc)
        {
            var parsed = DeltaDvpAddress.Parse(addr);
            Assert.Equal(expectedAddr, parsed.Address);
            Assert.Equal(readFc, parsed.ReadFunctionCode);
            Assert.Equal(writeFc, parsed.WriteFunctionCode);
        }

        [Theory]
        [InlineData("d100", 0x1000 + 100)]
        [InlineData("y0", 0x0000)]
        [InlineData("x5", 0x0005)]
        [InlineData("m50", 0x0800 + 50)]
        public void Parse_CaseInsensitive(string addr, ushort expectedAddr)
        {
            var parsed = DeltaDvpAddress.Parse(addr);
            Assert.Equal(expectedAddr, parsed.Address);
        }

        [Theory]
        [InlineData("D100", false, true)]
        [InlineData("Y0", true, false)]
        [InlineData("X0", true, false)]
        [InlineData("M100", true, false)]
        public void AreaType_Identified(string addr, bool isBit, bool isReg)
        {
            var parsed = DeltaDvpAddress.Parse(addr);
            Assert.Equal(isBit, parsed.IsBitArea);
            Assert.Equal(isReg, parsed.IsRegisterArea);
        }

        [Fact]
        public void Parse_WhitespaceTrimmed()
        {
            var parsed = DeltaDvpAddress.Parse("  D100  ");
            Assert.Equal(0x1000 + 100, parsed.Address);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Parse_EmptyThrows(string? addr)
        {
            Assert.Throws<ArgumentException>(() => DeltaDvpAddress.Parse(addr!));
        }

        [Fact]
        public void Parse_InvalidNumberThrows()
        {
            Assert.Throws<ArgumentException>(() => DeltaDvpAddress.Parse("DABC"));
        }

        [Fact]
        public void TryParse_InvalidReturnsNull()
        {
            Assert.Null(DeltaDvpAddress.TryParse(""));
        }

        [Fact]
        public void WithOffset_CalculatesCorrectly()
        {
            var base_ = DeltaDvpAddress.Parse("D100");
            var offset = base_.WithOffset(5);
            Assert.Equal(0x1000 + 105, offset.Address);
        }

        [Fact]
        public void ToString_ContainsInfo()
        {
            var parsed = DeltaDvpAddress.Parse("D100");
            string s = parsed.ToString();
            Assert.Contains("DataRegister", s);
            Assert.Contains("FC3", s);
        }

        [Fact]
        public void IsReadOnly_XArea()
        {
            var x = DeltaDvpAddress.Parse("X0");
            Assert.True(x.IsReadOnly);
            var d = DeltaDvpAddress.Parse("D100");
            Assert.False(d.IsReadOnly);
        }
    }

    public class DeltaDvpVirtualServerTests : IDisposable
    {
        private readonly DeltaDvpVirtualServer _server;

        public DeltaDvpVirtualServerTests()
        {
            _server = new DeltaDvpVirtualServer(0);
            _server.Start();
        }

        [Fact]
        public void Server_StartsAndStops()
        {
            Assert.True(_server.IsRunning);
            _server.Stop();
            Assert.False(_server.IsRunning);
        }

        [Fact]
        public void SetGetHoldingRegister()
        {
            _server.SetHoldingRegister(100, 12345);
            Assert.Equal(12345, _server.GetHoldingRegister(100));
        }

        [Fact]
        public void SetGetCoil()
        {
            _server.SetCoil(50, true);
            Assert.True(_server.GetCoil(50));
            _server.SetCoil(50, false);
            Assert.False(_server.GetCoil(50));
        }

        public void Dispose()
        {
            _server?.Stop();
            _server?.Dispose();
        }
    }

    public class DeltaDvpConnectionPoolTests : IDisposable
    {
        private DeltaDvpVirtualServer? _server;

        public void Dispose()
        {
            _server?.Stop();
            _server?.Dispose();
        }

        [Fact]
        public void ConnectionPool_ReadWrite_ReusesPersistentConnection()
        {
            int port = GetFreeTcpPort();
            _server = new DeltaDvpVirtualServer(port);
            _server.Start();

            using var pool = new DeltaDvpConnectionPool("127.0.0.1", port, maxPoolSize: 1);

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
            int port = GetFreeTcpPort();
            _server = new DeltaDvpVirtualServer(port);
            _server.SetHoldingRegister(0x1000 + 10, 0x1234);
            _server.Start();

            using var pool = new DeltaDvpConnectionPool("127.0.0.1", port);
            int sent = 0;
            int received = 0;
            pool.OnMessageSent += (_, _) => Interlocked.Increment(ref sent);
            pool.OnMessageReceived += (_, _) => Interlocked.Increment(ref received);

            var read = pool.ReadUInt16("D10");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal((ushort)0x1234, read.Content);
            Assert.True(sent > 0);
            Assert.True(received > 0);
        }

        [Fact]
        public void ConnectionPool_BatchReadWrite()
        {
            int port = GetFreeTcpPort();
            _server = new DeltaDvpVirtualServer(port);
            _server.Start();

            using var pool = new DeltaDvpConnectionPool("127.0.0.1", port);
            var write = pool.BatchWrite(new[]
            {
                new KeyValuePair<string, object>("D20", (short)111),
                new KeyValuePair<string, object>("D21", (short)222),
            });
            Assert.True(write.IsSuccess, write.Message);

            var read = pool.BatchRead(new[] { "D20", "D21" });
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal((short)111, read.Content["D20"]);
            Assert.Equal((short)222, read.Content["D21"]);
        }

        [Fact]
        public void ConnectionPool_ReadWriteBools()
        {
            int port = GetFreeTcpPort();
            _server = new DeltaDvpVirtualServer(port);
            _server.Start();

            using var pool = new DeltaDvpConnectionPool("127.0.0.1", port);

            var write = pool.WriteBools("Y10", new[] { true, false, true });
            Assert.True(write.IsSuccess, write.Message);

            var read = pool.ReadBools("Y10", 3);
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal(new[] { true, false, true }, read.Content);
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
