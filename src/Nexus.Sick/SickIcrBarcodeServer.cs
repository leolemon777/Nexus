// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.
//
// Sick ICR barcode scanner server — listens for barcode pushes from Sick ICR
// scanners (also works with Hikvision/Keyence/Datalogic scanners configured
// to push barcodes via TCP). Adapted from HSL's SickIcrTcpServer.

using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Device;

namespace Nexus.Sick
{
    /// <summary>
    /// Sick ICR 条码扫描仪服务器 — 监听来自 Sick ICR / 海康 / 基恩士 / Datalogic 扫码器的
    /// TCP 条码推送。扫码器需配置为"主动推送"模式,将读到的条码发送到本服务器端口。
    /// </summary>
    /// <remarks>
    /// 使用方法:
    /// <code>
    /// var server = new SickIcrBarcodeServer();
    /// server.OnReceivedBarCode += (ip, code) => Console.WriteLine($"[{ip}] {code}");
    /// server.ServerStart(8000);
    /// </code>
    /// </remarks>
    public class SickIcrBarcodeServer : DeviceServer
    {
        /// <summary>条码接收事件。</summary>
        public event Action<string, string>? OnReceivedBarCode;

        /// <summary>是否自动清理条码数据中的非打印字符。</summary>
        public bool CleanNonPrintable { get; set; } = true;

        protected override async Task HandleClientAsync(TcpClient client, string clientId, CancellationToken cancellationToken)
        {
            using (var ns = client.GetStream())
            {
                byte[] buffer = new byte[4096];
                while (!cancellationToken.IsCancellationRequested)
                {
                    int n;
                    try
                    {
                        n = await ns.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    }
                    catch { break; }
                    if (n == 0) break;

                    string raw = Encoding.ASCII.GetString(buffer, 0, n);
                    string code = CleanNonPrintable ? CleanBarcode(raw) : raw;

                    if (!string.IsNullOrEmpty(code))
                    {
                        OnReceivedBarCode?.Invoke(clientId, code);
                    }
                }
            }
        }

        /// <summary>清理条码字符串:去掉 STX/ETX/CR/LF 等控制字符。</summary>
        public static string CleanBarcode(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                // 只保留可打印 ASCII(0x20-0x7E)。
                if (c >= 0x20 && c <= 0x7E)
                    sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
