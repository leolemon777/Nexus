using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

public partial class TagConfigPage : Page
{
    public TagConfigPage()
    {
        InitializeComponent();
        DataContext = ((App)Application.Current).Services.GetRequiredService<TagConfigViewModel>();
    }
}
