using System;
using System.Windows;

namespace Nexus.App.Services
{
    /// <summary>
    /// 写入确认服务 — 在写入操作前弹出确认对话框。
    /// <para>对标 HSL WriteVerification，防止误写。</para>
    /// </summary>
    public static class WriteConfirmationService
    {
        /// <summary>是否启用写入确认</summary>
        public static bool IsEnabled { get; set; } = true;

        /// <summary>是否允许当前写入操作</summary>
        public static bool Confirm(string address, string dataType, string value)
        {
            if (!IsEnabled) return true;

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
    }
}
