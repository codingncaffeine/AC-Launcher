using ACLauncher.Core;
using ACLauncher.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ACLauncher.Views;

/// <summary>Adds or edits an account. Closes with an <see cref="AccountEdit"/>, or null when cancelled.</summary>
public partial class AccountDialog : Window
{
    public AccountDialog() : this(null, "")
    {
    }

    public AccountDialog(Account? existing, string currentPassword)
    {
        InitializeComponent();
        Title = existing is null ? "Add account" : "Edit account";
        if (existing is not null)
        {
            UsernameBox.Text = existing.Username;
            PasswordBox.Text = currentPassword;
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
        Close(new AccountEdit(username, password, alias));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
