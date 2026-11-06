using SkiaSharp;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Tests.ViewModels;

public class AvatarCropDialogViewModelTests
{
    private static AvatarImage MakeImage(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height));
        bitmap.Erase(SKColors.Red);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return AvatarImage.Decode(data.ToArray());
    }

    [Fact]
    public async Task Save_returns_a_square_png_of_the_current_crop()
    {
        var vm = new AvatarCropDialogViewModel(MakeImage(400, 200));
        vm.ZoomPosition = 1; // 放大 5 倍：200 / 5 = 40

        await vm.SaveCommand.ExecuteAsync(null);

        var png = await vm.Result;
        Assert.NotNull(png);
        using var output = SKBitmap.Decode(png);
        Assert.Equal((40, 40), (output.Width, output.Height));
    }

    [Fact]
    public async Task Cancel_returns_null_and_releases_the_image()
    {
        var image = MakeImage(10, 10);
        var vm = new AvatarCropDialogViewModel(image);

        vm.CancelCommand.Execute(null);

        Assert.Null(await vm.Result);
        Assert.Throws<ObjectDisposedException>(() => image.RenderPng(new(0, 0, 1)));
    }

    [Fact]
    public async Task Closing_the_window_counts_as_cancel_and_does_not_override_a_save()
    {
        var closed = new AvatarCropDialogViewModel(MakeImage(10, 10));
        closed.Dispose();
        Assert.Null(await closed.Result);

        var saved = new AvatarCropDialogViewModel(MakeImage(10, 10));
        await saved.SaveCommand.ExecuteAsync(null);
        saved.Dispose();
        Assert.NotNull(await saved.Result);
    }

    [Fact]
    public async Task Render_failure_surfaces_through_the_result()
    {
        var image = MakeImage(10, 10);
        var vm = new AvatarCropDialogViewModel(image);
        image.Dispose();

        await vm.SaveCommand.ExecuteAsync(null);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => vm.Result);
    }

    [Fact]
    public void Zoom_commands_and_slider_stay_in_sync()
    {
        using var vm = new AvatarCropDialogViewModel(MakeImage(1000, 1000));
        var changes = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ZoomPosition))
                changes++;
        };

        vm.ZoomInCommand.Execute(null);
        Assert.Equal(0.1, vm.ZoomPosition, 9);
        Assert.Equal(1, changes);

        vm.ZoomOutCommand.Execute(null);
        vm.ZoomOutCommand.Execute(null);
        Assert.Equal(0, vm.ZoomPosition, 9);
    }

    [Fact]
    public void Tiny_images_cannot_zoom()
    {
        using var vm = new AvatarCropDialogViewModel(MakeImage(1, 1));

        Assert.False(vm.CanZoom);
    }
}
