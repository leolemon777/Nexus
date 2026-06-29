using System;
using System.Collections.Generic;

namespace Nexus.Bridge
{
    /// <summary>
    /// 桥接点配置 — 定义一个从源协议读取、推送到目标的映射。
    /// </summary>
    public class BridgePoint
    {
        /// <summary>采集地址（源协议格式）。</summary>
        public string Address { get; set; } = "";

        /// <summary>数据类型: Int16, UInt16, Int32, Float, Double, Bool, String。</summary>
        public string DataType { get; set; } = "Int16";

        /// <summary>发布到目标时使用的键/主题后缀。</summary>
        public string Tag { get; set; } = "";

        /// <summary>缩放系数（默认 1.0）。</summary>
        public double Scale { get; set; } = 1.0;

        /// <summary>偏移量（默认 0.0）。</summary>
        public double Offset { get; set; } = 0.0;
    }

    /// <summary>
    /// 桥接配置。
    /// </summary>
    public class BridgeConfig
    {
        /// <summary>源设备类型: ModbusTcp, S7, Mc3E 等。</summary>
        public string SourceType { get; set; } = "ModbusTcp";

        /// <summary>源设备 IP。</summary>
        public string SourceIp { get; set; } = "127.0.0.1";

        /// <summary>源设备端口（null 时使用协议默认端口）。</summary>
        public int? SourcePort { get; set; } = 502;

        /// <summary>源设备站号（Modbus 等串行协议使用）。</summary>
        public byte SourceStation { get; set; } = 1;

        /// <summary>Siemens PLC 型号（SourceType=SiemensS7 时使用）。</summary>
        public string SourcePlcModel { get; set; } = "";

        /// <summary>目标类型: Mqtt, Console, Csv。</summary>
        public string TargetType { get; set; } = "Mqtt";

        /// <summary>目标主机。</summary>
        public string TargetHost { get; set; } = "127.0.0.1";

        /// <summary>目标端口。</summary>
        public int TargetPort { get; set; } = 1883;

        /// <summary>MQTT 主题前缀。</summary>
        public string MqttTopicPrefix { get; set; } = "nexus/";

        /// <summary>MQTT Client ID。</summary>
        public string MqttClientId { get; set; } = "nexus-bridge";

        /// <summary>CSV 文件路径（TargetType=Csv 时使用）。</summary>
        public string CsvFilePath { get; set; } = "nexus_bridge.csv";

        /// <summary>CSV 是否追加模式。</summary>
        public bool CsvAppend { get; set; } = true;

        /// <summary>Redis 连接字符串（TargetType=Redis 时使用）。</summary>
        public string RedisConnectionString { get; set; } = "127.0.0.1:6379";

        /// <summary>Redis 键前缀。</summary>
        public string RedisKeyPrefix { get; set; } = "nexus:";

        /// <summary>InfluxDB URL（TargetType=InfluxDb 时使用）。</summary>
        public string InfluxDbUrl { get; set; } = "http://127.0.0.1:8086";

        /// <summary>InfluxDB 数据库名。</summary>
        public string InfluxDbDatabase { get; set; } = "nexus";

        /// <summary>轮询间隔（毫秒）。</summary>
        public int PollIntervalMs { get; set; } = 1000;

        /// <summary>桥接点列表。</summary>
        public List<BridgePoint> Points { get; set; } = new List<BridgePoint>();

        /// <summary>是否启用历史数据存储。</summary>
        public bool EnableHistory { get; set; }

        /// <summary>历史数据存储目录。</summary>
        public string HistoryDataDirectory { get; set; } = "data/history";

        /// <summary>历史数据压缩方式: None, Deadband, SwingDoor。</summary>
        public string HistoryCompression { get; set; } = "Deadband";

        /// <summary>历史数据死区阈值。</summary>
        public double HistoryDeadbandThreshold { get; set; } = 0.01;

        /// <summary>历史数据保留天数。</summary>
        public int HistoryRetentionDays { get; set; } = 30;

        /// <summary>历史数据最大内存记录数。</summary>
        public int HistoryMaxMemoryRecords { get; set; } = 100000;

        /// <summary>历史数据自动落盘间隔（秒）。</summary>
        public int HistoryFlushIntervalSeconds { get; set; } = 60;

        /// <summary>历史数据变化检测阈值（变化量超过此值才记录）。</summary>
        public double HistoryChangeThreshold { get; set; } = 0.0;
    }
}
