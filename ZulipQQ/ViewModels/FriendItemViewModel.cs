using ZulipQQ.Models;

namespace ZulipQQ.ViewModels;

public class FriendItemViewModel(Friend friend, PresenceState presence, string? statusText) : ViewModelBase
{
    public Friend Friend { get; } = friend;

    public string DisplayName => Friend.DisplayName;

    public bool IsOnline => presence == PresenceState.Online;

    /// <summary>第二行：“[在线] 个性签名”。</summary>
    public string StatusText
    {
        get
        {
            var state = presence switch
            {
                PresenceState.Online => "[在线]",
                PresenceState.Idle => "[离开]",
                _ => "[离线]",
            };
            return $"{state} {statusText}".TrimEnd();
        }
    }
}
