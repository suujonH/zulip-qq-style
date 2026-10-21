namespace ZulipQQ.Models;

/// <summary>“我的设备”分组中的设备。</summary>
public sealed record Device(long Id, string Name, bool IsOnline);
