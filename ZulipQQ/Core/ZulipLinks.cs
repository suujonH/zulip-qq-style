using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ZulipQQ.Core;

/// <summary>解析出的 Zulip 内部链接。</summary>
public sealed record ParsedLink(long? StreamId, string? Topic, IReadOnlyList<long>? DmUserIds, long? NearMessageId);

/// <summary>生成和解析 Zulip 网页端的 <c>#narrow/…</c> 链接。</summary>
public static class ZulipLinks
{
    /// <summary>与 Zulip 网页端 encodeHashComponent 一致：URL 编码后把 % 换成 .，并转义 ( ) .。</summary>
    public static string EncodeHashComponent(string s)
    {
        var escaped = Uri.EscapeDataString(s);
        var sb = new StringBuilder(escaped.Length);
        // 必须一次扫描完成，否则新生成的 “.” 会被再次替换
        foreach (var c in escaped)
        {
            switch (c)
            {
                case '%': sb.Append('.'); break;
                case '(': sb.Append(".28"); break;
                case ')': sb.Append(".29"); break;
                case '.': sb.Append(".2E"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary><see cref="EncodeHashComponent"/> 的反向操作。</summary>
    public static string DecodeHashComponent(string s)
    {
        try
        {
            return Uri.UnescapeDataString(s.Replace('.', '%'));
        }
        catch (UriFormatException)
        {
            return s;
        }
    }

    /// <summary>
    /// 与 Zulip 网页端 encode_stream_id 相同：<c>{id}-{名称}</c> 编码后明显变长（例如中文名称）时只用 id。
    /// </summary>
    public static string ChannelSlug(long id, string name)
    {
        var slug = $"{id.ToString(CultureInfo.InvariantCulture)}-{(name.Length == 0 ? "unknown" : name).Replace(' ', '-')}";
        var encoded = EncodeHashComponent(slug);
        return encoded.Length - slug.Length > 10 || encoded.Length > slug.Length * 1.5
            ? id.ToString(CultureInfo.InvariantCulture)
            : encoded;
    }

    public static string ChannelLink(Uri realm, long streamId, string channelName) =>
        $"{Base(realm)}#narrow/channel/{ChannelSlug(streamId, channelName)}";

    public static string TopicLink(Uri realm, long streamId, string channelName, string topic) =>
        $"{ChannelLink(realm, streamId, channelName)}/topic/{EncodeHashComponent(topic)}";

    /// <summary>
    /// 消息链接（与 Zulip 网页端 by_conversation_and_time_url 相同）。话题：话题链接 + <c>/near/{id}</c>；
    /// 私聊：<c>#narrow/dm/{全部参与者 id，含自己，升序}-dm/near/{id}</c>，3 人及以上为 <c>-group</c>。
    /// </summary>
    public static string MessageLink(Uri realm, ConversationKey key, long selfId, long messageId, string channelName, string topicName)
    {
        var near = "/near/" + messageId.ToString(CultureInfo.InvariantCulture);
        if (key is TopicKey t)
            return TopicLink(realm, t.StreamId, channelName, topicName) + near;

        var dm = (DmKey)key;
        var ids = dm.OtherUserIds.Append(selfId).Distinct().Order()
            .Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();
        return $"{Base(realm)}#narrow/dm/{string.Join(',', ids)}-{(ids.Count >= 3 ? "group" : "dm")}{near}";
    }

    /// <summary>
    /// 解析本组织的 <c>#narrow/channel/{id}-…[/topic/{t}][/near|with/{id}]</c> 与 <c>#narrow/dm/{ids}-…[/near/{id}]</c>
    /// （只有频道时 <see cref="ParsedLink.Topic"/> 为 null）。其它组织的链接或其它形式返回 false。
    /// </summary>
    public static bool TryParse(string url, Uri realm, out ParsedLink link)
    {
        link = new ParsedLink(null, null, null, null);
        var hashIndex = url.IndexOf("#narrow/", StringComparison.Ordinal);
        if (hashIndex < 0)
            return false;

        // 消息中渲染的链接是相对地址 /#narrow/…
        var prefix = url[..hashIndex].TrimEnd('/');
        if (prefix.Length > 0 && !string.Equals(prefix, Base(realm).TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = url[(hashIndex + "#narrow/".Length)..].Split('/');
        long? streamId = null;
        string? topic = null;
        List<long>? dmIds = null;
        long? near = null;

        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var op = parts[i];
            var operand = parts[i + 1];
            switch (op)
            {
                case "channel":
                case "stream":
                    if (!TryParseLeadingId(operand, out var sid))
                        return false;
                    streamId = sid;
                    break;
                case "topic":
                case "subject":
                    topic = DecodeHashComponent(operand);
                    break;
                case "dm":
                case "pm-with":
                    {
                        var idPart = operand.Split('-')[0];
                        dmIds = [];
                        foreach (var p in idPart.Split(','))
                        {
                            if (!long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                                return false;
                            dmIds.Add(id);
                        }
                        break;
                    }
                case "near":
                case "with":
                    if (!long.TryParse(operand, NumberStyles.None, CultureInfo.InvariantCulture, out var mid))
                        return false;
                    near = mid;
                    break;
                default:
                    return false;
            }
        }

        if (streamId is not null || dmIds is not null)
        {
            link = new ParsedLink(streamId, topic, dmIds, near);
            return true;
        }
        return false;
    }

    private static bool TryParseLeadingId(string operand, out long id)
    {
        var dash = operand.IndexOf('-');
        var idText = dash >= 0 ? operand[..dash] : operand;
        return long.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    private static string Base(Uri realm)
    {
        var s = realm.ToString();
        return s.EndsWith('/') ? s : s + "/";
    }
}
