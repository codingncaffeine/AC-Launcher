using System.Collections.ObjectModel;
using ACLauncher.Core;
using ACLauncher.Core.Servers;
using ACLauncher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ACLauncher.ViewModels;

/// <summary>One account in the main list, with the servers it can launch on.</summary>
public sealed partial class AccountItemViewModel : ObservableObject
{
    private readonly LauncherState _state;
    private readonly Action _enabledChanged;
    private readonly List<AccountServerItemViewModel> _allServers = [];
    private bool _loading = true;

    /// <param name="showAllServers">Null picks a default: all servers until at least one is ticked.</param>
    public AccountItemViewModel(LauncherState state, Account account, bool expanded, bool? showAllServers, Action enabledChanged)
    {
        _state = state;
        _enabledChanged = enabledChanged;
        Account = account;
        Enabled = account.Enabled;
        IsExpanded = expanded;

        // Every visible server, plus any hidden one this account still launches on.
        var servers = state.AllServers
            .Where(s => !state.Settings.HiddenServers.Contains(s.Id) || account.Servers.Any(l => l.ServerId == s.Id && l.Selected))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var server in servers)
            _allServers.Add(new AccountServerItemViewModel(state, this, server));

        ShowAllServers = showAllServers ?? !_allServers.Any(s => s.Selected);
        _loading = false;
        ApplyServerFilter();
        UpdateSummary();
    }

    public Account Account { get; }
    public string DisplayName => Account.DisplayName;
    public ObservableCollection<AccountServerItemViewModel> ShownServers { get; } = [];

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool ShowAllServers { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial bool HasNoServers { get; set; }

    partial void OnEnabledChanged(bool value)
    {
        if (_loading) return;
        Account.Enabled = value;
        _state.SaveAccounts();
        _enabledChanged();
    }

    /// <summary>The account's server list shows its ticked servers; this switches to every server to pick from, and back.</summary>
    public string ServerListToggleLabel => ShowAllServers ? "Show only this account's servers" : "Add or remove servers";

    public string EmptyHint => ShowAllServers
        ? "No servers available yet. Refresh the lists or add a custom server under Servers."
        : "No servers ticked yet. Use Add or remove servers to pick one.";

    [RelayCommand]
    private void ToggleServerList() => ShowAllServers = !ShowAllServers;

    partial void OnShowAllServersChanged(bool value)
    {
        OnPropertyChanged(nameof(ServerListToggleLabel));
        OnPropertyChanged(nameof(EmptyHint));
        if (!_loading) ApplyServerFilter();
    }

    internal void SelectionChanged() => UpdateSummary();

    public void RefreshStatus(Guid serverId)
    {
        var changed = false;
        foreach (var server in _allServers.Where(s => s.Server.Id == serverId))
        {
            server.RefreshStatus();
            changed |= server.Selected;
        }
        if (changed) UpdateSummary();
    }

    public void RefreshRunning()
    {
        foreach (var server in _allServers) server.RefreshRunning();
        UpdateSummary();
    }

    private void ApplyServerFilter()
    {
        ShownServers.Clear();
        foreach (var server in _allServers)
            if (ShowAllServers || server.Selected) ShownServers.Add(server);
        HasNoServers = ShownServers.Count == 0;
    }

    private void UpdateSummary()
    {
        var selected = _allServers.Where(s => s.Selected).ToList();
        if (selected.Count == 0)
        {
            Summary = "no servers ticked";
            return;
        }
        Summary = string.Join(",  ", selected.Select(s =>
        {
            var state = _state.StatusOf(s.Server.Id).State switch
            {
                ServerUpState.Up => "up",
                ServerUpState.Down => "down",
                _ => "…",
            };
            return s.IsRunning ? $"{s.Name} ({state}, running)" : $"{s.Name} ({state})";
        }));
    }
}
