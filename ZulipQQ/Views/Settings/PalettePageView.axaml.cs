using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.Views.Settings;

public partial class PalettePageView : UserControl
{
    public PalettePageView()
    {
        InitializeComponent();
        PickImageButton.Click += async (_, _) =>
        {
            if (DataContext is not PalettePageViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
                return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择背景图片",
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.ImageAll],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
                vm.SetBackground(path);
        };
    }
}
