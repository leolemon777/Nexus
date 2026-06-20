using System;
using System.Windows;

namespace Nexus.App.Services
{
    /// <summary>
    /// <see cref="IConfirmationDialog"/> 的默认实现 — 包装 WPF <see cref="MessageBox"/>。
    /// <para>保留原 <c>WriteConfirmationService</c> 的中文确认措辞与"默认选否"的工业安全策略。</para>
    /// <para>仅运行于 UI 线程；非 UI 线程调用回退为拒绝写入（避免跨线程弹窗异常导致误写）。</para>
    /// </summary>
    public sealed class MessageBoxConfirmationDialog : IConfirmationDialog
    {
        /// <summary>是否启用写入确认（与旧静态 <c>IsEnabled</c> 等价，便于全局开关）。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <inheritdoc />
        public bool Confirm(string address, string dataType, string value)
        {
            if (!IsEnabled) return true;

            // 工业安全：非 UI 线程或无应用实例时拒绝写入，绝不静默放行。
            if (Application.Current == null) return false;
            if (!Application.Current.Dispatcher.CheckAccess()) return false;

            try
            {
                var result = MessageBox.Show(
                    $"即将写入 PLC 数据：\n\n" +
                    $"  地址：{address}\n" +
                    $"  类型：{dataType}\n" +
                    $"  值：{value}\n\n" +
                    $"确认写入？此操作将直接修改 PLC 数据。",
                    "⚠ 写入确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                return result == MessageBoxResult.Yes;
            }
            catch
            {
                // 弹窗本身失败（UI 线程损坏等）— 安全优先，视为拒绝。
                return false;
            }
        }
    }
}
