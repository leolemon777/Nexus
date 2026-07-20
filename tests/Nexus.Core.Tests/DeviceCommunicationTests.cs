using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Device;
using Nexus.IMessage;
using Nexus.Pipe;
using Xunit;

namespace Nexus.Core.Tests
{
    /// <summary>
    /// PR #B4 回归测试 — 验证 <see cref="DeviceCommunication"/> 正确组合 Pipe + IByteTransform +
    /// INetMessage,提供完整的收发能力。
    /// </summary>
    public class DeviceCommunicationTests
    {
        /// <summary>4 字节长度前缀 echo TCP 服务器。</summary>
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

        /// <summary>
        /// Stub 协议客户端:用 4 字节长度前缀协议。响应长度 = 头 4 字节 + payload。
        /// 用 LengthPrefixNetMessage 动态决定响应总长度(从请求推断或从响应头读取)。
        /// 为简化,本 stub 固定 4+4=8 字节响应,只发 8 字节请求(payload=4)。
        /// </summary>
        private sealed class StubDevice : DeviceCommunication
        {
            public StubDevice(string host, int port)
                : base(new PipeTcpNet(host, port))
            {
                MessageFrame = new LengthPrefixMessage();
            }

            // 覆盖两阶段读 — 我们知道响应 = 4 字节头 + N 字节 payload(等于请求的 payload 长度)。
            // 为简单,这里走固定长度模式:4 + 请求 payload 长度。
            private int LastRequestPayloadLen = 2;

            protected override int EstimatePayloadLength() => LastRequestPayloadLen;

            public OperateResult<short> DoReadInt16(short addr)
            {
                byte[] request = new byte[6];
                request[3] = 2;
                request[4] = (byte)(addr >> 8);
                request[5] = (byte)addr;

                var r = ReadFromCoreServer(request);
                if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
                return OperateResult<short>.Success((short)((r.Content[4] << 8) | r.Content[5]));
            }
        }

        /// <summary>
        /// 简单 4 字节长度前缀帧解析:头 4 字节(大端长度 N),负载 N 字节。
        /// </summary>
        private sealed class LengthPrefixMessage : NetMessageBase
        {
            public override int ProtocolHeadBytesLength => 4;
            public override int GetContentLength(byte[] head)
            {
                if (head == null || head.Length < 4) return 0;
                return (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];
            }
        }

        [Fact]
        public async Task DeviceCommunication_ConnectAndRead_RoundTrip()
        {
            using (var server = new EchoServer())
            using (var device = new StubDevice("127.0.0.1", server.Port))
            {
                Assert.False(device.IsConnected);

                var r = device.DoReadInt16(0x1234);
                Assert.True(r.IsSuccess, r.Message);
                Assert.Equal(0x1234, r.Content);
                Assert.True(device.IsConnected);
            }
        }

        [Fact]
        public async Task DeviceCommunication_AsyncRoundTrip_Works()
        {
            using (var server = new EchoServer())
            using (var device = new StubDevice("127.0.0.1", server.Port))
            {
                // 用 2 字节 payload(与 StubDevice 的 EstimatePayloadLength 一致)。
                byte[] request = new byte[6];
                request[3] = 2;
                request[4] = 0xDE; request[5] = 0xAD;

                var r = await device.ReadFromCoreServerAsync(request);
                Assert.True(r.IsSuccess, r.Message);
                // 响应 = 4 字节长度 + 2 字节 payload(回显)。
                Assert.Equal((ushort)0xDEAD, (ushort)((r.Content[4] << 8) | r.Content[5]));
            }
        }

        [Fact]
        public void DefaultReadWriteOperations_ReturnFailedNotThrow()
        {
            // DeviceCommunication 默认未实现的 Read/Write 返回 OperateResult.Failed,不抛 NIE。
            using (var device = new StubDevice("127.0.0.1", 12345))
            {
                Assert.False(device.ReadBool("D0").IsSuccess);
                Assert.False(device.ReadInt32("D0").IsSuccess);
                Assert.False(device.Write("D0", 1).IsSuccess);
                Assert.False(device.Write("D0", "x").IsSuccess);
            }
        }

        [Fact]
        public async Task DefaultAsyncOperations_ReturnFailedNotThrow()
        {
            using (var device = new StubDevice("127.0.0.1", 12345))
            {
                Assert.False((await device.ReadBoolAsync("D0")).IsSuccess);
                Assert.False((await device.WriteAsync("D0", 1)).IsSuccess);
            }
        }

        [Fact]
        public void Connect_WithoutPipe_ReturnsFailed()
        {
            // 不调用 base 的 Pipe 设置就直接用 — Pipe 属性会抛,但 Connect 应优雅返回 Failed。
            var device = new NoPipeDevice();
            var r = device.Connect();
            Assert.False(r.IsSuccess);
        }

        private sealed class NoPipeDevice : DeviceCommunication
        {
            // 故意不设置 Pipe,验证 Connect 路径的优雅失败。
        }

        [Fact]
        public void ByteTransform_DefaultIsRegularBigEndian()
        {
            using (var device = new StubDevice("127.0.0.1", 12345))
            {
                Assert.Equal(Endianness.BigEndian, device.ByteTransform.ByteOrder);
                // 字节变换器可用 — 验证大端 GetBytes(short)
                Assert.Equal(new byte[] { 0x12, 0x34 }, device.ByteTransform.GetBytes((short)0x1234));
            }
        }

        [Fact]
        public void ByteTransform_CanBeReplaced()
        {
            using (var device = new StubDevice("127.0.0.1", 12345))
            {
                device.ByteTransform = ReverseBytesTransform.Instance; // 小端
                Assert.Equal(Endianness.LittleEndian, device.ByteTransform.ByteOrder);
                Assert.Equal(new byte[] { 0x34, 0x12 }, device.ByteTransform.GetBytes((short)0x1234));
            }
        }

        [Fact]
        public async Task DisposedDevice_OperationsReturnFailed()
        {
            using (var server = new EchoServer())
            {
                var device = new StubDevice("127.0.0.1", server.Port);
                device.Dispose();
                var r = await device.ReadFromCoreServerAsync(new byte[] { 0 });
                Assert.False(r.IsSuccess);
                Assert.Contains("已释放", r.Message);
            }
        }

        [Fact]
        public async Task DeviceCommunication_ConnectionFailure_ReturnsFailed()
        {
            // 用一个未监听的端口,连接应失败但 ReadFromCoreServer 应优雅返回。
            using (var device = new StubDevice("127.0.0.1", 1))
            {
                // PipeTcpNet ReceiveTimeout 默认 5000ms,这里临时调小加速测试。
                ((PipeTcpNet)device.Pipe).ReceiveTimeout = 200;
                var r = device.DoReadInt16(0);
                Assert.False(r.IsSuccess);
            }
        }
    }
}
