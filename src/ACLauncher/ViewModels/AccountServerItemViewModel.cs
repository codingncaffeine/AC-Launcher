using ACLauncher.Core;
using ACLauncher.Core.Servers;
using ACLauncher.Services;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ACLauncher.ViewModels;

/// <summary>One server row under an account.</summary>
public sealed partial class AccountServerItemViewModel : ObservableObject
{
    private static readonly IBrush UpBrush = new SolidColorBrush(Color.Parse("#9CCC65"));
    private static readonly IBrush DownBrush = new SolidColorBrush(Color.Parse("#E57373"));
    private static readonly IBrush UnknownBrush = new SolidColorBrush(Color.Parse("#8A7B63"));

    private readonly LauncherState _state;
    private readonly AccountItemViewModel _owner;
    private readonly bool _loading;

    public AccountServerItemViewModel(LauncherState state, AccountItemViewModel owner, Server server)
    {
        _state = state;
        _owner = owner;
        Server = server;
        _loading = true;
        Selected = owner.Account.Servers.Any(l => l.ServerId == server.Id && l.Selected);
        _loading = false;
        RefreshStatus();
        RefreshRunning();
    }

    public Server Server { get; }
    public string Name => Server.Name;
    public string Emulator => Server.Emulator.ToString();
    public bool HasDiscord => SafeLinks.TryGetWebLink(Server.DiscordUrl, out _);
    public bool HasWebsite => SafeLinks.TryGetWebLink(Server.WebsiteUrl, out _);

    public string Detail
    {
        get
        {
            var parts = new List<string> { Server.Address };
            if (!string.IsNullOrWhiteSpace(Server.Type)) parts.Add(Server.Type!);
            if (!string.IsNullOrWhiteSpace(Server.Description)) parts.Add(Server.Description);
            return string.Join("  ·  ", parts);
        }
    }

    [ObservableProperty]
    public partial bool Selected { get; set; }

    [ObservableProperty]
    public partial IBrush StatusBrush { get; set; } = UnknownBrush;

    [ObservableProperty]
    public partial string StatusTip { get; set; } = "Not checked yet";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsRunning { get; set; }

    partial void OnSelectedChanged(bool value)
    {
        if (_loading) return;
        var link = _owner.Account.Servers.FirstOrDefault(l => l.ServerId == Server.Id);
        if (link is null)
        {
            link = new AccountServer { ServerId = Server.Id };
            _owner.Account.Servers.Add(link);
        }
        link.Selected = value;
        _state.SaveAccounts();
        _owner.SelectionChanged();
    }

    public void RefreshStatus()
    {
        var status = _state.StatusOf(Server.Id);
        (StatusBrush, StatusTip) = status.State switch
        {
            ServerUpState.Up => (UpBrush, status.Latency is { } latency ? $"Up — replied in {latency.TotalMilliseconds:F0} ms" : "Up"),
            ServerUpState.Down => (DownBrush, $"Down — {status.Detail ?? "no reply"}"),
            _ => (UnknownBrush, _state.Settings.CheckServerStatus ? "Checking…" : "Status checks are off"),
        };
    }

    public void RefreshRunning() => IsRunning = _state.Games.IsRunning(_owner.Account.Id, Server.Id);

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        foreach (var session in _state.Games.Sessions.Where(s => s.Target.Account.Id == _owner.Account.Id && s.Target.Server.Id == Server.Id))
            session.Stop();
    }

    [RelayCommand]
    private void OpenDiscord()
    {
        Dialogs.OpenWebLink(Server.DiscordUrl);
    }

    [RelayCommand]
    private void OpenWebsite()
    {
        Dialogs.OpenWebLink(Server.WebsiteUrl);
    }
}
