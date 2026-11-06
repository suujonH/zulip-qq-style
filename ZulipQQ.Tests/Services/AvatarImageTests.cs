using SkiaSharp;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.Tests.Services;

public class AvatarImageTests
{
    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Green = new(0, 255, 0);
    private static readonly SKColor Blue = new(0, 0, 255);
    private static readonly SKColor Yellow = new(255, 255, 0);

    /// <summary>四个象限不同颜色的图片：左上红、右上绿、左下蓝、右下黄。</summary>
    private static SKBitmap Quadrants(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = (x < width / 2, y < height / 2) switch
                {
                    (true, true) => Red,
                    (false, true) => Green,
                    (true, false) => Blue,
                    _ => Yellow,
                };
                bitmap.SetPixel(x, y, color);
            }
        }
        return bitmap;
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality = 100)
    {
        using var data = bitmap.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>在 JPEG 的 SOI 之后插入只含方向标记的 EXIF 段。</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, int orientation)
    {
        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 0x22, // APP1，长度 34
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, // 大端 TIFF 头，IFD 偏移 8
            0, 1, // 1 个条目
            0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, // Orientation，SHORT，1 个
            0, 0, 0, 0, // 没有下一个 IFD
        ];
        return [.. jpeg[..2], .. exif, .. jpeg[2..]];
    }

    private static SKBitmap DecodePng(byte[] png)
    {
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        return SKBitmap.Decode(png);
    }

    private static void AssertClose(SKColor expected, SKColor actual, int tolerance = 40)
    {
        Assert.InRange(actual.Red, Math.Max(0, expected.Red - tolerance), Math.Min(255, expected.Red + tolerance));
        Assert.InRange(actual.Green, Math.Max(0, expected.Green - tolerance), Math.Min(255, expected.Green + tolerance));
        Assert.InRange(actual.Blue, Math.Max(0, expected.Blue - tolerance), Math.Min(255, expected.Blue + tolerance));
    }

    private static AvatarCropRect Whole(AvatarImage image) => new(0, 0, Math.Min(image.Width, image.Height));

    [Fact]
    public void Crop_outputs_exactly_the_selected_pixels()
    {
        using var source = Quadrants(100, 60);
        using var image = AvatarImage.Decode(Encode(source, SKEncodedImageFormat.Png));
        Assert.Equal((100, 60), (image.Width, image.Height));

        // 跨过中线的 20×20：左半红、右半绿
        using var output = DecodePng(image.RenderPng(new AvatarCropRect(40, 5, 20)));

        Assert.Equal((20, 20), (output.Width, output.Height));
        Assert.Equal(Red, output.GetPixel(0, 0));
        Assert.Equal(Red, output.GetPixel(9, 19));
        Assert.Equal(Green, output.GetPixel(10, 0));
        Assert.Equal(Green, output.GetPixel(19, 19));
    }

    [Theory]
    [InlineData(1, 40, 20, 255, 0, 0, 0, 255, 0)]
    [InlineData(2, 40, 20, 0, 255, 0, 255, 0, 0)]
    [InlineData(3, 40, 20, 255, 255, 0, 0, 0, 255)]
    [InlineData(4, 40, 20, 0, 0, 255, 255, 255, 0)]
    [InlineData(5, 20, 40, 255, 0, 0, 0, 0, 255)]
    [InlineData(6, 20, 40, 0, 0, 255, 255, 0, 0)]
    [InlineData(7, 20, 40, 255, 255, 0, 0, 255, 0)]
    [InlineData(8, 20, 40, 0, 255, 0, 255, 255, 0)]
    public void Exif_orientation_is_applied(int orientation, int width, int height,
        byte tlR, byte tlG, byte tlB, byte trR, byte trG, byte trB)
    {
        using var source = Quadrants(40, 20);
        var jpeg = WithExifOrientation(Encode(source, SKEncodedImageFormat.Jpeg), orientation);

        using var image = AvatarImage.Decode(jpeg);

        Assert.Equal((width, height), (image.Width, image.Height));
        // 分别裁出左上角和右上角的小方块再比较
        using var topLeft = DecodePng(image.RenderPng(new AvatarCropRect(0, 0, 4)));
        using var topRight = DecodePng(image.RenderPng(new AvatarCropRect(image.Width - 4, 0, 4)));
        AssertClose(new SKColor(tlR, tlG, tlB), topLeft.GetPixel(2, 2));
        AssertClose(new SKColor(trR, trG, trB), topRight.GetPixel(2, 2));
    }

    [Fact]
    public void Transparency_is_preserved()
    {
        using var source = new SKBitmap(new SKImageInfo(10, 10, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        source.Erase(SKColors.Transparent);
        for (var y = 0; y < 10; y++)
            source.SetPixel(0, y, new SKColor(255, 0, 0, 128));
        using var image = AvatarImage.Decode(Encode(source, SKEncodedImageFormat.Png));

        using var output = DecodePng(image.RenderPng(Whole(image)));

        Assert.Equal(0, output.GetPixel(9, 9).Alpha);
        var edge = output.GetPixel(0, 5);
        Assert.InRange((int)edge.Alpha, 126, 130);
        Assert.True(edge.Red > 240 && edge.Green == 0 && edge.Blue == 0, edge.ToString());
    }

    [Fact]
    public void Large_crop_is_downscaled_with_averaging()
    {
        // 1 像素棋盘格：缩小时如果只是取样，结果会是纯黑或纯白而不是灰色
        using var source = new SKBitmap(new SKImageInfo(2048, 2048, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (var y = 0; y < 2048; y++)
        {
            for (var x = 0; x < 2048; x++)
                source.SetPixel(x, y, (x + y) % 2 == 0 ? SKColors.Black : SKColors.White);
        }
        using var image = AvatarImage.Decode(Encode(source, SKEncodedImageFormat.Png));

        using var output = DecodePng(image.RenderPng(Whole(image)));

        Assert.Equal((AvatarCrop.MaxOutputSide, AvatarCrop.MaxOutputSide), (output.Width, output.Height));
        Assert.InRange((int)output.GetPixel(256, 256).Red, 90, 200);
    }

    [Fact]
    public void Small_images_are_not_upscaled()
    {
        using var source = Quadrants(2, 2);
        using var image = AvatarImage.Decode(Encode(source, SKEncodedImageFormat.Png));

        using var output = DecodePng(image.RenderPng(Whole(image)));

        Assert.Equal((2, 2), (output.Width, output.Height));
        Assert.Equal(Yellow, output.GetPixel(1, 1));
    }

    [Fact]
    public void Images_larger_than_the_working_size_are_shrunk_keeping_aspect()
    {
        using var source = Quadrants(8192, 100);
        using var image = AvatarImage.Decode(Encode(source, SKEncodedImageFormat.Png));

        Assert.Equal((AvatarImage.MaxWorkingSide, 50), (image.Width, image.Height));
    }

    [Fact]
    public void Images_over_the_pixel_limit_are_rejected_before_decoding()
    {
        using var source = Quadrants(100, 100);

        var ex = Assert.Throws<AvatarImageException>(() =>
            AvatarImage.Decode(Encode(source, SKEncodedImageFormat.Png), maxDecodedPixels: 9999));

        Assert.Equal("图片太大，无法处理", ex.Message);
    }

    [Fact]
    public void Webp_images_decode()
    {
        using var source = Quadrants(20, 20);
        using var image = AvatarImage.Decode(Encode(source, SKEncodedImageFormat.Webp));

        Assert.Equal((20, 20), (image.Width, image.Height));
    }

    [Fact]
    public void Unreadable_data_is_rejected()
    {
        Assert.Throws<AvatarImageException>(() => AvatarImage.Decode([]));
        Assert.Throws<AvatarImageException>(() => AvatarImage.Decode([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]));
    }

    [Fact]
    public void Truncated_image_is_rejected()
    {
        using var source = Quadrants(200, 200);
        var png = Encode(source, SKEncodedImageFormat.Png);

        Assert.Throws<AvatarImageException>(() => AvatarImage.Decode(png[..(png.Length / 2)]));
    }

    [Fact]
    public void Missing_file_is_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");

        var ex = Assert.Throws<AvatarImageException>(() => AvatarImage.Load(path));

        Assert.Equal("无法读取这张图片", ex.Message);
    }

    [Theory]
    [InlineData(-1, 0, 5)]
    [InlineData(0, -1, 5)]
    [InlineData(6, 0, 5)]
    [InlineData(0, 6, 5)]
    [InlineData(0, 0, 0)]
    public void Crop_outside_the_image_is_rejected(int x, int y, int side)
    {
        using var source = Quadrants(10, 10);
        using var image = AvatarImage.Decode(Encode(source, SKEncodedImageFormat.Png));

        Assert.Throws<ArgumentOutOfRangeException>(() => image.RenderPng(new AvatarCropRect(x, y, side)));
    }

    [Fact]
    public void Rendering_after_dispose_throws()
    {
        using var source = Quadrants(10, 10);
        var image = AvatarImage.Decode(Encode(source, SKEncodedImageFormat.Png));
        image.Dispose();
        image.Dispose();

        Assert.Throws<ObjectDisposedException>(() => image.RenderPng(new AvatarCropRect(0, 0, 5)));
    }
}
