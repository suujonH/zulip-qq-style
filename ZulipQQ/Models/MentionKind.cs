namespace ZulipQQ.Models;

/// <summary>未读消息对自己的提及类型。</summary>
public enum MentionKind
{
    None,

    /// <summary>@我（含所在的用户组）。</summary>
    Personal,

    /// <summary>@全体成员、@topic。</summary>
    Wildcard,
}
