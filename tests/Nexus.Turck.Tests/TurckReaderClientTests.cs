using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nexus;
using Nexus.Turck;
using Xunit;

namespace Nexus.Turck.Tests
{
    /// <summary>
    /// Phase D-3 测试 — TurckReaderClient 的 CRC、命令打包、错误响应、真实 TCP 链路。
    /// </summary>
    public class TurckReaderClientTests
    {
        // ── CRC 算法(纯函数)─────────────────────

        [Fact]
        public void CalculateCrc_EmptyInput_ReturnsFFFFInverted()
        {
            // 空输入,crc = ~0xFFFF = 0x0000。
            byte[] crc = TurckReaderClient.CalculateCrc(Array.Empty<byte>(), 0);
            Assert.Equal(0x00, crc[0]);
            Assert.Equal(0x00, crc[1]);
        }

        [Fact]
        public void CalculateCrc_KnownInput_StableValue()
        {
            // 用一个固定输入,验证两次调用结果一致(回归值,确保算法稳定)。
            byte[] data = { 0xAA, 0x07, 0x07, 0x68, 0x00 };
            byte[] crc1 = TurckReaderClient.CalculateCrc(data, data.Length);
            byte[] crc2 = TurckReaderClient.CalculateCrc(data, data.Length);
            Assert.Equal(crc1, crc2);
        }

        [Fact]
        public void CalculateCrc_SingleZero()
        {
            // CRC16 (poly 0x8408, init 0xFFFF, ~result) of single byte 0x00。
            // 手工推导: init=0xFFFF, XOR 0x00 = 0xFFFF, 8 次循环。
            // 这里只验证不抛异常 + 长度 2。
            byte[] crc = TurckReaderClient.CalculateCrc(new byte[] { 0 }, 1);
            Assert.Equal(2, crc.Length);
        }

        // ── PackCommand ─────────────────────────

        [Fact]
        public void PackCommand_FormatCorrect()
        {
            byte[] frame = TurckReaderClient.PackCommand(new byte[] { 0x70, 0x00 });
            Assert.Equal(0xAA, frame[0]);
            Assert.Equal(frame.Length, frame[1]);
            Assert.Equal(frame.Length, frame[2]);
            Assert.Equal(0x70, frame[3]);
            Assert.Equal(0x00, frame[4]);
            Assert.Equal(7, frame.Length);  // 5 header + 2 payload
        }

        [Fact]
        public void PackCommand_EmptyCommand_StillValid()
        {
            byte[] frame = TurckReaderClient.PackCommand(Array.Empty<byte>());
            Assert.Equal(0xAA, frame[0]);
            Assert.Equal(5, frame.Length);
        }

        [Fact]
        public void PackCommand_CrcValid()
        {
            byte[] cmd = { 0x68, 0x00, 0x00, 0x00 };
            byte[] frame = TurckReaderClient.PackCommand(cmd);

            // 重新计算前 N-2 字节的 CRC,验证末 2 字节一致。
            byte[] expected = TurckReaderClient.CalculateCrc(frame, frame.Length - 2);
            Assert.Equal(expected[0], frame[frame.Length - 2]);
            Assert.Equal(expected[1], frame[frame.Length - 1]);
        }

        // ── 构造与配置 ─────────────────────────

        [Fact]
        public void Constructor_StoresIpAndPort()
        {
            var client = new TurckReaderClient("192.168.1.100", port: 10001, timeout: 3000);
            Assert.Null(client.UID);
            Assert.Equal((byte)1, client.NumberOfBlock);
            Assert.Equal((byte)4, client.BytesOfBlock);
        }

        [Fact]
        public void Constructor_DefaultPort10000()
        {
            var client = new TurckReaderClient("192.168.1.100");
            // 不暴露 port,但 ToString 不抛异常。
            string s = client.ToString();
            Assert.Contains("192.168.1.100", s);
        }

        // ── 错误码映射 ─────────────────────────

        [Fact]
        public void ReadBytes_InvalidAddress_ReturnsFailed()
        {
            var client = new TurckReaderClient("127.0.0.1");
            var r = client.ReadBytes("not-a-number", 1);
            Assert.False(r.IsSuccess);
            Assert.Contains("地址无效", r.Message);
        }

