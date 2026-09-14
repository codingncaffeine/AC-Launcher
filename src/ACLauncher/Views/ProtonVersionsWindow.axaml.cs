using ACLauncher.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ACLauncher.Views;

/// <summary>Closes with the chosen Proton value, or null.</summary>
public partial class ProtonVersionsWindow : Window
{
    public ProtonVersionsWindow() => InitializeComponent();

    public ProtonVersionsWindow(ProtonVersionsViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.Chosen += value => Close(value);
        Opened += async (_, _) => await viewModel.InitializeAsync();
        Closing += (_, _) =>
        {
            if (viewModel.IsDownloading) viewModel.CancelDownloadCommand.Execute(null);
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(null);
}
