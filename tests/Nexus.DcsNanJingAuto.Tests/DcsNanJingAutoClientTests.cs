using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus;
using Nexus.DcsNanJingAuto;
using Nexus.Modbus;
using Xunit;

namespace Nexus.DcsNanJingAuto.Tests
{
    /// <summary>
    /// Phase D-2 集成测试 — 用真实 TCP 服务器模拟南京 DCS:
    /// 1. 接受连接 → 收到 12 字节状态检查命令 → 回 6 字节成功响应(后 4 字节为 0)。
    /// 2. 之后的读写按标准 Modbus TCP 处理。
    /// </summary>
    public class DcsNanJingAutoClientTests
    {
        /// <summary>模拟南京 DCS 服务器。</summary>
        private sealed class FakeDcsServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly Task _task;
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly byte[] _registerValue;

            public int Port { get; }
            public int HandshakeCount => _handshakeField;
            public int ModbusReadCount => _modbusField;
            private int _handshakeField;
            private int _modbusField;

            /// <param name="registerValue">用于 Modbus FC03 响应的寄存器值(2 字节)。</param>
            public FakeDcsServer(byte[] registerValue)
            {
                _registerValue = registerValue;
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
                            byte[] buf = new byte[256];
                            int n;
                            try { n = await ns.ReadAsync(buf, 0, buf.Length, _cts.Token).ConfigureAwait(false); }
                            catch { break; }
                            if (n == 0) break;

                            // 南京 DCS 握手命令 = 标准 Modbus FC03 读 40001。
                            // 本 fake server 统一以标准 Modbus TCP 响应所有请求。
                            // 用 station + FC + 地址字段判断握手(buf[6]=station, buf[7]=FC03,
                            // buf[8..11]=地址+数量)。
                            if (n >= 12)
                            {
                                // 握手特征:FC03 + 读地址 0 + 数量 1(buf[10]=0,buf[11]=1)。
                                bool isHandshake = buf[7] == 0x03 && buf[8] == 0x00 && buf[9] == 0x00
                                    && buf[10] == 0x00 && buf[11] == 0x01;
                                if (isHandshake)
                                    Interlocked.Increment(ref _handshakeField);
                                else
                                    Interlocked.Increment(ref _modbusField);

                                // 标准 Modbus TCP 响应。
                                // MBAP: [txHi txLo protoHi protoLo lenHi lenLo unit] (header 7 字节)
                                // Payload: [fc byteCount dataHi dataLo] (4 字节)
                                // length 字段(从 unit 开始算)= 1(unit) + 1(fc) + 1(byteCount) + 2(data) = 5。
                                // Nexus ModbusTcpClient.GetResponsePayloadLength = length - 1 = 4(去掉已读 unit 字节)。
                                byte txHi = buf[0], txLo = buf[1];
                                byte unit = buf[6];
                                byte fc = buf[7];
                                byte[] resp = new byte[11];
                                resp[0] = txHi; resp[1] = txLo;
                                resp[2] = 0; resp[3] = 0;
                                resp[4] = 0; resp[5] = 5;   // length = 5(unit+fc+byteCount+2 data)
                                resp[6] = unit; resp[7] = fc; resp[8] = 2;
                                resp[9] = _registerValue[0];
                                resp[10] = _registerValue[1];
                                await ns.WriteAsync(resp, 0, resp.Length, _cts.Token).ConfigureAwait(false);
                            }
                        }
                    }
                    catch { }
                }
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
        public async Task Connect_PerformsHandshake_Success()
        {
            using (var server = new FakeDcsServer(new byte[] { 0x12, 0x34 }))
            using (var client = new DcsNanJingAutoClient("127.0.0.1", server.Port, station: 1, timeout: 2000))
            {
                var connect = client.Connect();
                Assert.True(connect.IsSuccess, connect.Message);
                Assert.True(client.IsConnected);

                // 服务器应收到握手。
                await Task.Delay(100); // 给服务器处理时间
                Assert.Equal(1, server.HandshakeCount);
            }
        }

        [Fact]
        public async Task ReadUInt16_AfterHandshake_Works()
        {
            using (var server = new FakeDcsServer(new byte[] { 0xAB, 0xCD }))
            using (var client = new DcsNanJingAutoClient("127.0.0.1", server.Port, station: 1, timeout: 2000))
            {
                Assert.True(client.Connect().IsSuccess);

                // 读寄存器 40001。FakeDcsServer 会回 0xABCD。
                var r = client.ReadUInt16("40001");
                Assert.True(r.IsSuccess, r.Message);
                Assert.Equal((ushort)0xABCD, r.Content);

                await Task.Delay(100);
                // Connect 读 1 次 + ReadUInt16 读 1 次 = 至少 2 次 40001 请求。
                Assert.True(server.HandshakeCount >= 2,
                    $"HandshakeCount={server.HandshakeCount}, 期望 >= 2");
            }
        }

        [Fact]
        public void InheritsModbusTcpClient()
        {
            using (var client = new DcsNanJingAutoClient("127.0.0.1", 502, station: 2, timeout: 3000))
            {
                ModbusTcpClient modbus = client;
                Assert.Equal((byte)2, modbus.Station);
            }
        }

        [Fact]
        public void Constructor_StoresConfig()
        {
            using (var client = new DcsNanJingAutoClient("192.168.1.10", port: 5020, station: 5, timeout: 4000))
            {
                Assert.Equal((byte)5, client.Station);
                Assert.True(client.FilterStatusFrame);
            }
        }

        [Fact]
        public async Task Connect_ToUnreachableServer_ReturnsFailed()
        {
            using (var client = new DcsNanJingAutoClient("127.0.0.1", 1, station: 1, timeout: 500))
            {
                var r = client.Connect();
                Assert.False(r.IsSuccess);
            }
        }

        [Fact]
        public void ToString_IncludesConnectionInfo()
        {
            using (var client = new DcsNanJingAutoClient("192.168.1.10", port: 5020, station: 3))
            {
                string s = client.ToString();
                Assert.Contains("192.168.1.10", s);
                Assert.Contains("5020", s);
                Assert.Contains("3", s);
                Assert.Contains("Dcs", s);
            }
        }

        /// <summary>
        /// 模拟握手失败(后 4 字节非 0),验证 Connect 返回 Failed。
        /// </summary>
        private sealed class FailingHandshakeServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly Task _task;
            public int Port { get; }

            public FailingHandshakeServer()
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
                    _ = Task.Run(async () =>
                    {
                        using (c)
                        using (var ns = c.GetStream())
                        {
                            byte[] buf = new byte[256];
                            try
                            {
                                int n = await ns.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                                if (n == 0) return;
                                // 回 6 字节错误响应(后 4 字节非 0)。
                                byte[] errResp = new byte[] { 0x00, 0x01, 0xFF, 0xFF, 0xFF, 0xFF };
                                await ns.WriteAsync(errResp, 0, 6).ConfigureAwait(false);
                            }
                            catch { }
                        }
                    });
                }
            }

            public void Dispose()
            {
                _cts.Cancel();
                try { _listener.Stop(); } catch { }
                try { _task.Wait(500); } catch { }
                _cts.Dispose();
            }
        }

        [Fact]
        public async Task Connect_HandshakeFailed_ReturnsFailed()
        {
            using (var server = new FailingHandshakeServer())
            using (var client = new DcsNanJingAutoClient("127.0.0.1", server.Port, station: 1, timeout: 2000))
            {
                var r = client.Connect();
                Assert.False(r.IsSuccess);
                Assert.Contains("握手", r.Message);
                // 失败后应断开。
                Assert.False(client.IsConnected);
            }
        }
    }
}
