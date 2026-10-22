using System.Collections.Generic;
using System.Linq;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>投票 / 待办消息的只读卡片。投票和勾选任务需要在网页中操作。</summary>
public sealed class WidgetCardViewModel
{
    private WidgetCardViewModel(string kind, string title, IReadOnlyList<WidgetRowViewModel> rows)
    {
        Kind = kind;
        Title = title;
        Rows = rows;
    }

    /// <summary>卡片类型：“投票”或“待办”。</summary>
    public string Kind { get; }

    /// <summary>投票问题 / 待办标题；没有设置时为空。</summary>
    public string Title { get; }

    public bool HasTitle => Title.Length > 0;

    public IReadOnlyList<WidgetRowViewModel> Rows { get; }

    public bool IsEmpty => Rows.Count == 0;

    public static WidgetCardViewModel Create(Widget widget, ZulipSession session) => widget switch
    {
        PollWidget poll => new WidgetCardViewModel("投票", poll.Question, poll.Options
            .Select(o => new WidgetRowViewModel(
                o.Text,
                $"{o.Voters.Count}票",
                IsHighlighted: o.Voters.Contains(session.SelfId),
                IsCompleted: false,
                IsTask: false,
                ToolTipText: o.Voters.Count == 0 ? null : string.Join("、", o.Voters.Select(session.Contacts.DisplayName))))
            .ToList()),
        TodoWidget todo => new WidgetCardViewModel("待办", todo.Title, todo.Tasks
            .Select(t => new WidgetRowViewModel(
                t.Text,
                t.Description,
                IsHighlighted: false,
                IsCompleted: t.Completed,
                IsTask: true,
                ToolTipText: null))
            .ToList()),
        _ => new WidgetCardViewModel("", "", []),
    };
}

/// <summary>卡片中的一行：投票选项（票数，自己投过的高亮）或待办任务（完成的划线）。</summary>
public sealed record WidgetRowViewModel(
    string Text, string Detail, bool IsHighlighted, bool IsCompleted, bool IsTask, string? ToolTipText)
{
    public bool HasDetail => Detail.Length > 0;
}
