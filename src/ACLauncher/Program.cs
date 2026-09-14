using Avalonia;
using Avalonia.Media;

namespace ACLauncher;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // The bundled Inter font is the default family, so the UI looks the same everywhere and the app
    // still starts on a system with no fonts installed (Avalonia refuses to start without a default).
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .LogToTrace();
}
