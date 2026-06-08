using System;

namespace Nexus.App.Services
{
    /// <summary>
    /// 监控标签条目 — 绑定到一个设备地址，周期性读取最新值。
    /// </summary>
    public sealed class TagEntry
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string DataType { get; set; } = "Int16";
        public string ProtocolName { get; set; } = string.Empty;

        /// <summary>最新读取的值（显示用）</summary>
        public string LastValue { get; set; } = "--";

        /// <summary>质量: Good / Bad / Pending</summary>
        public string Quality { get; set; } = "Pending";

        /// <summary>上次更新时间</summary>
        public DateTime? LastUpdate { get; set; }

        /// <summary>轮询周期（毫秒），0 = 不轮询</summary>
        public int PollIntervalMs { get; set; } = 1000;

        public override string ToString() => $"{Name} ({Address}) = {LastValue}";
    }
}
