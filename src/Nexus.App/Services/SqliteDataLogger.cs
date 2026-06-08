using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.App.Services
{
    /// <summary>
    /// SQLite 数据记录器 — 将采集数据持久化到本地 SQLite 数据库。
    /// <para>对标 HSL DataLogManager，无需 EF Core，直接操作 ADO.NET。</para>
    /// </summary>
    public sealed class SqliteDataLogger : IDisposable
    {
        private readonly string _dbPath;
        private readonly ConcurrentQueue<LogEntry> _queue = new();
        private readonly CancellationTokenSource _cts = new();
        private Task? _flushTask;
        private bool _started;
        private int _totalLogged;

        public string DatabasePath => _dbPath;
        public int PendingCount => _queue.Count;
        public int TotalLogged => _totalLogged;
        public bool IsStarted => _started;

        public event EventHandler<int>? OnFlushed;

        public SqliteDataLogger()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nexus", "DataLog");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            _dbPath = Path.Combine(dir, $"datalog_{DateTime.Now:yyyyMMdd}.db");
        }

        /// <summary>启动后台写入线程</summary>
        public void Start()
        {
            if (_started) return;
            _started = true;
            EnsureTable();
            _flushTask = Task.Run(() => FlushLoop(_cts.Token));
        }

        /// <summary>停止并刷完队列</summary>
        public void Stop()
        {
            _started = false;
            _cts.Cancel();
            FlushBatch(); // drain remaining
        }

        /// <summary>记录一条数据</summary>
        public void Log(string protocol, string address, string dataType, string value, string quality = "Good")
        {
            _queue.Enqueue(new LogEntry
            {
                Timestamp = DateTime.Now,
                Protocol = protocol,
                Address = address,
                DataType = dataType,
                Value = value,
                Quality = quality
            });
        }

        /// <summary>查询历史数据</summary>
        public List<LogEntry> Query(DateTime from, DateTime to, string? protocol = null, string? address = null, int limit = 1000)
        {
            var results = new List<LogEntry>();
            try
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
                conn.Open();

                string sql = @"SELECT Timestamp, Protocol, Address, DataType, Value, Quality
                               FROM DataLog
                               WHERE Timestamp >= @from AND Timestamp <= @to";

                if (!string.IsNullOrEmpty(protocol))
                    sql += " AND Protocol = @protocol";
                if (!string.IsNullOrEmpty(address))
                    sql += " AND Address = @address";
                sql += $" ORDER BY Timestamp DESC LIMIT {limit}";

                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@from", from.ToString("O"));
                cmd.Parameters.AddWithValue("@to", to.ToString("O"));
                if (!string.IsNullOrEmpty(protocol))
                    cmd.Parameters.AddWithValue("@protocol", protocol);
                if (!string.IsNullOrEmpty(address))
                    cmd.Parameters.AddWithValue("@address", address);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new LogEntry
                    {
                        Timestamp = reader.GetDateTime(0),
                        Protocol = reader.GetString(1),
                        Address = reader.GetString(2),
                        DataType = reader.GetString(3),
                        Value = reader.GetString(4),
                        Quality = reader.IsDBNull(5) ? "" : reader.GetString(5)
                    });
                }
            }
            catch { }
            return results;
        }

        /// <summary>导出为 CSV</summary>
        public string ExportCsv(DateTime from, DateTime to, string? protocol = null)
        {
            var entries = Query(from, to, protocol);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Timestamp,Protocol,Address,DataType,Value,Quality");
            foreach (var e in entries)
                sb.AppendLine($"{e.Timestamp:O},{e.Protocol},{e.Address},{e.DataType},{e.Value},{e.Quality}");
            return sb.ToString();
        }

        /// <summary>导出为 JSON</summary>
        public string ExportJson(DateTime from, DateTime to, string? protocol = null)
        {
            var entries = Query(from, to, protocol);
            return JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        }

        private void EnsureTable()
        {
            try
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS DataLog (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Timestamp TEXT NOT NULL,
                        Protocol TEXT NOT NULL,
                        Address TEXT NOT NULL,
                        DataType TEXT NOT NULL,
                        Value TEXT NOT NULL,
                        Quality TEXT DEFAULT 'Good'
                    );
                    CREATE INDEX IF NOT EXISTS IX_DataLog_Timestamp ON DataLog(Timestamp);
                    CREATE INDEX IF NOT EXISTS IX_DataLog_Protocol ON DataLog(Protocol);
                    CREATE INDEX IF NOT EXISTS IX_DataLog_Address ON DataLog(Address);
                ";
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        private async Task FlushLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { FlushBatch(); }
                catch { }
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }

        private void FlushBatch()
        {
            const int batchSize = 100;
            int count = 0;
            try
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
                conn.Open();
                using var tx = conn.BeginTransaction();

                while (_queue.TryDequeue(out var entry) && count < batchSize)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"INSERT INTO DataLog (Timestamp, Protocol, Address, DataType, Value, Quality)
                                        VALUES (@ts, @proto, @addr, @dt, @val, @q)";
                    cmd.Parameters.AddWithValue("@ts", entry.Timestamp.ToString("O"));
                    cmd.Parameters.AddWithValue("@proto", entry.Protocol);
                    cmd.Parameters.AddWithValue("@addr", entry.Address);
                    cmd.Parameters.AddWithValue("@dt", entry.DataType);
                    cmd.Parameters.AddWithValue("@val", entry.Value);
                    cmd.Parameters.AddWithValue("@q", entry.Quality ?? "Good");
                    cmd.ExecuteNonQuery();
                    count++;
                }

                tx.Commit();
                if (count > 0)
                {
                    Interlocked.Add(ref _totalLogged, count);
                    OnFlushed?.Invoke(this, count);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    public sealed class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Protocol { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Quality { get; set; } = "Good";
    }
}
