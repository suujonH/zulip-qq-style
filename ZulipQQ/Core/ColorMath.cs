using System;
using System.Globalization;

namespace ZulipQQ.Core;

/// <summary>不透明的 RGB 颜色。与界面库无关，供主题计算和测试使用。</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb Black = new(0, 0, 0);

    /// <summary>解析 <c>#RRGGBB</c>（# 可省略）。</summary>
    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        var s = text?.Trim().TrimStart('#');
        if (s is not { Length: 6 } || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return false;
        color = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>主题配色用到的颜色运算：RGB 线性混合、HSV 转换。</summary>
public static class ColorMath
{
    /// <summary>按比例混合：<paramref name="t"/> 为 0 时是 <paramref name="a"/>，为 1 时是 <paramref name="b"/>。</summary>
    public static Rgb Mix(Rgb a, Rgb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgb(Channel(a.R, b.R, t), Channel(a.G, b.G, t), Channel(a.B, b.B, t));
    }

    /// <summary>向白色混合 <paramref name="amount"/>（0～1）。</summary>
    public static Rgb Lighten(Rgb color, double amount) => Mix(color, Rgb.White, amount);

    /// <summary>HSV 转 RGB。<paramref name="h"/> 为 0～360，<paramref name="s"/>、<paramref name="v"/> 为 0～1。</summary>
    public static Rgb FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return new Rgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    /// <summary>RGB 转 HSV（H 为 0～360，S、V 为 0～1）。灰色的色相为 0。</summary>
    public static (double H, double S, double V) ToHsv(Rgb color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r)
                h = 60 * ((g - b) / d % 6);
            else if (max == g)
                h = 60 * ((b - r) / d + 2);
            else
                h = 60 * ((r - g) / d + 4);
        }
        if (h < 0)
            h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static byte Channel(byte from, byte to, double t) => (byte)Math.Round(from + (to - from) * t, MidpointRounding.AwayFromZero);

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);
}
