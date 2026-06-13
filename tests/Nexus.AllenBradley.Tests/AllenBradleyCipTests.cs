using Xunit;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Nexus.AllenBradley;

namespace Nexus.AllenBradley.Tests
{
    public class AllenBradleyCipTests
    {
        [Fact]
        public void Constructor_SetsDefaults()
        {
            var client = new AllenBradleyCipClient("192.168.1.1");
            Assert.Equal("192.168.1.1", client.IpAddress);
            Assert.Equal(44818, client.Port);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void Constructor_WithPort_SetsPort()
        {
            var client = new AllenBradleyCipClient("192.168.1.1", 5000);
            Assert.Equal(5000, client.Port);
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            var client = new AllenBradleyCipClient("192.168.1.1");
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            var client = new AllenBradleyCipClient("192.168.1.1");
            client.Dispose();
        }

        [Fact]
        public void BatchOperations_EmptyInput_ReturnsError()
        {
            var client = new AllenBradleyCipClient("192.168.1.1");

            Assert.False(client.BatchRead(new string[0]).IsSuccess);
            Assert.False(client.RandomRead(new string[0]).IsSuccess);
            Assert.False(client.BatchWrite(System.Array.Empty<System.Collections.Generic.KeyValuePair<string, object>>()).IsSuccess);
        }

        [Fact]
        public void ConnectionPool_ReadWrite_ReusesPersistentConnection()
        {
            int port = GetFreeTcpPort();
            using var server = new CipVirtualServer(port);
            server.AddTag("PoolDint", 0);
            server.Start();

            using var pool = new AllenBradleyCipConnectionPool("127.0.0.1", port, maxPoolSize: 1);

            var write = pool.Write("PoolDint", 2468);
            Assert.True(write.IsSuccess, write.Message);

            var read = pool.ReadInt32("PoolDint");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal(2468, read.Content);
            Assert.Equal(2468, server.GetTagValue<int>("PoolDint"));
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsPacketEvents()
        {
            int port = GetFreeTcpPort();
            using var server = new CipVirtualServer(port);
            server.AddTag("EventDint", 1357);
            server.Start();

            using var pool = new AllenBradleyCipConnectionPool("127.0.0.1", port, maxPoolSize: 1);

            int sent = 0;
            int received = 0;
            pool.OnMessageSent += (_, __) => Interlocked.Increment(ref sent);
            pool.OnMessageReceived += (_, __) => Interlocked.Increment(ref received);

            var read = pool.ReadInt32("EventDint");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal(1357, read.Content);
            Assert.True(Volatile.Read(ref sent) >= 2);
            Assert.True(Volatile.Read(ref received) >= 2);
        }

        [Fact]
        public void ConnectionPool_ReadDeviceIdentity()
        {
            int port = GetFreeTcpPort();
            using var server = new CipVirtualServer(port);
            server.DeviceName = "1756-L85E";
            server.Start();

            using var pool = new AllenBradleyCipConnectionPool("127.0.0.1", port, maxPoolSize: 1);

            var identity = pool.ReadDeviceIdentity();
            Assert.True(identity.IsSuccess, identity.Message);
            Assert.Equal("1756-L85E", identity.Content.ProductName);
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
