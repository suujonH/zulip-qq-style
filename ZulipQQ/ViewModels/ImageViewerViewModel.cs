using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 图片查看器：显示一组图片中的一张原图（通过 <see cref="ImageCache"/> 加载，不缩小解码），可切换上一张 / 下一张，
/// 另存为（本组织上传的图片下载原文件，其它图片保存为 PNG）、复制到剪贴板。
/// </summary>
public partial class ImageViewerViewModel : ViewModelBase
{
    private readonly ZulipSession _session;
    private IReadOnlyList<InlineImage> _images = [];

    // 切换图片时丢弃过期的加载结果
    private int _version;

    // 提示显示在查看器自己的窗口中（主窗口可能被挡住）
    private int _toastVersion;

    public ImageViewerViewModel(ZulipSession session)
    {
        _session = session;
    }

    [ObservableProperty]
    public partial string? ToastText { get; private set; }

    private void ShowToast(string text)
    {
        var version = ++_toastVersion;
        ToastText = text;
        DispatcherTimer.RunOnce(() =>
        {
            if (version == _toastVersion)
                ToastText = null;
        }, TimeSpan.FromSeconds(2));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CounterText), nameof(HasPrevious), nameof(HasNext))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(NextCommand))]
    public partial int Index { get; private set; }

    public int Count => _images.Count;

    public string CounterText => $"{Index + 1} / {Count}";

    public bool HasPrevious => Index > 0;

    public bool HasNext => Index < Count - 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand), nameof(CopyCommand))]
    public partial Bitmap? Bitmap { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsFailed { get; private set; }

    private InlineImage? Current => Index >= 0 && Index < _images.Count ? _images[Index] : null;

    /// <summary>显示一组图片中的第 <paramref name="index"/> 张（再次打开查看器时替换原来的图片）。</summary>
    public void Show(IReadOnlyList<InlineImage> images, int index)
    {
        _images = images;
        OnPropertyChanged(nameof(Count));
        Index = Math.Clamp(index, 0, Math.Max(0, images.Count - 1));
        // Index 未变时上面不会通知
        OnPropertyChanged(nameof(CounterText));
        OnPropertyChanged(nameof(HasNext));
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        _ = LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    private void Previous()
    {
        Index--;
        _ = LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasNext))]
    private void Next()
    {
        Index++;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var version = ++_version;
        Bitmap = null;
        IsFailed = false;
        if (Current is not { } image)
            return;
        IsLoading = true;
        var bitmap = await ImageCache.Shared.GetAsync(image.Original, null);
        if (version != _version)
            return;
        IsLoading = false;
        Bitmap = bitmap;
        IsFailed = bitmap is null;
    }

    private bool HasBitmap => Bitmap is not null;

    /// <summary>另存为：本组织上传的图片下载原文件，其它图片（外链）保存为 PNG。</summary>
    [RelayCommand(CanExecute = nameof(HasBitmap))]
    private async Task SaveAsAsync(TopLevel? top)
    {
        if (top is null || Current is not { } image || Bitmap is not { } bitmap)
            return;
        if (ZulipHtml.NormalizeUploadPath(image.Href) is { } path)
        {
            if (!await _session.Files.SaveAsAsync(ZulipHtml.UploadLinkOf(path, null), top) && !_session.IsDisposed)
                ShowToast("保存失败");
            return;
        }
        try
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "另存为",
                SuggestedFileName = $"图片{DateTime.Now:yyyyMMddHHmmss}.png",
                DefaultExtension = "png",
                FileTypeChoices = [FilePickerFileTypes.ImagePng],
            });
            if (file is null)
                return;
            await using var stream = await file.OpenWriteAsync();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        }
        catch (Exception ex)
        {
            Log.Warn("保存图片失败", ex);
            ShowToast("保存失败");
        }
    }

    [RelayCommand(CanExecute = nameof(HasBitmap))]
    private async Task CopyAsync(TopLevel? top)
    {
        if (top?.Clipboard is not { } clipboard || Bitmap is not { } bitmap)
            return;
        try
        {
            await clipboard.SetBitmapAsync(bitmap);
            ShowToast("已复制");
        }
        catch (Exception ex)
        {
            Log.Warn("复制图片失败", ex);
            ShowToast("复制失败");
        }
    }
}
