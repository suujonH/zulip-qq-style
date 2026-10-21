namespace ZulipQQ.Models;

/// <summary>用户（当前登录用户或好友本体）。</summary>
public sealed record User(long Id, string Nickname, string Signature, bool IsOnline);
