using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.App.Services;

namespace Nexus.App.ViewModels;

public partial class RecipeViewModel : ObservableObject, IDisposable
{
    private readonly RecipeService _recipeService;
    private readonly IDialogService _dialog;

    [ObservableProperty] private string _recipeName = string.Empty;
    [ObservableProperty] private string _recipeDescription = string.Empty;
    [ObservableProperty] private string _selectedRecipeName = string.Empty;
    [ObservableProperty] private int _recipeCount;

    // New parameter form
    [ObservableProperty] private string _paramName = string.Empty;
    [ObservableProperty] private string _paramAddress = string.Empty;
    [ObservableProperty] private string _paramDataType = "Int16";
    [ObservableProperty] private string _paramValue = string.Empty;

    public string[] DataTypes { get; } = { "Int16", "UInt16", "Int32", "UInt32", "Float", "Double", "String", "Bool" };

    public ObservableCollection<string> RecipeList { get; } = new();
    public ObservableCollection<RecipeParameter> CurrentParameters { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    private Recipe? _currentRecipe;

    public RecipeViewModel(RecipeService recipeService, IDialogService dialog)
    {
        _recipeService = recipeService;
        _dialog = dialog;
        RefreshList();
    }

    [RelayCommand]
    private void RefreshList()
    {
        RecipeList.Clear();
        foreach (var name in _recipeService.ListRecipes())
            RecipeList.Add(name);
        RecipeCount = RecipeList.Count;
    }

    [RelayCommand]
    private void NewRecipe()
    {
        if (string.IsNullOrWhiteSpace(RecipeName))
        {
            _dialog.ShowWarning("请输入配方名称");
            return;
        }
        _currentRecipe = new Recipe
        {
            Name = RecipeName.Trim(),
            Description = RecipeDescription.Trim(),
            Parameters = CurrentParameters.ToList()
        };
        _recipeService.SaveRecipe(_currentRecipe);
        AppendLog($"[OK] 配方 '{_currentRecipe.Name}' 已保存 ({_currentRecipe.Parameters.Count} 个参数)");
        RefreshList();
    }

    [RelayCommand]
    private void LoadRecipe(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        var recipe = _recipeService.LoadRecipe(name);
        if (recipe == null) { AppendLog($"[ERR] 未找到配方: {name}"); return; }

        _currentRecipe = recipe;
        RecipeName = recipe.Name;
        RecipeDescription = recipe.Description;
        SelectedRecipeName = recipe.Name;

        CurrentParameters.Clear();
        foreach (var p in recipe.Parameters)
            CurrentParameters.Add(p);

        AppendLog($"[OK] 已加载配方: {name} ({recipe.Parameters.Count} 个参数)");
    }

    [RelayCommand]
    private void DeleteRecipe(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (!_dialog.ShowConfirmation($"确定删除配方 '{name}'？")) return;
        _recipeService.DeleteRecipe(name);
        AppendLog($"[OK] 已删除配方: {name}");
        RefreshList();
    }

    [RelayCommand]
    private void AddParameter()
    {
        if (string.IsNullOrWhiteSpace(ParamName) || string.IsNullOrWhiteSpace(ParamAddress))
        {
            _dialog.ShowWarning("请填写参数名和地址");
            return;
        }
        CurrentParameters.Add(new RecipeParameter
        {
            Name = ParamName.Trim(),
            Address = ParamAddress.Trim(),
            DataType = ParamDataType,
            Value = ParamValue.Trim()
        });
        ParamName = string.Empty;
        ParamAddress = string.Empty;
        ParamValue = string.Empty;
    }

    [RelayCommand]
    private void RemoveParameter(RecipeParameter? param)
    {
        if (param != null) CurrentParameters.Remove(param);
    }

    [RelayCommand]
    private void ExportRecipe(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            var json = _recipeService.ExportToJson(name);
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"recipe_{name}.json");
            System.IO.File.WriteAllText(path, json);
            AppendLog($"[OK] 已导出到: {path}");
        }
        catch (Exception ex) { AppendLog($"[ERR] 导出失败: {ex.Message}"); }
    }

    private void AppendLog(string line)
    {
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        if (LogLines.Count > 200)
            LogLines.RemoveAt(0);
    }

    public void Dispose() { GC.SuppressFinalize(this); }
}
