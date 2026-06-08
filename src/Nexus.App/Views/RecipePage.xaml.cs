using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

public partial class RecipePage : Page
{
    private RecipeViewModel? _vm;

    public RecipePage()
    {
        InitializeComponent();
        _vm = ((App)Application.Current).Services.GetRequiredService<RecipeViewModel>();
        DataContext = _vm;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnPageUnloaded;
        _vm?.Dispose();
    }
}
