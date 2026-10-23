using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Core;

/// <summary>
/// 会话的标识：话题（QQ 的“群”）、私聊（含多人私聊和与自己的私聊），以及只用于置顶状态的会话夹条目（群助手、已解决话题）。
/// </summary>
public abstract record ConversationKey
{
    /// <summary>群助手条目。</summary>
    public static ConversationKey Helper => HelperKey.Instance;

    /// <summary>已解决话题条目。</summary>
    public static ConversationKey Resolved => ResolvedKey.Instance;

    /// <summary>
    /// SQLite 中保存的形式：话题 <c>t:{频道id}:{小写话题}</c>，私聊 <c>d:{id1,id2}</c>（与自己为 <c>d:</c>），群助手 <c>helper</c>，已解决话题 <c>resolved</c>。
    /// </summary>
    public abstract string ToStorageString();

    /// <summary>
    /// 解析 <see cref="ToStorageString"/> 的结果。解析出的 <see cref="TopicKey.Topic"/> 是小写的，只能用于比较，不能用于显示或请求。
    /// </summary>
    /// <exception cref="FormatException">格式不正确。</exception>
    public static ConversationKey Parse(string s)
    {
        if (s == HelperKey.StorageString)
            return Helper;
        if (s == ResolvedKey.StorageString)
            return Resolved;

        if (s.StartsWith("t:", StringComparison.Ordinal))
        {
            var parts = s.Split(':', 3);
            if (parts.Length == 3 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var streamId))
                return new TopicKey(streamId, parts[2]);
        }
        else if (s.StartsWith("d:", StringComparison.Ordinal))
        {
            var body = s[2..];
            if (body.Length == 0)
                return new DmKey([]);

            List<long> ids = [];
            foreach (var part in body.Split(','))
            {
                if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    throw new FormatException($"无效的会话键：{s}");
                ids.Add(id);
            }
            return new DmKey([.. ids.Distinct().Order()]);
        }

        throw new FormatException($"无效的会话键：{s}");
    }

    /// <summary>消息所属的会话。</summary>
    public static ConversationKey FromMessage(MessageDto m, long selfId)
    {
        return m.IsStream
            ? new TopicKey(m.StreamId ?? 0, m.Subject)
            : DmKey.Create(m.RecipientUserIds(), selfId);
    }
}

/// <summary>话题。话题名不区分大小写（与 Zulip 一致）。</summary>
public sealed record TopicKey(long StreamId, string Topic) : ConversationKey
{
    public override string ToStorageString() =>
        $"t:{StreamId.ToString(CultureInfo.InvariantCulture)}:{Topic.ToLowerInvariant()}";

    public bool Equals(TopicKey? other) =>
        other is not null
        && StreamId == other.StreamId
        && StringComparer.OrdinalIgnoreCase.Equals(Topic, other.Topic);

    public override int GetHashCode() =>
        HashCode.Combine(StreamId, StringComparer.OrdinalIgnoreCase.GetHashCode(Topic));
}

/// <summary>私聊。<see cref="OtherUserIds"/> 已排序、去重、不含自己；空数组表示与自己的私聊。</summary>
public sealed record DmKey(ImmutableArray<long> OtherUserIds) : ConversationKey
{
    /// <summary>去掉自己、去重并排序后构造。</summary>
    public static DmKey Create(IEnumerable<long> userIds, long selfId) =>
        new([.. userIds.Where(id => id != selfId).Distinct().Order()]);

    public bool IsSelfDm => OtherUserIds.IsDefaultOrEmpty;

    public bool IsGroupDm => !OtherUserIds.IsDefault && OtherUserIds.Length >= 2;

    public override string ToStorageString() =>
        "d:" + string.Join(',', Ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));

    // record 对 ImmutableArray 默认比较底层数组引用，必须按序列比较
    public bool Equals(DmKey? other) =>
        other is not null && Ids.AsSpan().SequenceEqual(other.Ids.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var id in Ids)
            hash.Add(id);
        return hash.ToHashCode();
    }

    private ImmutableArray<long> Ids => OtherUserIds.IsDefault ? [] : OtherUserIds;
}

/// <summary>会话夹条目：主列表中代表一组会话的入口，只用于保存置顶状态。</summary>
public abstract record FolderKey : ConversationKey;

/// <summary>群助手条目。</summary>
public sealed record HelperKey : FolderKey
{
    internal const string StorageString = "helper";

    public static readonly HelperKey Instance = new();

    private HelperKey()
    {
    }

    public override string ToStorageString() => StorageString;
}

/// <summary>已解决话题条目（开启“收起已解决话题”时）。</summary>
public sealed record ResolvedKey : FolderKey
{
    internal const string StorageString = "resolved";

    public static readonly ResolvedKey Instance = new();

    private ResolvedKey()
    {
    }

    public override string ToStorageString() => StorageString;
}
