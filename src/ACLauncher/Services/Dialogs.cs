using System.Diagnostics;
using ACLauncher.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace ACLauncher.Services;

public static class Dialogs
{
    public static async Task<string?> PickFolderAsync(Visual owner, string title, string? startPath)
    {
        var topLevel = TopLevel.GetTopLevel(owner);
        if (topLevel is null) return null;
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrWhiteSpace(startPath) && Directory.Exists(startPath))
            options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(startPath);
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public static Task MessageAsync(Window owner, string title, string message) =>
        Show(owner, title, message, ["OK"]);

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string confirm = "OK") =>
        await Show(owner, title, message, [confirm, "Cancel"]) == confirm;

    /// <summary>Opens a web link in the browser. Anything but an http or https link is refused, whatever its source.</summary>
    public static void OpenWebLink(string? url)
    {
        if (SafeLinks.TryGetWebLink(url, out var uri)) OpenExternal(uri.AbsoluteUri);
        else Log.Warn($"Did not open '{url}': only http and https links are opened");
    }

    /// <summary>Opens an existing local folder in the file manager.</summary>
    public static void OpenFolder(string path)
    {
        if (Directory.Exists(path)) OpenExternal(Path.GetFullPath(path));
    }

    private static void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", [target]) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Could not open {target}", e);
        }
    }

    private static async Task<string?> Show(Window owner, string title, string message, string[] buttons)
    {
        string? result = null;
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Classes = { "launcher" },
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var label in buttons)
        {
            var button = new Button { Content = label, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (label == buttons[0]) button.Classes.Add("accent");
            button.Click += (_, _) =>
            {
                result = label;
                dialog.Close();
            };
            row.Children.Add(button);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, row },
        };
        await dialog.ShowDialog(owner);
        return result;
    }
}
