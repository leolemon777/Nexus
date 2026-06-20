using System;
using System.Globalization;

namespace Nexus.App.Services
{
    /// <summary>
    /// <see cref="IWriteConfirmationService"/> 的默认实现 — 把"确认弹窗"与"审计落盘"组合成一条
    /// 安全流水线，替代原静态 <c>WriteConfirmationService</c>。
    /// <para>确认通过 <see cref="IConfirmationDialog"/>（可注入）实现；
    /// 审计通过 <see cref="IWriteAuditSink"/>（可注入）实现。</para>
    /// <para>所有可能含 IP / 地址 / 凭据的字段在写入审计前都经
    /// <c>SecretRedactor.Redact(string)</c> 脱敏（WS-B 提供）。若 <c>SecretRedactor</c>
    /// 尚未注入（早期启动或单测），则回退为原值 —— 脱敏失败绝不阻塞写入确认。</para>
    /// </summary>
    public sealed class WriteConfirmationService : IWriteConfirmationService
    {
        private readonly IConfirmationDialog _dialog;
        private readonly IWriteAuditSink _auditSink;

        /// <summary>
        /// 构造写入确认服务。
        /// </summary>
        /// <param name="dialog">确认对话框抽象（生产用 <see cref="MessageBoxConfirmationDialog"/>，测试用 fake）。</param>
        /// <param name="auditSink">审计落盘抽象（生产用 <see cref="WriteAuditSink"/>，测试用内存 fake）。</param>
        public WriteConfirmationService(IConfirmationDialog dialog, IWriteAuditSink auditSink)
        {
            _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        }

        private bool _confirmationEnabled = true;

        /// <inheritdoc />
        public bool IsConfirmationEnabled
        {
            get => _confirmationEnabled;
            set => _confirmationEnabled = value;
        }

        /// <inheritdoc />
        public bool ConfirmWrite(string protocol, string address, string dataType, string value)
        {
            bool confirmed;
            try
            {
                // 关闭确认时直接放行（仍记录审计）；开启时交由对话框决定。
                confirmed = !_confirmationEnabled || _dialog.Confirm(address, dataType, value);
            }
            catch
            {
                // 弹窗异常视为拒绝（工业安全优先），但仍记录审计。
                confirmed = false;
            }

            AppendAudit(protocol, address, dataType, value, confirmed ? "confirmed" : "skipped", null);
            return confirmed;
        }

        /// <inheritdoc />
        public void RecordOutcome(string protocol, string address, string dataType, string value, bool succeeded, string? failureMessage)
        {
            AppendAudit(protocol, address, dataType, value,
                succeeded ? "succeeded" : "failed",
                succeeded ? null : failureMessage);
        }

        private void AppendAudit(string protocol, string address, string dataType, string value, string outcome, string? failureMessage)
        {
            try
            {
                var record = new WriteAuditRecord
                {
                    Timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    Protocol = protocol ?? string.Empty,
                    Address = RedactOrFallback(address),
                    DataType = dataType ?? string.Empty,
                    Value = RedactOrFallback(value),
                    Outcome = outcome,
                    FailureMessage = string.IsNullOrEmpty(failureMessage) ? null : RedactOrFallback(failureMessage),
                };
                _auditSink.Append(record);
            }
            catch
            {
                // 审计构造失败不得阻塞主流程。WriteAuditSink 内部已吞 IO 异常，
                // 这里兜底保护 record 构造期间（如 SecretRedactor 抛异常）的意外。
            }
        }

        /// <summary>
        /// 调 <c>SecretRedactor.Redact</c> 脱敏；若类型不可用或抛异常则回退原值
        /// （脱敏失败不阻塞审计）。
        /// </summary>
        private static string RedactOrFallback(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            try
            {
                // SecretRedactor 与本服务同程序集（Nexus.App.Services），直接调用即可。
                string redacted = SecretRedactor.Redact(raw);
                return !string.IsNullOrEmpty(redacted) ? redacted : raw;
            }
            catch
            {
                // 脱敏失败 — 回退原值（审计可追溯优先于完美脱敏）。
                return raw;
            }
        }
    }
}
