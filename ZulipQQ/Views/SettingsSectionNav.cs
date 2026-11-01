using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.Views;

/// <summary>
/// 设置导航的二级列表（设置窗口与“Zulip 账号”页共用）：右侧页面的内容超过两屏时，把页中的分组标题（<c>TextBlock.section</c>）
/// 列在当前页的导航项下。滚动时选中当前所在的分组，点击分组时滚动到该分组的标题。
/// </summary>
public sealed class SettingsSectionNav
{
    // 分组标题滚到距顶部这个距离以内时视为进入该分组
    private const double TopTolerance = 8;

    private readonly ScrollViewer _scroller;
    private readonly Func<SettingsNavItem?> _currentItem;
    private SettingsNavItem? _item;
    private List<TextBlock> _headers = [];
    private bool _refreshPosted;

    // 点击分组后滚动到的位置：页面滚不到该分组的顶部时（靠近底部的短分组），停在这里期间保持选中点击的分组
    private int _clicked = -1;
    private double _clickedOffset;

    /// <param name="currentItem">当前页的导航项。</param>
    public SettingsSectionNav(ScrollViewer scroller, Func<SettingsNavItem?> currentItem)
    {
        _scroller = scroller;
        _currentItem = currentItem;
        _scroller.ScrollChanged += OnScrollChanged;
    }

    /// <summary>页面内容或当前页变化：布局完成后重新收集分组。</summary>
    public void Refresh()
    {
        if (_refreshPosted)
            return;
        _refreshPosted = true;
        // Background 低于布局，执行时新内容已完成布局
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPosted = false;
            Rebuild();
        }, DispatcherPriority.Background);
    }

    /// <summary>滚动到二级列表中的一个分组。</summary>
    public void ScrollTo(SettingsNavSection section)
    {
        var index = _item?.Sections.IndexOf(section) ?? -1;
        if (index < 0 || index >= _headers.Count)
            return;
        var max = Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height);
        _clicked = index;
        _clickedOffset = Math.Clamp(TopOf(_headers[index]), 0, max);
        _scroller.Offset = _scroller.Offset.WithY(_clickedOffset);
        Select(index);
    }

    public void Detach()
    {
        _scroller.ScrollChanged -= OnScrollChanged;
        _item?.SetSections([]);
        _item = null;
    }

    // 内容或视口大小变化时重新收集；只是滚动时更新选中
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
            Refresh();
        else if (e.OffsetDelta.Y != 0)
            UpdateSelection();
    }

    private void Rebuild()
    {
        var item = _currentItem();
        if (_item != item)
        {
            _item?.SetSections([]);
            _item = item;
            _clicked = -1;
        }
        if (_item is null)
            return;

        // 超过两屏（显示一屏之外还有一屏以上）才显示二级列表；隐藏的分组（如服务器不支持时）不列出
        var viewport = _scroller.Viewport.Height;
        _headers = viewport > 0 && _scroller.Extent.Height > viewport * 2
            ? _scroller.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("section") && t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
                .ToList()
            : [];
        _item.SetSections(_headers.Select(h => h.Text!).ToList());
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        if (_item is null || _headers.Count == 0)
            return;
        var offset = _scroller.Offset.Y;
        if (_clicked >= 0 && Math.Abs(offset - _clickedOffset) < 1)
        {
            Select(_clicked);
            return;
        }
        _clicked = -1;
        var max = _scroller.Extent.Height - _scroller.Viewport.Height;
        int index;
        if (offset >= max - 1)
        {
            // 滚到底部时最后一个分组的标题可能到不了顶部
            index = _headers.Count - 1;
        }
        else
        {
            index = 0;
            for (var i = 0; i < _headers.Count; i++)
            {
                if (TopOf(_headers[i]) <= offset + TopTolerance)
                    index = i;
            }
        }
        Select(index);
    }

    private void Select(int index)
    {
        if (_item is null)
            return;
        for (var i = 0; i < _item.Sections.Count; i++)
            _item.Sections[i].IsSelected = i == index;
    }

    // 标题在滚动内容中的纵坐标
    private double TopOf(Visual header) =>
        (header.TranslatePoint(default, _scroller)?.Y ?? 0) + _scroller.Offset.Y;
}
