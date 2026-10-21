using ZulipQQ.Models;

namespace ZulipQQ.ViewModels;

public class FriendItemViewModel(Friend friend) : ViewModelBase
{
    public Friend Friend { get; } = friend;

    public string DisplayName => Friend.DisplayName;

    public bool IsOnline => Friend.User.IsOnline;

    /// <summary>第二行：“[在线] 个性签名”。</summary>
    public string StatusText => $"{(IsOnline ? "[在线]" : "[离线]")} {Friend.User.Signature}".TrimEnd();
}
