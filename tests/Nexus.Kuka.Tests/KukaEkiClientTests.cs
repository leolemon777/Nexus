using System.Collections.Generic;
using Xunit;
using Nexus.Kuka;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Kuka.Tests
{
    public class KukaEkiClientTests
    {
        [Fact]
        public void Constructor_SetsDefaults()
        {
            var client = new KukaEkiClient("192.168.1.40");
            Assert.Equal("192.168.1.40", client.IpAddress);
            Assert.Equal(54600, client.Port);
            Assert.Equal(5000, client.Timeout);
            Assert.False(client.IsConnected);
            client.Dispose();
        }

        [Fact]
        public void Constructor_CustomPort_SetsCorrectly()
        {
            using var client = new KukaEkiClient("10.0.0.1", 54601, timeout: 3000);
            Assert.Equal("10.0.0.1", client.IpAddress);
            Assert.Equal(54601, client.Port);
            Assert.Equal(3000, client.Timeout);
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            using var client = new KukaEkiClient("127.0.0.1");
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var client = new KukaEkiClient("127.0.0.1");
            client.Dispose();
            client.Dispose();
        }

        [Fact]
        public void Connect_InvalidHost_Fails()
        {
            using var client = new KukaEkiClient("127.0.0.1", 19999, timeout: 500);
            var result = client.Connect();
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public async Task ConnectAsync_InvalidHost_Fails()
        {
            using var client = new KukaEkiClient("127.0.0.1", 19999, timeout: 500);
            var result = await client.ConnectAsync();
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Disconnect_WhenNotConnected_DoesNotThrow()
        {
            using var client = new KukaEkiClient("127.0.0.1");
            client.Disconnect();
        }

        [Fact]
        public void IsConnected_BeforeConnect_ReturnsFalse()
        {
            using var client = new KukaEkiClient("127.0.0.1");
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void Timeout_CanBeSet()
        {
            using var client = new KukaEkiClient("127.0.0.1");
            client.Timeout = 10000;
            Assert.Equal(10000, client.Timeout);
        }

        [Fact]
        public void KukaCartesianPosition_Defaults_AreZero()
        {
            var pos = new KukaCartesianPosition();
            Assert.Equal(0.0, pos.X);
            Assert.Equal(0.0, pos.Y);
            Assert.Equal(0.0, pos.Z);
            Assert.Equal(0.0, pos.A);
            Assert.Equal(0.0, pos.B);
            Assert.Equal(0.0, pos.C);
        }

        [Fact]
        public void KukaCartesianPosition_ToString_ContainsValues()
        {
            var pos = new KukaCartesianPosition { X = 100.5, Y = -200.3, Z = 300.0 };
            var s = pos.ToString();
            Assert.Contains("100.5", s);
            Assert.Contains("-200.3", s);
            Assert.Contains("300", s);
        }

        [Fact]
        public void KukaCartesianPosition_FullRotation_ToString()
        {
            var pos = new KukaCartesianPosition { A = 90.0, B = 180.0, C = 270.0 };
            var s = pos.ToString();
            Assert.Contains("90", s);
            Assert.Contains("180", s);
            Assert.Contains("270", s);
        }

        [Fact]
        public void KukaProgramState_Default_IsNotRunning()
        {
            var state = new KukaProgramState();
            Assert.False(state.IsRunning);
            Assert.False(state.IsPaused);
            Assert.Equal("", state.State);
        }

        [Fact]
        public void KukaProgramState_ToString_WhenRunning()
        {
            var state = new KukaProgramState { IsRunning = true };
            Assert.Equal("RUNNING", state.ToString());
        }

        [Fact]
        public void KukaProgramState_ToString_WhenPaused()
        {
            var state = new KukaProgramState { IsPaused = true };
            Assert.Equal("PAUSED", state.ToString());
        }

        [Fact]
        public void KukaProgramState_ToString_WhenIdle()
        {
            var state = new KukaProgramState { State = "IDLE" };
            Assert.Equal("IDLE", state.ToString());
        }

        // ── 未连接操作 ────────────────────────────

        [Fact]
        public void ReadOperations_NotConnected_ReturnError()
        {
            using var client = new KukaEkiClient("127.0.0.1");
            Assert.False(client.ReadInt16("COUNT").IsSuccess);
            Assert.False(client.ReadInt32("POS_X").IsSuccess);
            Assert.False(client.ReadFloat("TEMP").IsSuccess);
            Assert.False(client.ReadBool("DI01").IsSuccess);
            Assert.False(client.ReadString("NAME", 10).IsSuccess);
        }

        [Fact]
        public void WriteOperations_NotConnected_ReturnError()
        {
            using var client = new KukaEkiClient("127.0.0.1");
            Assert.False(client.Write("COUNT", (short)42).IsSuccess);
            Assert.False(client.Write("DI01", true).IsSuccess);
        }

        [Fact]
        public void BatchOperations_EmptyInput_ReturnsError()
        {
            using var client = new KukaEkiClient("127.0.0.1");
            Assert.False(client.BatchRead(new string[0]).IsSuccess);
            Assert.False(client.RandomRead(new string[0]).IsSuccess);
            Assert.False(client.BatchWrite(Array.Empty<KeyValuePair<string, object>>()).IsSuccess);
        }

        [Fact]
        public void Subscribe_Unsubscribe_DoesNotThrow()
        {
            using var client = new KukaEkiClient("127.0.0.1");
            client.Subscribe("COUNT", 1000, "Int16");
            client.Unsubscribe("COUNT");
            client.StartSubscriptions();
            client.StopSubscriptions();
        }

        // ── ConnectionPool 基础 ──────────────────────

        [Fact]
        public void ReadInt16_WithVirtualServer_ReturnsValue()
        {
            using var server = new KukaEkiVirtualServer(0);
            server.SetVariable("COUNT", "42");
            server.Start();
            Thread.Sleep(100);

            using var client = new KukaEkiClient("127.0.0.1", server.Port);
            var connect = client.Connect();
            Assert.True(connect.IsSuccess, connect.Message ?? "Connect failed");

            var result = client.ReadInt16("COUNT");

            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal((short)42, result.Content);
        }

        [Fact]
        public void ConnectionPool_ReadInt16_ReusesPersistentConnection()
        {
            using var server = new KukaEkiVirtualServer(0);
            server.SetVariable("COUNT", "42");
            server.Start();
            Thread.Sleep(100);
            using var pool = new KukaEkiConnectionPool("127.0.0.1", server.Port, maxPoolSize: 1);

            for (int i = 0; i < 3; i++)
            {
                var result = pool.ReadInt16("COUNT");
                Assert.True(result.IsSuccess, result.Message ?? "Pool read failed");
                Assert.Equal((short)42, result.Content);
            }

            WaitForConnections(server, 1);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
            Assert.Equal(1, server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_WriteAndReadString_RoundTrip()
        {
            using var server = new KukaEkiVirtualServer(0);
            server.Start();
            Thread.Sleep(100);
            using var pool = new KukaEkiConnectionPool("127.0.0.1", server.Port, maxPoolSize: 1);

            var write = pool.Write("PROGRAM", "MAIN");
            Assert.True(write.IsSuccess, write.Message ?? "Pool write failed");

            var read = pool.ReadString("PROGRAM", 10);
            Assert.True(read.IsSuccess, read.Message ?? "Pool read after write failed");
            Assert.Equal("MAIN", read.Content);

            WaitForConnections(server, 1);
            Assert.Equal(1, server.ConnectionCount);
        }

        [Fact]
        public async Task ConnectionPool_ExecuteAsync_ReadInt16_ReusesPersistentConnection()
        {
            using var server = new KukaEkiVirtualServer(0);
            server.SetVariable("COUNT", "77");
            server.Start();
            Thread.Sleep(100);
            using var pool = new KukaEkiConnectionPool("127.0.0.1", server.Port, maxPoolSize: 1);

            var result = await pool.ExecuteAsync(c => c.ReadInt16Async("COUNT"));

            Assert.True(result.IsSuccess, result.Message ?? "Pool async read failed");
            Assert.Equal((short)77, result.Content);
            WaitForConnections(server, 1);
            Assert.Equal(1, server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsMessageEvents()
        {
            using var server = new KukaEkiVirtualServer(0);
            server.SetVariable("COUNT", "42");
            server.Start();
            Thread.Sleep(100);
            using var pool = new KukaEkiConnectionPool("127.0.0.1", server.Port);
            int sent = 0;
            int received = 0;
            pool.OnMessageSent += (_, __) => Interlocked.Increment(ref sent);
            pool.OnMessageReceived += (_, __) => Interlocked.Increment(ref received);

            var result = pool.ReadInt16("COUNT");

            Assert.True(result.IsSuccess, result.Message ?? "Pool read failed");
            Assert.True(sent > 0);
            Assert.True(received > 0);
        }

        private static void WaitForConnections(KukaEkiVirtualServer server, int expected)
        {
            for (int i = 0; i < 20 && server.ConnectionCount < expected; i++)
                Thread.Sleep(25);
        }
    }
}
