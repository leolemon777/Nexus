using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Nexus.App.Services
{
    /// <summary>
    /// <see cref="IWriteAuditSink"/> 的默认实现 — 将每条记录以单行 JSON 追加到
    /// <c>%APPDATA%/Nexus/write-audit-yyyyMMdd.log</c>（按天滚动）。
    /// <para>线程安全（<see cref="object"/> 锁内串行写入）；任何 IO 异常被吞掉并写入
    /// <c>crash.log</c>，<b>绝不</b>冒泡到调用方（审计不得阻塞用户写入流程）。</para>
    /// <para>JSON 序列化手写极简实现，避免引入 <c>System.Text.Json</c> 之外的依赖与
    /// 转义歧义；字符串字段经 <see cref="EscapeJson"/> 转义。</para>
    /// </summary>
    public sealed class WriteAuditSink : IWriteAuditSink
    {
        private static readonly string AuditDirectory =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Nexus");

        private readonly object _lock = new object();

        /// <inheritdoc />
        public void Append(WriteAuditRecord record)
        {
            if (record == null) return;

            string line;
            try
            {
                line = Serialize(record);
            }
            catch (Exception ex)
            {
                WriteCrashSafely("WriteAuditSink.Serialize", ex);
                return;
            }

            try
            {
                string path = ResolvePath();
                lock (_lock)
                {
                    Directory.CreateDirectory(AuditDirectory);
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                // 审计落盘失败不得中断主流程；记录到 crash.log 便于事后排查。
                WriteCrashSafely("WriteAuditSink.Append", ex);
            }
        }

        /// <summary>按天滚动文件名：<c>write-audit-20260619.log</c>。</summary>
        private static string ResolvePath()
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            return Path.Combine(AuditDirectory, "write-audit-" + stamp + ".log");
        }

        /// <summary>极简单行 JSON 序列化（控制字段顺序，便于审计解析）。</summary>
        private static string Serialize(WriteAuditRecord r)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"timestamp\":\"").Append(EscapeJson(r.Timestamp)).Append("\",");
            sb.Append("\"protocol\":\"").Append(EscapeJson(r.Protocol)).Append("\",");
            sb.Append("\"address\":\"").Append(EscapeJson(r.Address)).Append("\",");
            sb.Append("\"dataType\":\"").Append(EscapeJson(r.DataType)).Append("\",");
            sb.Append("\"value\":\"").Append(EscapeJson(r.Value)).Append("\",");
            sb.Append("\"outcome\":\"").Append(EscapeJson(r.Outcome)).Append("\"");
            if (!string.IsNullOrEmpty(r.FailureMessage))
            {
                sb.Append(",\"failureMessage\":\"").Append(EscapeJson(r.FailureMessage)).Append("\"");
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>JSON 字符串转义 — 仅覆盖审计场景需要的最小集合。</summary>
        private static string EscapeJson(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>把审计落盘失败写入 crash.log（应用所在目录）。</summary>
        private static void WriteCrashSafely(string source, Exception ex)
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "crash.log");
                string entry = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}] [{source}]\n{ex.GetType().FullName}: {ex.Message}\n{new string('-', 60)}\n";
                File.AppendAllText(path, entry);
            }
            catch
            {
                // 连 crash.log 都写不进去（磁盘满/无权限）— 彻底放弃。
            }
        }
    }
}
