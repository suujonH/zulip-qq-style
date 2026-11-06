using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace ZulipQQ.Controls;

/// <summary>
/// 一级菜单项的图标：按菜单项的名称统一对照，所有菜单（右键菜单、下拉菜单）共用。
/// 菜单项套用模板时自动设置（见 <see cref="Register"/>）；子菜单中的项、已有图标的项、单选 / 复选项不设置。
/// 对照表中没有的名称不显示图标，文字仍与同一菜单中其它项对齐。带 noIcons 类的菜单（如图片查看器的菜单）都不设置。
/// </summary>
public static class MenuIcons
{
    private static readonly Dictionary<string, string> Icons = new()
    {
        // 消息
        ["剪切"] = "Icon.Cut",
        ["复制"] = "Icon.Copy",
        ["粘贴"] = "Icon.Paste",
        ["全选"] = "Icon.CheckCircle",
        ["另存为"] = "Icon.Download",
        ["收藏"] = "Icon.Star",
        ["取消收藏"] = "Icon.Star",
        ["引用"] = "Icon.Quote",
        ["转发"] = "Icon.Share",
        ["多选"] = "Icon.Check",
        ["编辑"] = "Icon.Edit",
        ["更多"] = "Icon.More",
        ["删除"] = "Icon.Delete",

        // 文件
        ["打开"] = "Icon.File",
        ["保存"] = "Icon.Download",
        ["打开所在文件夹"] = "Icon.Folder",
        ["打开文件夹"] = "Icon.Folder",
        ["频道文件"] = "Icon.Folder",
        ["投票"] = "Icon.Poll",
        ["定位到聊天"] = "Icon.Location",

        // 会话、话题、频道
        ["置顶"] = "Icon.Backtop",
        ["取消置顶"] = "Icon.Backtop",
        ["备注"] = "Icon.Edit",
        ["复制链接"] = "Icon.Link",
        ["复制频道链接"] = "Icon.Link",
        ["标为未读"] = "Icon.ChatBubble",
        ["标为已读"] = "Icon.CheckCircle",
        ["设置免打扰"] = "Icon.Notification",
        ["取消免打扰"] = "Icon.NotificationOff",
        ["消息设置"] = "Icon.Notification",
        ["打开独立聊天窗口"] = "Icon.Fullscreen",
        ["从消息列表中移除"] = "Icon.Delete",
        ["重命名话题"] = "Icon.Edit",
        ["删除话题"] = "Icon.Delete",
        ["退订频道"] = "Icon.Logout",
        ["频道颜色"] = "Icon.Palette",

        // 聊天成员
        ["发送消息"] = "Icon.Chat",
        ["@TA"] = "Icon.Mention",
        ["查看资料"] = "Icon.User",
        ["屏蔽此人"] = "Icon.Forbid",
        ["取消屏蔽"] = "Icon.Forbid",

        // 聊天记录筛选
        ["按成员"] = "Icon.User",
        ["按日期…"] = "Icon.Time",
        ["清除筛选"] = "Icon.Close",

        // 联系人、分组
        ["发消息"] = "Icon.Chat",
        ["添加分组"] = "Icon.Add",
        ["重命名分组"] = "Icon.Edit",
        ["删除分组"] = "Icon.Delete",
        ["移动联系人至"] = "Icon.Usergroup",
        ["移动到其他分组"] = "Icon.Usergroup",

        // “+”菜单
        ["发起群聊"] = "Icon.Usergroup",
        ["新建话题"] = "Icon.Add",
        ["加入频道"] = "Icon.ViewList",
        ["创建频道"] = "Icon.Add",
        ["邀请成员"] = "Icon.User",

        // 输入框
        ["常用语"] = "Icon.ChatBubble",
        ["定时发送…"] = "Icon.Time",

        // 账号
        ["退出账号"] = "Icon.Logout",
        ["删除账号"] = "Icon.Delete",
        ["更多设置"] = "Icon.Setting",
    };

    /// <summary>程序启动时调用一次：所有菜单项套用模板时按名称设置图标。</summary>
    public static void Register() =>
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<MenuItem>((item, _) => Apply(item));

    private static void Apply(MenuItem item)
    {
        if (item.Icon is not null || item.ToggleType != MenuItemToggleType.None || item.Parent is MenuItem
            || item.Parent is StyledElement parent && parent.Classes.Contains("noIcons")
            || item.Header is not string header || !Icons.TryGetValue(header, out var key))
            return;
        // 前景色继承自菜单项，与文字同色（危险项为红色）
        item.Icon = new TdIcon { IconKey = key, Width = 16, Height = 16 };
    }
}
