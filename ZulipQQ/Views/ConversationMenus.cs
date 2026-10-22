using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>会话、话题右键菜单中各页面共用的菜单项。没有权限的操作不生成菜单项。</summary>
internal static class ConversationMenus
{
    // 群消息设置的四个级别与菜单文字（与 QQ 一致）
    private static readonly (DndLevel Level, string Text)[] GroupLevels =
    [
        (DndLevel.Notify, "允许消息提醒"),
        (DndLevel.Silent, "接收消息但不提醒"),
        (DndLevel.Assistant, "收进群助手且不提醒"),
        (DndLevel.Blocked, "屏蔽群消息"),
    ];

    public static MenuItem Item(string header, Action action, bool isDanger = false)
    {
        var item = new MenuItem { Header = header };
        if (isDanger)
            item.Foreground = Application.Current?.FindResource("DangerBrush") as Avalonia.Media.IBrush;
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>“群消息设置 ▸”：四个级别的单选项，当前级别打勾。</summary>
    public static MenuItem LevelSubmenu(DndLevel current, Action<DndLevel> set)
    {
        var levels = new MenuItem { Header = "群消息设置" };
        foreach (var (level, text) in GroupLevels)
        {
            var option = Item(text, () => set(level));
            option.ToggleType = MenuItemToggleType.Radio;
            option.IsChecked = current == level;
            levels.Items.Add(option);
        }
        return levels;
    }

    /// <summary>“复制链接”：复制话题链接并提示。</summary>
    public static MenuItem CopyLinkItem(MainViewModel main, TopicKey key, Visual anchor) =>
        Item("复制链接", () => _ = CopyAsync(anchor, main.Topics.Link(key), main));

    public static MenuItem FollowItem(MainViewModel main, TopicKey key) =>
        Item(main.Topics.IsFollowed(key) ? "取消关注" : "关注话题", () => _ = main.Topics.ToggleFollowAsync(key));

    /// <summary>
    /// 话题管理：移动到其他频道、标记已解决、重命名、删除（红色，仅组织管理员）。
    /// 有任一项时先加一条分隔线。
    /// </summary>
    public static void AppendManagement(ItemCollection items, MainViewModel main, TopicKey key)
    {
        var topics = main.Topics;
        var start = items.Count;

        if (topics.CanMove(key) && topics.MoveTargets(key).ToList() is { Count: > 0 } targets)
        {
            var move = new MenuItem { Header = "移动群到其他分组" };
            foreach (var channel in targets)
                move.Items.Add(Item($"#{channel.Name}", () => _ = topics.MoveAsync(key, channel)));
            items.Add(move);
        }
        if (topics.CanResolve(key))
            items.Add(Item(topics.IsResolved(key) ? "取消已解决" : "标记为已解决", () => _ = topics.ToggleResolvedAsync(key)));
        if (topics.CanRename(key))
            items.Add(Item("重命名话题", () => topics.ShowRenameDialog(key)));
        if (topics.CanDelete(key))
            items.Add(Item("删除话题", () => _ = topics.DeleteAsync(key), isDanger: true));

        if (items.Count > start)
            items.Insert(start, new Separator());
    }

    /// <summary>复制链接到剪贴板并提示。</summary>
    public static async Task CopyAsync(Visual anchor, string text, MainViewModel main)
    {
        if (TopLevel.GetTopLevel(anchor)?.Clipboard is not { } clipboard)
            return;
        try
        {
            await clipboard.SetTextAsync(text);
            main.ShowToast("链接已复制");
        }
        catch (Exception ex)
        {
            Log.Warn("复制链接失败", ex);
        }
    }
}
