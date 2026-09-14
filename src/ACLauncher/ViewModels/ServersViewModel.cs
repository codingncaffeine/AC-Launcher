using System.Collections.ObjectModel;
using ACLauncher.Core;
using ACLauncher.Core.Servers;
using ACLauncher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ACLauncher.ViewModels;

public sealed partial class ServersViewModel : ObservableObject
{
    private readonly LauncherState _state;

    public ServersViewModel(LauncherState state)
    {
        _state = state;
        Rebuild();
    }

    public ObservableCollection<ServerRowViewModel> Servers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    public partial ServerRowViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsRefreshing { get; set; }

    public bool HasSelection => Selected is not null;

    partial void OnFilterChanged(string value) => Rebuild();

    private void Rebuild(Guid? select = null)
    {
        var selectedId = select ?? Selected?.Server.Id;
        Servers.Clear();
        foreach (var server in _state.AllServers
                     .Where(s => string.IsNullOrWhiteSpace(Filter) || s.Name.Contains(Filter.Trim(), StringComparison.CurrentCultureIgnoreCase))
                     .OrderBy(s => s.Source)
                     .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            Servers.Add(new ServerRowViewModel(_state, server));
        Selected = Servers.FirstOrDefault(s => s.Server.Id == selectedId);
        var published = _state.PublishedServers.Count;
        StatusText = $"{_state.UserServers.Count} of your servers, {published} from published lists";
    }

    [RelayCommand]
    private void Add()
    {
        var server = new Server { Name = "New server", Address = "", Emulator = EmulatorType.ACE, Source = ServerSource.User };
        _state.UserServers.Add(server);
        _state.SaveServers();
        Filter = "";
        Rebuild(server.Id);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy()
    {
        if (Selected is null) return;
        var source = Selected.Server;
        var server = new Server
        {
            Name = source.Name + " (copy)",
            Description = source.Description,
            Address = source.Address,
            Emulator = source.Emulator,
            Rodat = source.Rodat,
            DiscordUrl = source.DiscordUrl,
            WebsiteUrl = source.WebsiteUrl,
            Type = source.Type,
            GameDirectory = source.GameDirectory,
            Source = ServerSource.User,
        };
        _state.UserServers.Add(server);
        _state.SaveServers();
        Filter = "";
        Rebuild(server.Id);
    }

    private bool CanDelete() => Selected?.IsUser == true;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (Selected is not { IsUser: true } row) return;
        _state.UserServers.Remove(row.Server);
        _state.SaveServers();
        Rebuild();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        StatusText = "Downloading server lists…";
        await _state.RefreshServerListsAsync();
        IsRefreshing = false;
        Rebuild();
    }

    private bool CanRefresh() => !IsRefreshing;
}

/// <summary>One server in the editor. Published servers are read-only apart from hiding them.</summary>
public sealed partial class ServerRowViewModel(LauncherState state, Server server) : ObservableObject
{
    public static IReadOnlyList<EmulatorType> EmulatorOptions { get; } = Enum.GetValues<EmulatorType>();

    public Server Server { get; } = server;
    public bool IsUser => Server.Source == ServerSource.User;
    public bool IsReadOnly => !IsUser;
    public string SourceLabel => IsUser ? "Your server" : $"From the {Server.ListName} list (read-only — use Copy to customise)";

    public string Name
    {
        get => Server.Name;
        set => Edit(Server.Name, value, v => Server.Name = v);
    }

    public string Description
    {
        get => Server.Description;
        set => Edit(Server.Description, value, v => Server.Description = v);
    }

    public string Address
    {
        get => Server.Address;
        set
        {
            if (Edit(Server.Address, value, v => Server.Address = v.Trim())) OnPropertyChanged(nameof(AddressError));
        }
    }

    public string AddressError => ServerAddress.TryParse(Server.Address, out _) ? "" : "Enter the address as host:port, e.g. play.example.org:9000";

    public EmulatorType Emulator
    {
        get => Server.Emulator;
        set => Edit(Server.Emulator, value, v => Server.Emulator = v);
    }

    public bool Rodat
    {
        get => Server.Rodat;
        set => Edit(Server.Rodat, value, v => Server.Rodat = v);
    }

    public string? DiscordUrl
    {
        get => Server.DiscordUrl;
        set => Edit(Server.DiscordUrl, value, v => Server.DiscordUrl = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
    }

    public string? WebsiteUrl
    {
        get => Server.WebsiteUrl;
        set => Edit(Server.WebsiteUrl, value, v => Server.WebsiteUrl = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
    }

    public string? GameDirectory
    {
        get => Server.GameDirectory;
        set => Edit(Server.GameDirectory, value, v => Server.GameDirectory = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
    }

    public bool Hidden
    {
        get => state.Settings.HiddenServers.Contains(Server.Id);
        set
        {
            if (value == Hidden) return;
            if (value) state.Settings.HiddenServers.Add(Server.Id);
            else state.Settings.HiddenServers.Remove(Server.Id);
            state.SaveSettings();
            OnPropertyChanged();
        }
    }

    private bool Edit<T>(T current, T value, Action<T> apply, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (!IsUser || EqualityComparer<T>.Default.Equals(current, value)) return false;
        apply(value);
        state.SaveServers();
        OnPropertyChanged(property);
        return true;
    }
}
