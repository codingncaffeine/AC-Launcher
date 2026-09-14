using ACLauncher.Services;
using ACLauncher.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ACLauncher.Views;

/// <summary>Closes with the edited settings, or null when cancelled.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    public SettingsWindow(SettingsViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Opened += async (_, _) => await viewModel.LoadInstalledProtonBuildsAsync();
    }

    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext!;

    private async void OnBrowseGame(object? sender, RoutedEventArgs e)
    {
        var path = await Dialogs.PickFolderAsync(this, "Choose the Asheron's Call folder", ViewModel.GameDirectory);
        if (path is not null) ViewModel.GameDirectory = path;
    }

    private async void OnBrowseProton(object? sender, RoutedEventArgs e)
    {
        var start = Directory.Exists(ViewModel.ProtonPath) ? ViewModel.ProtonPath : null;
        var path = await Dialogs.PickFolderAsync(this, "Choose a Proton build folder", start);
        if (path is not null) ViewModel.ProtonPath = path;
    }

    private async void OnManageProton(object? sender, RoutedEventArgs e)
    {
        var chosen = await new ProtonVersionsWindow(new ProtonVersionsViewModel(ViewModel.State, ViewModel.ProtonPath)).ShowDialog<string?>(this);
        ViewModel.RefreshProtonChoices();
        if (chosen is not null) ViewModel.ProtonPath = chosen;
    }

    private async void OnBrowsePrefix(object? sender, RoutedEventArgs e)
    {
        var path = await Dialogs.PickFolderAsync(this, "Choose a Wine prefix folder", ViewModel.PrefixPath);
        if (path is not null) ViewModel.PrefixPath = path;
    }

    private void OnDefaultPrefix(object? sender, RoutedEventArgs e) => ViewModel.PrefixPath = "";

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.Apply() is { } settings) Close(settings);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
