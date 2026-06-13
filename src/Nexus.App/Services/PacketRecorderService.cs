using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Modbus;

namespace Nexus.App.Services
{
    /// <summary>
    /// 共享的 Modbus 报文记录与导出服务。
    /// 提供报文解析、结构化存储、可读摘要、JSONL 导出。
    /// 可被所有 Modbus 传输 ViewModel 共用（TCP/UDP/RTU/ASCII/RTU-over-TCP）。
    /// </summary>
    public sealed class PacketRecorderService
    {
        private readonly List<PacketRecord> _records = new();
        private readonly object _lock = new();
        private const int DefaultCap = 2000;

        /// <summary>最大记录数量。</summary>
        public int Capacity { get; set; } = DefaultCap;

        /// <summary>当前记录数量。</summary>
        public int Count
        {
            get { lock (_lock) return _records.Count; }
        }

        // ── 记录报文 ────────────────────────────

        /// <summary>
        /// 记录一条 Modbus TCP/UDP 报文（MBAP framing）。
        /// </summary>
        public PacketRecord RecordMbap(string protocol, string direction, string hex, ModbusPacketDirection packetDirection, Action<string>? logAction = null)
        {
            var record = CreateRecord(protocol, direction, hex, packetDirection, ParseTcpFrame);
            AddRecord(record);

            if (logAction != null)
            {
                string timestamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                logAction($"[{timestamp}] [{direction}] {hex}");
                logAction($"[{timestamp}] [PKT] {record.Summary}");
            }

            return record;
        }

        /// <summary>
        /// 记录一条 Modbus RTU 报文。
        /// </summary>
        public PacketRecord RecordRtu(string protocol, string direction, string hex, ModbusPacketDirection packetDirection, Action<string>? logAction = null)
        {
            var record = CreateRecord(protocol, direction, hex, packetDirection, ParseRtuFrame);
            AddRecord(record);

            if (logAction != null)
            {
                string timestamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                logAction($"[{timestamp}] [{direction}] {hex}");
                logAction($"[{timestamp}] [PKT] {record.Summary}");
            }

            return record;
        }

        /// <summary>
        /// 记录一条 Modbus ASCII 报文。
        /// </summary>
        public PacketRecord RecordAscii(string protocol, string direction, string hex, ModbusPacketDirection packetDirection, Action<string>? logAction = null)
        {
            var record = CreateRecord(protocol, direction, hex, packetDirection, ParseAsciiFrame);
            AddRecord(record);

            if (logAction != null)
            {
                string timestamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                logAction($"[{timestamp}] [{direction}] {hex}");
                logAction($"[{timestamp}] [PKT] {record.Summary}");
            }

            return record;
        }

        // ── 导出 ─────────────────────────────────

