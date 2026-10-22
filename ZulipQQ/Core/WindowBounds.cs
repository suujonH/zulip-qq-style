using System;
using System.Globalization;

namespace ZulipQQ.Core;

/// <summary>窗口位置与大小：位置为屏幕像素，大小为逻辑像素。保存为 <c>x,y,w,h</c>。</summary>
public readonly record struct WindowBounds(int X, int Y, double Width, double Height)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{X},{Y},{Width},{Height}");

    /// <summary>解析 <see cref="ToString"/> 的结果；格式不对或宽高不为正时返回 null。</summary>
    public static WindowBounds? Parse(string? s)
    {
        var parts = s?.Split(',');
        if (parts is not { Length: 4 }
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            || !(w > 0) || !(h > 0) || double.IsInfinity(w) || double.IsInfinity(h))
            return null;
        return new WindowBounds(x, y, w, h);
    }
}
