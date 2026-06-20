using System;

namespace Nexus.App.Services
{
    /// <summary>
    /// 单条写入审计记录。对应 <c>write-audit.log</c> 中的一行 JSON。
    /// 所有字段在写入前应已脱敏（地址、值等可能含 IP/凭据）。
    /// </summary>
    public sealed class WriteAuditRecord
    {
        /// <summary>ISO-8601 时间戳（UTC）。</summary>
        public string Timestamp { get; set; } = string.Empty;

        /// <summary>协议名（如 <c>modbus-tcp</c>）。</summary>
        public string Protocol { get; set; } = string.Empty;

        /// <summary>写入地址（已脱敏）。</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>数据类型（如 <c>Int16</c>）。</summary>
        public string DataType { get; set; } = string.Empty;

        /// <summary>请求写入的值（已脱敏）。</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>结果：confirmed / skipped / succeeded / failed。</summary>
        public string Outcome { get; set; } = string.Empty;

        /// <summary>失败时的错误信息（已脱敏）；成功或跳过时为空。</summary>
        public string? FailureMessage { get; set; }
    }
}
