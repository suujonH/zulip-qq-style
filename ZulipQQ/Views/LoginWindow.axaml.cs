using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
    }

    // 在密码框中按回车直接登录
    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not LoginViewModel vm || !vm.LoginCommand.CanExecute(null))
            return;
        e.Handled = true;
        vm.LoginCommand.Execute(null);
    }

    private void OnAccountPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: SavedAccountViewModel account } || DataContext is not LoginViewModel vm)
            return;
        vm.SelectAccountCommand.Execute(account);
        AccountsButton.Flyout?.Hide();
    }

    private void OnRemoveAccountClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: SavedAccountViewModel account } && DataContext is LoginViewModel vm)
            vm.RemoveAccountCommand.Execute(account);
        if (DataContext is LoginViewModel { HasAccounts: false })
            AccountsButton.Flyout?.Hide();
    }

    private async void OnZuliprcClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoginViewModel vm)
            return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 zuliprc 文件",
            AllowMultiple = false,
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await vm.LoginWithZuliprcAsync(path);
    }
}
