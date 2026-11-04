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
    public static MenuItem Item(string header, Action action, bool isDanger = false)
    {
        var item = new MenuItem { Header = header };
        if (isDanger)
            item.Bind(MenuItem.ForegroundProperty, item.GetResourceObservable("DangerBrush"));
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>“群消息设置 ▸”：四个级别的单选项，当前级别打勾。</summary>
    public static MenuItem LevelSubmenu(DndLevel current, Action<DndLevel> set)
    {
        var levels = new MenuItem { Header = "消息设置" };
        foreach (var level in LevelOption.All)
        {
            var option = Item(level.Text, () => set(level.Level));
            option.ToggleType = MenuItemToggleType.Radio;
            option.IsChecked = current == level.Level;
            levels.Items.Add(option);
        }
        return levels;
    }

    /// <summary>“复制链接”：复制话题链接并提示。</summary>
    public static MenuItem CopyLinkItem(MainViewModel main, TopicKey key, Visual anchor) =>
        Item("复制链接", () => _ = CopyAsync(anchor, main.Topics.Link(key), main));

    /// <summary>
    /// “更多设置 ▸”：标为未读（<paramref name="markUnread"/> 不为 null 时）、复制链接，
    /// 以及有权限的移动到其他分组、标记已解决、删除（红色）、重命名话题。
    /// </summary>
    public static MenuItem MoreSubmenu(MainViewModel main, TopicKey key, Visual anchor, Action? markUnread = null)
    {
        var topics = main.Topics;
        var more = new MenuItem { Header = "更多设置" };
        if (markUnread is not null)
            more.Items.Add(Item("标为未读", markUnread));
        more.Items.Add(CopyLinkItem(main, key, anchor));
        if (topics.CanMove(key) && topics.MoveTargets(key).ToList() is { Count: > 0 } targets)
        {
            var move = new MenuItem { Header = "移动到其他分组" };
            foreach (var channel in targets)
                move.Items.Add(Item($"#{channel.Name}", () => _ = topics.MoveAsync(key, channel)));
            more.Items.Add(move);
        }
        if (topics.CanResolve(key))
            more.Items.Add(Item(topics.IsResolved(key) ? "取消已解决" : "标记为已解决", () => _ = topics.ToggleResolvedAsync(key)));
        if (topics.CanDelete(key))
            more.Items.Add(Item("删除话题", () => _ = topics.DeleteAsync(key), isDanger: true));
        if (topics.CanRename(key))
            more.Items.Add(Item("重命名话题", () => topics.ShowRenameDialog(key)));
        return more;
    }

    /// <summary>复制链接到剪贴板并提示。</summary>
    public static async Task CopyAsync(Visual anchor, string text, IDialogHost host)
    {
        if (TopLevel.GetTopLevel(anchor)?.Clipboard is not { } clipboard)
            return;
        try
        {
            await clipboard.SetTextAsync(text);
            host.ShowToast("链接已复制");
        }
        catch (Exception ex)
        {
            Log.Warn("复制链接失败", ex);
        }
    }
}
