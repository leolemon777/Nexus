using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Nexus.App.Views;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        BuildColorSwatches();
        BuildFormButtons();
    }

    private void BuildColorSwatches()
    {
        foreach (var name in ThemeManager.AvailableColors)
        {
            var btn = new ToggleButton
            {
                Content = name,
                Width = 100,
                Height = 36,
                Margin = new Thickness(0, 0, 8, 8),
                IsChecked = name == ThemeManager.CurrentColor,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Cursor = System.Windows.Input.Cursors.Hand,
            };

            var accent = (Brush)Application.Current.FindResource("Brush.Accent");

            btn.Click += (_, _) =>
            {
                ThemeManager.ApplyColor(name);
                // 更新选中状态
                foreach (var child in ColorPanel.Children)
                {
                    if (child is ToggleButton tb)
                        tb.IsChecked = tb.Content?.ToString() == ThemeManager.CurrentColor;
                }
            };

            ColorPanel.Children.Add(btn);
        }
    }

    private void BuildFormButtons()
    {
        foreach (var name in ThemeManager.AvailableForms)
        {
            var btn = new ToggleButton
            {
                Content = name,
                Width = 100,
                Height = 36,
                Margin = new Thickness(0, 0, 8, 8),
                IsChecked = name == ThemeManager.CurrentForm,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Cursor = System.Windows.Input.Cursors.Hand,
            };

            btn.Click += (_, _) =>
            {
                ThemeManager.ApplyForm(name);
                foreach (var child in FormPanel.Children)
                {
                    if (child is ToggleButton tb)
                        tb.IsChecked = tb.Content?.ToString() == ThemeManager.CurrentForm;
                }
            };

            FormPanel.Children.Add(btn);
        }
    }
}
