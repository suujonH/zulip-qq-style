using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public class ColorMathTests
{
    [Theory]
    [InlineData("#0099FF", 0x00, 0x99, 0xFF)]
    [InlineData("e37318", 0xE3, 0x73, 0x18)]
    public void Parse(string text, byte r, byte g, byte b)
    {
        Assert.True(Rgb.TryParse(text, out var c));
        Assert.Equal(new Rgb(r, g, b), c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#GG0000")]
    public void Parse_Invalid(string? text) => Assert.False(Rgb.TryParse(text, out _));

    [Fact]
    public void ToHex_RoundTrip()
    {
        Assert.True(Rgb.TryParse("#0A1B2C", out var c));
        Assert.Equal("#0A1B2C", c.ToHex());
    }

    [Fact]
    public void Mix_EndpointsAndMiddle()
    {
        var a = new Rgb(0, 100, 200);
        Assert.Equal(a, ColorMath.Mix(a, Rgb.White, 0));
        Assert.Equal(Rgb.White, ColorMath.Mix(a, Rgb.White, 1));
        Assert.Equal(new Rgb(128, 178, 228), ColorMath.Mix(a, Rgb.White, 0.5));
        Assert.Equal(new Rgb(0, 50, 100), ColorMath.Mix(a, Rgb.Black, 0.5));
    }

    [Fact]
    public void Lighten_IsMixWithWhite() =>
        Assert.Equal(ColorMath.Mix(new Rgb(0, 153, 255), Rgb.White, 0.1), ColorMath.Lighten(new Rgb(0, 153, 255), 0.1));

    [Theory]
    [InlineData(0, 255, 0, 0)]
    [InlineData(120, 0, 255, 0)]
    [InlineData(240, 0, 0, 255)]
    [InlineData(360, 255, 0, 0)]
    [InlineData(60, 255, 255, 0)]
    public void FromHsv_Primaries(double h, byte r, byte g, byte b) =>
        Assert.Equal(new Rgb(r, g, b), ColorMath.FromHsv(h, 1, 1));

    [Theory]
    [InlineData("#0099FF")]
    [InlineData("#E37318")]
    [InlineData("#8E56DD")]
    [InlineData("#4B4B4B")]
    public void Hsv_RoundTrip(string hex)
    {
        Assert.True(Rgb.TryParse(hex, out var c));
        var (h, s, v) = ColorMath.ToHsv(c);
        Assert.Equal(c, ColorMath.FromHsv(h, s, v));
    }

    [Fact]
    public void ToHsv_Gray_HasZeroSaturation()
    {
        var (h, s, v) = ColorMath.ToHsv(new Rgb(128, 128, 128));
        Assert.Equal(0, h);
        Assert.Equal(0, s);
        Assert.Equal(128 / 255d, v, 6);
    }
}
