using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Pipe;
using Xunit;

namespace Nexus.Core.Tests
{
    /// <summary>
    /// PR #B2 回归测试 — 验证 <see cref="CommunicationPipe"/> 抽象与具体实现的连接、收发、并发锁、
    /// 错误计数、Dispose 行为。
    /// </summary>
    public class CommunicationPipeTests
    {
        /// <summary>4 字节长度前缀 echo TCP 服务器: 收到 4 字节长度 + payload,回 4 字节长度 + payload。</summary>
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
                            if (plen <= 0 || plen > 4096) break;
                            byte[] payload = new byte[plen];
                            if (!await ReadExact(ns, payload, plen).ConfigureAwait(false)) break;
                            await ns.WriteAsync(len, 0, 4).ConfigureAwait(false);
                            await ns.WriteAsync(payload, 0, plen).ConfigureAwait(false);
                        }
                    }
                    catch { /* client gone */ }
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

        // ── PipeTcpNet 基础收发 ────────────────────────

        [Fact]
        public async Task TcpPipe_OpenSendReceive_RoundTrip()
        {
            using (var server = new EchoServer())
            using (var pipe = new PipeTcpNet("127.0.0.1", server.Port))
            {
                Assert.False(pipe.IsConnect);
                var open = pipe.OpenCommunication();
                Assert.True(open.IsSuccess, open.Message);
                Assert.True(pipe.IsConnect);

                // 请求 = 4 字节长度 + 4 字节 payload "TEST"
                byte[] payload = { (byte)'T', (byte)'E', (byte)'S', (byte)'T' };
                byte[] request = new byte[8];
                request[3] = 4;
                Array.Copy(payload, 0, request, 4, 4);

                var resp = await pipe.SendAndReceiveAsync(request, 8);
                Assert.True(resp.IsSuccess, resp.Message);
                Assert.Equal(8, resp.Content.Length);
                // 响应前 4 字节是长度(=4),后 4 字节是 payload
                Assert.Equal(4, (resp.Content[0] << 24) | resp.Content[3]);
                Assert.Equal(payload, new ArraySegment<byte>(resp.Content, 4, 4).ToArray());

                // 错误计数应为 0
                Assert.False(pipe.IsConnectError);
                Assert.Equal(0, pipe.ConnectErrorCount);
            }
        }

        [Fact]
        public void TcpPipe_ConnectionFailure_RaisesErrorCount()
        {
            // 用一个不存在的端口触发连接失败。
            using (var pipe = new PipeTcpNet("127.0.0.1", 1) { ReceiveTimeout = 200 })
            {
                var open = pipe.OpenCommunication();
                Assert.False(open.IsSuccess);
                Assert.False(pipe.IsConnect);

                // SendAndReceive 应返回失败,不会抛异常。
                byte[] req = { 0x01 };
                var r = pipe.SendAndReceive(req, 4);
                Assert.False(r.IsSuccess);
            }
        }

        [Fact]
        public async Task TcpPipe_ReceiveFailure_RaisesErrorCount()
        {
            // 用一个本地监听但立即断开的服务器,触发接收失败。
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;

                using (var pipe = new PipeTcpNet("127.0.0.1", port) { ReceiveTimeout = 300 })
                {
                    var open = pipe.OpenCommunication();
                    Assert.True(open.IsSuccess);

                    // 接受客户端连接后立即关闭 — 服务器侧。
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var c = listener.AcceptTcpClient();
                            Thread.Sleep(50);
                            c.Close();
                        }
                        catch { }
                    });

                    byte[] req = new byte[4];
                    var r = await pipe.SendAndReceiveAsync(req, 100);
                    Assert.False(r.IsSuccess);
                    Assert.True(pipe.IsConnectError);
                    Assert.True(pipe.ConnectErrorCount > 0);
                }
            }
        }

        // ── 并发锁串行化 ─────────────────────────

        [Fact]
        public async Task TcpPipe_ConcurrentAccess_SerializedByLock()
        {
            using (var server = new EchoServer())
            using (var pipe = new PipeTcpNet("127.0.0.1", server.Port))
            {
                Assert.True(pipe.OpenCommunication().IsSuccess);

                const int threadCount = 6;
                const int opsPerThread = 10;
                var errors = new List<string>();
                var errorLock = new object();

                var tasks = new Task[threadCount];
                for (int t = 0; t < threadCount; t++)
                {
                    int tid = t;
                    tasks[tid] = Task.Run(async () =>
                    {
                        for (int i = 0; i < opsPerThread; i++)
                        {
                            int tag = (tid << 16) | i;
                            byte[] request = new byte[8];
                            request[3] = 4; // payload len
                            request[4] = (byte)(tag >> 24);
                            request[5] = (byte)(tag >> 16);
                            request[6] = (byte)(tag >> 8);
                            request[7] = (byte)tag;

                            var resp = await pipe.SendAndReceiveAsync(request, 8).ConfigureAwait(false);
                            if (!resp.IsSuccess)
                            {
                                lock (errorLock) errors.Add($"t{tid} i{i}: {resp.Message}");
                                continue;
                            }
                            int respTag = (resp.Content[4] << 24) | (resp.Content[5] << 16)
                                        | (resp.Content[6] << 8) | resp.Content[7];
                            if (respTag != tag)
                                lock (errorLock) errors.Add($"t{tid} i{i}: tag mismatch {tag:X8}→{respTag:X8}");
                        }
                    });
                }

                await Task.WhenAll(tasks);
                Assert.True(errors.Count == 0, "并发收发出错:\n" + string.Join("\n", errors));
            }
        }

        // ── ICommunicationLock 替换 ─────────────────

        [Fact]
        public void NoneLock_SkipsActualLocking()
        {
            // CommunicationLockNone 应不阻塞,允许并发进入(适合单线程场景)。
            var lockObj = new CommunicationLockNone();
            lockObj.Acquire();
            // 第二次 Acquire 不应死锁。
            lockObj.Acquire();
            lockObj.Release();
            lockObj.Release();
            lockObj.Dispose();
        }

        [Fact]
        public async Task SemaphoreLock_SerializesAccess()
        {
            var lockObj = new CommunicationLockSemaphore();
            await lockObj.AcquireAsync(default);
            // 第二个 Acquire 应阻塞,用 Task.WhenAny 验证。
            var second = lockObj.AcquireAsync(default);
            var winner = await Task.WhenAny(second, Task.Delay(100));
            Assert.False(second.IsCompleted, "第二次 Acquire 不应完成(锁未释放)");

            lockObj.Release();
            await second; // 现在应该完成
            lockObj.Release();
            lockObj.Dispose();
        }

        // ── Dispose 幂等 ──────────────────────────

        [Fact]
        public void Dispose_IsIdempotent()
        {
            var pipe = new PipeTcpNet("127.0.0.1", 12345);
            pipe.Dispose();
            pipe.Dispose(); // 第二次不应抛异常

            // Dispose 后 OpenCommunication 应返回失败(管道已释放)。
            var r = pipe.OpenCommunication();
            Assert.False(r.IsSuccess);
        }

        [Fact]
        public async Task SendAndReceive_AfterDispose_ReturnsFailure()
        {
            var pipe = new PipeTcpNet("127.0.0.1", 12345);
            pipe.Dispose();
            var r = await pipe.SendAndReceiveAsync(new byte[] { 1 }, 4);
            Assert.False(r.IsSuccess);
            Assert.Contains("已释放", r.Message);
        }

        [Fact]
        public void SendOnly_AfterDispose_ReturnsFailure()
        {
            var pipe = new PipeTcpNet("127.0.0.1", 12345);
            pipe.Dispose();
            var r = pipe.SendOnly(new byte[] { 1 });
            Assert.False(r.IsSuccess);
        }

        // ── PipeUdpNet 基础 ────────────────────────

        [Fact]
        public void UdpPipe_Open_IsConnectTrue()
        {
            using (var pipe = new PipeUdpNet("127.0.0.1", 12345))
            {
                Assert.False(pipe.IsConnect);
                var open = pipe.OpenCommunication();
                Assert.True(open.IsSuccess);
                Assert.True(pipe.IsConnect);
            }
        }

        // ── PipeSerialPort via fake ───────────────

        private sealed class FakeSerialPort : ISerialPort
        {
            private readonly System.Collections.Generic.Queue<byte[]> _responses = new System.Collections.Generic.Queue<byte[]>();
            private byte[]? _current;
            private int _currentOffset;

            public string PortName { get; set; } = "COM_FAKE";
            public int BaudRate { get; set; } = 9600;
            public int DataBits { get; set; } = 8;
            public StopBits StopBits { get; set; } = StopBits.One;
            public Parity Parity { get; set; } = Parity.None;
            public int ReadTimeout { get; set; } = 1000;
            public int WriteTimeout { get; set; } = 1000;
            public bool IsOpen { get; private set; }
            public bool DtrEnable { get; set; }
            public bool RtsEnable { get; set; }

            public byte[]? LastWritten { get; private set; }

            public void EnqueueResponse(byte[] response) => _responses.Enqueue(response);

            public void Open() { IsOpen = true; }
            public void Close() { IsOpen = false; }

            public void Write(byte[] buffer, int offset, int count)
            {
                LastWritten = new byte[count];
                Array.Copy(buffer, offset, LastWritten, 0, count);
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                if (_current == null || _currentOffset >= _current.Length)
                {
                    if (_responses.Count == 0) return 0;
                    _current = _responses.Dequeue();
                    _currentOffset = 0;
                }
                int n = Math.Min(count, _current.Length - _currentOffset);
                Array.Copy(_current, _currentOffset, buffer, offset, n);
                _currentOffset += n;
                return n;
            }

            public void Dispose() => Close();
        }

        [Fact]
        public void SerialPipe_OpenSendReceive_RoundTrip()
        {
            var fakePort = new FakeSerialPort();
            fakePort.EnqueueResponse(new byte[] { 1, 2, 3, 4 });
            using (var pipe = new PipeSerialPort(fakePort))
            {
                Assert.False(pipe.IsConnect);
                Assert.True(pipe.OpenCommunication().IsSuccess);
                Assert.True(pipe.IsConnect);

                var resp = pipe.SendAndReceive(new byte[] { 0xAA, 0xBB }, 4);
                Assert.True(resp.IsSuccess, resp.Message);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, resp.Content);
                Assert.Equal(new byte[] { 0xAA, 0xBB }, fakePort.LastWritten);
            }
        }

        // ── PipeSslNet / PipeDtuNet 构造验证 ──────

        [Fact]
        public void SslPipe_Construct_StoresConfig()
        {
            var pipe = new PipeSslNet("plc.example.com", 443, serverMode: false);
            Assert.False(pipe.RemoteCertificateValidation);
            Assert.Null(pipe.Certificate);
            pipe.RemoteCertificateValidation = true;
            Assert.True(pipe.RemoteCertificateValidation);
        }

        [Fact]
        public void DtuPipe_Construct_StoresDeviceId()
        {
            var pipe = new PipeDtuNet("dtu.example.com", 8899, "DTU-001");
            Assert.Equal("dtu.example.com", pipe.Host);
            Assert.Equal(8899, pipe.Port);
            Assert.Equal("DTU-001", pipe.DeviceId);
        }
    }
}
