using CommunityToolkit.Mvvm.ComponentModel;
using ZulipQQ.Models;

namespace ZulipQQ.ViewModels;

/// <summary>联系人页中的一个用户。数据刷新时就地更新。</summary>
public partial class FriendItemViewModel(long userId) : ViewModelBase
{
    public long UserId { get; } = userId;

    [ObservableProperty]
    public partial string DisplayName { get; private set; } = "";

    [ObservableProperty]
    public partial string? AvatarUrl { get; private set; }

    [ObservableProperty]
    public partial bool IsOnline { get; private set; }

    /// <summary>第二行：“[在线] 个性签名”。</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public void Update(Friend friend, PresenceState presence, string? statusText)
    {
        DisplayName = friend.DisplayName;
        AvatarUrl = friend.User.AvatarUrl;
        IsOnline = presence == PresenceState.Online;
        var state = presence switch
        {
            PresenceState.Online => "[在线]",
            PresenceState.Idle => "[离开]",
            _ => "[离线]",
        };
        StatusText = $"{state} {statusText}".TrimEnd();
    }
}
