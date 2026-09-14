using ACLauncher.Core;

namespace ACLauncher.Tests;

public sealed class AccountServerLinksTests
{
    private static readonly Guid ServerA = Guid.NewGuid();
    private static readonly Guid ServerB = Guid.NewGuid();

    [Fact]
    public void TickingANewServerAddsALink()
    {
        var account = new Account();

        Assert.True(AccountServerLinks.SetSelected(account, ServerA, true));

        Assert.True(AccountServerLinks.IsSelected(account, ServerA));
        Assert.False(AccountServerLinks.IsSelected(account, ServerB));
        Assert.Single(account.Servers);
    }

    [Fact]
    public void UntickingKeepsTheLinkButClearsIt()
    {
        var account = new Account { Servers = [new AccountServer { ServerId = ServerA, Selected = true }] };

        Assert.True(AccountServerLinks.SetSelected(account, ServerA, false));

        Assert.False(AccountServerLinks.IsSelected(account, ServerA));
        Assert.Single(account.Servers);
    }

    [Fact]
    public void UntickingAServerNeverLinkedChangesNothing()
    {
        var account = new Account();

        Assert.False(AccountServerLinks.SetSelected(account, ServerA, false));

        Assert.Empty(account.Servers);
    }

    [Fact]
    public void SettingTheSameStateReportsNoChange()
    {
        var account = new Account { Servers = [new AccountServer { ServerId = ServerA, Selected = true }] };

        Assert.False(AccountServerLinks.SetSelected(account, ServerA, true));
    }

    [Fact]
    public void DuplicateLinksAreAllUpdated()
    {
        var account = new Account
        {
            Servers =
            [
                new AccountServer { ServerId = ServerA, Selected = true },
                new AccountServer { ServerId = ServerA, Selected = true },
            ],
        };

        Assert.True(AccountServerLinks.SetSelected(account, ServerA, false));

        Assert.False(AccountServerLinks.IsSelected(account, ServerA));
    }
}
