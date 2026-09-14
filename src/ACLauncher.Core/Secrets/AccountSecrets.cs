namespace ACLauncher.Core.Secrets;

/// <summary>
/// Keeps account passwords in the desktop keyring when one is available, and in the owner-only
/// <c>accounts.json</c> otherwise.
/// </summary>
public sealed class AccountSecrets(ISecretStore store)
{
    private bool? _available;

    public string StoreName => store.Name;

    public async Task<bool> IsKeyringAvailableAsync(CancellationToken cancellationToken)
    {
        _available ??= await store.IsAvailableAsync(cancellationToken).ConfigureAwait(false);
        return _available.Value;
    }

    /// <summary>The password to launch with, or to show when editing the account.</summary>
    /// <exception cref="SecretStoreException">The keyring holds no password for the account, or cannot be read.</exception>
    public async Task<string> GetPasswordAsync(Account account, CancellationToken cancellationToken)
    {
        if (!account.PasswordInKeyring) return account.Password;
        return await store.LookupAsync(account.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new SecretStoreException($"{char.ToUpperInvariant(store.Name[0])}{store.Name[1..]} has no password for {account.DisplayName}. Edit the account and enter it again.");
    }

    /// <summary>
    /// Stores a new password: in the keyring when possible (and the account keeps none itself), otherwise in the
    /// account. The caller saves <c>accounts.json</c> afterwards either way.
    /// </summary>
    public async Task SetPasswordAsync(Account account, string password, CancellationToken cancellationToken)
    {
        if (password.Length > 0 && await IsKeyringAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await store.StoreAsync(account.Id, Label(account), password, cancellationToken).ConfigureAwait(false);
                account.Password = "";
                account.PasswordInKeyring = true;
                return;
            }
            catch (SecretStoreException e)
            {
                Log.Warn($"{e.Message}; the password for {account.DisplayName} is kept in accounts.json instead");
            }
        }
        if (account.PasswordInKeyring) await RemoveAsync(account, cancellationToken).ConfigureAwait(false);
        account.Password = password;
        account.PasswordInKeyring = false;
    }

    /// <summary>Removes the account's password from the keyring, if it is there.</summary>
    public async Task RemoveAsync(Account account, CancellationToken cancellationToken)
    {
        if (!account.PasswordInKeyring) return;
        try
        {
            await store.ClearAsync(account.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (SecretStoreException e)
        {
            Log.Warn($"{e.Message} (account {account.DisplayName})");
        }
    }

    /// <summary>Moves passwords still kept in <c>accounts.json</c> into the keyring.</summary>
    /// <returns>True when any account changed and <c>accounts.json</c> needs saving.</returns>
    public async Task<bool> MigrateAsync(IEnumerable<Account> accounts, CancellationToken cancellationToken)
    {
        var pending = accounts.Where(a => !a.PasswordInKeyring && a.Password.Length > 0).ToList();
        if (pending.Count == 0 || !await IsKeyringAvailableAsync(cancellationToken).ConfigureAwait(false)) return false;

        var changed = false;
        foreach (var account in pending)
        {
            try
            {
                // Store first: if anything fails after this, the password is still in accounts.json.
                await store.StoreAsync(account.Id, Label(account), account.Password, cancellationToken).ConfigureAwait(false);
                account.Password = "";
                account.PasswordInKeyring = true;
                changed = true;
                Log.Info($"Moved the password for {account.DisplayName} into {store.Name}");
            }
            catch (SecretStoreException e)
            {
                Log.Warn($"{e.Message}; passwords stay in accounts.json");
                break;
            }
        }
        return changed;
    }

    private static string Label(Account account) => $"AC Launcher: {account.Username}";
}
