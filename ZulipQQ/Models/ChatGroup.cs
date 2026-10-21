namespace ZulipQQ.Models;

/// <summary>群聊。</summary>
public sealed record ChatGroup(long Id, string Name, int MemberCount);
