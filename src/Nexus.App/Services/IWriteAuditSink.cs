using System;

namespace Nexus.App.Services
{
    /// <summary>
    /// 写入审计落盘抽象 — 记录每一次 PLC 写入尝试（confirmed/skipped/succeeded/failed），
    /// 用于事后合规追溯。实现必须 <b>不抛异常</b>（审计失败不得阻塞用户写入流程）。
    /// </summary>
    public interface IWriteAuditSink
    {
        /// <summary>
        /// 追加一条写入审计记录。所有可能含 IP/地址/凭据的字段应由调用方先经
        /// <c>SecretRedactor.Redact</c> 脱敏后再传入；实现内部不再二次脱敏以保证字段语义清晰。
        /// </summary>
        /// <param name="record">已填好的审计记录（不可为 null）。</param>
        void Append(WriteAuditRecord record);
    }
}
