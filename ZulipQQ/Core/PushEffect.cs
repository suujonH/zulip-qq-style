using System;
using System.Collections.Generic;

namespace ZulipQQ.Core;

/// <summary>某一路外部通知（手机推送或邮件）会因哪些频道消息触发。</summary>
[Flags]
public enum PushTriggers
{
    None = 0,

    /// <summary>@我（包括 @我所在的用户组）。</summary>
    Mention = 1,

    /// <summary>@全体成员（频道或话题通配符）。</summary>
    Wildcard = 2,

    /// <summary>所有消息。</summary>
    AllMessages = 4,
}

/// <summary>计算一路通知所需的全局设置（推送和邮件各一份）。</summary>
/// <param name="DmAndMention">enable_offline_push_notifications / enable_offline_email_notifications。</param>
/// <param name="Stream">enable_stream_push_notifications / enable_stream_email_notifications。</param>
/// <param name="FollowedTopic">enable_followed_topic_push_notifications / enable_followed_topic_email_notifications。</param>
/// <param name="WildcardMentions">wildcard_mentions_notify（推送和邮件共用）。</param>
/// <param name="FollowedTopicWildcardMentions">enable_followed_topic_wildcard_mentions_notify（共用）。</param>
public readonly record struct PushRules(bool DmAndMention, bool Stream, bool FollowedTopic, bool WildcardMentions, bool FollowedTopicWildcardMentions);

/// <summary>
/// 按 Zulip 服务端规则计算频道消息的手机推送 / 邮件触发条件
/// （移植自 zerver/lib/notification_data.py 的 user_allows_notifications_in_StreamTopic）。
/// </summary>
public static class PushEffect
{
    /// <param name="rules">全局设置。</param>
    /// <param name="channelMuted">频道是否静音。</param>
    /// <param name="channelAll">频道的 push_notifications / email_notifications；null 表示跟随全局。</param>
    /// <param name="channelWildcard">频道的 wildcard_mentions_notify；null 表示跟随全局。</param>
    /// <param name="topicPolicy">话题的 visibility_policy，见 <see cref="TopicVisibility"/>。</param>
    public static PushTriggers Evaluate(PushRules rules, bool channelMuted, bool? channelAll, bool? channelWildcard, int topicPolicy)
    {
        var result = PushTriggers.None;
        var followed = topicPolicy == TopicVisibility.Followed;
        if (rules.DmAndMention)
            result |= PushTriggers.Mention;
        // @全体 与 @我 一样受“私信和 @我”开关限制
        if (rules.DmAndMention
            && ((followed && rules.FollowedTopicWildcardMentions)
                || Allows(channelMuted, topicPolicy, channelWildcard, rules.WildcardMentions, overridesMute: true)))
            result |= PushTriggers.Wildcard;
        if ((followed && rules.FollowedTopic)
            || Allows(channelMuted, topicPolicy, channelAll, rules.Stream, overridesMute: false))
            result |= PushTriggers.AllMessages;
        return result;
    }

    /// <summary>“@我、@全体、全部消息”；没有任何触发时为“无”。</summary>
    public static string Describe(PushTriggers triggers)
    {
        var parts = new List<string>();
        if (triggers.HasFlag(PushTriggers.Mention))
            parts.Add("@我");
        if (triggers.HasFlag(PushTriggers.Wildcard))
            parts.Add("@全体");
        if (triggers.HasFlag(PushTriggers.AllMessages))
            parts.Add("全部消息");
        return parts.Count == 0 ? "无" : string.Join("、", parts);
    }

    // 话题静音时不通知；频道静音时只有取消静音的话题照常（关注的话题也不例外），
    // 但频道上单独设置的 @全体 可以越过频道静音；其余情况频道设置优先，未设置时用全局设置
    private static bool Allows(bool channelMuted, int policy, bool? channelSetting, bool globalSetting, bool overridesMute)
    {
        if (policy == TopicVisibility.Muted)
            return false;
        if (channelMuted && policy != TopicVisibility.Unmuted)
            return overridesMute && channelSetting is { } v && v;
        return channelSetting ?? globalSetting;
    }
}
