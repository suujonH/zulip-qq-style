namespace ZulipQQ.Models;

/// <summary>频道中的一个话题。<see cref="Name"/> 为原始大小写。</summary>
public sealed record TopicInfo(string Name, long MaxId);
