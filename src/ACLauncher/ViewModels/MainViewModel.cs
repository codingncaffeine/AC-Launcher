using System.Collections.ObjectModel;
using ACLauncher.Core;
using ACLauncher.Core.Launching;
using ACLauncher.Core.Proton;
using ACLauncher.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ACLauncher.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly List<AccountItemViewModel> _allAccounts = [];
    private CancellationTokenSource? _launchCancel;
    private bool _loading;

    public MainViewModel(LauncherState state)
    {
        State = state;
        LoadSettings();
        RebuildAccounts();
        state.ServersChanged += () => Dispatcher.UIThread.Post(RebuildAccounts);
        state.StatusChanged += id => Dispatcher.UIThread.Post(() =>
        {
            foreach (var account in _allAccounts) account.RefreshStatus(id);
        });
        state.Games.SessionStarted += _ => Dispatcher.UIThread.Post(RefreshRunning);
        state.Games.SessionEnded += _ => Dispatcher.UIThread.Post(RefreshRunning);
    }

    public LauncherState State { get; }

    public ObservableCollection<AccountItemViewModel> VisibleAccounts { get; } = [];

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelLaunchCommand))]
    public partial bool IsLaunching { get; set; }

    [ObservableProperty]
    public partial string? GameDirectory { get; set; }

    [ObservableProperty]
    public partial string GameFolderMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool GameFolderValid { get; set; }

    [ObservableProperty]
    public partial bool ShowEnabledOnly { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedAccount))]
    public partial AccountItemViewModel? SelectedAccount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunning))]
    [NotifyCanExecuteChangedFor(nameof(StopAllCommand))]
    public partial int RunningCount { get; set; }

    public bool HasSelectedAccount => SelectedAccount is not null;
    public bool HasAccounts => _allAccounts.Count > 0;
    public bool HasRunning => RunningCount > 0;

    /// <summary>Starts the background work: refreshing server lists and checking server status.</summary>
    public void Start()
    {
        _ = State.RefreshServerListsAsync();
        _ = State.RunStatusLoopAsync();
    }

    /// <summary>Re-reads the values this view shows from the settings, e.g. after the settings window saved.</summary>
    public void LoadSettings()
    {
        _loading = true;
        GameDirectory = State.Settings.GameDirectory;
        ShowEnabledOnly = State.Settings.ShowEnabledAccountsOnly;
        _loading = false;
        ValidateGameFolder();
        ApplyFilter();
    }

    partial void OnGameDirectoryChanged(string? value)
    {
        if (_loading) return;
        State.Settings.GameDirectory = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        State.SaveSettings();
        ValidateGameFolder();
    }

    partial void OnShowEnabledOnlyChanged(bool value)
    {
        if (_loading) return;
        State.Settings.ShowEnabledAccountsOnly = value;
        State.SaveSettings();
        ApplyFilter();
    }

    private void ValidateGameFolder()
    {
        var check = GameInstall.Check(State.Settings.GameDirectory);
        GameFolderValid = check.IsValid;
        GameFolderMessage = check.Message;
    }

    public void RebuildAccounts()
    {
        var expanded = _allAccounts.Where(a => a.IsExpanded).Select(a => a.Account.Id).ToHashSet();
        var showAll = _allAccounts.Where(a => a.ShowAllServers).Select(a => a.Account.Id).ToHashSet();
        var known = _allAccounts.Select(a => a.Account.Id).ToHashSet();
        var selectedId = SelectedAccount?.Account.Id;

        _allAccounts.Clear();
        foreach (var account in State.Accounts)
        {
            bool? keepShowAll = known.Contains(account.Id) ? showAll.Contains(account.Id) : null;
            _allAccounts.Add(new AccountItemViewModel(State, account, expanded.Contains(account.Id), keepShowAll, OnAccountEnabledChanged));
        }
        ApplyFilter();
        SelectedAccount = VisibleAccounts.FirstOrDefault(a => a.Account.Id == selectedId);
        OnPropertyChanged(nameof(HasAccounts));
        RefreshRunning();
    }

    public void AddAccount(Account account)
    {
        State.Accounts.Add(account);
        State.SaveAccounts();
        RebuildAccounts();
        SelectedAccount = VisibleAccounts.FirstOrDefault(a => a.Account == account);
        if (SelectedAccount is not null) SelectedAccount.IsExpanded = true;
        StatusText = $"Added {account.DisplayName}. Tick the servers it should launch on.";
    }

    public void AccountEdited()
    {
        State.SaveAccounts();
        RebuildAccounts();
    }

    public void DeleteAccount(AccountItemViewModel item)
    {
        State.Accounts.Remove(item.Account);
        State.SaveAccounts();
        RebuildAccounts();
        StatusText = $"Deleted {item.DisplayName}";
    }

    private void OnAccountEnabledChanged()
    {
        if (ShowEnabledOnly) ApplyFilter();
    }

    private void ApplyFilter()
    {
        var selected = SelectedAccount;
        VisibleAccounts.Clear();
        foreach (var account in _allAccounts)
            if (!ShowEnabledOnly || account.Enabled) VisibleAccounts.Add(account);
        SelectedAccount = selected is not null && VisibleAccounts.Contains(selected) ? selected : null;
    }

    private void RefreshRunning()
    {
        RunningCount = State.Games.Sessions.Count;
        foreach (var account in _allAccounts) account.RefreshRunning();
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        var targets = (
            from account in State.Accounts
            where account.Enabled
            from link in account.Servers
            where link.Selected
            let server = State.FindServer(link.ServerId)
            where server is not null
            select new LaunchTarget(account, server)).ToList();
        if (targets.Count == 0)
        {
            StatusText = "Tick an account and at least one of its servers first.";
            return;
        }

        IsLaunching = true;
        _launchCancel = new CancellationTokenSource();
        var token = _launchCancel.Token;
        var progress = new Progress<string>(text => StatusText = text);
        try
        {
            var proton = await ResolveProtonAsync(progress, token);
            if (proton is null) return;
            var environment = await EnsureUmuAsync(proton, progress, token);
            if (environment is null) return;

            if (!environment.IsPrefixInitialized)
            {
                StatusText = "Setting up the Wine prefix. The first time, umu downloads the runtime and Proton (about 1 GB) — see Log.";
                var code = await GameManager.RunToolAsync(environment, "wineboot", ["-u"], token);
                if (!environment.IsPrefixInitialized)
                {
                    StatusText = $"Setting up the Wine prefix failed (exit code {code}). See Log for details.";
                    return;
                }
            }

            if (State.Settings.ForceWindowed)
            {
                var ini = ClientPreferences.PathFor(environment.PrefixPath);
                if (ClientPreferences.EnsureWindowed(ini)) Log.Info($"Set FullScreen=False in {ini}");
            }

            var delay = TimeSpan.FromSeconds(Math.Max(0, State.Settings.LaunchDelaySeconds));
            var started = await State.Games.LaunchAllAsync(environment, targets, delay, progress, token);
            if (started > 0) StatusText = started == 1 ? "Started 1 client" : $"Started {started} clients";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Launch cancelled";
        }
        catch (Exception e) when (e is LaunchException or ProtonDownloadException or HttpRequestException or IOException
                                      or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Error("Launch failed", e);
            StatusText = $"Launch failed: {e.Message}";
        }
        finally
        {
            IsLaunching = false;
            _launchCancel.Dispose();
            _launchCancel = null;
        }
    }

    private bool CanLaunch() => !IsLaunching;

    /// <summary>
    /// Turns the Proton setting into a directory. A "latest" choice checks for a newer release and installs it;
    /// offline, it falls back to the newest version already installed.
    /// </summary>
    private async Task<string?> ResolveProtonAsync(IProgress<string> progress, CancellationToken token)
    {
        var value = State.Settings.ProtonPath;
        if (ProtonChoiceValue.LatestFamily(value) is not { } family)
        {
            if (Directory.Exists(value) && File.Exists(Path.Combine(value, "proton"))) return value;
            if (!string.IsNullOrWhiteSpace(value) && !value.Contains('/')) return value; // a build name umu resolves itself
            StatusText = $"The Proton version chosen in Settings is missing: {value}";
            return null;
        }

        var info = ProtonFamilyInfo.Get(family);
        ProtonRelease? latest;
        try
        {
            var releases = await ProtonReleases.GetAsync(State.Http, family, AppPaths.CacheDir, forceRefresh: false, token);
            latest = releases.FirstOrDefault(r => !r.Prerelease);
        }
        catch (ProtonDownloadException e)
        {
            Log.Warn(e.Message);
            latest = null;
        }

        if (latest is null)
        {
            if (ProtonInstaller.NewestInstalled(family) is { } fallback) return fallback;
            StatusText = $"Could not find a {info.Name} release to install. Check your connection, or pick a version in Settings.";
            return null;
        }
        if (ProtonInstaller.FindInstalled(latest.Tag) is { } installed) return installed;

        Log.Info($"Installing {latest.Tag}, the latest {info.Name}");
        var installProgress = new Progress<ProtonInstallProgress>(p => progress.Report(ProtonVersionsViewModel.FormatProgress(p)));
        return await ProtonInstaller.InstallAsync(State.Http, latest, installProgress, token);
    }

    /// <summary>Finds umu-run, installing the launcher's own copy when the system has none.</summary>
    private async Task<LaunchEnvironment?> EnsureUmuAsync(string proton, IProgress<string> progress, CancellationToken token)
    {
        var environment = State.ResolveEnvironment(proton, out var problem);
        if (environment is not null) return environment;
        if (!string.IsNullOrWhiteSpace(State.Settings.UmuRunPath))
        {
            StatusText = problem!;
            return null;
        }
        if (!Umu.HasPython)
        {
            StatusText = "umu-launcher is not installed and python3 is missing. Install umu-launcher from your distribution (e.g. pacman -S umu-launcher).";
            return null;
        }
        await Umu.InstallManagedAsync(State.Http, progress, token);
        environment = State.ResolveEnvironment(proton, out problem);
        if (environment is null) StatusText = problem!;
        return environment;
    }

    [RelayCommand(CanExecute = nameof(IsLaunching))]
    private void CancelLaunch() => _launchCancel?.Cancel();

    [RelayCommand(CanExecute = nameof(HasRunning))]
    private void StopAll() => State.Games.StopAll();
}
