using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

public enum GroupChoiceKind
{
    /// <summary>“我的好友”：不属于任何自定义分组。</summary>
    Default,
    Group,
    CreateNew,
}

/// <summary>下拉框中的一项：“我的好友”、自定义分组，或末尾的“创建新的分组”。</summary>
public sealed record GroupChoice(GroupChoiceKind Kind, FriendGroup? Group = null)
{
    public bool IsCreateNew => Kind == GroupChoiceKind.CreateNew;

    public string Name => Kind switch
    {
        GroupChoiceKind.Default => "我的好友",
        GroupChoiceKind.Group => Group!.Name,
        _ => "创建新的分组",
    };
}

/// <summary>
/// 多选后的“设置分组”对话框：选择分组后确定，把选中的好友全部移入该分组。
/// 选择“创建新的分组”时打开“添加分组”对话框，创建成功后自动选中新分组。
/// 关闭时调用 onClosed：确定传入 true，取消或关闭传入 false。
/// </summary>
public partial class SetGroupDialogViewModel : ViewModelBase
{
    private readonly ContactStore _store;
    private readonly IReadOnlyCollection<long> _userIds;
    private readonly Action<Action<FriendGroup>> _showAddGroupDialog;
    private readonly Action<bool> _onClosed;

    public SetGroupDialogViewModel(ContactStore store, IReadOnlyCollection<long> userIds,
        Action<Action<FriendGroup>> showAddGroupDialog, Action<bool> onClosed)
    {
        _store = store;
        _userIds = userIds;
        _showAddGroupDialog = showAddGroupDialog;
        _onClosed = onClosed;
        Choices = BuildChoices();
    }

    [ObservableProperty]
    public partial IReadOnlyList<GroupChoice> Choices { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial GroupChoice? SelectedChoice { get; set; }

    partial void OnSelectedChoiceChanged(GroupChoice? oldValue, GroupChoice? newValue)
    {
        if (newValue is not { IsCreateNew: true })
            return;

        // “创建新的分组”不是可选的分组：恢复原来的选择（下拉框正在提交选择，需延后执行），再打开添加分组对话框
        Dispatcher.UIThread.Post(() => SelectedChoice = oldValue);
        _showAddGroupDialog(group =>
        {
            Choices = BuildChoices();
            SelectedChoice = Choices.First(c => c.Group?.Id == group.Id);
        });
    }

    private bool CanConfirm => SelectedChoice is { Kind: not GroupChoiceKind.CreateNew };

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        _store.MoveUsers(_userIds, SelectedChoice!.Group?.Id);
        _onClosed(true);
    }

    [RelayCommand]
    private void Cancel() => _onClosed(false);

    private List<GroupChoice> BuildChoices() =>
        [new GroupChoice(GroupChoiceKind.Default), .. _store.Groups.Select(g => new GroupChoice(GroupChoiceKind.Group, g)),
            new GroupChoice(GroupChoiceKind.CreateNew)];
}
