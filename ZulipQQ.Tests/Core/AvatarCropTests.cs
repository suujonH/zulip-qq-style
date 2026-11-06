using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public class AvatarCropTests
{
    private const double Viewport = 320;

    [Theory]
    [InlineData(1000, 1000, 0, 0, 1000)]
    [InlineData(1000, 600, 200, 0, 600)]
    [InlineData(600, 1000, 0, 200, 600)]
    [InlineData(7, 3, 2, 0, 3)]
    [InlineData(1, 1, 0, 0, 1)]
    public void Initial_crop_is_the_centered_largest_square(int w, int h, int x, int y, int side)
    {
        var crop = new AvatarCrop(w, h, Viewport);

        Assert.Equal(new AvatarCropRect(x, y, side), crop.GetCropRect());
    }

    [Fact]
    public void Initial_image_rect_covers_the_viewport()
    {
        var (x, y, width, height) = new AvatarCrop(1000, 500, Viewport).GetImageRect();

        Assert.Equal(640, width, 6);
        Assert.Equal(320, height, 6);
        Assert.Equal(-160, x, 6);
        Assert.Equal(0, y, 6);
    }

    [Fact]
    public void Panning_converts_viewport_distance_to_image_pixels()
    {
        var crop = new AvatarCrop(1000, 500, Viewport); // 1 取景框单位 = 1.5625 原图像素

        crop.Pan(64, 0);

        Assert.Equal(new AvatarCropRect(150, 0, 500), crop.GetCropRect());
    }

    [Theory]
    [InlineData(10000, 0, 0)]
    [InlineData(-10000, 0, 500)]
    [InlineData(0, 10000, 0)]
    [InlineData(0, -10000, 0)]
    public void Panning_stops_at_the_image_edges(double dx, double dy, int expectedX)
    {
        var crop = new AvatarCrop(1000, 500, Viewport);
        if (dx == 0)
            crop.Pan(10000, 0); // 水平方向先到最左，确认垂直方向不能移动

        crop.Pan(dx, dy);

        var rect = crop.GetCropRect();
        Assert.Equal(expectedX, rect.X);
        Assert.Equal(0, rect.Y);
        Assert.Equal(500, rect.Side);
    }

    [Fact]
    public void Zoom_position_maps_to_scale_geometrically()
    {
        var crop = new AvatarCrop(1000, 1000, Viewport);

        crop.ZoomPosition = 0.5;
        Assert.Equal(crop.MinScale * Math.Sqrt(AvatarCrop.MaxZoom), crop.Scale, 9);

        crop.ZoomPosition = 1;
        Assert.Equal(new AvatarCropRect(400, 400, 200), crop.GetCropRect());
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    public void Zoom_position_is_clamped(double value, double expected)
    {
        var crop = new AvatarCrop(1000, 1000, Viewport) { ZoomPosition = value };

        Assert.Equal(expected, crop.ZoomPosition);
    }

    [Fact]
    public void Zooming_keeps_the_anchor_point_fixed()
    {
        var crop = new AvatarCrop(1000, 1000, Viewport);
        crop.ZoomPosition = 0.5;
        var imageX = crop.CenterX + (100 - Viewport / 2) / crop.Scale;
        var imageY = crop.CenterY + (120 - Viewport / 2) / crop.Scale;

        crop.SetZoom(0.8, 100, 120);

        Assert.Equal(imageX, crop.CenterX + (100 - Viewport / 2) / crop.Scale, 6);
        Assert.Equal(imageY, crop.CenterY + (120 - Viewport / 2) / crop.Scale, 6);
    }

    [Fact]
    public void Zooming_out_at_an_edge_pulls_the_image_back_inside()
    {
        var crop = new AvatarCrop(1000, 1000, Viewport) { ZoomPosition = 1 };
        crop.Pan(100000, 100000);
        Assert.Equal(new AvatarCropRect(0, 0, 200), crop.GetCropRect());

        crop.ZoomPosition = 0;

        Assert.Equal(new AvatarCropRect(0, 0, 1000), crop.GetCropRect());
        var (x, y, _, _) = crop.GetImageRect();
        Assert.Equal(0, x, 6);
        Assert.Equal(0, y, 6);
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(4, 4, true)]
    [InlineData(1, 5000, false)]
    public void Tiny_images_never_produce_an_empty_or_out_of_range_crop(int w, int h, bool canZoom)
    {
        var crop = new AvatarCrop(w, h, Viewport);
        Assert.Equal(canZoom, crop.CanZoom);

        crop.ZoomPosition = 1;
        crop.Pan(-100000, -100000);

        var rect = crop.GetCropRect();
        Assert.InRange(rect.Side, 1, Math.Min(w, h));
        Assert.InRange(rect.X, 0, w - rect.Side);
        Assert.InRange(rect.Y, 0, h - rect.Side);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(512, 512)]
    [InlineData(513, 512)]
    [InlineData(4096, 512)]
    public void Output_side_never_upscales_and_is_capped(int cropSide, int expected)
    {
        Assert.Equal(expected, AvatarCrop.GetOutputSide(cropSide));
    }

    [Fact]
    public void Changed_fires_only_on_actual_change()
    {
        var crop = new AvatarCrop(1000, 1000, Viewport);
        var count = 0;
        crop.Changed += (_, _) => count++;

        crop.Pan(10, 10); // 未放大时图片正好铺满，不能移动
        crop.ZoomPosition = 0;
        Assert.Equal(0, count);

        crop.ZoomPosition = 0.5;
        Assert.Equal(1, count);
        crop.Pan(10, 10);
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void Rejects_empty_images(int w, int h)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AvatarCrop(w, h, Viewport));
    }
}
