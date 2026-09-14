using System.Collections.ObjectModel;
using ACLauncher.Core;
using ACLauncher.Core.Proton;
using ACLauncher.Core.Steam;
using ACLauncher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ACLauncher.ViewModels;

/// <summary>Lists the published versions of a Proton build and installs, removes or chooses them.</summary>
public sealed partial class ProtonVersionsViewModel : ObservableObject
{
    private readonly LauncherState _state;
    private readonly string _current;
    private CancellationTokenSource? _download;
    private IReadOnlyList<ProtonRelease> _releases = [];
    private bool _initialized;

    public ProtonVersionsViewModel(LauncherState state, string currentValue)
    {
        _state = state;
        _current = currentValue;
        var family = ProtonChoiceValue.LatestFamily(currentValue)
                     ?? ProtonFamilyInfo.FromName(Path.GetFileName(currentValue.TrimEnd('/')))
                     ?? ProtonFamily.GEProton;
        SelectedFamily = ProtonFamilyInfo.Get(family);
    }

    /// <summary>Raised with a Proton directory or a <c>latest:</c> value when the user picks one.</summary>
    public event Action<string>? Chosen;

    public IReadOnlyList<ProtonFamilyInfo> Families => ProtonFamilyInfo.All;
    public ObservableCollection<ProtonVersionRow> Versions { get; } = [];
    public ObservableCollection<ProtonVersionRow> Elsewhere { get; } = [];

    [ObservableProperty]
    public partial ProtonFamilyInfo SelectedFamily { get; set; }

