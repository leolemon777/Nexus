using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Device;
using Nexus.LengthPrefixTcp;
using Xunit;

namespace Nexus.LengthPrefixTcp.Tests
{
    /// <summary>
    /// PR #B7 验证测试 — 用 Nexus.LengthPrefixTcp 示范协议证明 Phase B 新架构
    /// (DeviceCommunication + PipeTcpNet + LengthPrefixTcpMessage) 能端到端工作。
    /// </summary>
    public class LengthPrefixTcpTests
    {
        /// <summary>Echo 服务器:收 [4-byte len][payload],回 [4-byte len][payload]。</summary>
        private sealed class EchoServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly Task _task;
            public int Port { get; }

            public EchoServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _task = Task.Run(RunAsync);
            }

            private async Task RunAsync()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                    catch { break; }
                    lock (_clients) _clients.Add(c);
                    _ = Task.Run(() => HandleAsync(c));
                }
            }

            private async Task HandleAsync(TcpClient client)
            {
                using (client)
                using (var ns = client.GetStream())
                {
                    try
                    {
                        while (!_cts.IsCancellationRequested)
                        {
                            byte[] len = new byte[4];
                            if (!await ReadExact(ns, len, 4).ConfigureAwait(false)) break;
                            int plen = (len[0] << 24) | (len[1] << 16) | (len[2] << 8) | len[3];
                            if (plen < 0 || plen > 65536) break;
                            byte[] payload = new byte[plen];
                            if (plen > 0 && !await ReadExact(ns, payload, plen).ConfigureAwait(false)) break;
                            await ns.WriteAsync(len, 0, 4).ConfigureAwait(false);
                            if (plen > 0)
                                await ns.WriteAsync(payload, 0, plen).ConfigureAwait(false);
                        }
                    }
                    catch { }
                }
            }

            private static async Task<bool> ReadExact(NetworkStream ns, byte[] buf, int count)
            {
                int off = 0;
                while (off < count)
                {
                    int n = await ns.ReadAsync(buf, off, count - off).ConfigureAwait(false);
                    if (n == 0) return false;
                    off += n;
                }
                return true;
            }

            public void Dispose()
            {
                _cts.Cancel();
                try { _listener.Stop(); } catch { }
                lock (_clients) { foreach (var c in _clients) try { c.Close(); } catch { } _clients.Clear(); }
                try { _task.Wait(500); } catch { }
                _cts.Dispose();
            }
        }

        [Fact]
        public async Task Client_Connects_And_Echoes_Payload()
        {
            using (var server = new EchoServer())
            using (var client = new LengthPrefixTcpClient("127.0.0.1", server.Port, timeout: 2000))
            {
                Assert.False(client.IsConnected);

                byte[] payload = { 0x01, 0x02, 0x03, 0x04, 0x05 };
                var r = client.SendPayload(payload);
                Assert.True(r.IsSuccess, r.Message);
                // 响应 = 4 字节长度 + 5 字节 payload(回显)。
                Assert.Equal(9, r.Content.Length);
                Assert.Equal(5, (r.Content[0] << 24) | r.Content[3]);
                Assert.Equal(payload, new ArraySegment<byte>(r.Content, 4, 5).ToArray());

                Assert.True(client.IsConnected);
            }
        }

        [Fact]
        public async Task Client_MultipleRequests_Work()
        {
            using (var server = new EchoServer())
            using (var client = new LengthPrefixTcpClient("127.0.0.1", server.Port, timeout: 2000))
            {
                for (int i = 0; i < 5; i++)
                {
                    byte[] payload = BitConverter.GetBytes(i);
                    var r = client.SendPayload(payload);
                    Assert.True(r.IsSuccess, $"iter {i}: {r.Message}");
                    Assert.Equal(payload, new ArraySegment<byte>(r.Content, 4, 4).ToArray());
                }
            }
        }

        [Fact]
        public async Task Client_OversizedPayload_ReturnsFailed()
        {
            using (var server = new EchoServer())
            using (var client = new LengthPrefixTcpClient("127.0.0.1", server.Port, timeout: 2000))
            {
                byte[] big = new byte[0x1000001]; // > 16MB
                var r = client.SendPayload(big);
                Assert.False(r.IsSuccess);
            }
        }

        [Fact]
        public async Task Client_ConnectionFailure_ReturnsFailed()
        {
            // 用未监听端口(1 号端口通常需要 root,触发连接失败)。
            using (var client = new LengthPrefixTcpClient("127.0.0.1", 1, timeout: 200))
            {
                var r = client.SendPayload(new byte[] { 1 });
                Assert.False(r.IsSuccess);
            }
        }

        [Fact]
        public void Client_StoresConnectionConfig()
        {
            using (var client = new LengthPrefixTcpClient("192.168.1.100", 502, timeout: 3000))
            {
                Assert.Equal("192.168.1.100", client.IpAddress);
                Assert.Equal(502, client.Port);
                Assert.Equal(3000, client.Timeout);
            }
        }
    }
}
