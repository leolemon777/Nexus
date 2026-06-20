using System;

namespace Nexus.App.Services
{
    /// <summary>
    /// 写入确认对话框抽象 — 使 ViewModel 测试可在不弹出真实 UI 的情况下验证确认流程。
    /// <para>生产实现 <see cref="MessageBoxConfirmationDialog"/> 包装 <c>MessageBox.Show</c>；
    /// 测试可注入返回固定结果的 fake。</para>
    /// </summary>
    public interface IConfirmationDialog
    {
        /// <summary>
        /// 弹出确认对话框，返回用户是否同意。
        /// <para>实现必须保证不抛异常（弹窗失败视为拒绝写入，工业安全优先）。</para>
        /// </summary>
        /// <param name="address">写入地址（已脱敏）。</param>
        /// <param name="dataType">数据类型。</param>
        /// <param name="value">待写入的值（已脱敏）。</param>
        /// <returns><c>true</c> 表示用户确认；<c>false</c> 表示取消或弹窗失败。</returns>
        bool Confirm(string address, string dataType, string value);
    }
}
