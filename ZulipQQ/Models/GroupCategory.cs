using System.Collections.Generic;

namespace ZulipQQ.Models;

/// <summary>群聊分类，例如“我创建的群聊”、“我加入的群聊”。</summary>
public sealed record GroupCategory(string Name, IReadOnlyList<ChatGroup> Groups);
