using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Nexus.App;

public partial class MainWindow : Window
{
    private NavItem? _highlightedItem;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 通过 DI 设置 DataContext。由 App.OnStartup 中调用。
    /// </summary>
    public void SetViewModel(MainViewModel vm)
    {
        DataContext = vm;
        vm.NavigationRequested += OnNavigate;

        // 监听 TreeView 的鼠标点击（TreeView 当前 Visibility=Collapsed，不影响绑定注册）
        NavTree.AddHandler(MouseDownEvent, new MouseButtonEventHandler(OnNavTreeMouseDown), true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 默认选中第一个分组的第一个子项
        if (DataContext is MainViewModel vm && vm.NavGroups.Count > 0)
        {
            var firstGroup = vm.NavGroups[0];
            if (firstGroup.Items.Count > 0)
            {
                SelectNavItem(firstGroup.Items[0]);
            }
        }
    }

    private void OnNavTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        var hit = e.OriginalSource as DependencyObject;
        while (hit != null)
        {
            if (hit is FrameworkElement fe)
            {
                // 点击 NavItem（子项）→ 选中并导航
                if (fe.DataContext is NavItem navItem)
                {
                    SelectNavItem(navItem);
                    e.Handled = true;
                    return;
                }

                // 点击 NavGroup（分组头）→ 展开/收拢切换
                if (fe.DataContext is NavGroup)
                {
                    var tvi = FindAncestor<TreeViewItem>(fe);
                    if (tvi != null)
                        tvi.IsExpanded = !tvi.IsExpanded;
                    e.Handled = true;
                    return;
                }
            }
            hit = VisualTreeHelper.GetParent(hit);
        }
    }

    /// <summary>
    /// 向上查找指定类型的视觉树祖先。
    /// </summary>
    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T result) return result;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void SelectNavItem(NavItem item)
    {
        if (DataContext is not MainViewModel vm) return;

        // 清除旧高亮
        ClearHighlight();

        // 设置选中项
        vm.SelectedNav = item;

        // 高亮新的（TreeView 不可见时为 no-op）
        _highlightedItem = item;
        ApplyHighlight(item, true);

        // 更新底部状态栏的协议名
        ProtocolNameText.Text = item.Label;

        // 导航页面
        OnNavigate(item);
    }

    private void ClearHighlight()
    {
        if (_highlightedItem != null)
        {
            ApplyHighlight(_highlightedItem, false);
            _highlightedItem = null;
        }
    }

    private void ApplyHighlight(NavItem target, bool highlight)
    {
        NavTree.UpdateLayout();
        foreach (var group in NavTree.Items)
        {
            if (group is not NavGroup g) continue;
            var groupContainer = NavTree.ItemContainerGenerator.ContainerFromItem(g) as TreeViewItem;
            if (groupContainer == null) continue;

            groupContainer.UpdateLayout();
            foreach (var child in g.Items)
            {
                if (child != target) continue;
                var childContainer = groupContainer.ItemContainerGenerator.ContainerFromItem(child) as TreeViewItem;
                if (childContainer == null) continue;

                // 找到 DataTemplate 里的 Border
                var border = FindVisualChild<Border>(childContainer);
                if (border == null) return;

                if (highlight)
                {
                    border.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
                    var grid = border.Child as Grid;
                    if (grid != null && grid.Children.Count > 1 && grid.Children[1] is TextBlock tb)
                    {
                        tb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
                        tb.FontWeight = FontWeights.SemiBold;
                    }
                }
                else
                {
                    border.Background = Brushes.Transparent;
                    var grid = border.Child as Grid;
                    if (grid != null && grid.Children.Count > 1 && grid.Children[1] is TextBlock tb)
                    {
                        tb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
                        tb.FontWeight = FontWeights.Normal;
                    }
                }
                return;
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            var desc = FindVisualChild<T>(child);
            if (desc != null) return desc;
        }
        return null;
    }

    private void OnNavigate(NavItem item)
    {
        var page = System.Activator.CreateInstance(item.PageType);
        if (page is INavigablePage navigable)
            navigable.OnNavigatedTo(item.Tag);
        ContentFrame.Navigate(page);

        // H-2: 从 Page 的 DataContext 提取 ProtocolViewModelBase，设为 ActiveViewModel
        // 这样右侧报文面板和底部状态栏才能绑定到当前协议的数据。
        if (page is System.Windows.Controls.Page p && p.DataContext is ProtocolViewModelBase vm)
        {
            _vm.ActiveViewModel = vm;
            _vm.StatusBarProtocol = item.Label;
        }
    }
}
