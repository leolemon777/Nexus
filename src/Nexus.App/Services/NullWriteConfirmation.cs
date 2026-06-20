using System;

namespace Nexus.App.Services
{
    /// <summary>
    /// <see cref="IWriteConfirmationService"/> 的安全空实现 — 当 DI 容器不可用
    /// （设计时、早期启动、无 host 的单测）时作为回退。
    /// <para>语义：确认始终放行（不弹窗），审计记录丢弃。等价于旧静态
    /// <c>WriteConfirmationService</c> 在 <c>IsEnabled=false</c> 且无审计时的行为，
    /// 保证 UI 在容器异常时不卡死。</para>
    /// <para>生产路径应始终从 <c>App.Services</c> 解析到真实实现；此类型主要服务于
    /// 设计时与回退路径。</para>
    /// </summary>
    public sealed class NullWriteConfirmation : IWriteConfirmationService
    {
        /// <summary>单例（无状态）。</summary>
        public static readonly NullWriteConfirmation Instance = new NullWriteConfirmation();

        private NullWriteConfirmation() { }

        /// <inheritdoc />
        public bool IsConfirmationEnabled { get; set; }

        /// <inheritdoc />
        public bool ConfirmWrite(string protocol, string address, string dataType, string value) => true;

        /// <inheritdoc />
        public void RecordOutcome(string protocol, string address, string dataType, string value, bool succeeded, string? failureMessage)
        {
            // 丢弃 — 无审计 sink 可用。
        }
    }
}
