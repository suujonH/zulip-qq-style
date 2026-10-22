using System.Collections.Generic;
using ZulipQQ.Core;

namespace ZulipQQ.ViewModels;

/// <summary>群消息设置的一个级别与显示文字（与 QQ 一致）。右键菜单和更多面板共用。</summary>
public sealed record LevelOption(DndLevel Level, string Text)
{
    public static readonly IReadOnlyList<LevelOption> All =
    [
        new(DndLevel.Notify, "允许消息提醒"),
        new(DndLevel.Silent, "接收消息但不提醒"),
        new(DndLevel.Assistant, "收进群助手且不提醒"),
        new(DndLevel.Blocked, "屏蔽群消息"),
    ];

    public override string ToString() => Text;
}
