using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Nexus;

namespace Nexus.App.ViewModels;

public partial class TagConfigViewModel : ObservableObject
{
    private readonly TagDatabase _db = new();

    public ObservableCollection<DeviceTag> Tags { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTag))]
    private DeviceTag? _selectedTag;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _filterDevice = string.Empty;

    [ObservableProperty] private string _newFullName = "PLC1.Temperature";
    [ObservableProperty] private string _newDeviceName = "PLC1";
    [ObservableProperty] private string _newGroupName = "Default";
    [ObservableProperty] private string _newAddress = "D100";
    [ObservableProperty] private string _newDataType = "Float";
    [ObservableProperty] private string _newDescription = "";
    [ObservableProperty] private string _newUnit = "";
    [ObservableProperty] private int _newScanRate = 1000;

    public string[] DataTypes { get; } = { "Bool", "Int16", "UInt16", "Int32", "UInt32", "Float", "Double", "String" };
    public string[] AccessLevels { get; } = { "Operator", "Engineer", "Admin" };

    public int TagCount => Tags.Count;
    public bool HasSelectedTag => SelectedTag != null;

    [RelayCommand]
    private void AddTag()
    {
        if (string.IsNullOrWhiteSpace(NewFullName))
        {
            MessageBox.Show("请输入标签全名", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var tag = new DeviceTag
        {
            FullName = NewFullName.Trim(),
            DeviceName = NewDeviceName.Trim(),
            GroupName = NewGroupName.Trim(),
            Address = NewAddress.Trim(),
            DataType = NewDataType,
            Description = NewDescription.Trim(),
            Unit = NewUnit.Trim(),
            ScanRateMs = NewScanRate
        };
        _db.AddTag(tag);
        RefreshList();
    }

    [RelayCommand]
    private void RemoveTag(DeviceTag? tag)
    {
        if (tag == null) return;
        if (MessageBox.Show($"确定删除标签 '{tag.FullName}'？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _db.RemoveTag(tag.FullName);
        RefreshList();
    }

    [RelayCommand]
    private void ImportJson()
    {
        var dialog = new OpenFileDialog { Filter = "JSON 文件|*.json" };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                int count = _db.ImportFromJson(dialog.FileName);
                RefreshList();
                MessageBox.Show($"已导入 {count} 个标签", "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void ExportJson()
    {
        var dialog = new SaveFileDialog { Filter = "JSON 文件|*.json", FileName = "tags.json" };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                _db.ExportToJson(dialog.FileName);
                MessageBox.Show($"已导出 {Tags.Count} 个标签", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void ImportCsv()
    {
        var dialog = new OpenFileDialog { Filter = "CSV 文件|*.csv" };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                int count = _db.ImportFromCsv(dialog.FileName);
                RefreshList();
                MessageBox.Show($"已导入 {count} 个标签", "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = "tags.csv" };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                _db.ExportToCsv(dialog.FileName);
                MessageBox.Show($"已导出 {Tags.Count} 个标签", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void RefreshList()
    {
        var tags = _db.GetAllTags();
        if (!string.IsNullOrWhiteSpace(FilterText))
            tags = tags.Where(t => t.FullName.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                                   t.Description.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                                   t.Address.Contains(FilterText, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(FilterDevice))
            tags = tags.Where(t => t.DeviceName.Contains(FilterDevice, StringComparison.OrdinalIgnoreCase)).ToList();
        Tags.Clear();
        foreach (var t in tags) Tags.Add(t);
        OnPropertyChanged(nameof(TagCount));
    }

    [RelayCommand]
    private void ClearAll()
    {
        if (MessageBox.Show("确定清空所有标签？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _db.Clear();
        Tags.Clear();
        OnPropertyChanged(nameof(TagCount));
    }
}
