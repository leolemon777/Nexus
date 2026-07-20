using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Device;
using Nexus.IMessage;
using Nexus.Pipe;
using Xunit;

namespace Nexus.Core.Tests
{
    /// <summary>
    /// PR #B5 + #B6 回归测试 — DeviceServer 虚拟服务器基类 + 3 个便利基类
    /// (DeviceTcpNet / DeviceUdpNet / DeviceSerialPort)。
    /// </summary>
    public class DeviceServerAndBasesTests
    {
        // ── DeviceServer 端到端 ─────────────────────

        /// <summary>极简 echo 服务器:收到什么字节,原样回。</summary>
        private sealed class EchoDeviceServer : DeviceServer
        {
            protected override async Task HandleClientAsync(TcpClient client, string clientId, CancellationToken ct)
            {
                using (var ns = client.GetStream())
                {
                    byte[] buf = new byte[1024];
                    while (!ct.IsCancellationRequested)
                    {
                        int n;
                        try { n = await ns.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false); }
                        catch { break; }
                        if (n == 0) break;
                        await ns.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        [Fact]
        public async Task DeviceServer_StartsOnRandomPort_AcceptsClients()
        {
            using (var server = new EchoDeviceServer())
            {
                Assert.False(server.IsRunning);
                var start = server.ServerStart(port: 0);
                Assert.True(start.IsSuccess, start.Message);
                Assert.True(server.IsRunning);
                Assert.True(server.Port > 0);

                // 用 TcpClient 连接,echo 验证。
                using (var client = new TcpClient("127.0.0.1", server.Port))
                using (var ns = client.GetStream())
                {
                    byte[] req = Encoding.ASCII.GetBytes("hello");
                    await ns.WriteAsync(req, 0, req.Length);
                    byte[] buf = new byte[16];
                    int n = await ns.ReadAsync(buf, 0, buf.Length);
                    Assert.Equal("hello", Encoding.ASCII.GetString(buf, 0, n));
                }

                // OnlineCount 在客户端断开后应回到 0(允许一点延迟)。
                for (int i = 0; i < 30 && server.OnlineCount > 0; i++)
                    await Task.Delay(50);
                Assert.Equal(0, server.OnlineCount);

                server.ServerClose();
                Assert.False(server.IsRunning);
            }
        }

        [Fact]
        public void DeviceServer_ServerStart_Twice_ReturnsFailed()
        {
            using (var server = new EchoDeviceServer())
            {
                Assert.True(server.ServerStart(0).IsSuccess);
                var second = server.ServerStart(0);
                Assert.False(second.IsSuccess);
                server.ServerClose();
            }
        }

        [Fact]
        public void DeviceServer_Dispose_IsIdempotent()
        {
            var server = new EchoDeviceServer();
            server.Dispose();
            server.Dispose(); // 第二次不抛异常。
            // Dispose 后 ServerStart 应失败。
            var r = server.ServerStart(0);
            Assert.False(r.IsSuccess);
        }

        // ── DeviceTcpNet 便利基类 ───────────────────

        /// <summary>用 DeviceTcpNet 实现一个最小 echo 客户端。</summary>
        private sealed class EchoTcpClient : DeviceTcpNet
        {
            public EchoTcpClient(string ip, int port) : base(ip, port, timeout: 2000)
            {
                // 用 4 字节长度前缀帧解析。
                MessageFrame = new LengthPrefixFrame();
            }

            protected override int EstimatePayloadLength() => 4; // 假设 payload 4 字节

            public OperateResult<byte[]> Echo(byte[] payload)
            {
                byte[] request = new byte[4 + payload.Length];
                request[3] = (byte)payload.Length;
                Array.Copy(payload, 0, request, 4, payload.Length);
                var r = ReadFromCoreServer(request);
                return r;
            }
        }

        private sealed class LengthPrefixFrame : NetMessageBase
        {
            public override int ProtocolHeadBytesLength => 4;
            public override int GetContentLength(byte[] head)
                => head == null || head.Length < 4 ? 0 : (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];
        }

        [Fact]
        public async Task DeviceTcpNet_ConnectsAndExchanges()
        {
            using (var server = new EchoDeviceServer())
            {
                Assert.True(server.ServerStart(0).IsSuccess);
                using (var client = new EchoTcpClient("127.0.0.1", server.Port))
                {
                    Assert.False(client.IsConnected);
                    byte[] payload = { 0xAA, 0xBB, 0xCC, 0xDD };
                    var r = client.Echo(payload);
                    Assert.True(r.IsSuccess, r.Message);
                    // 响应 = 4 字节长度 + 4 字节 payload。
                    Assert.Equal(payload, new ArraySegment<byte>(r.Content, 4, 4).ToArray());
                    Assert.True(client.IsConnected);

                    // 持久模式切换。
                    client.SetPersistentConnection();
                    Assert.True(client.IsPersistent);
                }
                server.ServerClose();
            }
        }

        [Fact]
        public void DeviceTcpNet_StoresIpAndPort()
        {
            using (var c = new EchoTcpClient("192.168.1.1", 502))
            {
                Assert.Equal("192.168.1.1", c.IpAddress);
                Assert.Equal(502, c.Port);
                Assert.Equal(2000, c.Timeout);
            }
        }

        // ── DeviceSerialPort 便利基类 ───────────────

        private sealed class FakeSerial : ISerialPort
        {
            private readonly System.Collections.Generic.Queue<byte[]> _responses = new System.Collections.Generic.Queue<byte[]>();
            private byte[]? _current;
            private int _offset;
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

            public void EnqueueResponse(byte[] resp) => _responses.Enqueue(resp);
            public void Open() { IsOpen = true; }
            public void Close() { IsOpen = false; }

            public void Write(byte[] buffer, int offset, int count)
            {
                LastWritten = new byte[count];
                Array.Copy(buffer, offset, LastWritten, 0, count);
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                if (_current == null || _offset >= _current.Length)
                {
                    if (_responses.Count == 0) return 0;
                    _current = _responses.Dequeue();
                    _offset = 0;
                }
                int n = Math.Min(count, _current.Length - _offset);
                Array.Copy(_current, _offset, buffer, offset, n);
                _offset += n;
                return n;
            }

            public void Dispose() => Close();
        }

        private sealed class EchoSerialClient : DeviceSerialPort
        {
            public EchoSerialClient(ISerialPort port, int timeout = 1000, int interFrameDelay = 0)
                : base(port, timeout, interFrameDelay) { }

            // 用预定义响应长度(无 MessageFrame 模式)。
            public OperateResult<byte[]> SendAndExpect(byte[] req, int respLen)
            {
                // 临时设 MessageFrame 为 null,让 DeviceCommunication 走 GetResponseLength 路径。
                // 但 GetResponseLength 默认 1024,我们临时改用 Pipe 直接调用以精确控制长度。
                // 简化:直接走 Pipe.SendAndReceive。
                return Pipe.SendAndReceive(req, respLen);
            }
        }

        [Fact]
        public void DeviceSerialPort_RoundTrip()
        {
            var port = new FakeSerial();
            port.EnqueueResponse(new byte[] { 1, 2, 3, 4 });
            using (var client = new EchoSerialClient(port))
            {
                Assert.False(client.IsConnected);
                Assert.True(client.Connect().IsSuccess);
                Assert.True(client.IsConnected);

                var r = client.SendAndExpect(new byte[] { 0xAA }, 4);
                Assert.True(r.IsSuccess, r.Message);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, r.Content);
                Assert.Equal(new byte[] { 0xAA }, port.LastWritten);

                // 暴露的 SerialPort 引用应等于 port。
                Assert.Same(port, client.SerialPort);
            }
        }

        // ── DeviceUdpNet 便利基类 ───────────────────

        private sealed class EmptyUdpClient : DeviceUdpNet
        {
            public EmptyUdpClient(string ip, int port) : base(ip, port) { }
        }

        [Fact]
        public void DeviceUdpNet_Construct_StoresConfig()
        {
            using (var c = new EmptyUdpClient("127.0.0.1", 9999))
            {
                Assert.Equal("127.0.0.1", c.IpAddress);
                Assert.Equal(9999, c.Port);
                Assert.Equal(5000, c.Timeout);
            }
        }

        [Fact]
        public void DeviceUdpNet_Connect_Succeeds()
        {
            using (var c = new EmptyUdpClient("127.0.0.1", 9999))
            {
                Assert.True(c.Connect().IsSuccess);
                Assert.True(c.IsConnected);
            }
        }
    }
}
