namespace ZulipQQ.Models;

/// <summary>好友分组。名称允许重复，以 <see cref="Id"/> 区分。</summary>
public sealed record FriendGroup(long Id, string Name);