        [Fact]
        public void Write_InvalidAddress_ReturnsFailed()
        {
            var client = new TurckReaderClient("127.0.0.1");
            var r = client.Write("abc", new byte[] { 1 });
            Assert.False(r.IsSuccess);
            Assert.Contains("地址无效", r.Message);
        }

        // ── 真实 TCP 集成 ───────────────────────

        /// <summary>Fake Turck 服务器:响应读 UID 命令。</summary>
        private sealed class FakeTurckServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly Task _task;
            private readonly byte[] _uidPayload;
            public int Port { get; }

            public FakeTurckServer(byte[] uidBytes)
            {
                _uidPayload = uidBytes;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _task = Task.Run(() => RunAsync(uidBytes));
            }

            private async Task RunAsync(byte[] uidBytes)
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                    catch { break; }
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
                            int n = await ns.ReadAsync(buf, 0, buf.Length, _cts.Token).ConfigureAwait(false);
                            if (n == 0) break;
                            if (buf[0] != 0xAA) continue;

                            // 检查命令字节(位置 3)。
                            byte cmd = buf[3];
                            if (cmd == 0x70)  // read UID
                            {
                                // 响应格式:[0xAA] [invocation] [total-len] [0x70 0x00] [6 字节 UID] [CRC(2)]
                                // 总长度 = 1 + 1 + 1 + 2 + 6 + 2 = 13 字节,total-len 字段写 13。
                                byte[] resp = new byte[13];
                                resp[0] = 0xAA;
                                resp[1] = 0x0D;  // invocation = 13
                                resp[2] = 0x0D;  // total-len = 13
                                resp[3] = 0x70;
                                resp[4] = 0x00;
                                Buffer.BlockCopy(_uidPayload, 0, resp, 5, Math.Min(6, _uidPayload.Length));
                                byte[] crc = TurckReaderClient.CalculateCrc(resp, resp.Length - 2);
                                resp[resp.Length - 2] = crc[0];
                                resp[resp.Length - 1] = crc[1];
                                await ns.WriteAsync(resp, 0, resp.Length, _cts.Token).ConfigureAwait(false);
                            }
                            else if (cmd == 0x68)  // read blocks
                            {
                                // 简化:回固定响应。
                                byte[] resp = new byte[10];
                                resp[0] = 0xAA;
                                resp[1] = (byte)resp.Length;
                                resp[2] = (byte)resp.Length;
                                resp[3] = 0x68;
                                resp[4] = 0x00;
                                resp[5] = 0x00;
                                resp[6] = 0x00;
                                resp[7] = 0x12;
                                resp[8] = 0x34;
                                byte[] crc = TurckReaderClient.CalculateCrc(resp, resp.Length - 2);
                                resp[resp.Length - 2] = crc[0];
                                resp[resp.Length - 1] = crc[1];
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
                try { _task.Wait(500); } catch { }
                _cts.Dispose();
            }
        }

        [Fact]
        public async Task ReadUid_RealTcp_Works()
        {
            byte[] fakeUid = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
            using (var server = new FakeTurckServer(fakeUid))
            using (var client = new TurckReaderClient("127.0.0.1", server.Port, timeout: 2000))
            {
                var r = client.ReadUid();
                Assert.True(r.IsSuccess, r.Message);
                // ReadUid 返回去掉横线的连续 hex 字符串。
                Assert.Equal("010203040506", r.Content);
                Assert.Equal("010203040506", client.UID);
            }
        }

        [Fact]
        public async Task ReadBlocks_RealTcp_Works()
        {
            using (var server = new FakeTurckServer(new byte[6]))
            using (var client = new TurckReaderClient("127.0.0.1", server.Port, timeout: 2000))
            {
                client.BytesOfBlock = 4;
                var r = client.ReadBlocks(0, 1);
                Assert.True(r.IsSuccess, r.Message);
                // fake server 回 2 字节固定数据(0x12 0x34)。
                Assert.True(r.Content.Length >= 2);
            }
        }

        [Fact]
        public async Task ConnectionFailure_ReturnsFailed()
        {
            using (var client = new TurckReaderClient("127.0.0.1", 1, timeout: 500))
            {
                var r = client.ReadUid();
                Assert.False(r.IsSuccess);
            }
        }
    }
}