    [ObservableProperty]
    public partial bool ShowPrereleases { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelDownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial bool ProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial bool HasElsewhere { get; set; }

    public string UseLatestLabel => $"Always use the latest {SelectedFamily.Name}";

    public async Task InitializeAsync()
    {
        _initialized = true;
        await LoadAsync(force: false);
        var found = await Task.Run(() => ProtonLocator.Find(SteamLocator.Find()));
        foreach (var build in found)
        {
            Elsewhere.Add(new ProtonVersionRow(this)
            {
                Name = build.DisplayName,
                Detail = string.IsNullOrWhiteSpace(build.Version) ? build.Directory : $"{build.Version}  ·  {build.Directory}",
                Directory = build.Directory,
                External = true,
                IsInstalled = true,
                IsInUse = SamePath(build.Directory, _current),
            });
        }
        HasElsewhere = Elsewhere.Count > 0;
    }

    partial void OnSelectedFamilyChanged(ProtonFamilyInfo value)
    {
        OnPropertyChanged(nameof(UseLatestLabel));
        if (_initialized) _ = LoadAsync(force: false);
    }

    partial void OnShowPrereleasesChanged(bool value) => BuildRows();

    partial void OnIsDownloadingChanged(bool value)
    {
        foreach (var row in Versions) row.RefreshCommands();
    }

    private async Task LoadAsync(bool force)
    {
        var family = SelectedFamily.Family;
        StatusText = $"Loading {SelectedFamily.Name} releases…";
        Versions.Clear();
        try
        {
            _releases = await ProtonReleases.GetAsync(_state.Http, family, AppPaths.CacheDir, force, _state.ShutdownToken);
            StatusText = "";
        }
        catch (ProtonDownloadException e)
        {
            _releases = [];
            StatusText = e.Message;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (SelectedFamily.Family == family) BuildRows();
    }

    private void BuildRows()
    {
        var family = SelectedFamily.Family;
        var installed = ProtonInstaller.ListInstalled().Where(p => p.Family == family).ToList();
        var releases = _releases.Where(r => r.Family == family).ToList();
        var latestTag = releases.FirstOrDefault(r => !r.Prerelease && r.IsVerifiable)?.Tag;

        Versions.Clear();
        foreach (var release in releases.Where(r => ShowPrereleases || !r.Prerelease))
        {
            var directory = installed.FirstOrDefault(i => i.Name == release.Tag)?.Directory;
            Versions.Add(new ProtonVersionRow(this)
            {
                Name = release.Tag,
                Release = release,
                Detail = $"Released {release.Published.LocalDateTime:yyyy-MM-dd}  ·  {release.ArchiveSize / (1024 * 1024)} MB download" +
                         (release.IsVerifiable ? "" : "  ·  no published checksum, so it cannot be installed"),
                IsLatest = release.Tag == latestTag,
                Prerelease = release.Prerelease,
                Directory = directory,
                IsInstalled = directory is not null,
                IsInUse = directory is not null && SamePath(directory, _current),
            });
        }
        // Installed builds that are no longer in the published list.
        foreach (var build in installed.Where(i => releases.All(r => r.Tag != i.Name)))
        {
            Versions.Add(new ProtonVersionRow(this)
            {
                Name = build.Name,
                Detail = "Installed",
                Directory = build.Directory,
                IsInstalled = true,
                IsInUse = SamePath(build.Directory, _current),
            });
        }
    }

    private bool CanRefresh() => !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync(force: true);

    [RelayCommand]
    private void UseLatest() =>
        Chosen?.Invoke(SelectedFamily.Family == ProtonFamily.GEProton ? ProtonChoiceValue.LatestGE : ProtonChoiceValue.LatestUMU);

    [RelayCommand(CanExecute = nameof(IsDownloading))]
    private void CancelDownload() => _download?.Cancel();

    internal async Task InstallAsync(ProtonVersionRow row)
    {
        if (row.Release is null || IsDownloading) return;
        _download = new CancellationTokenSource();
        IsDownloading = true;
        ProgressValue = 0;
        ProgressIndeterminate = true;
        var progress = new Progress<ProtonInstallProgress>(p =>
        {
            StatusText = FormatProgress(p);
            if (p.BytesTotal is > 0)
            {
                ProgressIndeterminate = false;
                ProgressValue = (double)p.BytesDone / p.BytesTotal.Value;
            }
            else
            {
                ProgressIndeterminate = true;
            }
        });
        try
        {
            row.Directory = await ProtonInstaller.InstallAsync(_state.Http, row.Release, progress, _download.Token);
            row.IsInstalled = true;
            StatusText = $"Installed {row.Name}.";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Cancelled installing {row.Name}.";
        }
        catch (Exception e) when (e is ProtonDownloadException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            Log.Error($"Installing {row.Name} failed", e);
            StatusText = $"Installing {row.Name} failed: {e.Message}";
        }
        finally
        {
            _download.Dispose();
            _download = null;
            IsDownloading = false;
        }
    }

    internal void Remove(ProtonVersionRow row)
    {
        if (!row.CanRemove) return;
        try
        {
            ProtonInstaller.Remove(row.Name);
            row.Directory = null;
            row.IsInstalled = false;
            if (row.Release is null) Versions.Remove(row);
            StatusText = $"Removed {row.Name}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Removing {row.Name} failed", e);
            StatusText = $"Removing {row.Name} failed: {e.Message}";
        }
    }

    internal void Use(ProtonVersionRow row)
    {
        if (row.Directory is not null) Chosen?.Invoke(row.Directory);
    }

    internal static string FormatProgress(ProtonInstallProgress p) =>
        p.BytesTotal is { } total && total > 0
            ? $"{p.Message}: {p.BytesDone / (1024 * 1024)} of {total / (1024 * 1024)} MB"
            : p.Message;

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);
}

public sealed partial class ProtonVersionRow(ProtonVersionsViewModel owner) : ObservableObject
{
    public required string Name { get; init; }
    public string Detail { get; init; } = "";
    public ProtonRelease? Release { get; init; }
    public bool IsLatest { get; init; }
    public bool Prerelease { get; init; }

    /// <summary>A build installed by something else (Steam, a compatibility-tool folder); it can be used but not removed.</summary>
    public bool External { get; init; }

    public string? Directory { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    public partial bool IsInUse { get; set; }

    public bool CanRemove => IsInstalled && !External && !IsInUse;

    private bool CanInstall() => Release is { IsVerifiable: true } && !IsInstalled && !owner.IsDownloading;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync() => owner.InstallAsync(this);

    [RelayCommand]
    private void Use() => owner.Use(this);

    [RelayCommand]
    private void Remove() => owner.Remove(this);

    internal void RefreshCommands() => InstallCommand.NotifyCanExecuteChanged();
}
