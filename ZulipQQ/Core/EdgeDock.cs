using System.Collections.Generic;
using System.Linq;

namespace ZulipQQ.Core;

/// <summary>主窗口停靠的屏幕边缘。</summary>
public enum DockEdge
{
    None,
    Top,
    Left,
    Right,
}

/// <summary>屏幕像素矩形。</summary>
public readonly record struct PixelBox(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    /// <summary>点在矩形内（各边向外扩 <paramref name="margin"/> 像素）。</summary>
    public bool Contains(int x, int y, int margin = 0) =>
        x >= X - margin && x < Right + margin && y >= Y - margin && y < Bottom + margin;
}

/// <summary>边缘自动隐藏的计算（与 QQ 相同：贴住屏幕上、左、右边缘的窗口在鼠标离开后缩进边缘，只露出一条）。</summary>
public static class EdgeDock
{
    /// <summary>隐藏后露出的宽度（像素）。</summary>
    public const int VisibleStrip = 4;

    /// <summary>窗口贴住的边缘：先判断上边，再判断左边、右边。</summary>
    public static DockEdge Detect(PixelBox window, PixelBox work)
    {
        if (window.Y <= work.Y)
            return DockEdge.Top;
        if (window.X <= work.X)
            return DockEdge.Left;
        if (window.Right >= work.Right)
            return DockEdge.Right;
        return DockEdge.None;
    }

    /// <summary>
    /// 屏幕的这条边外侧紧挨着另一块屏幕（多显示器之间的边界）。这样的边不能隐藏：
    /// 缩进去的部分会显示在相邻的屏幕上。<paramref name="screen"/> 与 <paramref name="others"/> 为屏幕的完整范围。
    /// </summary>
    public static bool HasNeighbor(DockEdge edge, PixelBox screen, IEnumerable<PixelBox> others) =>
        others.Any(o => edge switch
        {
            DockEdge.Top => o.Bottom == screen.Y && o.X < screen.Right && o.Right > screen.X,
            DockEdge.Left => o.Right == screen.X && o.Y < screen.Bottom && o.Bottom > screen.Y,
            DockEdge.Right => o.X == screen.Right && o.Y < screen.Bottom && o.Bottom > screen.Y,
            _ => false,
        });

    /// <summary>停靠时的显示位置：把窗口贴齐边缘（拖出屏幕外的部分拉回来）。</summary>
    public static (int X, int Y) ShownPosition(DockEdge edge, PixelBox window, PixelBox work) => edge switch
    {
        DockEdge.Top => (window.X, work.Y),
        DockEdge.Left => (work.X, window.Y),
        DockEdge.Right => (work.Right - window.Width, window.Y),
        _ => (window.X, window.Y),
    };

    /// <summary>隐藏后的位置：窗口移到边缘外，只露出 <see cref="VisibleStrip"/> 像素。</summary>
    public static (int X, int Y) HiddenPosition(DockEdge edge, PixelBox window, PixelBox work) => edge switch
    {
        DockEdge.Top => (window.X, work.Y - window.Height + VisibleStrip),
        DockEdge.Left => (work.X - window.Width + VisibleStrip, window.Y),
        DockEdge.Right => (work.Right - VisibleStrip, window.Y),
        _ => (window.X, window.Y),
    };
}
