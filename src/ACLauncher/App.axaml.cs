using ACLauncher.Core;
using ACLauncher.Services;
using ACLauncher.ViewModels;
using ACLauncher.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ACLauncher;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppPaths.EnsurePrivateDirectories();
            Log.Open(AppPaths.LogDir);
            Log.Info($"AC Launcher {typeof(App).Assembly.GetName().Version?.ToString(3)} starting (settings in {AppPaths.ConfigDir})");

            var state = LauncherState.Load();
            var viewModel = new MainViewModel(state);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) =>
            {
                state.Dispose();
                Log.Info("AC Launcher exiting");
                Log.Close();
            };
            viewModel.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
