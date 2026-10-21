namespace ZulipQQ.Core;

/// <summary>会话的四级免打扰，数值保存在 SQLite 中，不能修改。</summary>
public enum DndLevel
{
    /// <summary>允许消息提醒。</summary>
    Notify = 0,

    /// <summary>接收消息但不提醒。</summary>
    Silent = 1,

    /// <summary>收进群助手且不提醒。</summary>
    Assistant = 2,

    /// <summary>屏蔽群消息（收到即视为已读）。</summary>
    Blocked = 3,
}

/// <summary>Zulip 话题的 visibility_policy。</summary>
public static class TopicVisibility
{
    public const int Inherit = 0;
    public const int Muted = 1;
    public const int Unmuted = 2;
    public const int Followed = 3;
}
