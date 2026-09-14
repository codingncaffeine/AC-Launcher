using ACLauncher.Services;
using ACLauncher.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ACLauncher.Views;

public partial class ServersWindow : Window
{
    public ServersWindow() => InitializeComponent();

    public ServersWindow(ServersViewModel viewModel) : this() => DataContext = viewModel;

    private async void OnBrowseGameFolder(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ServerRowViewModel { IsUser: true } row) return;
        var path = await Dialogs.PickFolderAsync(this, "Choose the Asheron's Call folder for this server", row.GameDirectory);
        if (path is not null) row.GameDirectory = path;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
