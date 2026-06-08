using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Nexus.App.Controls;

public partial class ProtocolLogViewer : UserControl, IDisposable
{
    private static readonly Regex PrefixRegex = new(
        @"^\[[\d:.]+\]\s+(?<tag>\[[\w]+\]|💡)\s?",
        RegexOptions.Compiled);

    public ProtocolLogViewer()
    {
        InitializeComponent();
        LogRichTextBox.Document = new FlowDocument { PagePadding = new Thickness(2) };
        _doc = LogRichTextBox.Document;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ── Dependency Properties ─────────────────────────────────

    public static readonly DependencyProperty LogLinesProperty =
        DependencyProperty.Register(nameof(LogLines), typeof(ObservableCollection<string>),
            typeof(ProtocolLogViewer), new FrameworkPropertyMetadata(null, OnLogLinesChanged));

    public ObservableCollection<string>? LogLines
    {
        get => (ObservableCollection<string>?)GetValue(LogLinesProperty);
        set => SetValue(LogLinesProperty, value);
    }

    public static readonly DependencyProperty MaxLinesProperty =
        DependencyProperty.Register(nameof(MaxLines), typeof(int),
            typeof(ProtocolLogViewer), new FrameworkPropertyMetadata(500));

    public int MaxLines
    {
        get => (int)GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    public static readonly DependencyProperty IsHexViewProperty =
        DependencyProperty.Register(nameof(IsHexView), typeof(bool),
            typeof(ProtocolLogViewer), new FrameworkPropertyMetadata(false, OnViewModeChanged));

    public bool IsHexView
    {
        get => (bool)GetValue(IsHexViewProperty);
        set => SetValue(IsHexViewProperty, value);
    }

    public static readonly DependencyProperty AutoScrollProperty =
        DependencyProperty.Register(nameof(AutoScroll), typeof(bool),
            typeof(ProtocolLogViewer), new FrameworkPropertyMetadata(true));

    public bool AutoScroll
    {
        get => (bool)GetValue(AutoScrollProperty);
        set => SetValue(AutoScrollProperty, value);
    }

    public static readonly DependencyProperty SearchTextProperty =
        DependencyProperty.Register(nameof(SearchText), typeof(string),
            typeof(ProtocolLogViewer), new FrameworkPropertyMetadata(string.Empty, OnSearchChanged));

    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    // ── Commands ──────────────────────────────────────────────

    public ICommand ClearCommand => new RelayCommand(_ => ClearLog());
    public ICommand ExportCommand => new RelayCommand(_ => ExportLog());

    // ── Internal state ────────────────────────────────────────

    private ObservableCollection<string>? _boundCollection;
    private readonly ObservableCollection<string> _allLines = new();
    private FlowDocument? _doc;
    private bool _suppressCollectionChanged;

    private static void OnLogLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ProtocolLogViewer viewer)
            viewer.BindCollection(e.OldValue as ObservableCollection<string>, e.NewValue as ObservableCollection<string>);
    }

