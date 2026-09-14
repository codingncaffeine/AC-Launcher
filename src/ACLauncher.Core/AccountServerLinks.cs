namespace ACLauncher.Core;

/// <summary>Which servers an account launches on. Both the main window and the Servers window change links here.</summary>
public static class AccountServerLinks
{
    public static bool IsSelected(Account account, Guid serverId) =>
        account.Servers.Any(l => l.ServerId == serverId && l.Selected);

    /// <returns>True if the account changed and needs saving.</returns>
    public static bool SetSelected(Account account, Guid serverId, bool selected)
    {
        var changed = false;
        var found = false;
        // Every link to the server is updated, so a duplicate left by hand-editing cannot keep a server ticked.
        foreach (var link in account.Servers.Where(l => l.ServerId == serverId))
        {
            found = true;
            if (link.Selected == selected) continue;
            link.Selected = selected;
            changed = true;
        }
        if (!found && selected)
        {
            account.Servers.Add(new AccountServer { ServerId = serverId, Selected = true });
            changed = true;
        }
        return changed;
    }
}
