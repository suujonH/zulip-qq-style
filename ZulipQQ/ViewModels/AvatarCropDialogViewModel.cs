using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 裁剪头像的对话框。<see cref="Result"/> 在关闭时完成：保存时是裁剪后的 PNG，取消（或所在窗口关闭）时是 null，
/// 生成图片失败时抛出异常。对话框接管 <paramref name="image"/>，关闭时释放。
/// </summary>
public sealed partial class AvatarCropDialogViewModel : ViewModelBase, IDisposable
{
    /// <summary>取景框边长（设备无关单位）。</summary>
    public const double ViewportSide = 320;

    private const double ZoomStep = 0.1;

    private readonly TaskCompletionSource<byte[]?> _result = new();

    public AvatarCropDialogViewModel(AvatarImage image)
    {
        Image = image;
        Crop = new AvatarCrop(image.Width, image.Height, ViewportSide);
        Crop.Changed += (_, _) => OnPropertyChanged(nameof(ZoomPosition));
    }

    public AvatarImage Image { get; }

    public AvatarCrop Crop { get; }

    public Task<byte[]?> Result => _result.Task;

    public bool CanZoom => Crop.CanZoom;

    /// <summary>缩放滑块的位置（0～1）。</summary>
    public double ZoomPosition
    {
        get => Crop.ZoomPosition;
        set => Crop.ZoomPosition = value;
    }

    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    [RelayCommand]
    private void ZoomIn() => ZoomPosition += ZoomStep;

    [RelayCommand]
    private void ZoomOut() => ZoomPosition -= ZoomStep;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsSaving || _result.Task.IsCompleted)
            return;
        IsSaving = true;
        var crop = Crop.GetCropRect();
        try
        {
            var png = await Task.Run(() => Image.RenderPng(crop));
            _result.TrySetResult(png);
        }
        catch (Exception ex)
        {
            _result.TrySetException(ex);
        }
        finally
        {
            Image.Dispose();
        }
    }

    [RelayCommand]
    private void Cancel() => Dispose();

    public void Dispose()
    {
        _result.TrySetResult(null);
        Image.Dispose();
    }
}
