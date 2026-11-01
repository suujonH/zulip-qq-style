using System;
using System.Linq;
using ZulipQQ.Core;

namespace ZulipQQ.Services;

/// <summary>输入框渲染 Markdown 时判断提及、频道、表情是否存在（与服务器一样，不存在的按普通文字显示）。</summary>
public sealed class ZulipMarkdownResolver(ZulipSession session) : IMarkdownResolver
{
    public bool HasUser(string name, long? id) =>
        id is { } userId
            ? session.Users.Get(userId) is not null
            : session.Users.ActiveHumans(excludeSelf: false).Concat(session.Users.ActiveBots())
                .Any(u => string.Equals(u.FullName, name, StringComparison.OrdinalIgnoreCase));

    public bool HasGroup(string name) =>
        session.UserGroups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool HasChannel(string name) =>
        session.Channels.All.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool HasEmoji(string name) => session.Emoji.Catalog.TryGet(name) is not null;
}
