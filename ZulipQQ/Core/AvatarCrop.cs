using System;

namespace ZulipQQ.Core;

/// <summary>原图上的正方形裁剪区域（像素）。</summary>
public readonly record struct AvatarCropRect(int X, int Y, int Side);

/// <summary>
/// 头像裁剪的状态与换算（不依赖界面）：正方形取景框固定，图片在框下平移和缩放，图片始终铺满取景框。
/// 缩放用 0～1 的滑块位置表示：0 是图片短边正好铺满取景框，1 是最大放大倍数，中间按等比变化。
/// </summary>
public sealed class AvatarCrop
{
    /// <summary>相对“铺满”的最大放大倍数。</summary>
    public const double MaxZoom = 5;

    /// <summary>输出边长上限：Zulip 最大只用到 500 像素的头像，再大只是增加上传体积。</summary>
    public const int MaxOutputSide = 512;

    private double _zoomPosition;

    public AvatarCrop(int imageWidth, int imageHeight, double viewportSide)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportSide);
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        ViewportSide = viewportSide;
        MinScale = viewportSide / Math.Min(imageWidth, imageHeight);
        // 放大到取景框只剩 1 个原图像素为止，很小的图片因此可能不能再放大
        MaxScale = Math.Max(MinScale, Math.Min(MinScale * MaxZoom, viewportSide));
        CenterX = imageWidth / 2.0;
        CenterY = imageHeight / 2.0;
    }

    /// <summary>位置或缩放变化时触发。</summary>
    public event EventHandler? Changed;

    public int ImageWidth { get; }

    public int ImageHeight { get; }

    public double ViewportSide { get; }

    public double MinScale { get; }

    public double MaxScale { get; }

    public bool CanZoom => MaxScale > MinScale;

    /// <summary>取景框中心在原图上的位置（像素）。</summary>
    public double CenterX { get; private set; }

    public double CenterY { get; private set; }

    /// <summary>屏幕长度 / 原图长度。</summary>
    public double Scale => MinScale * Math.Pow(MaxScale / MinScale, _zoomPosition);

    /// <summary>滑块位置（0～1），以取景框中心为基准缩放。</summary>
    public double ZoomPosition
    {
        get => _zoomPosition;
        set => SetZoom(value, ViewportSide / 2, ViewportSide / 2);
    }

    /// <summary>缩放到 <paramref name="position"/>，取景框内坐标（anchorX, anchorY）处的图片内容保持不动。</summary>
    public void SetZoom(double position, double anchorX, double anchorY)
    {
        position = CanZoom ? Math.Clamp(position, 0, 1) : 0;
        if (position == _zoomPosition)
            return;
        var half = ViewportSide / 2;
        var oldScale = Scale;
        var imageX = CenterX + (anchorX - half) / oldScale;
        var imageY = CenterY + (anchorY - half) / oldScale;
        _zoomPosition = position;
        var scale = Scale;
        MoveCenter(imageX - (anchorX - half) / scale, imageY - (anchorY - half) / scale);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>图片跟随指针移动（dx, dy 是取景框内的距离），到边缘后不再移动。</summary>
    public void Pan(double dx, double dy)
    {
        var (x, y) = (CenterX, CenterY);
        var scale = Scale;
        MoveCenter(x - dx / scale, y - dy / scale);
        if (x != CenterX || y != CenterY)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>图片在取景框坐标系中的位置和大小。</summary>
    public (double X, double Y, double Width, double Height) GetImageRect()
    {
        var scale = Scale;
        var half = ViewportSide / 2;
        return (half - CenterX * scale, half - CenterY * scale, ImageWidth * scale, ImageHeight * scale);
    }

    /// <summary>取景框对应的原图像素区域：取整后仍是正方形，且不超出原图。</summary>
    public AvatarCropRect GetCropRect()
    {
        var side = (int)Math.Round(ViewportSide / Scale);
        side = Math.Clamp(side, 1, Math.Min(ImageWidth, ImageHeight));
        var x = (int)Math.Round(CenterX - side / 2.0);
        var y = (int)Math.Round(CenterY - side / 2.0);
        return new AvatarCropRect(Math.Clamp(x, 0, ImageWidth - side), Math.Clamp(y, 0, ImageHeight - side), side);
    }

    /// <summary>输出边长：不放大小图，大图缩到 <see cref="MaxOutputSide"/>。</summary>
    public static int GetOutputSide(int cropSide) => Math.Clamp(cropSide, 1, MaxOutputSide);

    private void MoveCenter(double x, double y)
    {
        var half = ViewportSide / 2 / Scale;
        CenterX = Math.Clamp(x, half, ImageWidth - half);
        CenterY = Math.Clamp(y, half, ImageHeight - half);
    }
}
