using System.Collections.ObjectModel;
using ACLauncher.Core;
using ACLauncher.Services;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ACLauncher.Views;

public partial class LogWindow : Window
{
    private const int MaxLines = 5000;
    private readonly ObservableCollection<string> _lines = [];

    public LogWindow()
    {
        InitializeComponent();
        foreach (var entry in Log.Snapshot()) _lines.Add(Format(entry));
        LogList.ItemsSource = _lines;
        Log.EntryAdded += OnEntryAdded;
        Closed += (_, _) => Log.EntryAdded -= OnEntryAdded;
        Opened += (_, _) => ScrollToEnd();
    }

    private void OnEntryAdded(LogEntry entry) => Dispatcher.UIThread.Post(() =>
    {
        _lines.Add(Format(entry));
        while (_lines.Count > MaxLines) _lines.RemoveAt(0);
        if (AutoScroll.IsChecked == true) ScrollToEnd();
    });

    private void ScrollToEnd()
    {
        if (_lines.Count > 0) LogList.ScrollIntoView(_lines.Count - 1);
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e) => Dialogs.OpenExternal(AppPaths.LogDir);

    private void OnClear(object? sender, RoutedEventArgs e) => _lines.Clear();

    private static string Format(LogEntry entry)
    {
        var level = entry.Level switch
        {
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            _ => "     ",
        };
        return $"{entry.Time:HH:mm:ss} {level} {entry.Message}";
    }
}
