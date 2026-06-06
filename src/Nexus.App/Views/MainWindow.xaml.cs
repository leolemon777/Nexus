using System.Windows;

namespace Nexus.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.NavigationRequested += OnNavigate;

            // 默认选中第一项
            if (NavList.Items.Count > 0)
                NavList.SelectedIndex = 0;
        }
    }

    private void OnNavigate(NavItem item)
    {
        var page = System.Activator.CreateInstance(item.PageType);
        ContentFrame.Navigate(page);
    }
}
