using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ZulipQQ.Controls;

/// <summary>
/// 列表中的条目高度将要一起变化（可能分多次，如异步取回内容）时，让视口顶部的条目留在屏幕上原来的位置。
/// 用户开始滚动或超时后不再干预。
/// </summary>
public sealed class ScrollPositionHold
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(5);

    private readonly ScrollViewer _scroller;
    private readonly ItemsControl _list;
    private (object Item, double Delta, DateTime Until)? _held;

    public ScrollPositionHold(ScrollViewer scroller, ItemsControl list)
    {
        _scroller = scroller;
        _list = list;
        void Release(object? sender, RoutedEventArgs e) => _held = null;
        scroller.AddHandler(InputElement.PointerWheelChangedEvent, Release, RoutingStrategies.Tunnel, handledEventsToo: true);
        scroller.AddHandler(InputElement.PointerPressedEvent, Release, RoutingStrategies.Tunnel, handledEventsToo: true);
        scroller.AddHandler(InputElement.KeyDownEvent, Release, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>在高度变化之前调用：记下第一条顶边在视口内的条目（没有时是盖住视口顶部的那条）。</summary>
    public void Begin()
    {
        var offset = _scroller.Offset.Y;
        var visible = _list.GetRealizedContainers()
            .Where(c => c.IsVisible && c.Bounds.Height > 0 && c.DataContext is not null)
            .Select(c => (Container: c, Y: Y(c)))
            .Where(x => x.Y + x.Container.Bounds.Height > offset && x.Y < offset + _scroller.Viewport.Height)
            .OrderBy(x => x.Y)
            .ToList();
        if (visible.Count == 0)
            return;
        var (container, y) = visible.FirstOrDefault(x => x.Y >= offset, visible[0]);
        _held = (container.DataContext!, y - offset, DateTime.UtcNow + Duration);
    }

    /// <summary>取消（如跳转到别处、切换会话）。</summary>
    public void Cancel() => _held = null;

    /// <summary>每次布局完成后调用。</summary>
    public void Apply()
    {
        if (_held is not { } held)
            return;
        if (DateTime.UtcNow > held.Until || _list.ContainerFromItem(held.Item) is not { } container)
        {
            _held = null;
            return;
        }
        var max = Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height);
        var target = Math.Clamp(Y(container) - held.Delta, 0, max);
        if (Math.Abs(target - _scroller.Offset.Y) > 0.5)
            _scroller.Offset = new Vector(_scroller.Offset.X, target);
    }

    private double Y(Control container) => container.TranslatePoint(default, _list)?.Y ?? 0;
}
