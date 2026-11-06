using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.Views.Settings;

public partial class FilesPageView : UserControl
{
    public FilesPageView()
    {
        InitializeComponent();
        ChangeSaveButton.Click += async (_, _) =>
        {
            if (DataContext is FilesPageViewModel vm && await PickFolderAsync("选择文件保存位置", vm.SaveRoot) is { } path)
                vm.SetSaveRoot(path);
        };
        ChangeDataButton.Click += async (_, _) =>
        {
            if (DataContext is FilesPageViewModel vm && await PickFolderAsync("选择聊天信息保存位置", vm.DataRoot) is { } path)
                await vm.MoveDataRootAsync(path);
        };
    }

    private async Task<string?> PickFolderAsync(string title, string start)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(start),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
