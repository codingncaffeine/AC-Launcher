using ACLauncher.Core;
using ACLauncher.Services;
using ACLauncher.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ACLauncher.Views;

public partial class MainWindow : Window
{
    private LogWindow? _log;

    public MainWindow() => InitializeComponent();

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    private async void OnAddAccount(object? sender, RoutedEventArgs e)
    {
        var edit = await new AccountDialog(null, "").ShowDialog<AccountEdit?>(this);
        if (edit is not null) await ViewModel.AddAccountAsync(edit);
    }

    private async void OnEditAccount(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAccount is not { } item) return;
        var password = await ViewModel.GetPasswordForEditAsync(item.Account);
        var edit = await new AccountDialog(item.Account, password).ShowDialog<AccountEdit?>(this);
        if (edit is not null) await ViewModel.UpdateAccountAsync(item, edit);
    }

    private async void OnDeleteAccount(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAccount is not { } item) return;
        if (await Dialogs.ConfirmAsync(this, "Delete account", $"Delete {item.DisplayName}? Its saved password is removed too.", "Delete"))
            await ViewModel.DeleteAccountAsync(item);
    }

    private async void OnServers(object? sender, RoutedEventArgs e)
    {
        await new ServersWindow(new ServersViewModel(ViewModel.State)).ShowDialog(this);
        ViewModel.RebuildAccounts();
    }

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        var saved = await new SettingsWindow(new SettingsViewModel(ViewModel.State)).ShowDialog<LauncherSettings?>(this);
        if (saved is null) return;
        ViewModel.State.ReplaceSettings(saved);
        ViewModel.LoadSettings();
    }

    private void OnLog(object? sender, RoutedEventArgs e)
    {
        if (_log is not null)
        {
            _log.Activate();
            return;
        }
        _log = new LogWindow();
        _log.Closed += (_, _) => _log = null;
        _log.Show();
    }

    private async void OnBrowseGameFolder(object? sender, RoutedEventArgs e)
    {
        var path = await Dialogs.PickFolderAsync(this, "Choose the Asheron's Call folder", ViewModel.GameDirectory);
        if (path is not null) ViewModel.GameDirectory = path;
    }
}
