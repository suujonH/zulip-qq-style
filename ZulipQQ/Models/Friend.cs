namespace ZulipQQ.Models;

/// <summary>好友关系：用户 + 所在分组 + 我给他设置的备注。</summary>
public sealed record Friend(User User, long GroupId, string? Remark = null)
{
    /// <summary>有备注显示备注，否则显示昵称。</summary>
    public string DisplayName => string.IsNullOrEmpty(Remark) ? User.Nickname : Remark;
}
