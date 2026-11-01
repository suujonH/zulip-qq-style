namespace ZulipQQ.Models;

/// <summary>好友关系：用户 + 所在分组 + 我给他设置的备注。<see cref="GroupId"/> 为 null 表示只在“我的好友”中。</summary>
public sealed record Friend(ZulipUser User, long? GroupId, string? Remark = null)
{
    /// <summary>有备注显示备注，否则显示昵称。</summary>
    public string DisplayName => string.IsNullOrEmpty(Remark) ? User.FullName : Remark;
}
