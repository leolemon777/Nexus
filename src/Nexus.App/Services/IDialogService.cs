using System.Windows;

namespace Nexus.App.Services
{
    /// <summary>
    /// 通用对话框服务抽象 — 替代 ViewModel 中直接调用 MessageBox.Show。
    /// <para>生产实现 <see cref="DialogService"/> 包装 MessageBox.Show；
    /// 测试可注入返回固定结果的 fake。</para>
    /// </summary>
    public interface IDialogService
    {
        void ShowInfo(string message, string title = "提示");
        void ShowWarning(string message, string title = "提示");
        void ShowError(string message, string title = "错误");
        bool ShowConfirmation(string message, string title = "确认");
    }

    /// <summary>
    /// IDialogService 的 WPF MessageBox 实现。
    /// </summary>
    public sealed class DialogService : IDialogService
    {
        public void ShowInfo(string message, string title)
            => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

        public void ShowWarning(string message, string title)
            => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

        public void ShowError(string message, string title)
            => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

        public bool ShowConfirmation(string message, string title)
            => MessageBox.Show(message, title, MessageBoxButton.YesNo) == MessageBoxResult.Yes;
    }
}
