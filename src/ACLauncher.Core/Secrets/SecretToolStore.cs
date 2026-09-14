using System.Diagnostics;
using System.Globalization;
using ACLauncher.Core.Launching;

namespace ACLauncher.Core.Secrets;

public sealed class SecretStoreException(string message) : Exception(message);

/// <summary>Somewhere to keep account passwords outside the launcher's own files.</summary>
public interface ISecretStore
{
    /// <summary>How to name the store to the user, e.g. "the desktop keyring".</summary>
    string Name { get; }

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

    /// <exception cref="SecretStoreException">The secret could not be stored.</exception>
    Task StoreAsync(Guid accountId, string label, string secret, CancellationToken cancellationToken);

    /// <returns>The secret, or null when none is stored for the account.</returns>
    /// <exception cref="SecretStoreException">The store could not be read.</exception>
    Task<string?> LookupAsync(Guid accountId, CancellationToken cancellationToken);

    /// <exception cref="SecretStoreException">The secret could not be removed.</exception>
    Task ClearAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>
/// The desktop keyring (GNOME Keyring, KWallet, KeePassXC — anything implementing the freedesktop Secret Service),
/// reached through libsecret's <c>secret-tool</c>. Secrets travel over standard input, never the command line.
/// </summary>
public sealed class SecretToolStore(string? secretToolPath = null) : ISecretStore
{
    private const string Application = "ac-launcher";

    /// <summary>Long enough for the user to answer a keyring unlock prompt.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(2);

    public string Name => "the desktop keyring";

    private string? ToolPath => secretToolPath ?? Umu.FindOnPath("secret-tool");

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (ToolPath is null) return false;
        try
        {
            // A clean miss exits 1 and prints nothing; a missing or broken Secret Service prints an error.
            var result = await RunAsync(["lookup", "application", Application, "account", "availability-check"], null, cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode is 0 or 1 && string.IsNullOrWhiteSpace(result.Error);
        }
        catch (SecretStoreException)
        {
            return false;
        }
    }

    public async Task StoreAsync(Guid accountId, string label, string secret, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["store", "--label", label, "application", Application, "account", Key(accountId)], secret, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new SecretStoreException(Describe("store the password", result));
    }

    public async Task<string?> LookupAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["lookup", "application", Application, "account", Key(accountId)], null, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode == 0) return result.Output.TrimEnd('\n');
        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Error)) return null;
        throw new SecretStoreException(Describe("read the password", result));
    }

    public async Task ClearAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["clear", "application", Application, "account", Key(accountId)], null, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0 && !(result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Error)))
            throw new SecretStoreException(Describe("remove the password", result));
    }

    private static string Key(Guid accountId) => accountId.ToString("D", CultureInfo.InvariantCulture);

    private static string Describe(string action, (int ExitCode, string Output, string Error) result) =>
        $"The keyring could not {action} (secret-tool exited {result.ExitCode}{(string.IsNullOrWhiteSpace(result.Error) ? "" : ": " + result.Error.Trim())})";

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(IReadOnlyList<string> arguments, string? input,
        CancellationToken cancellationToken)
    {
        var tool = ToolPath ?? throw new SecretStoreException("secret-tool is not installed.");
        var startInfo = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new SecretStoreException($"Could not run secret-tool: {e.Message}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new SecretStoreException("The keyring did not answer in time.");
        }
    }
}
