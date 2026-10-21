namespace ZulipQQ.Core;

/// <summary>
/// 本地四级免打扰与 Zulip 话题静音的双向同步规则。
/// Zulip 只有“静音 / 不静音”：本地的 Silent、Assistant、Blocked 都对应静音，Notify 对应不静音。
/// </summary>
public static class DndResolver
{
    /// <summary>话题在 Zulip 中是否处于静音状态。</summary>
    /// <param name="channelMuted">所属频道是否静音。</param>
    /// <param name="topicVisibilityPolicy">话题的 visibility_policy，见 <see cref="TopicVisibility"/>。</param>
    public static bool IsZulipMuted(bool channelMuted, int topicVisibilityPolicy)
    {
        if (topicVisibilityPolicy == TopicVisibility.Muted)
            return true;
        // 频道静音时，只有被单独取消静音或关注的话题不静音
        return channelMuted && topicVisibilityPolicy is not (TopicVisibility.Unmuted or TopicVisibility.Followed);
    }

    /// <summary>
    /// 话题的实际级别。本地级别与 Zulip 静音状态矛盾时以 Zulip 为准：
    /// 在网页端被静音时 Notify 降为 Silent；被取消静音时其它级别升为 Notify。
    /// </summary>
    public static DndLevel Resolve(DndLevel? stored, bool zulipMuted)
    {
        if (stored is null)
            return zulipMuted ? DndLevel.Silent : DndLevel.Notify;
        if (stored == DndLevel.Notify && zulipMuted)
            return DndLevel.Silent;
        if (stored != DndLevel.Notify && !zulipMuted)
            return DndLevel.Notify;
        return stored.Value;
    }

    /// <summary>私聊没有 Zulip 静音，级别只存本地。</summary>
    public static DndLevel ResolveLocalOnly(DndLevel? stored) => stored ?? DndLevel.Notify;

    /// <summary>
    /// 把话题设为 <paramref name="target"/> 时需要提交的 visibility_policy；null 表示不需要调用 API。
    /// 设为非 Notify 时总是显式静音话题：日后频道被取消静音，话题仍保持静音，本地级别不会被升级为 Notify。
    /// </summary>
    public static int? PolicyToApply(DndLevel target, bool channelMuted, int currentPolicy)
    {
        if (target == DndLevel.Notify)
        {
            if (currentPolicy == TopicVisibility.Muted)
                return channelMuted ? TopicVisibility.Unmuted : TopicVisibility.Inherit;
            if (currentPolicy == TopicVisibility.Inherit && channelMuted)
                return TopicVisibility.Unmuted;
            // 已取消静音或已关注：保持不变（已关注的话题继续关注）
            return null;
        }

        return currentPolicy == TopicVisibility.Muted ? null : TopicVisibility.Muted;
    }
}
