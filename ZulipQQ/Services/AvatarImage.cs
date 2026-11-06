using System;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using ZulipQQ.Core;

namespace ZulipQQ.Services;

/// <summary>读取失败或图片超出处理能力；<see cref="Exception.Message"/> 可以直接显示给用户。</summary>
public sealed class AvatarImageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 待裁剪的头像图片：已按 EXIF 方向摆正、转换到 sRGB，保留透明度（动图取第一帧）。
/// 很大的图片缩到长边 <see cref="MaxWorkingSide"/> 以内；裁剪坐标都相对于处理后的尺寸。
/// </summary>
public sealed class AvatarImage : IDisposable
{
    public const int MaxWorkingSide = 4096;

    /// <summary>解码时允许的最大像素数（每像素 4 字节）。超过时不解码，以免占满内存。</summary>
    public const long MaxDecodedPixels = 100_000_000;

    private const string UnreadableMessage = "无法读取这张图片";
    private const string TooLargeMessage = "图片太大，无法处理";

    private static readonly SKSamplingOptions Downsampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private readonly SKBitmap _bitmap;
    private readonly object _lock = new();
    private bool _disposed;

    private AvatarImage(SKBitmap bitmap) => _bitmap = bitmap;

    public int Width => _bitmap.Width;

    public int Height => _bitmap.Height;

    /// <summary>读取图片文件；失败时抛出 <see cref="AvatarImageException"/>。</summary>
    public static AvatarImage Load(string path)
    {
        try
        {
            return Decode(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            throw new AvatarImageException(UnreadableMessage, ex);
        }
    }

    /// <summary>解码图片数据；失败时抛出 <see cref="AvatarImageException"/>。</summary>
    public static AvatarImage Decode(byte[] data, long maxDecodedPixels = MaxDecodedPixels)
    {
        using var skData = SKData.CreateCopy(data);
        using var codec = skData is null ? null : SKCodec.Create(skData);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            throw new AvatarImageException(UnreadableMessage);

        var bitmap = DecodePixels(codec, maxDecodedPixels);
        try
        {
            ApplyOrigin(ref bitmap, codec.EncodedOrigin);
            ShrinkToWorkingSize(ref bitmap);
            return new AvatarImage(bitmap);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>裁剪并编码为 PNG（保留透明度）。边长见 <see cref="AvatarCrop.GetOutputSide"/>。</summary>
    public byte[] RenderPng(AvatarCropRect crop)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (crop.Side <= 0 || crop.X < 0 || crop.Y < 0 || crop.X + crop.Side > Width || crop.Y + crop.Side > Height)
                throw new ArgumentOutOfRangeException(nameof(crop));

            using var subset = new SKBitmap();
            if (!_bitmap.ExtractSubset(subset, new SKRectI(crop.X, crop.Y, crop.X + crop.Side, crop.Y + crop.Side)))
                throw new InvalidOperationException("提取裁剪区域失败");

            var side = AvatarCrop.GetOutputSide(crop.Side);
            if (side == crop.Side)
                return Encode(subset);
            using var output = new SKBitmap(new SKImageInfo(side, side, _bitmap.ColorType, _bitmap.AlphaType, _bitmap.ColorSpace));
            if (!subset.ScalePixels(output, Downsampling))
                throw new InvalidOperationException("缩放裁剪区域失败");
            return Encode(output);
        }
    }

    /// <summary>生成界面显示用的位图（复制像素，与本对象的生命周期无关）。</summary>
    public Bitmap ToBitmap()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, _bitmap.GetPixels(),
                new PixelSize(Width, Height), new Vector(96, 96), _bitmap.RowBytes);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _bitmap.Dispose();
        }
    }

    private static SKBitmap DecodePixels(SKCodec codec, long maxDecodedPixels)
    {
        // JPEG、WebP 可以直接按比例解码，超大照片不必先占用全尺寸的内存
        var longSide = Math.Max(codec.Info.Width, codec.Info.Height);
        var size = codec.GetScaledDimensions(Math.Min(1f, (float)MaxWorkingSide / longSide));
        if ((long)size.Width * size.Height > maxDecodedPixels)
            throw new AvatarImageException(TooLargeMessage);

        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var bitmap = new SKBitmap();
        try
        {
            if (!bitmap.TryAllocPixels(info))
                throw new AvatarImageException(TooLargeMessage);
            // 文件被截断时 Skia 返回 IncompleteInput，只解出一部分像素，这种图片也算读取失败
            if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
                throw new AvatarImageException(UnreadableMessage);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void ApplyOrigin(ref SKBitmap bitmap, SKEncodedOrigin origin)
    {
        var (w, h) = ((float)bitmap.Width, (float)bitmap.Height);
        // (x, y) 映射到摆正后的 (x', y')
        SKMatrix? matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => null,
        };
        if (matrix is not { } m)
            return;

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var info = bitmap.Info.WithSize(swap ? bitmap.Height : bitmap.Width, swap ? bitmap.Width : bitmap.Height);
        var result = new SKBitmap(info);
        using (var canvas = new SKCanvas(result))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
        {
            canvas.SetMatrix(m);
            canvas.DrawBitmap(bitmap, 0, 0, paint);
        }
        bitmap.Dispose();
        bitmap = result;
    }

    private static void ShrinkToWorkingSize(ref SKBitmap bitmap)
    {
        var longSide = Math.Max(bitmap.Width, bitmap.Height);
        if (longSide <= MaxWorkingSide)
            return;
        var ratio = (double)MaxWorkingSide / longSide;
        var info = bitmap.Info.WithSize(Math.Max(1, (int)Math.Round(bitmap.Width * ratio)), Math.Max(1, (int)Math.Round(bitmap.Height * ratio)));
        var result = new SKBitmap(info);
        if (!bitmap.ScalePixels(result, Downsampling))
        {
            result.Dispose();
            throw new AvatarImageException(UnreadableMessage);
        }
        bitmap.Dispose();
        bitmap = result;
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("编码 PNG 失败");
        return data.ToArray();
    }
}
