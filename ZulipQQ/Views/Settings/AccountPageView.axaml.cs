using Avalonia.Controls;
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
                await vm.UploadAvatarAsync(path);
        };
    }
}
