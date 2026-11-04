using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ZulipQQ.ViewModels;

/// <summary>
/// “发起投票”对话框（Zulip 的 /poll）：填写问题和选项，确定时生成 “/poll 问题” 加每行一个选项的消息内容，
/// 交给 <c>send</c> 发送；之后（确定或取消）调用 <c>onClosed</c>。
/// 问题和选项都只能是一行（换行会被服务器当成新的选项），空白的选项不发送。
/// </summary>
public partial class PollDialogViewModel : ViewModelBase
{
    /// <summary>最少显示的选项输入框个数（少于此数时不能删除）。</summary>
    public const int MinOptions = 2;

    private readonly Action<string> _send;
    private readonly Action _onClosed;

    public PollDialogViewModel(Action<string> send, Action onClosed)
    {
        _send = send;
        _onClosed = onClosed;
        Options.CollectionChanged += (_, _) => UpdateCanRemove();
        for (var i = 0; i < MinOptions; i++)
            AddOption();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Question { get; set; } = "";

    public ObservableCollection<PollOptionItem> Options { get; } = [];

    private bool CanConfirm => !string.IsNullOrWhiteSpace(Question);

    [RelayCommand]
    private void AddOption() => Options.Add(new PollOptionItem(this));

    internal void Remove(PollOptionItem option)
    {
        if (Options.Count > MinOptions)
            Options.Remove(option);
    }

    private void UpdateCanRemove()
    {
        foreach (var option in Options)
            option.CanRemove = Options.Count > MinOptions;
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        var lines = Options.Select(o => OneLine(o.Text)).Where(t => t.Length > 0);
        _send(string.Join("\n", [$"/poll {OneLine(Question)}", .. lines]));
        _onClosed();
    }

    [RelayCommand]
    private void Cancel() => _onClosed();

    private static string OneLine(string text) =>
        string.Join(' ', text.Split('\n', '\r').Select(s => s.Trim()).Where(s => s.Length > 0));
}

/// <summary>投票对话框中的一个选项输入框。</summary>
public partial class PollOptionItem(PollDialogViewModel owner) : ViewModelBase
{
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>选项多于 <see cref="PollDialogViewModel.MinOptions"/> 个时才能删除。</summary>
    [ObservableProperty]
    public partial bool CanRemove { get; set; }

    [RelayCommand]
    private void Remove() => owner.Remove(this);
}
