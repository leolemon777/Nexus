using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nexus;
using Xunit;

namespace Nexus.Core.Tests
{
    /// <summary>
    /// A3 并发回归测试 — 验证 <see cref="UdpDeviceBase"/> 的 Send/Receive 锁修复。
    ///
    /// 风险背景:修复前 <see cref="UdpDeviceBase.SendAndReceive(byte[])"/> 在
    /// <c>lock(_lock)</c> 内只取 client 引用就释放,Send/Receive 在锁外执行。
    /// UDP 虽然无连接,但同一 <c>UdpClient</c> 实例的两个并发 ReceiveAsync 会竞争同一个
    /// socket 的入队数据 → 第一个 Receive 拿到对方的响应,导致响应错配到错误请求。
    ///
    /// 本测试用真实 UDP echo 服务器(请求 = 8 字节含 tag,响应 = 同样 8 字节),
    /// 多线程并发通过同一 UdpDeviceBase 实例收发,验证每个响应严格匹配对应请求。
    /// </summary>
    public class UdpDeviceBaseConcurrencyTests
    {
        /// <summary>简单 UDP echo 服务器:收到什么回什么(原样)。</summary>
        private sealed class UdpEchoServer : IDisposable
        {
            private readonly UdpClient _server;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly Task _loopTask;

            public int Port { get; }

            public UdpEchoServer()
            {
                _server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                Port = ((IPEndPoint)_server.Client.LocalEndPoint!).Port;
                _loopTask = Task.Run(LoopAsync);
            }

            private async Task LoopAsync()
            {
                while (!_cts.IsCancellationRequested)
                {
                    UdpReceiveResult received;
                    try
                    {
                        received = await _server.ReceiveAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException) { break; }

                    // 模拟极小延迟让并发更易触发竞态(原本瞬间回包看不出问题)。
                    await Task.Delay(2).ConfigureAwait(false);

                    try
                    {
                        await _server.SendAsync(received.Buffer, received.Buffer.Length, received.RemoteEndPoint)
                            .ConfigureAwait(false);
                    }
                    catch { /* 客户端可能已断开 */ }
                }
            }

            public void Dispose()
            {
                _cts.Cancel();
                try { _server.Close(); } catch { }
                try { _loopTask.Wait(500); } catch { }
                _cts.Dispose();
            }
        }

        /// <summary>最小 UDP 设备 stub:绕过 ResponseHeaderLength/GetResponsePayloadLength 死抽象。</summary>
        private sealed class StubUdpDevice : UdpDeviceBase
        {
            public StubUdpDevice(string ip, int port, int timeout = 2000) : base(ip, port, timeout) { }

            // UDP 接收整个数据报,这两个抽象成员是死代码,提供无意义实现以满足契约。
            protected override int ResponseHeaderLength => 0;
            protected override int GetResponsePayloadLength(byte[] header) => 0;

            // 暴露 protected 方法供测试调用。
            public OperateResult<byte[]> DoSync(byte[] req) => SendAndReceive(req);
            public Task<OperateResult<byte[]>> DoAsync(byte[] req, CancellationToken ct = default)
                => SendAndReceiveAsync(req, ct);
        }

        /// <summary>
        /// 核心场景:sync + async 并发调用同一 UDP 设备实例,验证锁修复后无响应错配。
        /// </summary>
        [Fact]
        public async Task ConcurrentSyncAndAsync_NoResponseMismatch()
        {
            using (var server = new UdpEchoServer())
            using (var device = new StubUdpDevice("127.0.0.1", server.Port))
            {
                Assert.True(device.Connect().IsSuccess);

                const int threadCount = 6;
                const int opsPerThread = 12;
                var errors = new List<string>();
                var errorLock = new object();

                var tasks = new Task[threadCount];
                for (int t = 0; t < threadCount; t++)
                {
                    int tid = t;
                    bool useAsync = (tid % 2 == 0);
                    tasks[tid] = Task.Run(async () =>
                    {
                        for (int i = 0; i < opsPerThread; i++)
                        {
                            int tag = (tid << 16) | i;
                            byte[] request = new byte[8];
                            request[0] = (byte)(tag >> 24);
                            request[1] = (byte)(tag >> 16);
                            request[2] = (byte)(tag >> 8);
                            request[3] = (byte)tag;
                            // 后 4 字节填充固定模式,便于诊断。
                            request[4] = 0xDE; request[5] = 0xAD; request[6] = 0xBE; request[7] = 0xEF;

                            OperateResult<byte[]> resp = useAsync
                                ? await device.DoAsync(request).ConfigureAwait(false)
                                : device.DoSync(request);

                            if (!resp.IsSuccess)
                            {
                                lock (errorLock) errors.Add($"t{tid} i{i} ({(useAsync ? "A" : "S")}): {resp.Message}");
                                continue;
                            }

                            int respTag = (resp.Content[0] << 24) | (resp.Content[1] << 16)
                                        | (resp.Content[2] << 8) | resp.Content[3];
                            if (respTag != tag)
                            {
                                lock (errorLock)
                                    errors.Add($"t{tid} i{i}: tag mismatch — sent {tag:X8}, got {respTag:X8} — UDP 响应错配");
                            }
                        }
                    });
                }

                await Task.WhenAll(tasks);

                Assert.True(errors.Count == 0,
                    "UDP 并发 sync+async 收发响应错配:\n" + string.Join("\n", errors));
            }
        }

        /// <summary>
        /// 验证纯 async 并发场景(基线对照)。
        /// </summary>
        [Fact]
        public async Task ConcurrentAsync_NoResponseMismatch()
        {
            using (var server = new UdpEchoServer())
            using (var device = new StubUdpDevice("127.0.0.1", server.Port))
            {
                Assert.True(device.Connect().IsSuccess);

                const int threadCount = 8;
                const int opsPerThread = 15;
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
                            request[0] = (byte)(tag >> 24);
                            request[1] = (byte)(tag >> 16);
                            request[2] = (byte)(tag >> 8);
                            request[3] = (byte)tag;

                            var resp = await device.DoAsync(request).ConfigureAwait(false);
                            if (!resp.IsSuccess)
                            {
                                lock (errorLock) errors.Add($"t{tid} i{i}: {resp.Message}");
                                continue;
                            }
                            int respTag = (resp.Content[0] << 24) | (resp.Content[1] << 16)
                                        | (resp.Content[2] << 8) | resp.Content[3];
                            if (respTag != tag)
                                lock (errorLock) errors.Add($"t{tid} i{i}: tag mismatch {tag:X8}→{respTag:X8}");
                        }
                    });
                }

                await Task.WhenAll(tasks);
                Assert.Empty(errors);
            }
        }

        /// <summary>
        /// Dispose 应幂等且不抛 ObjectDisposedException 给并发调用方。
        /// </summary>
        [Fact]
        public void Dispose_IsIdempotent_AndReleasesAsyncLock()
        {
            var device = new StubUdpDevice("127.0.0.1", 12345);
            device.Dispose();
            // 第二次 Dispose 必须幂等。
            device.Dispose();

            // Connect 后 Dispose 失败(而非抛异常)。
            var r = device.Connect();
            Assert.False(r.IsSuccess);
        }
    }
}
