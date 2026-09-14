using ACLauncher.Core;
using ACLauncher.Core.Secrets;

namespace ACLauncher.Tests;

public sealed class AccountSecretsTests
{
    private sealed class FakeStore : ISecretStore
    {
        public bool Available = true;
        public bool FailStore;
        public readonly Dictionary<Guid, string> Items = [];
        public int Clears;

        public string Name => "the fake keyring";
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(Available);

        public Task StoreAsync(Guid accountId, string label, string secret, CancellationToken cancellationToken)
        {
            if (FailStore) throw new SecretStoreException("store refused");
            Items[accountId] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> LookupAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.TryGetValue(accountId, out var s) ? s : null);

        public Task ClearAsync(Guid accountId, CancellationToken cancellationToken)
        {
            Clears++;
            Items.Remove(accountId);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task NewPasswordGoesToTheKeyringAndNotTheAccountsFile()
    {
        var store = new FakeStore();
        var secrets = new AccountSecrets(store);
        var account = new Account { Username = "player" };

        await secrets.SetPasswordAsync(account, "hunter2", CancellationToken.None);

        Assert.True(account.PasswordInKeyring);
        Assert.Equal("", account.Password);
        Assert.Equal("hunter2", store.Items[account.Id]);
        Assert.Equal("hunter2", await secrets.GetPasswordAsync(account, CancellationToken.None));
        Assert.DoesNotContain("hunter2", System.Text.Json.JsonSerializer.Serialize(account, JsonStore.Options));
    }

    [Fact]
    public async Task WithoutAKeyringThePasswordStaysInTheAccount()
    {
        var secrets = new AccountSecrets(new FakeStore { Available = false });
        var account = new Account { Username = "player" };

        await secrets.SetPasswordAsync(account, "hunter2", CancellationToken.None);

        Assert.False(account.PasswordInKeyring);
        Assert.Equal("hunter2", account.Password);
        Assert.Equal("hunter2", await secrets.GetPasswordAsync(account, CancellationToken.None));
    }

    [Fact]
    public async Task AKeyringThatRefusesFallsBackToTheAccount()
    {
        var secrets = new AccountSecrets(new FakeStore { FailStore = true });
        var account = new Account { Username = "player" };

        await secrets.SetPasswordAsync(account, "hunter2", CancellationToken.None);

        Assert.False(account.PasswordInKeyring);
        Assert.Equal("hunter2", account.Password);
    }

    [Fact]
    public async Task MovingBackToTheFileRemovesTheKeyringCopy()
    {
        var store = new FakeStore();
        var account = new Account { Username = "player" };
        await new AccountSecrets(store).SetPasswordAsync(account, "old", CancellationToken.None);

        store.Available = false;
        await new AccountSecrets(store).SetPasswordAsync(account, "new", CancellationToken.None);

        Assert.False(account.PasswordInKeyring);
        Assert.Equal("new", account.Password);
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task MigrationMovesFilePasswordsOnceAndReportsChanges()
    {
        var store = new FakeStore();
        var secrets = new AccountSecrets(store);
        var inFile = new Account { Username = "a", Password = "pa" };
        var empty = new Account { Username = "b", Password = "" };
        var already = new Account { Username = "c", PasswordInKeyring = true };
        store.Items[already.Id] = "pc";

        Assert.True(await secrets.MigrateAsync([inFile, empty, already], CancellationToken.None));
        Assert.False(await secrets.MigrateAsync([inFile, empty, already], CancellationToken.None));

        Assert.True(inFile.PasswordInKeyring);
        Assert.Equal("", inFile.Password);
        Assert.Equal("pa", store.Items[inFile.Id]);
        Assert.False(empty.PasswordInKeyring);
        Assert.Equal("pc", store.Items[already.Id]);
    }

    [Fact]
    public async Task MigrationThatCannotStoreLeavesPasswordsInTheFile()
    {
        var account = new Account { Username = "a", Password = "pa" };

        Assert.False(await new AccountSecrets(new FakeStore { FailStore = true }).MigrateAsync([account], CancellationToken.None));

        Assert.False(account.PasswordInKeyring);
        Assert.Equal("pa", account.Password);
    }

    [Fact]
    public async Task AMissingKeyringEntryIsReportedNotReturnedAsEmpty()
    {
        var secrets = new AccountSecrets(new FakeStore());
        var account = new Account { Username = "player", PasswordInKeyring = true };

        var e = await Assert.ThrowsAsync<SecretStoreException>(() => secrets.GetPasswordAsync(account, CancellationToken.None));
        Assert.Contains("player", e.Message);
    }

    [Fact]
    public async Task RemovingAnAccountClearsItsKeyringEntry()
    {
        var store = new FakeStore();
        var secrets = new AccountSecrets(store);
        var account = new Account { Username = "player" };
        await secrets.SetPasswordAsync(account, "hunter2", CancellationToken.None);

        await secrets.RemoveAsync(account, CancellationToken.None);

        Assert.Empty(store.Items);
        Assert.Equal(1, store.Clears);
    }
}
