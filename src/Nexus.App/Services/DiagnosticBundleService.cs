using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Nexus.App.Services
{
    /// <summary>
    /// 诊断包导出服务 — 将应用日志、TX/RX 报文记录、连接配置打包为 ZIP 文件。
    /// 用于现场故障排查后的离线分析和远程支持。
    /// </summary>
    public sealed class DiagnosticBundleService
    {
        private readonly PacketRecorderService _packetRecorder;

        public DiagnosticBundleService(PacketRecorderService packetRecorder)
        {
            _packetRecorder = packetRecorder ?? throw new ArgumentNullException(nameof(packetRecorder));
        }

        /// <summary>
        /// 导出诊断包到指定路径。
        /// 包含: packet_log.jsonl, connection_settings.json, app_info.json, session_log.txt。
        /// </summary>
        /// <param name="filePath">目标 ZIP 文件路径。</param>
        /// <param name="connectionInfo">当前连接配置信息。</param>
        /// <param name="sessionLog">会话日志文本（从 ProtocolViewModelBase 的 LogLines 收集）。</param>
        /// <returns>操作结果。</returns>
        public OperateResult ExportBundle(string filePath, ConnectionInfo? connectionInfo = null, string? sessionLog = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return OperateResult.Failed("文件路径不能为空");

            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

                // 1. Packet log (JSONL) — export to temp file then read back
                string tempJsonl = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jsonl");
                try
                {
                    _packetRecorder.ExportJsonl(tempJsonl);
                    AddEntryFromFile(zip, "packet_log.jsonl", tempJsonl);
                }
                finally
                {
                    try { File.Delete(tempJsonl); } catch { }
                }

                // 2. Connection settings
                if (connectionInfo != null)
                {
                    string json = JsonSerializer.Serialize(new
                    {
                        connectionInfo.Protocol,
                        connectionInfo.Host,
                        connectionInfo.Port,
                        connectionInfo.Station,
                        connectionInfo.Timeout,
                        connectionInfo.ByteOrder,
                        connectionInfo.Extra
                    }, new JsonSerializerOptions { WriteIndented = true });
                    AddEntry(zip, "connection_settings.json", json);
                }

                // 3. App info
                string appInfo = JsonSerializer.Serialize(new
                {
                    AppName = "Nexus",
                    Version = typeof(DiagnosticBundleService).Assembly.GetName().Version?.ToString() ?? "unknown",
                    ExportTime = DateTime.Now.ToString("O"),
                    DotNetVersion = Environment.Version.ToString(),
                    OS = Environment.OSVersion.ToString(),
                    MachineName = Environment.MachineName,
                    PacketCount = _packetRecorder.Count
                }, new JsonSerializerOptions { WriteIndented = true });
                AddEntry(zip, "app_info.json", appInfo);

                // 4. Session log (human-readable)
                if (!string.IsNullOrWhiteSpace(sessionLog))
                    AddEntry(zip, "session_log.txt", sessionLog);

                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"导出诊断包失败: {ex.Message}");
            }
        }

        private static void AddEntry(ZipArchive zip, string entryName, string content)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void AddEntryFromFile(ZipArchive zip, string entryName, string filePath)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var fs = File.OpenRead(filePath);
            fs.CopyTo(stream);
        }
    }

    /// <summary>
    /// 连接配置信息 — 用于诊断包导出。
    /// </summary>
    public sealed class ConnectionInfo
    {
        public string Protocol { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public byte Station { get; set; }
        public int Timeout { get; set; }
        public string ByteOrder { get; set; } = "";
        public Dictionary<string, string> Extra { get; set; } = new();
    }
}
