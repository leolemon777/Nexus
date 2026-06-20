using System;

namespace Nexus.App.Services
{
    /// <summary>
    /// 写入确认 + 审计门面 — 在写入前确认（弹窗可注入），并全程追加审计记录。
    /// <para>替代原静态 <c>WriteConfirmationService</c>，使 ViewModel 可在单测中注入
    /// 不弹 UI 的 fake，同时保留工业安全语义（默认拒绝、全程可追溯）。</para>
    /// </summary>
    public interface IWriteConfirmationService
    {
        /// <summary>是否启用写入确认弹窗（关闭后 <see cref="ConfirmWrite"/> 直接返回 true，但仍记录审计）。</summary>
        bool IsConfirmationEnabled { get; set; }

        /// <summary>
        /// 在写入前确认是否放行，并追加 <c>confirmed</c>/<c>skipped</c> 审计记录。
        /// 调用方应在确认通过后再执行实际写入，并通过 <see cref="RecordOutcome"/> 上报结果。
        /// </summary>
        /// <param name="protocol">协议名（如 <c>modbus-tcp</c>）。</param>
        /// <param name="address">原始写入地址 — 内部经 <c>SecretRedactor.Redact</c> 脱敏后再审计。</param>
        /// <param name="dataType">数据类型。</param>
        /// <param name="value">原始待写入值 — 内部脱敏后再审计。</param>
        /// <returns><c>true</c> 表示用户确认放行；<c>false</c> 表示取消或弹窗失败。</returns>
        bool ConfirmWrite(string protocol, string address, string dataType, string value);

        /// <summary>
        /// 上报实际写入结果，追加 <c>succeeded</c>/<c>failed</c> 审计记录。永不抛异常。
        /// </summary>
        /// <param name="protocol">协议名。</param>
        /// <param name="address">原始写入地址（内部脱敏）。</param>
        /// <param name="dataType">数据类型。</param>
        /// <param name="value">原始待写入值（内部脱敏）。</param>
        /// <param name="succeeded">实际写入是否成功。</param>
        /// <param name="failureMessage">失败时的错误信息（内部脱敏）；成功时忽略。</param>
        void RecordOutcome(string protocol, string address, string dataType, string value, bool succeeded, string? failureMessage);
    }
}
