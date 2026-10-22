using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ZulipQQ.Core;
using ZulipQQ.Models;

namespace ZulipQQ.ViewModels;

/// <summary>生成会话列表项所需的数据。</summary>
/// <param name="LastMessageTime">Unix 秒；0 表示没有消息。</param>
/// <param name="Preview">已带“发送者：”前缀的预览文字。</param>
/// <param name="AvatarUrl">头像图片地址；为 null 时显示文字头像。</param>
/// <param name="AvatarText">文字头像取首字的文字。</param>
/// <param name="AvatarColor">文字头像的背景色（#RRGGBB）；为 null 时按文字取色板颜色。</param>
public sealed record ConversationSnapshot(
    ConversationKey Key,
    string Title,
    long LastMessageTime,
    string Preview,
    string? Draft,
    int UnreadCount,
    ConversationNotice Notice,
    DndLevel Level,
    bool IsPinned,
    string? AvatarUrl,
    string AvatarText,
    string? AvatarColor);

/// <summary>会话列表中的一项，负责把 <see cref="ConversationSnapshot"/> 转成界面显示用的文字。数据变化时就地更新。</summary>
public partial class ConversationItemViewModel(ConversationKey key) : ViewModelBase
{
    private const int MaxUnreadDisplay = 99;

    // 频道颜色等字符串颜色只解析一次
    private static readonly Dictionary<string, IBrush> Brushes = [];

    public ConversationKey Key { get; } = key;

    /// <summary>群助手条目：显示固定的橙色图标头像，不显示未读数。</summary>
    public bool IsHelper => Key is HelperKey;

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    public partial string TimeText { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasDraft { get; private set; }

    [ObservableProperty]
    public partial string PreviewText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? NoticeText { get; private set; }

    public bool HasNotice => NoticeText is not null;

    [ObservableProperty]
    public partial string UnreadText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMutedIcon))]
    public partial bool HasUnread { get; private set; }

    /// <summary>免打扰的会话未读数显示为灰色。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMuted), nameof(IsBlocked), nameof(ShowMutedIcon))]
    public partial DndLevel Level { get; private set; }

    public bool IsMuted => Level != DndLevel.Notify;

    /// <summary>屏蔽群消息：显示禁止图标，不显示未读。</summary>
    public bool IsBlocked => Level == DndLevel.Blocked;

    /// <summary>接收不提醒、收进群助手的会话没有未读时，在未读位置显示免打扰图标。</summary>
    public bool ShowMutedIcon => Level is DndLevel.Silent or DndLevel.Assistant && !HasUnread;

    [ObservableProperty]
    public partial bool IsPinned { get; private set; }

    [ObservableProperty]
    public partial string? AvatarUrl { get; private set; }

    [ObservableProperty]
    public partial string AvatarText { get; private set; } = "";

    [ObservableProperty]
    public partial IBrush? AvatarFill { get; private set; }

    public void Update(ConversationSnapshot s, DateTime now, bool use24h)
    {
        Title = s.Title;
        TimeText = s.LastMessageTime > 0 ? Core.TimeText.ListTime(Core.TimeText.FromUnix(s.LastMessageTime), now, use24h) : "";
        HasDraft = !string.IsNullOrEmpty(s.Draft);
        PreviewText = HasDraft ? s.Draft! : s.Preview;

        // 有草稿时 QQ 只显示草稿，不显示红色提示；屏蔽的会话不显示提示和未读
        var blocked = s.Level == DndLevel.Blocked;
        NoticeText = HasDraft || blocked ? null : NoticeOf(s.Notice);

        var unread = blocked || IsHelper ? 0 : s.UnreadCount;
        UnreadText = unread > MaxUnreadDisplay
            ? $"{MaxUnreadDisplay}+"
            : unread.ToString(CultureInfo.InvariantCulture);
        HasUnread = unread > 0;
        Level = s.Level;
        IsPinned = s.IsPinned;
        AvatarUrl = s.AvatarUrl;
        AvatarText = s.AvatarText;
        AvatarFill = BrushOf(s.AvatarColor);
    }

    private static string? NoticeOf(ConversationNotice notice) => notice switch
    {
        ConversationNotice.AtAll => "[@全体成员]",
        ConversationNotice.AtMe => "[有人@我]",
        ConversationNotice.NewFile => "[有新文件]",
        _ => null,
    };

    /// <summary>把 #RRGGBB 颜色转为画刷（缓存）；为空或无法解析时返回 null。</summary>
    internal static IBrush? BrushOf(string? color)
    {
        if (string.IsNullOrEmpty(color))
            return null;
        if (!Brushes.TryGetValue(color, out var brush))
        {
            if (!Color.TryParse(color, out var parsed))
                return null;
            Brushes[color] = brush = new SolidColorBrush(parsed);
        }
        return brush;
    }
}
