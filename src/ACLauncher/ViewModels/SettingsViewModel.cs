using System.Collections.ObjectModel;
using ACLauncher.Core;
using ACLauncher.Core.Launching;
using ACLauncher.Core.Proton;
using ACLauncher.Core.Steam;
using ACLauncher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ACLauncher.ViewModels;

public sealed record ProtonChoice(string Label, string Value);

/// <summary>Edits a copy of the settings; nothing is applied until <see cref="Apply"/>.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly LauncherSettings _settings;
    private readonly List<ProtonChoice> _elsewhere = [];
    private bool _choosingProton;

    public SettingsViewModel(LauncherState state)
    {
        State = state;
        _settings = state.CloneSettings();
        GameDirectory = _settings.GameDirectory;
        PrefixPath = _settings.PrefixPath ?? "";
        ProtonPath = _settings.ProtonPath;
        UmuRunPath = _settings.UmuRunPath ?? "";
        LaunchDelaySeconds = _settings.LaunchDelaySeconds;
        ForceWindowed = _settings.ForceWindowed;
        CheckServerStatus = _settings.CheckServerStatus;
        EnvironmentText = string.Join('\n', _settings.Environment.Select(kv => $"{kv.Key}={kv.Value}"));
        RefreshProtonChoices();
        RefreshUmuStatus();
    }

    public LauncherState State { get; }
    public string DefaultPrefix => AppPaths.DefaultPrefix;
    public ObservableCollection<ProtonChoice> ProtonChoices { get; } = [];

    [ObservableProperty]
    public partial string? GameDirectory { get; set; }

    [ObservableProperty]
    public partial string GameFolderMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool GameFolderValid { get; set; }

    [ObservableProperty]
    public partial string PrefixPath { get; set; }

    [ObservableProperty]
    public partial string ProtonPath { get; set; }

    [ObservableProperty]
    public partial ProtonChoice? SelectedProtonChoice { get; set; }

    [ObservableProperty]
    public partial string UmuRunPath { get; set; }

    [ObservableProperty]
    public partial string UmuStatus { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUmuCommand))]
    public partial bool IsInstallingUmu { get; set; }

    [ObservableProperty]
    public partial decimal LaunchDelaySeconds { get; set; }

    [ObservableProperty]
    public partial bool ForceWindowed { get; set; }

    [ObservableProperty]
    public partial bool CheckServerStatus { get; set; }

    [ObservableProperty]
    public partial string EnvironmentText { get; set; }

    [ObservableProperty]
    public partial string EnvironmentError { get; set; } = "";

    partial void OnGameDirectoryChanged(string? value)
    {
        var check = GameInstall.Check(value);
        GameFolderValid = check.IsValid;
        GameFolderMessage = check.Message;
    }

    partial void OnProtonPathChanged(string value)
    {
        if (_choosingProton) return;
        if (!ProtonChoices.Any(c => c.Value == value)) RefreshProtonChoices();
        else SyncProtonChoice();
    }

    partial void OnSelectedProtonChoiceChanged(ProtonChoice? value)
    {
        if (value is null) return;
        _choosingProton = true;
        ProtonPath = value.Value;
        _choosingProton = false;
    }

    partial void OnUmuRunPathChanged(string value) => RefreshUmuStatus();

    partial void OnEnvironmentTextChanged(string value) =>
        EnvironmentError = ParseEnvironment(value, out var environment)
            ?? (environment.ContainsKey("PROTON_LOG")
                ? "Note: PROTON_LOG makes Proton write the game's full command line, including the account password, to a log file in your home folder."
                : "");

    /// <summary>
    /// The choices: the two "latest" options, every version the launcher has installed, builds found elsewhere,
    /// and the current value if it is none of those.
    /// </summary>
    public void RefreshProtonChoices()
    {
        _choosingProton = true;
        ProtonChoices.Clear();
        ProtonChoices.Add(new ProtonChoice("Latest GE-Proton — installed and updated automatically", ProtonChoiceValue.LatestGE));
        ProtonChoices.Add(new ProtonChoice("Latest UMU-Proton (Valve's Proton) — installed and updated automatically", ProtonChoiceValue.LatestUMU));
        foreach (var build in ProtonInstaller.ListInstalled())
            ProtonChoices.Add(new ProtonChoice($"{build.Name}", build.Directory));
        foreach (var choice in _elsewhere.Where(e => ProtonChoices.All(c => c.Value != e.Value)))
            ProtonChoices.Add(choice);
        if (!string.IsNullOrWhiteSpace(ProtonPath) && ProtonChoices.All(c => c.Value != ProtonPath))
            ProtonChoices.Add(new ProtonChoice($"{ProtonPath} (custom)", ProtonPath));
        _choosingProton = false;
        SyncProtonChoice();
    }

    private void SyncProtonChoice()
    {
        _choosingProton = true;
        SelectedProtonChoice = ProtonChoices.FirstOrDefault(c => c.Value == ProtonPath);
        _choosingProton = false;
    }

    /// <summary>Adds Proton builds already on disk (Steam libraries and compatibility-tool folders) to the choices.</summary>
    public async Task LoadInstalledProtonBuildsAsync()
    {
        var builds = await Task.Run(() => ProtonLocator.Find(SteamLocator.Find()));
        _elsewhere.Clear();
        foreach (var build in builds)
        {
            var version = string.IsNullOrWhiteSpace(build.Version) ? "" : $" ({build.Version})";
            _elsewhere.Add(new ProtonChoice($"{build.DisplayName}{version} — already on this computer", build.Directory));
        }
        RefreshProtonChoices();
    }

    private void RefreshUmuStatus()
    {
        var explicitPath = string.IsNullOrWhiteSpace(UmuRunPath) ? null : UmuRunPath.Trim();
        var found = Umu.Find(explicitPath);
        UmuStatus = found switch
        {
            null when explicitPath is not null => $"Not found: {explicitPath}",
            null when !Umu.HasPython => "Not installed, and python3 is missing. Install umu-launcher from your distribution (e.g. pacman -S umu-launcher).",
            null => "Not installed. It will be downloaded on the first launch, or use Install now.",
            _ when found == Umu.ManagedRunPath => $"Using the launcher's own copy: {found}",
            _ => $"Using {found}",
        };
    }

    private bool CanInstallUmu() => !IsInstallingUmu;

    [RelayCommand(CanExecute = nameof(CanInstallUmu))]
    private async Task InstallUmuAsync()
    {
        IsInstallingUmu = true;
        try
        {
            var progress = new Progress<string>(text => UmuStatus = text);
            await Umu.InstallManagedAsync(State.Http, progress, State.ShutdownToken);
            RefreshUmuStatus();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Error("Installing umu-launcher failed", e);
            UmuStatus = $"Install failed: {e.Message}";
        }
        finally
        {
            IsInstallingUmu = false;
        }
    }

    /// <summary>Returns the edited settings, or null with <see cref="EnvironmentError"/> set when something is invalid.</summary>
    public LauncherSettings? Apply()
    {
        var error = ParseEnvironment(EnvironmentText, out var environment);
        if (error is not null)
        {
            EnvironmentError = error;
            return null;
        }
        _settings.GameDirectory = string.IsNullOrWhiteSpace(GameDirectory) ? null : GameDirectory.Trim();
        _settings.PrefixPath = string.IsNullOrWhiteSpace(PrefixPath) ? null : PrefixPath.Trim();
        _settings.ProtonPath = ProtonPath.Trim();
        _settings.UmuRunPath = string.IsNullOrWhiteSpace(UmuRunPath) ? null : UmuRunPath.Trim();
        _settings.LaunchDelaySeconds = (int)Math.Clamp(LaunchDelaySeconds, 0, 600);
        _settings.ForceWindowed = ForceWindowed;
        _settings.CheckServerStatus = CheckServerStatus;
        _settings.Environment = environment;
        return _settings;
    }

    /// <summary>Runs winecfg in the prefix with the current (unsaved) settings.</summary>
    [RelayCommand]
    private async Task RunWinecfgAsync()
    {
        var umu = Umu.Find(string.IsNullOrWhiteSpace(UmuRunPath) ? null : UmuRunPath.Trim());
        if (umu is null)
        {
            UmuStatus = "Install umu-launcher first.";
            return;
        }
        var proton = ProtonChoiceValue.LatestFamily(ProtonPath) is { } family ? ProtonInstaller.NewestInstalled(family) : ProtonPath.Trim();
        if (proton is null)
        {
            UmuStatus = "No version of that Proton build is installed yet. Launch once, or install one under Manage versions.";
            return;
        }
        ParseEnvironment(EnvironmentText, out var environment);
        var prefix = string.IsNullOrWhiteSpace(PrefixPath) ? AppPaths.DefaultPrefix : PrefixPath.Trim();
        var launch = new LaunchEnvironment(umu, prefix, proton, GameDirectory, environment);
        try
        {
            await GameManager.RunToolAsync(launch, "winecfg", [], State.ShutdownToken);
        }
        catch (Exception e) when (e is LaunchException or OperationCanceledException)
        {
            Log.Error("winecfg failed", e);
        }
    }

    [RelayCommand]
    private void OpenPrefixFolder()
    {
        var prefix = string.IsNullOrWhiteSpace(PrefixPath) ? AppPaths.DefaultPrefix : PrefixPath.Trim();
        if (Directory.Exists(prefix)) Dialogs.OpenFolder(prefix);
        else UmuStatus = "The prefix has not been created yet; it is set up on the first launch.";
    }

    internal static string? ParseEnvironment(string text, out Dictionary<string, string> environment)
    {
        environment = [];
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var equals = line.IndexOf('=');
            if (equals <= 0) return $"Line {lineNumber}: expected NAME=value";
            var name = line[..equals].Trim();
            if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || char.IsAsciiDigit(name[0]))
                return $"Line {lineNumber}: '{name}' is not a valid variable name";
            environment[name] = line[(equals + 1)..];
        }
        return null;
    }
}
