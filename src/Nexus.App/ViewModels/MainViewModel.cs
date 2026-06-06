using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Nexus.App;

public sealed class NavItem
{
    public string Icon { get; init; }
    public string Label { get; init; }
    public string Tag { get; init; }
    public Type PageType { get; init; }

    public NavItem(string icon, string label, string tag, Type pageType)
    {
        Icon = icon; Label = label; Tag = tag; PageType = pageType;
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private NavItem? _selectedNav;

    public ObservableCollection<NavItem> NavItems { get; } = new()
    {
        new NavItem("📡", "Modbus TCP", "modbus-tcp", typeof(Views.ModbusTcpPage)),
        new NavItem("🏭", "Siemens S7", "siemens", typeof(Views.SiemensPage)),
        new NavItem("🗼", "三菱 MC", "mitsubishi", typeof(Views.MitsubishiPage)),
        new NavItem("🟠", "欧姆龙 FINS", "omron", typeof(Views.OmronPage)),
        new NavItem("🔵", "AB CIP", "allenbradley", typeof(Views.AllenBradleyPage)),
        new NavItem("📊", "实时监控", "monitor", typeof(Views.MonitorPage)),
        new NavItem("⚙️", "设置", "settings", typeof(Views.SettingsPage)),
    };

    public event Action<NavItem>? NavigationRequested;

    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (_selectedNav == value) return;
            _selectedNav = value;
            OnPropertyChanged();
            if (value != null) NavigationRequested?.Invoke(value);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