        /// <summary>导出所有记录为 JSONL 格式。</summary>
        public bool ExportJsonl(string filePath)
        {
            try
            {
                List<PacketRecord> snapshot;
                lock (_lock) { snapshot = new List<PacketRecord>(_records); }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = false,
                    Converters = { new JsonStringEnumConverter() }
                };

                using (var writer = new StreamWriter(filePath, false, System.Text.Encoding.UTF8))
                {
                    foreach (var record in snapshot)
                        writer.WriteLine(JsonSerializer.Serialize(record, options));
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>清空所有记录。</summary>
        public void Clear()
        {
            lock (_lock) { _records.Clear(); }
        }

        // ── 统计与分析 ────────────────────────────

        /// <summary>获取报文统计摘要。</summary>
        public PacketStats GetStats()
        {
            lock (_lock)
            {
                int total = _records.Count;
                int tx = 0, rx = 0, exceptions = 0, errors = 0;
                double minLatencyMs = double.MaxValue, maxLatencyMs = 0, totalLatencyMs = 0;
                int latencyCount = 0;
                var fcCounts = new Dictionary<byte, int>();

                for (int i = 0; i < total; i++)
                {
                    var r = _records[i];
                    if (r.Direction == "TX") tx++;
                    else rx++;

                    if (r.IsException) exceptions++;
                    if (!r.IsValid) errors++;

                    if (r.FunctionCode.HasValue)
                    {
                        byte fc = r.BaseFunctionCode ?? r.FunctionCode.Value;
                        if (!fcCounts.ContainsKey(fc))
                            fcCounts[fc] = 0;
                        fcCounts[fc]++;
                    }

                    // 计算 TX→RX 延迟（匹配 TransactionId）
                    if (r.Direction == "TX" && r.TransactionId.HasValue && i + 1 < total)
                    {
                        var next = _records[i + 1];
                        if (next.Direction == "RX" && next.TransactionId == r.TransactionId)
                        {
                            double latency = (next.Timestamp - r.Timestamp).TotalMilliseconds;
                            if (latency < minLatencyMs) minLatencyMs = latency;
                            if (latency > maxLatencyMs) maxLatencyMs = latency;
                            totalLatencyMs += latency;
                            latencyCount++;
                        }
                    }
                }

                return new PacketStats
                {
                    TotalPackets = total,
                    TxCount = tx,
                    RxCount = rx,
                    ExceptionCount = exceptions,
                    ErrorCount = errors,
                    MinLatencyMs = latencyCount > 0 ? minLatencyMs : 0,
                    MaxLatencyMs = latencyCount > 0 ? maxLatencyMs : 0,
                    AvgLatencyMs = latencyCount > 0 ? totalLatencyMs / latencyCount : 0,
                    FunctionCodeCounts = fcCounts
                };
            }
        }

        /// <summary>获取异常报文列表。</summary>
        public List<PacketRecord> GetExceptions()
        {
            lock (_lock)
            {
                var result = new List<PacketRecord>();
                foreach (var r in _records)
                {
                    if (r.IsException || !r.IsValid)
                        result.Add(r);
                }
                return result;
            }
        }

        // ── 内部实现 ──────────────────────────────

        private delegate ModbusPacketInfo ParseFrameDelegate(byte[] frame, ModbusPacketDirection direction);

        private PacketRecord CreateRecord(string protocol, string direction, string hex, ModbusPacketDirection packetDirection, ParseFrameDelegate parser)
        {
            var record = new PacketRecord
            {
                Timestamp = DateTimeOffset.Now,
                Protocol = protocol,
                Direction = direction,
                Hex = hex ?? string.Empty
            };

            if (!TryParseHexBytes(hex, out var frame, out var hexError))
            {
                record.IsValid = false;
                record.Error = hexError;
                record.Summary = direction + " invalid hex: " + hexError;
                return record;
            }

            var packet = parser(frame, packetDirection);
            record.IsValid = packet.IsValid;
            record.TransactionId = packet.TransactionId;
            record.UnitId = packet.UnitId;
            record.FunctionCode = packet.FunctionCode;
            record.BaseFunctionCode = packet.BaseFunctionCode;
            record.IsException = packet.IsException;
            record.ExceptionCode = packet.ExceptionCode;
            record.Address = packet.Address;
            record.Quantity = packet.Quantity;
            record.AndMask = packet.AndMask;
            record.OrMask = packet.OrMask;
            record.ByteCount = packet.ByteCount;
            record.MeiType = packet.MeiType;
            record.SubFunction = packet.SubFunction;
            record.ReadDeviceIdLevel = packet.ReadDeviceIdLevel;
            record.ConformityLevel = packet.ConformityLevel;
            record.ObjectCount = packet.ObjectCount;
            record.DataHex = packet.Data.Length == 0 ? string.Empty : BitConverter.ToString(packet.Data).Replace("-", " ");
            record.Error = packet.Error ?? string.Empty;
            record.Summary = BuildPacketSummary(packet, direction);
            return record;
        }

        private static ModbusPacketInfo ParseTcpFrame(byte[] frame, ModbusPacketDirection dir) => ModbusPacketParser.ParseTcp(frame, dir);
        private static ModbusPacketInfo ParseRtuFrame(byte[] frame, ModbusPacketDirection dir) => ModbusPacketParser.ParseRtu(frame, dir);
        private static ModbusPacketInfo ParseAsciiFrame(byte[] frame, ModbusPacketDirection dir) => ModbusPacketParser.ParseAscii(frame, dir);

        private void AddRecord(PacketRecord record)
        {
            lock (_lock)
            {
                _records.Add(record);
                if (_records.Count > Capacity)
                {
                    int remove = _records.Count - Capacity;
                    _records.RemoveRange(0, remove);
                }
            }
        }

        // ── 摘要构建 ──────────────────────────────

        private static string BuildPacketSummary(ModbusPacketInfo packet, string direction)
        {
            var parts = new List<string>
            {
                direction,
                "TID=" + Format(packet.TransactionId),
                "Unit=" + Format(packet.UnitId),
                "FC=" + FormatHex(packet.FunctionCode)
            };

            if (packet.IsException)
            {
                parts.Add("Exception=" + FormatHex(packet.ExceptionCode));
            }
            else
            {
                // FC08 Diagnostics
                if (packet.SubFunction.HasValue)
                    parts.Add("Sub=" + packet.SubFunction.Value.ToString("X4", CultureInfo.InvariantCulture));

                // FC43 Read Device ID
                if (packet.ReadDeviceIdLevel.HasValue)
                    parts.Add("Level=" + packet.ReadDeviceIdLevel.Value.ToString(CultureInfo.InvariantCulture));
                if (packet.ConformityLevel.HasValue)
                    parts.Add("Conf=" + packet.ConformityLevel.Value.ToString(CultureInfo.InvariantCulture));
                if (packet.ObjectCount.HasValue)
                    parts.Add("Objs=" + packet.ObjectCount.Value.ToString(CultureInfo.InvariantCulture));

                // Standard fields
                if (packet.Address.HasValue) parts.Add("Addr=" + packet.Address.Value.ToString(CultureInfo.InvariantCulture));
                if (packet.Quantity.HasValue) parts.Add("Qty=" + packet.Quantity.Value.ToString(CultureInfo.InvariantCulture));
                if (packet.AndMask.HasValue) parts.Add("And=0x" + packet.AndMask.Value.ToString("X4", CultureInfo.InvariantCulture));
                if (packet.OrMask.HasValue) parts.Add("Or=0x" + packet.OrMask.Value.ToString("X4", CultureInfo.InvariantCulture));
                if (packet.ByteCount.HasValue) parts.Add("Bytes=" + packet.ByteCount.Value.ToString(CultureInfo.InvariantCulture));
                if (packet.Data.Length > 0) parts.Add("Data=" + BitConverter.ToString(packet.Data).Replace("-", " "));
            }

            if (!packet.IsValid && !string.IsNullOrWhiteSpace(packet.Error))
                parts.Add("ERR=" + packet.Error);

            return string.Join(" ", parts);
        }

        private static string Format(ushort? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";
        private static string Format(byte? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";
        private static string FormatHex(byte? value) => value.HasValue ? "0x" + value.Value.ToString("X2", CultureInfo.InvariantCulture) : "-";

        // ── Hex 解析 ──────────────────────────────

        internal static bool TryParseHexBytes(string? hex, out byte[] frame, out string error)
        {
            frame = Array.Empty<byte>();
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(hex))
            {
                error = "empty frame";
                return false;
            }

            string compact = hex.Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace(":", string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty)
                .Replace("\t", string.Empty);

            if ((compact.Length % 2) != 0)
            {
                error = "odd number of hex characters";
                return false;
            }

            byte[] bytes = new byte[compact.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                string part = compact.Substring(i * 2, 2);
                if (!byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                {
                    error = "invalid hex byte '" + part + "'";
                    return false;
                }
            }

            frame = bytes;
            return true;
        }
    }

    /// <summary>Modbus 报文记录（用于 JSONL 导出）。</summary>
    public sealed class PacketRecord
    {
        public DateTimeOffset Timestamp { get; set; }
        public string Protocol { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public string Hex { get; set; } = string.Empty;
        public bool IsValid { get; set; }
        public ushort? TransactionId { get; set; }
        public byte? UnitId { get; set; }
        public byte? FunctionCode { get; set; }
        public byte? BaseFunctionCode { get; set; }
        public bool IsException { get; set; }
        public byte? ExceptionCode { get; set; }
        public ushort? Address { get; set; }
        public ushort? Quantity { get; set; }
        public ushort? AndMask { get; set; }
        public ushort? OrMask { get; set; }
        public byte? ByteCount { get; set; }
        public byte? MeiType { get; set; }
        public ushort? SubFunction { get; set; }
        public byte? ReadDeviceIdLevel { get; set; }
        public byte? ConformityLevel { get; set; }
        public byte? ObjectCount { get; set; }
        public string DataHex { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
    }

    /// <summary>报文统计摘要。</summary>
    public sealed class PacketStats
    {
        public int TotalPackets { get; set; }
        public int TxCount { get; set; }
        public int RxCount { get; set; }
        public int ExceptionCount { get; set; }
        public int ErrorCount { get; set; }
        public double MinLatencyMs { get; set; }
        public double MaxLatencyMs { get; set; }
        public double AvgLatencyMs { get; set; }
        public Dictionary<byte, int> FunctionCodeCounts { get; set; } = new();

        public override string ToString()
        {
            return $"Total={TotalPackets}, TX={TxCount}, RX={RxCount}, " +
                   $"Exceptions={ExceptionCount}, Errors={ErrorCount}, " +
                   $"Latency={AvgLatencyMs:F1}ms (min={MinLatencyMs:F1}, max={MaxLatencyMs:F1})";
        }
    }
}
