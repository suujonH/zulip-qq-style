using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.Views.Settings;

public partial class FilesPageView : UserControl
{
    public FilesPageView()
    {
        InitializeComponent();
        ChangeButton.Click += async (_, _) =>
        {
            if (DataContext is not FilesPageViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
                return;
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择文件保存位置",
                SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(vm.DownloadRoot),
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
                vm.SetDownloadRoot(path);
        };
    }
}
