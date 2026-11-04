using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;
using Path = System.IO.Path;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 图片查看器：显示一组图片中的一张原图（通过 <see cref="ImageCache"/> 加载，不缩小解码），可切换上一张 / 下一张，
/// 另存为（本组织上传的图片下载原文件，其它图片保存为 PNG）、复制到剪贴板、转发、用系统的图片查看器打开。
/// 转发等对话框显示在查看器自己的窗口中。
/// </summary>
public partial class ImageViewerViewModel : ViewModelBase, IDialogHost
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

    public ObservableCollection<ViewModelBase> Dialogs { get; } = [];

    public void ShowToast(string text)
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
    [NotifyPropertyChangedFor(nameof(HasPrevious), nameof(HasNext))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(NextCommand))]
    public partial int Index { get; private set; }

    public int Count => _images.Count;

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
        var bitmap = await ImageCache.Shared.GetAsync(image.Original, null, _session.Client);
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
        if (top is not null && Current is { } image && Bitmap is not null)
            await MessageLinks.SaveImageAsAsync(_session, image, top, ShowToast);
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

    /// <summary>转发：本组织上传的图片发送上传链接（显示为图片），其它图片发送原链接。</summary>
    [RelayCommand]
    private void Forward()
    {
        if (Current is not { } image)
            return;
        var markdown = ZulipHtml.NormalizeUploadPath(image.Href) is { } path
            ? ComposerViewModel.LinkMarkdown(ZulipHtml.UploadLinkOf(path, null).FileName, path)
            : string.IsNullOrEmpty(image.Href) ? image.Src : image.Href;
        ForwardDialogViewModel.Show(_session, this, "[图片]", () => Task.FromResult<IReadOnlyList<string>>([markdown]));
    }

    /// <summary>用系统的图片查看器打开原图：从缓存复制到临时目录（带扩展名）后打开。</summary>
    [RelayCommand]
    private async Task OpenExternalAsync()
    {
        if (Current is not { } image)
            return;
        if (await CopyToTempAsync(_session, image) is not { } path)
        {
            ShowToast("打开失败");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("用系统查看器打开图片失败", ex);
            ShowToast("打开失败");
        }
    }

    /// <summary>
    /// 把原图从缓存（缓存文件没有扩展名）复制到临时目录，起好文件名与扩展名；返回复制后的路径，失败返回 null。
    /// 用系统查看器打开、聊天中图片的“打开文件夹”共用。
    /// </summary>
    public static async Task<string?> CopyToTempAsync(ZulipSession session, InlineImage image)
    {
        if (await ImageCache.Shared.GetFileAsync(image.Original, session.Client) is not { } cached)
            return null;
        try
        {
            // 同名的不同图片放在各自的目录中（目录名取缓存文件名的前 8 位）
            var dir = Path.Combine(Path.GetTempPath(), "ZulipQQ", Path.GetFileName(cached)[..8]);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, FileNameOf(image, cached));
            if (!File.Exists(path))
                File.Copy(cached, path);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn("复制图片到临时目录失败", ex);
            return null;
        }
    }

    // 上传的图片用原文件名；其它按地址中的文件名，没有图片扩展名时按文件头判断
    private static string FileNameOf(InlineImage image, string cached)
    {
        var name = ZulipHtml.NormalizeUploadPath(image.Href) is { } path
            ? ZulipHtml.UploadLinkOf(path, null).FileName
            : Uri.TryCreate(image.Original, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.AbsolutePath) : "";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        if (Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp")
            return name;
        return (name.Length > 0 ? Path.GetFileNameWithoutExtension(name) : "图片") + SniffExtension(cached);
    }

    private static string SniffExtension(string file)
    {
        Span<byte> head = stackalloc byte[12];
        using (var stream = File.OpenRead(file))
            head = head[..stream.Read(head)];
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']))
            return ".png";
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]))
            return ".jpg";
        if (head.StartsWith("GIF8"u8))
            return ".gif";
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            return ".webp";
        if (head.StartsWith("BM"u8))
            return ".bmp";
        return ".png";
    }

    public Task<bool> ConfirmAsync(string message, string confirmText = "确定", bool isDanger = false)
    {
        var tcs = new TaskCompletionSource<bool>();
        ConfirmDialogViewModel dialog = null!;
        dialog = new ConfirmDialogViewModel(message, confirmText, isDanger, confirmed =>
        {
            Dialogs.Remove(dialog);
            tcs.TrySetResult(confirmed);
        });
        Dialogs.Add(dialog);
        return tcs.Task;
    }

    /// <summary>窗口关闭：关闭仍打开的对话框（退订事件、未回答的确认视为取消）。</summary>
    public void Close()
    {
        foreach (var dialog in Dialogs.ToList())
            (dialog as IDisposable)?.Dispose();
        Dialogs.Clear();
    }
}
