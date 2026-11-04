using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 投票 / 待办消息的卡片：点击选项投票（再次点击撤销），点击任务勾选，右上角“+”添加选项 / 任务。
/// 修改问题 / 标题不支持。
/// </summary>
public sealed class WidgetCardViewModel
{
    private WidgetCardViewModel(bool isPoll, string kind, string title, IReadOnlyList<WidgetRowViewModel> rows)
    {
        IsPoll = isPoll;
        Kind = kind;
        Title = title;
        Rows = rows;
    }

    public bool IsPoll { get; }

    /// <summary>卡片类型：“投票”或“待办”。</summary>
    public string Kind { get; }

    /// <summary>投票问题 / 待办标题；没有设置时为空。</summary>
    public string Title { get; }

    public bool HasTitle => Title.Length > 0;

    public IReadOnlyList<WidgetRowViewModel> Rows { get; }

    public bool IsEmpty => Rows.Count == 0;

    public string AddToolTip => IsPoll ? "添加选项" : "添加任务";

    public string AddPlaceholder => IsPoll ? "新选项，回车添加" : "新任务，回车添加";

    /// <summary>已有同名的选项 / 任务（网页端不允许重复）。</summary>
    public bool Contains(string text) => Rows.Any(r => r.Text == text);

    public static WidgetCardViewModel Create(Widget widget, MessageItemViewModel owner, ZulipSession session) => widget switch
    {
        PollWidget poll => CreatePoll(poll, owner, session),
        TodoWidget todo => new WidgetCardViewModel(false, "待办", todo.Title, todo.Tasks
            .Select(t => new WidgetRowViewModel(
                owner,
                t.Key,
                t.Text,
                t.Description,
                share: 0,
                voters: [],
                isHighlighted: false,
                isCompleted: t.Completed,
                isTask: true,
                toolTipText: null))
            .ToList()),
        _ => new WidgetCardViewModel(false, "", "", []),
    };

    // 占比为选项票数占全部票数（一人可投多项）的比例
    private static WidgetCardViewModel CreatePoll(PollWidget poll, MessageItemViewModel owner, ZulipSession session)
    {
        var total = poll.Options.Sum(o => o.Voters.Count);
        return new WidgetCardViewModel(true, "投票", poll.Question, poll.Options
            .Select(o => new WidgetRowViewModel(
                owner,
                o.Key,
                o.Text,
                $"{o.Voters.Count}票({(total == 0 ? 0 : o.Voters.Count * 100.0 / total):0}%)",
                share: total == 0 ? 0 : (double)o.Voters.Count / total,
                // 自己投过时自己的头像排第一个（OrderBy 是稳定排序，其他人保持原顺序）
                voters: o.Voters
                    .OrderBy(id => id == session.SelfId ? 0 : 1)
                    .Select(id => new VoterViewModel(session.Users.Get(id)?.AvatarUrl, session.Contacts.DisplayName(id), id == session.SelfId))
                    .ToList(),
                isHighlighted: o.Voters.Contains(session.SelfId),
                isCompleted: false,
                isTask: false,
                toolTipText: o.Voters.Count == 0 ? "点击投票" : string.Join("、", o.Voters.Select(session.Contacts.DisplayName))))
            .ToList());
    }
}

/// <summary>卡片中的一行：投票选项（票数，自己投过的高亮）或待办任务（完成的划线）。</summary>
public sealed partial class WidgetRowViewModel(
    MessageItemViewModel owner, string key, string text, string detail, double share, IReadOnlyList<VoterViewModel> voters,
    bool isHighlighted, bool isCompleted, bool isTask, string? toolTipText)
{
    /// <summary>选项 / 任务的键，发送操作时引用。</summary>
    public string Key { get; } = key;

    public string Text { get; } = text;

    public string Detail { get; } = detail;

    /// <summary>投票选项的票数占比（0～1），显示为选项下方的进度条。</summary>
    public double Share { get; } = share;

    /// <summary>投票人（按投票顺序），头像重叠显示在进度条中。</summary>
    public IReadOnlyList<VoterViewModel> Voters { get; } = voters;

    public bool IsHighlighted { get; } = isHighlighted;

    public bool IsCompleted { get; } = isCompleted;

    public bool IsTask { get; } = isTask;

    public string? ToolTipText { get; } = toolTipText;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>投票 / 撤销投票，或切换任务的完成状态。</summary>
    [RelayCommand]
    private Task ActivateAsync() => owner.ActivateWidgetRowAsync(this);
}

/// <summary>投票人的头像。</summary>
public sealed record VoterViewModel(string? AvatarUrl, string Name, bool IsSelf = false);
