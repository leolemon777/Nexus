using System;
using System.Windows;

namespace Nexus.App;

/// <summary>
/// 运行时换肤：配色(Color)和款式(Form)各是一份 ResourceDictionary，
/// 互相独立，可任意组合。组件用 DynamicResource 引用 token，换字典即重绘。
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _color;
    private static ResourceDictionary? _form;

    public static string CurrentColor { get; private set; } = "mono";
    public static string CurrentForm { get; private set; } = "soft";

    public static string[] AvailableColors { get; } =
    {
        "industrial","claude","obsidian","apple","ferrari","nord","daylight","business","tiffany","hermes",
        "mono","platinum","mercedes","bmw","mclaren","aston","linear","lambo","rolls","dracula",
        "spotify","porsche","bugatti","cyber","fluentc","materialc"
    };

    public static string[] AvailableForms { get; } =
    {
        "soft","sharp","flat","pill","editorial","neu","glass","terminal","brutal",
        "aurora","skeu","memphis","hud","fluent","material"
    };

    public static void Init(string color, string form)
    {
        ApplyColor(color);
        ApplyForm(form);
    }

    public static void ApplyColor(string name)
    {
        CurrentColor = name;
        Swap(ref _color, $"Themes/Color.{name}.xaml");
    }

    public static void ApplyForm(string name)
    {
        CurrentForm = name;
        Swap(ref _form, $"Styles/Form.{name}.xaml");
    }

    private static void Swap(ref ResourceDictionary? slot, string relativePath)
    {
        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{relativePath}", UriKind.Absolute)
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (slot != null) merged.Remove(slot);
        merged.Add(dict);
        slot = dict;
    }
}
