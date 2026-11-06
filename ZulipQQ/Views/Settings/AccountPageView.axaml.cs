using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.Views.Settings;

public partial class AccountPageView : UserControl
{
    public AccountPageView()
    {
        InitializeComponent();
        AvatarButton.Click += async (_, _) =>
        {
            if (DataContext is not AccountPageViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
                return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择头像",
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.ImageAll],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
                await vm.ChangeAvatarAsync(path);
        };
    }

    // 查看头像（大图）
    private void OnAvatarTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is AccountPageViewModel { AvatarUrl.Length: > 0 } vm)
            ImageViewerWindow.ShowAvatar(TopLevel.GetTopLevel(this) as Window, vm.Session, vm.AvatarUrl!);
    }
}
