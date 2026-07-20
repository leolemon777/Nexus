using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Nexus.Sick;
using Xunit;

namespace Nexus.Sick.Tests
{
    public class SickIcrBarcodeServerTests
    {
        // ── CleanBarcode 纯函数 ─────────────

        [Theory]
        [InlineData("ABC123", "ABC123")]
        [InlineData("ABC123\r\n", "ABC123")]             // CR/LF 去除
        [InlineData("", "")]
        [InlineData("Hello World 123", "Hello World 123")]
        public void CleanBarcode_RemovesControlChars(string input, string expected)
        {
            Assert.Equal(expected, SickIcrBarcodeServer.CleanBarcode(input));
        }

        [Fact]
        public void CleanBarcode_RemovesStxEtx()
        {
            // STX(0x02) + "ABC123" + ETX(0x03) — 用 char 构造避免 InlineData 的 \x 转义问题。
            string input = ((char)2).ToString() + "ABC123" + ((char)3).ToString();
            Assert.Equal("ABC123", SickIcrBarcodeServer.CleanBarcode(input));
        }

        [Fact]
        public void CleanBarcode_AllControlChars_ReturnsEmpty()
        {
            string input = new string(new char[] { (char)0, (char)1, (char)2, (char)3 });
            Assert.Equal("", SickIcrBarcodeServer.CleanBarcode(input));
        }

        // ── 真实 TCP 集成 ───────────────────

        [Fact]
        public async Task ServerStart_AcceptsBarcodePush()
        {
            string? receivedIp = null;
            string? receivedCode = null;

            using (var server = new SickIcrBarcodeServer())
            {
                server.OnReceivedBarCode += (ip, code) =>
                {
                    receivedIp = ip;
                    receivedCode = code;
                };

                Assert.True(server.ServerStart(0).IsSuccess);

                // 用 TcpClient 模拟扫码器推送。
                using (var client = new TcpClient("127.0.0.1", server.Port))
                using (var ns = client.GetStream())
                {
                    // 手工构造字节数组,避免 \x 转义问题。
                    byte[] data = new byte[] { 0x02, (byte)'A', (byte)'B', (byte)'C', (byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', 0x03, 0x0D, 0x0A };
                    await ns.WriteAsync(data, 0, data.Length);

                    // 等待服务器处理。
                    for (int i = 0; i < 30 && receivedCode == null; i++)
                        await Task.Delay(50);
                }

                Assert.NotNull(receivedCode);
                Assert.Equal("ABC12345", receivedCode);
                Assert.Contains("127.0.0.1", receivedIp);

                server.ServerClose();
            }
        }

        [Fact]
        public void ServerStart_Twice_ReturnsFailed()
        {
            using (var server = new SickIcrBarcodeServer())
            {
                Assert.True(server.ServerStart(0).IsSuccess);
                Assert.False(server.ServerStart(0).IsSuccess);
                server.ServerClose();
            }
        }

        [Fact]
        public void Dispose_IsIdempotent()
        {
            var server = new SickIcrBarcodeServer();
            server.Dispose();
            server.Dispose();
        }

        [Fact]
        public void CleanNonPrintable_DefaultTrue()
        {
            var server = new SickIcrBarcodeServer();
            Assert.True(server.CleanNonPrintable);
        }
    }
}