    private static void OnViewModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ProtocolLogViewer viewer)
            viewer.RebuildDocument();
    }

    private static void OnSearchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ProtocolLogViewer viewer)
            viewer.RebuildDocument();
    }

    private void BindCollection(ObservableCollection<string>? oldCol, ObservableCollection<string>? newCol)
    {
        if (oldCol != null)
            oldCol.CollectionChanged -= OnSourceCollectionChanged;

        _boundCollection = newCol;
        _allLines.Clear();

        if (newCol != null)
        {
            foreach (var line in newCol)
                _allLines.Add(line);
            newCol.CollectionChanged += OnSourceCollectionChanged;
        }

        RebuildDocument();
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressCollectionChanged) return;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems != null)
                {
                    foreach (string line in e.NewItems)
                        _allLines.Add(line);
                    TrimIfNeeded();
                    if (e.NewItems.Count == 1)
                        AppendLine((string)e.NewItems[0]!);
                    else
                        RebuildDocument();
                }
                break;

            case NotifyCollectionChangedAction.Reset:
                _allLines.Clear();
                RebuildDocument();
                break;
        }
    }

    private void TrimIfNeeded()
    {
        int max = MaxLines;
        while (_allLines.Count > max)
            _allLines.RemoveAt(0);
    }

    // ── Document building ─────────────────────────────────────

    private void RebuildDocument()
    {
        if (_doc == null) return;
        _doc.Blocks.Clear();

        string filter = SearchText ?? string.Empty;
        bool hasFilter = !string.IsNullOrWhiteSpace(filter);

        foreach (var line in _allLines)
        {
            if (hasFilter && line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            _doc.Blocks.Add(CreateColoredParagraph(line));
        }

        ScrollToEnd();
    }

    private void AppendLine(string line)
    {
        if (_doc == null) return;

        string filter = SearchText ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(filter) && line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            return;

        _doc.Blocks.Add(CreateColoredParagraph(line));
        TrimDocument();
        ScrollToEnd();
    }

    private Paragraph CreateColoredParagraph(string line)
    {
        var para = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };

        string displayLine = IsHexView ? FormatAsHex(line) : line;

        var match = PrefixRegex.Match(displayLine);
        if (match.Success)
        {
            string timestamp = displayLine[..match.Groups["tag"].Index];
            string tag = match.Groups["tag"].Value;
            string rest = displayLine[(match.Index + match.Length)..];

            Brush tagBrush = GetTagBrush(tag);
            Brush textBrush = (Brush)FindResource("Brush.Text2");

            para.Inlines.Add(new Run(timestamp) { Foreground = (Brush)FindResource("Brush.Text3") });
            para.Inlines.Add(new Run(tag + " ") { Foreground = tagBrush, FontWeight = FontWeights.SemiBold });
            para.Inlines.Add(new Run(rest) { Foreground = textBrush });
        }
        else
        {
            para.Inlines.Add(new Run(displayLine) { Foreground = (Brush)FindResource("Brush.Text2") });
        }

        return para;
    }

    private Brush GetTagBrush(string tag)
    {
        return tag switch
        {
            "[WR]" or "[RD]" => (Brush)FindResource("Brush.Ok"),
            "[ERR]" => (Brush)FindResource("Brush.Alarm"),
            "[OK]" => (Brush)FindResource("Brush.Ok"),
            "[--]" => (Brush)FindResource("Brush.Text3"),
            "[WARN]" => (Brush)FindResource("Brush.Warn"),
            "[SKIP]" => (Brush)FindResource("Brush.Warn"),
            "💡" => (Brush)FindResource("Brush.Accent"),
            _ => (Brush)FindResource("Brush.Text2"),
        };
    }

    private static string FormatAsHex(string line)
    {
        return line;
    }

    private void TrimDocument()
    {
        if (_doc == null) return;
        int max = MaxLines;
        while (_doc.Blocks.Count > max)
        {
            var first = _doc.Blocks.FirstBlock;
            if (first != null)
                _doc.Blocks.Remove(first);
            else
                break;
        }
    }

    private void ScrollToEnd()
    {
        if (!AutoScroll) return;
        LogRichTextBox?.ScrollToEnd();
    }

    // ── Actions ───────────────────────────────────────────────

    private void ClearLog()
    {
        _allLines.Clear();

        _suppressCollectionChanged = true;
        LogLines?.Clear();
        _suppressCollectionChanged = false;

        _doc?.Blocks.Clear();
    }

    private void ExportLog()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"NexusLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                File.WriteAllLines(dlg.FileName, _allLines);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (LogLines != null && _boundCollection != LogLines)
            BindCollection(_boundCollection, LogLines);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_boundCollection != null)
            _boundCollection.CollectionChanged -= OnSourceCollectionChanged;
    }

    public void Dispose()
    {
        if (_boundCollection != null)
            _boundCollection.CollectionChanged -= OnSourceCollectionChanged;
    }

    // ── RelayCommand (minimal) ────────────────────────────────

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommand(Action<object?> execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public event EventHandler? CanExecuteChanged;
        public void Execute(object? parameter) => _execute(parameter);
    }
}

public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
