using ACLauncher.Core;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ACLauncher.Views;

/// <summary>Adds or edits an account. Closes with the account, or null when cancelled.</summary>
public partial class AccountDialog : Window
{
    private readonly Account? _existing;

    public AccountDialog() : this(null)
    {
    }

    public AccountDialog(Account? existing)
    {
        InitializeComponent();
        _existing = existing;
        Title = existing is null ? "Add account" : "Edit account";
        if (existing is not null)
        {
            UsernameBox.Text = existing.Username;
            PasswordBox.Text = existing.Password;
            AliasBox.Text = existing.Alias;
        }
        Opened += (_, _) => UsernameBox.Focus();
    }

    private void OnShowPasswordChanged(object? sender, RoutedEventArgs e) =>
        PasswordBox.RevealPassword = ShowPassword.IsChecked == true;

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text?.Trim() ?? "";
        var password = PasswordBox.Text ?? "";
        var alias = string.IsNullOrWhiteSpace(AliasBox.Text) ? null : AliasBox.Text.Trim();

        var error = username.Length == 0 ? "Enter the account's user name."
            : username.Any(char.IsWhiteSpace) || password.Any(char.IsWhiteSpace) ? "The game client cannot accept spaces in a user name or password."
            : null;
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
            return;
        }

        if (_existing is not null)
        {
            _existing.Username = username;
            _existing.Password = password;
            _existing.Alias = alias;
            Close(_existing);
        }
        else
        {
            Close(new Account { Username = username, Password = password, Alias = alias });
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
